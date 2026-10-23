using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Web.Configuration;

namespace CustomerHelpAgent.Web.Ai;

/// <summary>
/// 送出一次 Chat Completions 呼叫：HTTP、錯誤對應、Token 用量記錄都在這裡，
/// 0.9.72 起也在這裡把完整請求與回應記進 AI 對話紀錄（兩邊以同一個 CallId 關聯）。
///
/// 0.9.68 起由 <see cref="AiLogAnalysisService"/> 抽出，AI 日誌分析與 AI 例外分析共用，
/// 兩邊的錯誤處理與記帳規則才不會各自漂移。
/// </summary>
public interface IAiChatCompletionClient
{
    /// <summary>
    /// 送出並解析回應。
    /// ⚠️ 這個方法<b>不丟例外</b>，所有失敗都以 <see cref="AiAnalysisResult"/> 回報
    /// （<see cref="AiAnalysisResult.Prompt"/> 由呼叫端自行補上）。
    /// </summary>
    Task<AiAnalysisResult> CompleteAsync(AiChatCompletionRequest request, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IAiChatCompletionClient" />
public sealed class AiChatCompletionClient : IAiChatCompletionClient
{
    /// <summary>
    /// named client 的名稱。為什麼用 named client 而非 typed client，
    /// 見 ServiceCollectionExtensions.AddApplicationServices 的註解。
    /// </summary>
    public const string HttpClientName = "AiChatCompletions";

    private readonly ILogger<AiChatCompletionClient> logger;
    private readonly IHttpClientFactory httpClientFactory;
    private readonly IOptionsMonitor<AiSettings> optionsMonitor;
    private readonly ITokenUsageRecorder tokenUsageRecorder;
    private readonly CurrentUserService currentUserService;
    private readonly IAiCallLogRecorder aiCallLogRecorder;
    private readonly IAiQuotaService quotaService;
    private readonly IAuditLogService auditLogService;
    private readonly IOptionsMonitor<SlowOperationSettings>? slowOptions;

    public AiChatCompletionClient(
        ILogger<AiChatCompletionClient> logger,
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<AiSettings> optionsMonitor,
        ITokenUsageRecorder tokenUsageRecorder,
        CurrentUserService currentUserService,
        IAiCallLogRecorder aiCallLogRecorder,
        IAiQuotaService quotaService,
        IAuditLogService auditLogService,
        IOptionsMonitor<SlowOperationSettings>? slowOptions = null)
    {
        this.slowOptions = slowOptions;
        this.logger = logger;
        this.httpClientFactory = httpClientFactory;
        this.optionsMonitor = optionsMonitor;
        this.tokenUsageRecorder = tokenUsageRecorder;
        this.currentUserService = currentUserService;
        this.aiCallLogRecorder = aiCallLogRecorder;
        this.quotaService = quotaService;
        this.auditLogService = auditLogService;
    }

    /// <summary>
    /// 把這次呼叫記進「Token 用量」。
    ///
    /// ⚠️ 記錄點刻意放在這一層而不是畫面層：原始 usage、實際模型名稱、耗時，
    /// 回到畫面之後就已經丟失了。成功與失敗都記 ——
    /// 「回應成功但內容為空」那種情況付了錢卻沒拿到東西，最值得被看見。
    ///
    /// 記錄失敗絕不影響分析結果（Service 內部已全程吞例外）。
    /// 同時把結果寫到 <paramref name="capture"/>，兩邊以同一個 CallId 關聯。
    /// </summary>
    private async Task RecordUsageAsync(
        AiCallCapture capture,
        string operation,
        AiSettings settings,
        string? modelName,
        AiTokenUsage? usage,
        string? rawUsageJson,
        TimeSpan elapsed,
        bool success,
        AiAnalysisFailureReason reason)
    {
        var user = currentUserService.CurrentUser;

        capture.SetOutcome(success, reason.ToString());

        await tokenUsageRecorder.RecordAsync(new TokenUsageEntry
        {
            Operation = operation,
            CallKind = TokenUsageCallKinds.Chat,
            Provider = settings.GetProvider().ToString(),
            Model = string.IsNullOrWhiteSpace(modelName) ? settings.Model : modelName,
            Account = string.IsNullOrWhiteSpace(user.Account) ? null : user.Account,
            UserId = user.Id == 0 ? null : user.Id,
            InputCount = usage?.InputCount,
            OutputCount = usage?.OutputCount,
            TotalCount = usage?.TotalCount,
            CachedInputCount = usage?.CachedInputCount,
            ReasoningCount = usage?.ReasoningCount,
            ElapsedMilliseconds = (long)elapsed.TotalMilliseconds,
            Success = success,
            FailureReason = reason == AiAnalysisFailureReason.None ? null : reason.ToString(),
            RawUsageJson = rawUsageJson,
            CallId = capture.CallId,
        });
    }

    public async Task<AiAnalysisResult> CompleteAsync(
        AiChatCompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // ⚠️ 在呼叫時才讀設定（CurrentValue），不在建構時快取：Blazor Server 的 DI scope
        // 等於 SignalR circuit，可存活數小時，IOptions 的一次性快照會讓設定變更永遠吃不到。
        var settings = optionsMonitor.CurrentValue;

        var invalid = AiChatEndpoint.Validate(settings);
        if (invalid is not null)
        {
            logger.LogWarning("AI chat completion skipped because settings are incomplete. Operation={Operation}", request.Operation);
            return AiAnalysisResult.Failure(AiAnalysisFailureReason.NotConfigured, invalid);
        }

        // AI 用量上限（0.9.109 起）：設定完整之後、送出之前檢查。並行的呼叫可能讓已用金額小幅超過上限（已接受）。
        var caller = currentUserService.CurrentUser;
        var callerId = caller.Id == 0 ? (int?)null : caller.Id;
        var reached = await quotaService.CheckAsync(callerId, cancellationToken);
        if (reached is not null)
        {
            logger.LogWarning(
                "AI chat completion blocked by usage quota. Operation={Operation}, Scope={Scope}, Period={Period}",
                request.Operation, reached.Scope, reached.Period);
            await auditLogService.WriteAsync(
                AuditActions.Ai.QuotaBlocked,
                success: false,
                actorUserId: callerId,
                actorAccount: string.IsNullOrWhiteSpace(caller.Account) ? null : caller.Account,
                targetType: "AiQuota",
                targetId: $"{reached.Scope}:{reached.Period}",
                detail: $"operation={request.Operation}; limit={reached.LimitTwd}; used={AiQuotaService.Money(reached.UsedTwd)}");
            return AiAnalysisResult.Failure(AiAnalysisFailureReason.QuotaExceeded, AiQuotaService.BlockedMessage(reached));
        }

        var characters = request.Messages.Sum(message => message.Content.Length);
        var descriptor = AiChatEndpoint.Create(settings);
        var body = AiChatRequestFactory.CreateRequestJson(settings, request.Messages);

        var user = currentUserService.CurrentUser;
        var capture = new AiCallCapture(
            request.Operation,
            settings.GetProvider().ToString(),
            settings.Model,
            user.Account,
            user.Id,
            descriptor.RequestUri,
            body,
            request.RelatedInfo,
            request.ConversationId);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, descriptor.RequestUri)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };

            // ⚠️ 金鑰只掛在「這一次」的請求上，絕不設到 client.DefaultRequestHeaders：
            // named client 是從池子拿的，設在上面會把金鑰留給後續所有請求，
            // 而且設定重載換了金鑰之後用的還是舊的。
            httpRequest.Headers.TryAddWithoutValidation(descriptor.AuthHeaderName, descriptor.AuthHeaderValue);

            capture.MarkSent();
            using var response = await client.SendAsync(httpRequest, cancellationToken);
            capture.HttpStatus = (int)response.StatusCode;
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            capture.ResponseBody = payload;
            stopwatch.Stop();

            if (response.IsSuccessStatusCode == false)
            {
                var failure = MapHttpFailure(request, response.StatusCode, payload, characters, stopwatch.Elapsed);
                // 沒有用量可記，但呼叫確實發生過、上游也可能已經計費，至少要留下次數。
                await RecordUsageAsync(
                    capture, request.Operation, settings, settings.Model, null, null, stopwatch.Elapsed, false, failure.Reason);
                return failure;
            }

            var parsed = AiChatResponseParser.Parse(payload);
            capture.ResponseText = parsed.Content ?? string.Empty;
            capture.FinishReason = parsed.FinishReason;
            capture.ResponseModel = parsed.ModelName;
            if (string.IsNullOrWhiteSpace(parsed.Content))
            {
                var message = DescribeEmptyResponse(parsed);

                logger.LogWarning(
                    "AI chat completion returned empty content. Operation={Operation}, FinishReason={FinishReason}, Characters={Characters}",
                    request.Operation,
                    parsed.FinishReason,
                    characters);

                // ⚠️ 這一支最值得記：回應成功、上游照常計費，但使用者什麼都沒拿到。
                await RecordUsageAsync(
                    capture, request.Operation, settings, parsed.ModelName, parsed.Usage,
                    AiChatResponseParser.ExtractUsageJson(payload),
                    stopwatch.Elapsed, false, AiAnalysisFailureReason.EmptyResponse);

                return AiAnalysisResult.Failure(
                    AiAnalysisFailureReason.EmptyResponse,
                    message,
                    elapsed: stopwatch.Elapsed,
                    usage: parsed.Usage);
            }

            logger.LogInformation(
                "AI chat completion completed. Operation={Operation}, Messages={Messages}, Characters={Characters}, "
                + "UsageInput={UsageInput}, UsageOutput={UsageOutput}, ElapsedMs={ElapsedMs}",
                request.Operation,
                request.Messages.Count,
                characters,
                parsed.Usage?.InputCount,
                parsed.Usage?.OutputCount,
                stopwatch.ElapsedMilliseconds);

            // 慢 AI 呼叫（LOG-21）。
            var slowThreshold = slowOptions?.CurrentValue.AiCallMs ?? 0;
            if (SlowOperationSettings.IsSlow(stopwatch.Elapsed, slowThreshold))
            {
                logger.LogWarning(
                    "Slow AI chat completion. Operation={Operation}, ElapsedMilliseconds={ElapsedMilliseconds}, ThresholdMilliseconds={ThresholdMilliseconds}",
                    request.Operation, stopwatch.ElapsedMilliseconds, slowThreshold);
            }

            await RecordUsageAsync(
                capture, request.Operation, settings, parsed.ModelName, parsed.Usage,
                AiChatResponseParser.ExtractUsageJson(payload),
                stopwatch.Elapsed, true, AiAnalysisFailureReason.None);

            return new AiAnalysisResult
            {
                Success = true,
                Markdown = parsed.Content,
                Usage = parsed.Usage,
                ModelName = string.IsNullOrEmpty(parsed.ModelName)
                    ? settings.Model
                    : parsed.ModelName,
                IsTruncatedByLength =
                    string.Equals(parsed.FinishReason, "length", StringComparison.OrdinalIgnoreCase),
                Elapsed = stopwatch.Elapsed,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 呼叫端主動放棄。這是使用者的決定，不是故障 —— 記 Information 而非 Error，
            // 也不要落到下面的泛型 catch 被寫成「failed unexpectedly」。
            stopwatch.Stop();
            // Token 用量不記取消，但請求確實送出過，AI 對話紀錄照記。
            capture.SetOutcome(false, AiAnalysisFailureReason.Canceled.ToString());
            logger.LogInformation(
                "AI chat completion canceled by the caller. Operation={Operation}, ElapsedMs={ElapsedMs}",
                request.Operation,
                stopwatch.ElapsedMilliseconds);
            return AiAnalysisResult.Failure(
                AiAnalysisFailureReason.Canceled,
                "已取消本次 AI 分析。",
                elapsed: stopwatch.Elapsed);
        }
        catch (TaskCanceledException ex) when (cancellationToken.IsCancellationRequested == false)
        {
            // HttpClient 的逾時在 .NET 上表現為 TaskCanceledException（內含 TimeoutException），
            // 所以要用 cancellationToken 是否真的被取消來區分「使用者取消」與「逾時」。
            stopwatch.Stop();
            capture.ExceptionType = ex.GetType().Name;
            logger.LogError(
                ex,
                "AI chat completion timed out. Operation={Operation}, TimeoutSeconds={TimeoutSeconds}",
                request.Operation,
                settings.TimeoutSeconds);
            await RecordUsageAsync(
                capture, request.Operation, settings, settings.Model, null, null, stopwatch.Elapsed, false,
                AiAnalysisFailureReason.Timeout);
            return AiAnalysisResult.Failure(
                AiAnalysisFailureReason.Timeout,
                $"AI 分析逾時（超過 {settings.TimeoutSeconds} 秒），{request.TimeoutHint}",
                elapsed: stopwatch.Elapsed,
                detail: ex.ToString());
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            capture.ExceptionType = ex.GetType().Name;
            logger.LogError(ex, "AI chat completion request failed at transport level. Operation={Operation}", request.Operation);
            await RecordUsageAsync(
                capture, request.Operation, settings, settings.Model, null, null, stopwatch.Elapsed, false,
                AiAnalysisFailureReason.UpstreamError);
            return AiAnalysisResult.Failure(
                AiAnalysisFailureReason.UpstreamError,
                "無法連線到 AI 服務，請確認網路連線與 AiSettings:Endpoint 設定。",
                elapsed: stopwatch.Elapsed,
                detail: ex.ToString());
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            capture.ExceptionType = ex.GetType().Name;
            capture.SetOutcome(false, AiAnalysisFailureReason.Unexpected.ToString());
            logger.LogError(ex, "AI chat completion failed unexpectedly. Operation={Operation}", request.Operation);
            return AiAnalysisResult.Failure(
                AiAnalysisFailureReason.Unexpected,
                $"AI 分析失敗：{ex.GetType().Name}。",
                elapsed: stopwatch.Elapsed,
                detail: ex.ToString());
        }
        finally
        {
            // 單一記錄點：每個分支都已填好 capture。沒送出（MarkSent 之前就失敗）不記。
            // ⚠️ 不傳 cancellationToken：取消那一支的 token 已經取消了，照傳就記不進去。
            await AiCallCapture.RecordSafelyAsync(aiCallLogRecorder, capture, stopwatch.Elapsed, logger);
        }
    }

    /// <summary>
    /// 回應成功但沒有內容時，說明是為什麼。
    ///
    /// ⚠️ <c>finish_reason</c> 為 <c>length</c> 這一支特別重要：推論模型的思考 token 也算進
    /// <c>max_completion_tokens</c>，額度太小就會在產出任何可見文字之前耗盡。使用者拿到的是
    /// 空回應，卻仍要付輸入與思考的費用 —— 只說「請稍後再試」會讓人一再重試、一再付錢。
    /// </summary>
    private static string DescribeEmptyResponse(AiChatParseResult parsed)
    {
        if (string.IsNullOrEmpty(parsed.FilterCategory) == false)
        {
            return $"AI 回應被內容過濾攔下（類別：{parsed.FilterCategory}）。";
        }

        if (string.Equals(parsed.FinishReason, "length", StringComparison.OrdinalIgnoreCase))
        {
            return "回應在產出任何內容之前就達到長度上限。請調高 AiSettings:MaxOutputTokens "
                + "或將它設為 null（推論模型的思考 token 也算進這個額度）。";
        }

        return "AI 沒有回傳任何內容，請稍後再試。";
    }

    /// <summary>
    /// 把上游的 HTTP 失敗對應成使用者看得懂的中文訊息。
    ///
    /// ⚠️ 給使用者看的訊息只由固定字串加上 <c>error.code</c> 與 <c>error.param</c> 組成
    /// （後者是參數名稱，結構上不可能夾帶送出的內容）。上游的 <c>error.message</c> 只寫進
    /// 日誌，且已在解析時截斷 —— 它可能夾帶提示詞片段，而提示詞裡是日誌或例外內容。
    /// </summary>
    private AiAnalysisResult MapHttpFailure(
        AiChatCompletionRequest request,
        HttpStatusCode status,
        string payload,
        int characters,
        TimeSpan elapsed)
    {
        AiChatResponseParser.TryGetError(payload, out var upstream);

        var (reason, message) = status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => (
                AiAnalysisFailureReason.Unauthorized,
                "AI 服務拒絕存取，請確認 AiSettings:ApiKey 是否正確或已過期。"),
            HttpStatusCode.NotFound => (
                AiAnalysisFailureReason.NotConfigured,
                "找不到指定的 AI 部署或模型，請確認 AiSettings 的 Endpoint 與 Model 設定。"),
            HttpStatusCode.TooManyRequests => (
                AiAnalysisFailureReason.RateLimited,
                "AI 服務目前限流，請稍後再試。"),
            HttpStatusCode.BadRequest => (
                AiAnalysisFailureReason.UpstreamError,
                DescribeBadRequest(request, upstream)),
            _ => (
                AiAnalysisFailureReason.UpstreamError,
                $"AI 服務回應失敗（HTTP {(int)status}），請稍後再試。"),
        };

        // ⚠️ ErrorDetail 是已截斷的上游說明。它是排查這類失敗的唯一線索 ——
        // 只記代碼的話，「哪個參數不被接受」完全看不出來。
        logger.LogError(
            "AI chat completion upstream failure. Operation={Operation}, StatusCode={StatusCode}, ErrorCode={ErrorCode}, "
            + "ErrorParam={ErrorParam}, ErrorType={ErrorType}, ErrorDetail={ErrorDetail}, Characters={Characters}",
            request.Operation,
            (int)status,
            upstream.Code,
            upstream.Param,
            upstream.Type,
            upstream.Message,
            characters);

        return AiAnalysisResult.Failure(reason, message, elapsed: elapsed, detail: DescribeHttpFailure(status, upstream));
    }

    /// <summary>給管理員複製用的 HTTP 失敗摘要：只有狀態碼與上游代碼，刻意不含 error.message（理由同上）。</summary>
    internal static string DescribeHttpFailure(HttpStatusCode status, AiUpstreamError upstream)
    {
        var parts = new List<string> { $"HTTP {(int)status}" };
        AddIfPresent("code", upstream.Code);
        AddIfPresent("type", upstream.Type);
        AddIfPresent("param", upstream.Param);
        return string.Join("; ", parts);

        void AddIfPresent(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                parts.Add($"{name}={value}");
            }
        }
    }

    /// <summary>
    /// 把 400 的錯誤翻成「使用者知道要改哪裡」的訊息。
    ///
    /// 只回代碼是不夠的：實務上最常見的 400 是送了模型不接受的參數，而代碼
    /// （<c>unsupported_value</c>）本身看不出是哪一個。上游的 <c>param</c> 就是答案。
    /// </summary>
    private static string DescribeBadRequest(AiChatCompletionRequest request, AiUpstreamError upstream)
    {
        // 送出的內容沒有字元上限，所以「內容太長」是主要的失敗模式。
        // 這個錯誤沒有 param，落到通用分支的話訊息會講不出解法 —— 解法由呼叫端提供。
        if (string.Equals(upstream.Code, "context_length_exceeded", StringComparison.OrdinalIgnoreCase))
        {
            return request.ContextLengthExceededMessage;
        }

        // 推論模型（o 系列、gpt-5 家族）只接受 temperature 的預設值，
        // 這是切換模型時最常撞到的一個，直接告訴使用者怎麼改。
        if (string.Equals(upstream.Param, "temperature", StringComparison.OrdinalIgnoreCase))
        {
            return "此模型不接受 AiSettings:Temperature 的設定值，請將它設為 null（部分推論模型只接受預設值）。";
        }

        if (string.IsNullOrEmpty(upstream.Param) == false)
        {
            return $"AI 服務不接受參數 {upstream.Param} 的設定值（代碼 {upstream.Code}），請調整 AiSettings 後再試。";
        }

        return string.IsNullOrEmpty(upstream.Code)
            ? $"AI 服務拒絕本次請求，請確認模型設定與{request.SubmittedContentLabel}。"
            : $"AI 服務拒絕本次請求（代碼 {upstream.Code}），請確認模型設定與{request.SubmittedContentLabel}。";
    }
}

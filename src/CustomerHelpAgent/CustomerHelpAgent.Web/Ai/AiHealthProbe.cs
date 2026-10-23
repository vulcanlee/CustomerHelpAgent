using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Web.Configuration;

namespace CustomerHelpAgent.Web.Ai;

/// <inheritdoc cref="IAiHealthProbe" />
public sealed class AiHealthProbe : IAiHealthProbe
{
    /// <summary>
    /// 探測用的逾時。⚠️ 刻意<b>不</b>沿用 <c>AiSettings.TimeoutSeconds</c>（預設 600 秒）——
    /// 那是給「使用者主動按下分析、願意等」的情境用的。健康監控頁是自動載入，
    /// 端點一旦沒回應就會把整個管理頁卡住十分鐘。
    /// </summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    /// <summary>探測提示詞。刻意極短，把每次探測的成本壓到幾十個 token。</summary>
    private const string ProbeSystemPrompt = "You are a health probe. Reply with a single word.";
    private const string ProbeUserMessage = "hello";

    private readonly ILogger<AiHealthProbe> logger;
    private readonly IHttpClientFactory httpClientFactory;
    private readonly IOptionsMonitor<AiSettings> optionsMonitor;
    private readonly ITokenUsageRecorder tokenUsageRecorder;
    private readonly CurrentUserService currentUserService;
    private readonly IAiCallLogRecorder aiCallLogRecorder;
    private readonly IOptionsMonitor<SlowOperationSettings>? slowOptions;

    public AiHealthProbe(
        ILogger<AiHealthProbe> logger,
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<AiSettings> optionsMonitor,
        ITokenUsageRecorder tokenUsageRecorder,
        CurrentUserService currentUserService,
        IAiCallLogRecorder aiCallLogRecorder,
        IOptionsMonitor<SlowOperationSettings>? slowOptions = null)
    {
        this.slowOptions = slowOptions;
        this.logger = logger;
        this.httpClientFactory = httpClientFactory;
        this.optionsMonitor = optionsMonitor;
        this.tokenUsageRecorder = tokenUsageRecorder;
        this.currentUserService = currentUserService;
        this.aiCallLogRecorder = aiCallLogRecorder;
    }

    public async Task<AiHealthProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var settings = optionsMonitor.CurrentValue;

        // 一定要走 Validate：它會接住 provider 打錯字的例外並翻成中文訊息。
        // 直接呼叫 settings.GetProvider() 會讓一個錯字炸掉整份健康報告。
        var invalid = AiChatEndpoint.Validate(settings);
        if (invalid is not null)
        {
            // 未設定不發請求、不記帳 —— 與 AiLogAnalysisService 的 NotConfigured 一致。
            return AiHealthProbeResult.NotConfigured(invalid);
        }

        var descriptor = AiChatEndpoint.Create(settings);
        var endpointHost = descriptor.RequestUri.Host;
        var body = AiChatRequestFactory.CreateRequestJson(settings, ProbeSystemPrompt, ProbeUserMessage);

        var user = currentUserService.CurrentUser;
        var capture = new AiCallCapture(
            TokenUsageOperations.SystemHealthCheck,
            settings.GetProvider().ToString(),
            settings.Model,
            user.Account,
            user.Id,
            descriptor.RequestUri,
            body,
            "系統健康監控 AI 連線探測",
            conversationId: null);

        // 自己的逾時。linked source 讓呼叫端的取消仍然有效。
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(ProbeTimeout);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var client = httpClientFactory.CreateClient(AiChatCompletionClient.HttpClientName);

            using var request = new HttpRequestMessage(HttpMethod.Post, descriptor.RequestUri)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };

            // ⚠️ 金鑰只掛在「這一次」的請求上，絕不設到 client.DefaultRequestHeaders：
            // named client 是從池子拿的，設在上面會把金鑰留給後續所有請求。
            request.Headers.TryAddWithoutValidation(descriptor.AuthHeaderName, descriptor.AuthHeaderValue);

            capture.MarkSent();
            using var response = await client.SendAsync(request, timeoutSource.Token);
            capture.HttpStatus = (int)response.StatusCode;
            var payload = await response.Content.ReadAsStringAsync(timeoutSource.Token);
            capture.ResponseBody = payload;
            stopwatch.Stop();

            if (response.IsSuccessStatusCode == false)
            {
                // 只取 error.code，絕不把上游 body 放進訊息或日誌。
                var code = AiChatResponseParser.TryGetError(payload, out var upstream)
                    && string.IsNullOrEmpty(upstream.Code) == false
                        ? $"（{upstream.Code}）"
                        : string.Empty;

                logger.LogError(
                    "AI health probe failed. StatusCode={StatusCode}, ElapsedMs={ElapsedMs}",
                    (int)response.StatusCode,
                    stopwatch.ElapsedMilliseconds);

                await RecordAsync(capture, settings, settings.Model, null, null, stopwatch, false, "UpstreamError");

                return new AiHealthProbeResult
                {
                    IsConfigured = true,
                    Success = false,
                    Message = $"上游回應 HTTP {(int)response.StatusCode}{code}。",
                    ModelName = settings.Model,
                    EndpointHost = endpointHost,
                    ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
                };
            }

            var parsed = AiChatResponseParser.Parse(payload);
            capture.ResponseText = parsed.Content ?? string.Empty;
            capture.FinishReason = parsed.FinishReason;
            capture.ResponseModel = parsed.ModelName;
            var modelName = string.IsNullOrEmpty(parsed.ModelName) ? settings.Model : parsed.ModelName;
            var hasContent = string.IsNullOrWhiteSpace(parsed.Content) == false;

            logger.LogInformation(
                "AI health probe completed. HasContent={HasContent}, ElapsedMs={ElapsedMs}",
                hasContent,
                stopwatch.ElapsedMilliseconds);

            var slowThreshold = slowOptions?.CurrentValue.AiCallMs ?? 0;
            if (SlowOperationSettings.IsSlow(stopwatch.Elapsed, slowThreshold))
            {
                logger.LogWarning(
                    "Slow AI health probe. ElapsedMilliseconds={ElapsedMilliseconds}, ThresholdMilliseconds={ThresholdMilliseconds}",
                    stopwatch.ElapsedMilliseconds, slowThreshold);
            }

            await RecordAsync(
                capture,
                settings,
                parsed.ModelName,
                parsed.Usage,
                AiChatResponseParser.ExtractUsageJson(payload),
                stopwatch,
                hasContent,
                hasContent ? null : "EmptyResponse");

            return new AiHealthProbeResult
            {
                IsConfigured = true,
                Success = hasContent,
                Message = hasContent ? string.Empty : "上游回應成功，但內容為空。",
                ModelName = modelName,
                EndpointHost = endpointHost,
                ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 呼叫端主動放棄（例如使用者離開頁面）。不是故障，比照既有慣例不記帳；
            // 但請求可能已經送出，AI 對話紀錄照記（沒送出的由 capture.Sent 擋掉）。
            stopwatch.Stop();
            capture.SetOutcome(false, AiAnalysisFailureReason.Canceled.ToString());
            return new AiHealthProbeResult
            {
                IsConfigured = true,
                Success = false,
                Message = "已取消本次探測。",
                ModelName = settings.Model,
                EndpointHost = endpointHost,
                ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
            };
        }
        catch (Exception ex)
        {
            // 逾時（TaskCanceledException）、傳輸失敗（HttpRequestException）與其餘意外
            // 一律收斂在這裡：健康檢查的單一項目絕不能讓整份報告掛掉。
            stopwatch.Stop();
            var timedOut = ex is OperationCanceledException;
            capture.ExceptionType = ex.GetType().Name;

            logger.LogError(
                ex,
                "AI health probe failed. TimedOut={TimedOut}, ElapsedMs={ElapsedMs}",
                timedOut,
                stopwatch.ElapsedMilliseconds);

            await RecordAsync(
                capture, settings, settings.Model, null, null, stopwatch, false, timedOut ? "Timeout" : "TransportError");

            return new AiHealthProbeResult
            {
                IsConfigured = true,
                Success = false,
                Message = timedOut
                    ? $"探測逾時（超過 {ProbeTimeout.TotalSeconds:N0} 秒）。"
                    : $"無法連線到 AI 服務（{ex.GetType().Name}）。",
                ModelName = settings.Model,
                EndpointHost = endpointHost,
                ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
            };
        }
        finally
        {
            await AiCallCapture.RecordSafelyAsync(aiCallLogRecorder, capture, stopwatch.Elapsed, logger);
        }
    }

    /// <summary>
    /// 把這次探測記進「Token 用量」。
    ///
    /// 專案慣例（開發慣例與限制速查 §6.6）：每一個發出 LLM API 呼叫的地方都必須記一筆，
    /// 成功與失敗都要記，否則 Token 用量頁會漏帳。只有「未設定」與「呼叫端取消」免記。
    /// 記錄本身絕不影響探測結果（TokenUsageLogService 內部已全程吞例外）。
    /// 同時把結果寫到 <paramref name="capture"/>，兩邊以同一個 CallId 關聯。
    /// </summary>
    private async Task RecordAsync(
        AiCallCapture capture,
        AiSettings settings,
        string? modelName,
        AiTokenUsage? usage,
        string? rawUsageJson,
        Stopwatch stopwatch,
        bool success,
        string? failureReason)
    {
        var user = currentUserService.CurrentUser;

        capture.SetOutcome(success, failureReason);

        await tokenUsageRecorder.RecordAsync(new TokenUsageEntry
        {
            Operation = TokenUsageOperations.SystemHealthCheck,
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
            ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
            Success = success,
            FailureReason = failureReason,
            RawUsageJson = rawUsageJson,
            CallId = capture.CallId,
        });
    }
}

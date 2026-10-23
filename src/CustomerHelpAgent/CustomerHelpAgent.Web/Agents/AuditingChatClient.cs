using System.ClientModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Web.Ai;
using CustomerHelpAgent.Web.Configuration;

namespace CustomerHelpAgent.Web.Agents;

/// <summary>
/// 包住 Agent Framework 用的 <see cref="IChatClient"/>：每一次模型呼叫都比照
/// <see cref="AiChatCompletionClient.CompleteAsync"/> 查額度、記 Token 用量、記 AI 對話紀錄（同一個 CallId 關聯）。
///
/// ⚠️ 它必須在函式呼叫迴圈的<b>內側</b>（ChatClientAgent 會在外面再包 FunctionInvokingChatClient），
/// 一次 Agent 執行裡的每一回模型呼叫才會各自記一筆，且共用同一個對話 Id。
///
/// 與 AiChatCompletionClient 不同：失敗一律重拋（Agent 框架要看到例外），額度擋下拋 <see cref="AiQuotaExceededException"/>；
/// 回應文字為空不算失敗 —— 函式呼叫那一回本來就沒有文字。
/// </summary>
internal sealed class AuditingChatClient : DelegatingChatClient
{
    private readonly string operation;
    private readonly Guid conversationId;
    private readonly AiSettings settings;
    private readonly IOptionsMonitor<SlowOperationSettings> slowOptions;
    private readonly ITokenUsageRecorder tokenUsageRecorder;
    private readonly IAiCallLogRecorder aiCallLogRecorder;
    private readonly IAiQuotaService quotaService;
    private readonly IAuditLogService auditLogService;
    private readonly CurrentUserService currentUserService;
    private readonly ILogger logger;

    public AuditingChatClient(
        IChatClient innerClient,
        string operation,
        Guid conversationId,
        AiSettings settings,
        IOptionsMonitor<SlowOperationSettings> slowOptions,
        ITokenUsageRecorder tokenUsageRecorder,
        IAiCallLogRecorder aiCallLogRecorder,
        IAiQuotaService quotaService,
        IAuditLogService auditLogService,
        CurrentUserService currentUserService,
        ILogger logger)
        : base(innerClient)
    {
        this.operation = operation;
        this.conversationId = conversationId;
        this.settings = settings;
        this.slowOptions = slowOptions;
        this.tokenUsageRecorder = tokenUsageRecorder;
        this.aiCallLogRecorder = aiCallLogRecorder;
        this.quotaService = quotaService;
        this.auditLogService = auditLogService;
        this.currentUserService = currentUserService;
        this.logger = logger;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var list = messages as IList<ChatMessage> ?? messages.ToList();
        var capture = await BeginAsync(list, options, cancellationToken);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var response = await base.GetResponseAsync(list, options, cancellationToken);
            stopwatch.Stop();
            await RecordSuccessAsync(capture, response, stopwatch.Elapsed);
            return response;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            await RecordFailureAsync(capture, ex, stopwatch.Elapsed, cancellationToken);
            throw;
        }
        finally
        {
            // 單一記錄點；不傳 cancellationToken（取消那一支的 token 已經取消了）。
            await AiCallCapture.RecordSafelyAsync(aiCallLogRecorder, capture, stopwatch.Elapsed, logger);
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var list = messages as IList<ChatMessage> ?? messages.ToList();
        var capture = await BeginAsync(list, options, cancellationToken);
        var stopwatch = Stopwatch.StartNew();
        var updates = new List<ChatResponseUpdate>();
        var finished = false;
        try
        {
            await using var enumerator = base.GetStreamingResponseAsync(list, options, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                // yield 不能放在有 catch 的 try 裡，所以只包「取下一段」這一步。
                bool moved;
                try
                {
                    moved = await enumerator.MoveNextAsync();
                }
                catch (Exception ex)
                {
                    finished = true;
                    stopwatch.Stop();
                    await RecordFailureAsync(capture, ex, stopwatch.Elapsed, cancellationToken);
                    throw;
                }

                if (moved == false)
                {
                    break;
                }

                updates.Add(enumerator.Current);
                yield return enumerator.Current;
            }

            finished = true;
            stopwatch.Stop();
            await RecordSuccessAsync(capture, updates.ToChatResponse(), stopwatch.Elapsed);
        }
        finally
        {
            // 呼叫端中途放棄列舉（DisposeAsync）時才會走到這裡還沒有結果。
            if (finished == false)
            {
                stopwatch.Stop();
                await RecordAbandonedAsync(capture, updates, stopwatch.Elapsed, cancellationToken);
            }

            await AiCallCapture.RecordSafelyAsync(aiCallLogRecorder, capture, stopwatch.Elapsed, logger);
        }
    }

    /// <summary>額度檢查（擋下即拋例外）並建立已送出的 capture。</summary>
    private async Task<AiCallCapture> BeginAsync(
        IList<ChatMessage> messages,
        ChatOptions? options,
        CancellationToken cancellationToken)
    {
        var caller = currentUserService.CurrentUser;
        var callerId = caller.Id == 0 ? (int?)null : caller.Id;
        var reached = await quotaService.CheckAsync(callerId, cancellationToken);
        if (reached is not null)
        {
            logger.LogWarning(
                "AI chat completion blocked by usage quota. Operation={Operation}, Scope={Scope}, Period={Period}",
                operation, reached.Scope, reached.Period);
            await auditLogService.WriteAsync(
                AuditActions.Ai.QuotaBlocked,
                success: false,
                actorUserId: callerId,
                actorAccount: string.IsNullOrWhiteSpace(caller.Account) ? null : caller.Account,
                targetType: "AiQuota",
                targetId: $"{reached.Scope}:{reached.Period}",
                detail: $"operation={operation}; limit={reached.LimitTwd}; used={AiQuotaService.Money(reached.UsedTwd)}");
            throw new AiQuotaExceededException(AiQuotaService.BlockedMessage(reached));
        }

        // ⚠️ ChatClientAgent 的 instructions 走 ChatOptions.Instructions 而不是 system 訊息，要一起記才看得到完整提示詞。
        var requestBody = Snapshot(
            new { instructions = options?.Instructions, messages, tools = options?.Tools?.Select(t => t.Name) },
            "RequestBody");

        var capture = new AiCallCapture(
            operation,
            settings.GetProvider().ToString(),
            settings.Model,
            caller.Account,
            caller.Id,
            AiChatEndpoint.CreateBaseUri(settings),
            requestBody,
            relatedInfo: null,
            conversationId);
        capture.MarkSent();
        return capture;
    }

    /// <summary>記錄成功的呼叫。⚠️ 自身不外拋：模型已經回應成功，不可因為記錄失敗而讓呼叫端拿到例外。</summary>
    private async Task RecordSuccessAsync(AiCallCapture capture, ChatResponse response, TimeSpan elapsed)
    {
        try
        {
            CaptureResponse(capture, response);

            logger.LogInformation(
                "AI agent chat call completed. Operation={Operation}, ConversationId={ConversationId}, "
                + "UsageInput={UsageInput}, UsageOutput={UsageOutput}, ElapsedMs={ElapsedMs}",
                operation,
                conversationId,
                response.Usage?.InputTokenCount,
                response.Usage?.OutputTokenCount,
                (long)elapsed.TotalMilliseconds);

            // 慢 AI 呼叫（LOG-21）。
            var slowThreshold = slowOptions.CurrentValue.AiCallMs;
            if (SlowOperationSettings.IsSlow(elapsed, slowThreshold))
            {
                logger.LogWarning(
                    "Slow AI chat completion. Operation={Operation}, ElapsedMilliseconds={ElapsedMilliseconds}, ThresholdMilliseconds={ThresholdMilliseconds}",
                    operation, (long)elapsed.TotalMilliseconds, slowThreshold);
            }

            await RecordUsageAsync(
                capture,
                response.ModelId,
                response.Usage,
                response.Usage is null ? null : Snapshot(response.Usage, "RawUsageJson"),
                elapsed,
                AiAnalysisFailureReason.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to record AI agent chat call. Operation={Operation}, ConversationId={ConversationId}",
                operation, conversationId);
        }
    }

    /// <summary>
    /// 串流被呼叫端中途放棄（<c>DisposeAsync</c>）。取消 → 與 <see cref="RecordFailureAsync"/> 一致，不記用量；
    /// 沒取消 → 上游已在計費，以已收到的片段記一筆 <see cref="AiAnalysisFailureReason.Abandoned"/>。
    /// ⚠️ 在 finally 裡執行，自身不外拋。
    /// </summary>
    private async Task RecordAbandonedAsync(
        AiCallCapture capture,
        List<ChatResponseUpdate> updates,
        TimeSpan elapsed,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            capture.SetOutcome(false, AiAnalysisFailureReason.Canceled.ToString());
            logger.LogInformation(
                "AI chat completion canceled by the caller. Operation={Operation}, ElapsedMs={ElapsedMs}",
                operation, (long)elapsed.TotalMilliseconds);
            return;
        }

        try
        {
            var partial = updates.ToChatResponse();
            CaptureResponse(capture, partial);

            logger.LogInformation(
                "AI agent chat stream abandoned by the caller. Operation={Operation}, ConversationId={ConversationId}, "
                + "UpdateCount={UpdateCount}, ElapsedMs={ElapsedMs}",
                operation, conversationId, updates.Count, (long)elapsed.TotalMilliseconds);

            await RecordUsageAsync(
                capture,
                partial.ModelId,
                partial.Usage,
                partial.Usage is null ? null : Snapshot(partial.Usage, "RawUsageJson"),
                elapsed,
                AiAnalysisFailureReason.Abandoned);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to record AI agent chat call. Operation={Operation}, ConversationId={ConversationId}",
                operation, conversationId);
        }
    }

    /// <summary>把模型回應（完整或部分）寫進 capture。串流能列舉到片段，代表上游已回 200。</summary>
    private void CaptureResponse(AiCallCapture capture, ChatResponse response)
    {
        capture.HttpStatus = 200;
        capture.ResponseText = response.Text;
        capture.FinishReason = response.FinishReason?.Value;
        capture.ResponseModel = response.ModelId;
        capture.ResponseBody = Snapshot(response.Messages, "ResponseBody");
    }

    /// <summary>
    /// 序列化記錄用的快照。失敗（例如工具帶回未登錄的 <see cref="AIContent"/> 子類）改存
    /// <c>{"snapshotError":"&lt;ExceptionType&gt;"}</c> 並記警告 —— 記錄失敗不可影響模型呼叫本身。
    /// </summary>
    private string Snapshot<T>(T value, string part)
    {
        try
        {
            return JsonSerializer.Serialize(value, AIJsonUtilities.DefaultOptions);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to serialize AI agent chat snapshot. Operation={Operation}, Part={Part}",
                operation, part);
            return JsonSerializer.Serialize(new { snapshotError = ex.GetType().Name });
        }
    }

    private async Task RecordFailureAsync(
        AiCallCapture capture,
        Exception exception,
        TimeSpan elapsed,
        CancellationToken cancellationToken)
    {
        switch (exception)
        {
            case OperationCanceledException when cancellationToken.IsCancellationRequested:
                // 呼叫端主動放棄：不是故障。Token 用量不記，AI 對話紀錄照記。
                capture.SetOutcome(false, AiAnalysisFailureReason.Canceled.ToString());
                logger.LogInformation(
                    "AI chat completion canceled by the caller. Operation={Operation}, ElapsedMs={ElapsedMs}",
                    operation, (long)elapsed.TotalMilliseconds);
                break;

            case TaskCanceledException:
                // HttpClient 逾時在 .NET 上表現為 TaskCanceledException。
                capture.ExceptionType = exception.GetType().Name;
                logger.LogError(
                    exception,
                    "AI chat completion timed out. Operation={Operation}, TimeoutSeconds={TimeoutSeconds}",
                    operation, settings.TimeoutSeconds);
                await TryRecordFailureUsageAsync(capture, elapsed, AiAnalysisFailureReason.Timeout);
                break;

            case ClientResultException clientException:
                capture.HttpStatus = clientException.Status;
                capture.ExceptionType = exception.GetType().Name;
                var reason = clientException.Status switch
                {
                    401 or 403 => AiAnalysisFailureReason.Unauthorized,
                    429 => AiAnalysisFailureReason.RateLimited,
                    _ => AiAnalysisFailureReason.UpstreamError,
                };
                logger.LogError(
                    exception,
                    "AI chat completion upstream failure. Operation={Operation}, StatusCode={StatusCode}",
                    operation, clientException.Status);
                // 上游可能已經計費，至少留下次數。
                await TryRecordFailureUsageAsync(capture, elapsed, reason);
                break;

            default:
                capture.ExceptionType = exception.GetType().Name;
                capture.SetOutcome(false, AiAnalysisFailureReason.Unexpected.ToString());
                logger.LogError(exception, "AI chat completion failed unexpectedly. Operation={Operation}", operation);
                break;
        }
    }

    /// <summary>失敗路徑記用量；記錄失敗只記 Warning，不可蓋掉原本的上游例外。</summary>
    private async Task TryRecordFailureUsageAsync(AiCallCapture capture, TimeSpan elapsed, AiAnalysisFailureReason reason)
    {
        try
        {
            await RecordUsageAsync(capture, null, null, null, elapsed, reason);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to record AI agent chat call. Operation={Operation}, ConversationId={ConversationId}",
                operation, conversationId);
        }
    }

    /// <summary>記 Token 用量並把結果寫進 capture，兩邊以同一個 CallId 關聯。</summary>
    private async Task RecordUsageAsync(
        AiCallCapture capture,
        string? modelName,
        UsageDetails? usage,
        string? rawUsageJson,
        TimeSpan elapsed,
        AiAnalysisFailureReason reason)
    {
        var success = reason == AiAnalysisFailureReason.None;
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
            InputCount = (int?)usage?.InputTokenCount,
            OutputCount = (int?)usage?.OutputTokenCount,
            TotalCount = (int?)usage?.TotalTokenCount,
            CachedInputCount = (int?)usage?.CachedInputTokenCount,
            ReasoningCount = (int?)usage?.ReasoningTokenCount,
            ElapsedMilliseconds = (long)elapsed.TotalMilliseconds,
            Success = success,
            FailureReason = success ? null : reason.ToString(),
            RawUsageJson = rawUsageJson,
            CallId = capture.CallId,
        });
    }
}

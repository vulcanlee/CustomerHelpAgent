using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Shop;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Agents;
using CustomerHelpAgent.Share.Shop;
using CustomerHelpAgent.Web.Ai;
using CustomerHelpAgent.Web.Configuration;
// MAF 1.25 也有一個 Microsoft.Agents.AI.AgentSessionStore（伺服器端會話存放抽象），這裡用的是本專案的資料服務。
using AgentSessionStore = CustomerHelpAgent.Business.Services.DataAccess.AgentSessionStore;

namespace CustomerHelpAgent.Web.Agents.AfterSales;

/// <summary>
/// 售後 Agent 的執行底座（Scoped，1.0.4 起）。每次呼叫都重新組一個 Agent，會話由 <see cref="AgentSessionStore"/> 的
/// <c>StateJson</c> 還原（MAF <c>DeserializeSessionAsync</c>），結束時再存回。
///
/// 組裝：<c>AgentChatClientFactory.Create</c>（含 <see cref="AuditingChatClient"/>）→ <see cref="TracingChatClient"/>
/// → <c>UseFunctionInvocation</c>（步數上限）→ <see cref="ChatClientAgent"/>（6 個工具）→ <see cref="AfterSalesAgentGuard"/>（函式中介層）。
///
/// 確認關卡（實測 MAF 1.25）：模型提出 <c>create_after_sales_request</c> 時工具不會執行，回應裡是 <see cref="ToolApprovalRequestContent"/>；
/// Runner 以程式重新查詢組成 <see cref="ConfirmationCard"/> 並寫入 <c>AgentConfirmation</c>。顧客確認時以同一個（還原的）會話送回
/// <c>CreateResponse(true)</c>；框架的 ApprovalResponseBinding 會把回應綁回當初記錄在會話裡的那個呼叫（參數以框架記錄的為準），
/// 因此重建核准請求只需要 <c>RequestId</c>。顧客改打字而不按確認時，框架自動拒絕未回覆的請求，工具不執行。
///
/// 停止原因判定順序：Guard 設定者 → 顯示確認卡 → 確認請求屢遭程式拒絕 → 步數用完（<see cref="TracingChatClient.ForcedFinalCall"/>）
/// → 最後一段回覆含問號（？或 ?）＝等待回覆 → 已回答。例外：<see cref="AiQuotaExceededException"/>＝額度、呼叫端取消＝取消、其他＝模型錯誤。
/// </summary>
public sealed class AfterSalesAgentRunner
{
    /// <summary>上傳照片後續跑時代顧客送出的訊息。</summary>
    public const string PhotosUploadedMessageFormat = "（已上傳 {0} 張照片）";

    private const string RecheckFailedReason = "程式重核失敗";
    private const string CustomerCanceledReason = "顧客取消";
    private const string StaleConfirmationReply = "這張確認卡已經處理過或已失效，沒有重複送出申請。";
    private const string CreatedButReplyFailedFormat = "申請已建立（{0}），但 AI 回覆暫時失敗，請到「我的申請」查看進度。";
    private const string ConsecutiveFailuresExplanation = "連續幾次查詢都沒有成功，先停在這裡，避免給您不確定的答覆。請補充更多資訊，或洽客服人員協助。";

    /// <summary>同一輪內確認請求被程式拒絕（封鎖、重核失敗）後，最多再讓模型續跑幾次。</summary>
    private const int MaxRejectedApprovals = 3;

    private static readonly JsonSerializerOptions RawJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly AgentChatClientFactory chatClientFactory;
    private readonly IAiSystemPromptProvider promptProvider;
    private readonly AgentSessionStore sessionStore;
    private readonly AgentTraceService traceService;
    private readonly ShopOrderService shopOrderService;
    private readonly AfterSalesPolicyService policyService;
    private readonly AfterSalesRequestService requestService;
    private readonly IPermissionChecker permissionChecker;
    private readonly AgentPhotoInbox photoInbox;
    private readonly CurrentUserService currentUserService;
    private readonly IOptionsMonitor<AfterSalesAgentSettings> settings;
    private readonly TimeProvider timeProvider;
    private readonly ILoggerFactory loggerFactory;
    private readonly ILogger<AfterSalesAgentRunner> logger;

    public AfterSalesAgentRunner(
        AgentChatClientFactory chatClientFactory,
        IAiSystemPromptProvider promptProvider,
        AgentSessionStore sessionStore,
        AgentTraceService traceService,
        ShopOrderService shopOrderService,
        AfterSalesPolicyService policyService,
        AfterSalesRequestService requestService,
        IPermissionChecker permissionChecker,
        AgentPhotoInbox photoInbox,
        CurrentUserService currentUserService,
        IOptionsMonitor<AfterSalesAgentSettings> settings,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory)
    {
        this.chatClientFactory = chatClientFactory;
        this.promptProvider = promptProvider;
        this.sessionStore = sessionStore;
        this.traceService = traceService;
        this.shopOrderService = shopOrderService;
        this.policyService = policyService;
        this.requestService = requestService;
        this.permissionChecker = permissionChecker;
        this.photoInbox = photoInbox;
        this.currentUserService = currentUserService;
        this.settings = settings;
        this.timeProvider = timeProvider;
        this.loggerFactory = loggerFactory;
        logger = loggerFactory.CreateLogger<AfterSalesAgentRunner>();
    }

    /// <summary>測試接縫：每輪建立工具後呼叫（例如設定 <see cref="AfterSalesAgentTools.BeforeServiceCallForTesting"/> 模擬逾時）。</summary>
    internal Action<AfterSalesAgentTools>? ConfigureToolsForTesting { get; set; }

    private DateTime Now => timeProvider.GetLocalNow().DateTime;

    /// <summary>取最新一個未關閉的會話；沒有就建立新的。</summary>
    public async Task<Guid> StartOrResumeAsync()
    {
        var userId = RequireCustomer();
        var latest = await sessionStore.GetLatestOpenAsync(userId);
        return latest?.SessionKey ?? await sessionStore.CreateAsync(userId);
    }

    /// <summary>關閉目前的會話（清掉暫存照片、作廢未決的確認卡）並建立新的。</summary>
    public async Task<Guid> StartNewAsync()
    {
        var userId = RequireCustomer();
        var latest = await sessionStore.GetLatestOpenAsync(userId);
        if (latest is not null)
        {
            if (await sessionStore.GetPendingConfirmationAsync(latest.SessionKey, userId) is { } pending)
            {
                await sessionStore.MarkConfirmationAsync(pending.Id, approved: false, requestId: null);
            }

            await sessionStore.CloseAsync(latest.SessionKey, userId);
            photoInbox.Clear(latest.SessionKey);
        }

        return await sessionStore.CreateAsync(userId);
    }

    /// <summary>顧客送出一句話。若有未決的確認卡，它就此作廢（框架也會自動拒絕那個核准請求，工具不執行）。</summary>
    public async Task<AgentTurnResult> SendAsync(
        Guid sessionKey,
        string userText,
        Func<AgentTraceEvent, Task> onEvent,
        Func<string, Task> onTextDelta,
        CancellationToken ct)
    {
        var userId = RequireCustomer();
        var record = await RequireSessionAsync(sessionKey, userId);
        var pending = await sessionStore.GetPendingConfirmationAsync(sessionKey, userId);
        var turn = await BeginTurnAsync(record, userId, confirmationId: null, onEvent, onTextDelta);

        await turn.EmitAsync(AgentStepKind.UserMessage, AgentDataSource.Conversation, userText, null);
        if (pending is not null)
        {
            await sessionStore.MarkConfirmationAsync(pending.Id, approved: false, requestId: null);
            await turn.EmitAsync(AgentStepKind.ConfirmationDecided, AgentDataSource.Conversation,
                "你改以文字回覆、沒有按下確認，這張確認卡作廢，不會建立申請。", null);
        }

        return await RunTurnAsync(turn, new ChatMessage(ChatRole.User, userText), ct);
    }

    /// <summary>顧客在確認卡上按下「確認送出」或「取消」。卡片已處理過或已作廢時不做任何事（不會重複送出）。</summary>
    public async Task<AgentTurnResult> ConfirmAsync(
        Guid sessionKey,
        int confirmationId,
        bool approve,
        Func<AgentTraceEvent, Task> onEvent,
        Func<string, Task> onTextDelta,
        CancellationToken ct)
    {
        var userId = RequireCustomer();
        var record = await RequireSessionAsync(sessionKey, userId);
        var pending = await sessionStore.GetPendingConfirmationAsync(sessionKey, userId);
        // 先以條件式更新搶下決定：之後就不再是「未決」，重複按下或另一個分頁（即使同時讀到未決）都不會再送一次。
        if (pending is null || pending.Id != confirmationId
            || !await sessionStore.TryClaimConfirmationAsync(pending.Id, userId, approve))
        {
            logger.LogInformation(
                "Agent confirmation ignored because it is no longer pending. SessionKey={SessionKey}, ConfirmationId={ConfirmationId}",
                sessionKey, confirmationId);
            return new AgentTurnResult(StaleConfirmationReply, [], null, record.LastStopReason ?? AgentStopReason.Answered, null, null);
        }

        var turn = await BeginTurnAsync(record, userId, approve ? pending.Id : null, onEvent, onTextDelta);
        await turn.EmitAsync(AgentStepKind.ConfirmationDecided, AgentDataSource.Conversation,
            approve ? "你按下「確認送出」，程式會再核對一次資格後才建立申請。" : "你按下「取消」，不建立申請。",
            JsonSerializer.Serialize(new { confirmationId = pending.Id, approved = approve }, RawJsonOptions));

        // ToolCall 只是佔位：框架會把回應綁回會話裡記錄的原始呼叫，只認 RequestId。
        var request = new ToolApprovalRequestContent(
            pending.ApprovalRequestId,
            new FunctionCallContent(pending.ApprovalRequestId, pending.ToolName, ParseArguments(pending.ArgumentsJson)));
        var response = approve ? request.CreateResponse(true) : request.CreateResponse(false, CustomerCanceledReason);
        return await RunTurnAsync(turn, new ChatMessage(ChatRole.User, [response]), ct);
    }

    /// <summary>顧客上傳照片後續跑：以使用者訊息「（已上傳 N 張照片）」送出。</summary>
    public Task<AgentTurnResult> NotifyPhotosUploadedAsync(
        Guid sessionKey,
        Func<AgentTraceEvent, Task> onEvent,
        Func<string, Task> onTextDelta,
        CancellationToken ct)
        => SendAsync(
            sessionKey,
            string.Format(CultureInfo.InvariantCulture, PhotosUploadedMessageFormat, photoInbox.Count(sessionKey)),
            onEvent,
            onTextDelta,
            ct);

    /// <summary>
    /// 還原頁面用：會話裡的對話文字（顧客 <see cref="AgentStepKind.UserMessage"/>、Agent <see cref="AgentStepKind.ModelReply"/>），
    /// 以及最後一張未決的確認卡（<see cref="AgentStepKind.ConfirmationShown"/>，<c>RawJson</c> 為 <see cref="ConfirmationCard.ToJson"/>）。
    /// 工具呼叫與結果不在其中；因額度或模型錯誤而失敗的那一輪不會留在會話裡。
    /// </summary>
    public async Task<IReadOnlyList<AgentTraceEvent>> GetHistoryAsync(Guid sessionKey)
    {
        var userId = RequireCustomer();
        var record = await sessionStore.GetAsync(sessionKey, userId);
        if (record is null)
        {
            return [];
        }

        var events = new List<AgentTraceEvent>();
        if (!string.IsNullOrEmpty(record.StateJson))
        {
            // 只為了讀出會話內容：DeserializeSessionAsync 是 Agent 的方法，這個 Agent 不會呼叫模型。
            var reader = new ChatClientAgent(HistoryOnlyChatClient.Instance);
            var session = await reader.DeserializeSessionAsync(JsonSerializer.Deserialize<JsonElement>(record.StateJson));
            if (session.TryGetInMemoryChatHistory(out var messages))
            {
                foreach (var message in messages)
                {
                    var text = string.Concat(message.Contents.OfType<TextContent>().Select(x => x.Text)).Trim();
                    if (text.Length == 0 || (message.Role != ChatRole.User && message.Role != ChatRole.Assistant))
                    {
                        continue;
                    }

                    events.Add(new AgentTraceEvent(
                        message.Role == ChatRole.User ? AgentStepKind.UserMessage : AgentStepKind.ModelReply,
                        AgentDataSource.Conversation,
                        text,
                        null,
                        message.CreatedAt?.LocalDateTime ?? record.UpdatedAt));
                }
            }
        }

        if (await sessionStore.GetPendingConfirmationAsync(sessionKey, userId) is { } pending)
        {
            var card = ConfirmationCard.FromJson(pending.CardJson) with { ConfirmationId = pending.Id };
            events.Add(new AgentTraceEvent(AgentStepKind.ConfirmationShown, AgentDataSource.Program, card.Title, card.ToJson(), pending.CreatedAt));
        }

        return events;
    }

    private int RequireCustomer()
    {
        var userId = currentUserService.CurrentUser.Id;
        if (userId == 0)
        {
            throw new InvalidOperationException("尚未登入，無法使用線上客服。");
        }

        return userId;
    }

    private async Task<AgentSessionRecord> RequireSessionAsync(Guid sessionKey, int userId)
    {
        var record = await sessionStore.GetAsync(sessionKey, userId);
        if (record is null || record.IsClosed)
        {
            logger.LogWarning("Agent session not found or closed for the signed-in customer. SessionKey={SessionKey}", sessionKey);
            throw new InvalidOperationException("找不到這段對話，或對話已結束，請重新整理頁面。");
        }

        return record;
    }

    private async Task<Turn> BeginTurnAsync(
        AgentSessionRecord record, int userId, int? confirmationId, Func<AgentTraceEvent, Task> onEvent, Func<string, Task> onTextDelta)
    {
        var runId = await traceService.StartRunAsync(record.SessionKey, userId);
        return new Turn(record, userId, confirmationId, runId, traceService, onEvent, onTextDelta, timeProvider, logger);
    }

    /// <summary>組 Agent、還原會話、執行（含確認請求被程式拒絕後的續跑），判定停止原因並存回會話。</summary>
    private async Task<AgentTurnResult> RunTurnAsync(Turn turn, ChatMessage input, CancellationToken ct)
    {
        var options = settings.CurrentValue;
        var sessionKey = turn.Record.SessionKey;
        var guard = new AfterSalesAgentGuard(
            options.MaxConsecutiveToolFailures, turn.EmitAsync, timeProvider, loggerFactory.CreateLogger<AfterSalesAgentGuard>());
        TracingChatClient? tracing = null;
        AIAgent? agent = null;
        AgentSession? session = null;
        var replies = new List<string>();
        ConfirmationCard? card = null;
        var approvalsExhausted = false;
        AgentStopReason reason;
        string? explanation = null;
        var programReply = false;
        var completed = false;

        try
        {
            (agent, tracing) = await BuildAgentAsync(turn, guard, options, ct);
            session = string.IsNullOrEmpty(turn.Record.StateJson)
                ? await agent.CreateSessionAsync(ct)
                : await agent.DeserializeSessionAsync(JsonSerializer.Deserialize<JsonElement>(turn.Record.StateJson), cancellationToken: ct);

            var rejected = 0;
            ChatMessage? next = input;
            while (next is not null)
            {
                var updates = new List<AgentResponseUpdate>();
                await foreach (var update in agent.RunStreamingAsync(next, session, cancellationToken: ct))
                {
                    updates.Add(update);
                    if (!string.IsNullOrEmpty(update.Text))
                    {
                        await turn.DeltaAsync(update.Text);
                    }
                }

                var response = updates.ToAgentResponse();
                if (!string.IsNullOrWhiteSpace(response.Text))
                {
                    replies.Add(response.Text.Trim());
                }

                next = null;
                var approval = response.Messages.SelectMany(m => m.Contents).OfType<ToolApprovalRequestContent>().FirstOrDefault();
                if (approval is null)
                {
                    break;
                }

                var (shown, rejection) = await PrepareConfirmationAsync(turn, guard, approval);
                if (shown is not null)
                {
                    card = shown;
                    break;
                }

                if (++rejected > MaxRejectedApprovals)
                {
                    // 不再續跑；留在會話裡的請求下一輪會被框架自動拒絕。
                    approvalsExhausted = true;
                    break;
                }

                next = new ChatMessage(ChatRole.User, [approval.CreateResponse(false, rejection)]);
            }

            completed = true;
            reason = guard.StopReason
                ?? (card is not null ? AgentStopReason.AwaitingConfirmation
                : approvalsExhausted ? AgentStopReason.ConsecutiveToolFailures
                : tracing.ForcedFinalCall ? AgentStopReason.StepLimitReached
                : AsksQuestion(replies) ? AgentStopReason.AwaitingUserReply
                : AgentStopReason.Answered);
        }
        catch (AiQuotaExceededException ex)
        {
            reason = AgentStopReason.QuotaExceeded;
            explanation = ex.Message;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            reason = AgentStopReason.Canceled;
            explanation = "已取消這次的處理。";
        }
        catch (Exception ex)
        {
            reason = guard.StopReason == AgentStopReason.ConsecutiveToolFailures ? AgentStopReason.ConsecutiveToolFailures : AgentStopReason.ModelError;
            explanation = reason == AgentStopReason.ConsecutiveToolFailures
                ? ConsecutiveFailuresExplanation
                : "AI 服務暫時發生問題，這次沒有完成。請稍後再試，或洽客服人員協助。";
            logger.LogError(ex, "After-sales agent turn failed. SessionKey={SessionKey}, StopReason={StopReason}", sessionKey, reason);
        }

        var reply = string.Join("\n\n", replies);
        if (completed)
        {
            (reply, explanation, programReply) = Finalize(reason, guard.FixedReply, reply, options);
        }
        else
        {
            reply = explanation!;
            programReply = true;
            if (guard.CreatedRequestNo is { } createdNo)
            {
                // 確認後框架先執行寫入工具、才再呼叫模型：申請已建立而模型那一步失敗時，回覆一定要帶申請編號，
                // 不可只說「這次沒有完成」（顧客會以為沒送出而重送）。
                reply = string.Format(CultureInfo.InvariantCulture, CreatedButReplyFailedFormat, createdNo)
                    + (reason == AgentStopReason.QuotaExceeded ? " " + explanation : string.Empty);
            }
        }

        // 失敗的那一輪框架不會存入歷史；但申請已建立時要把程式回覆寫回會話，下一輪模型才知道已經送出（以及編號）。
        var appendReply = completed
            ? (programReply && card is null ? reply : null)
            : (guard.CreatedRequestNo is not null ? reply : null);
        if (agent is not null && session is not null)
        {
            await SaveSessionAsync(turn, agent, session, reason, appendReply);
        }

        await turn.EmitAsync(AgentStepKind.ModelReply, programReply ? AgentDataSource.Program : AgentDataSource.Conversation, reply, null);
        await turn.EmitAsync(AgentStepKind.Stop, AgentDataSource.Program,
            explanation is null ? StopText(reason, options) : $"{StopText(reason, options)} {explanation}",
            JsonSerializer.Serialize(new { stopReason = reason.ToString() }, RawJsonOptions));
        await EndRunAsync(turn, reason, tracing?.ModelCallCount ?? 0, guard.ToolCallCount);

        logger.LogInformation(
            "After-sales agent turn finished. SessionKey={SessionKey}, StopReason={StopReason}, ModelCalls={ModelCalls}, ToolCalls={ToolCalls}",
            sessionKey, reason, tracing?.ModelCallCount ?? 0, guard.ToolCallCount);
        return new AgentTurnResult(reply, guard.PolicyCards.ToList(), card, reason, explanation, guard.CreatedRequestNo);
    }

    /// <summary>依停止原因決定顧客看到的回覆與說明；回傳的 programReply 表示回覆由程式產生（不是模型文字）。</summary>
    private static (string Reply, string? Explanation, bool ProgramReply) Finalize(
        AgentStopReason reason, string? fixedReply, string modelReply, AfterSalesAgentSettings options)
    {
        if (fixedReply is not null)
        {
            // 逾時、權限、服務不可用：程式的固定說明取代模型輸出。
            return (fixedReply, fixedReply, true);
        }

        string? explanation = reason switch
        {
            AgentStopReason.StepLimitReached
                => $"這次需要的步驟超過上限（{options.MaxSteps} 步），先停在這裡，避免給您不確定的答覆。請補充說明，或洽客服人員協助。",
            AgentStopReason.ConsecutiveToolFailures => ConsecutiveFailuresExplanation,
            AgentStopReason.PolicyConflict => "這類問題同時有多條生效的規則，無法自動判定，建議轉人工客服。",
            _ => null,
        };

        if (!string.IsNullOrWhiteSpace(modelReply))
        {
            return (modelReply, explanation, false);
        }

        var fallback = explanation ?? reason switch
        {
            AgentStopReason.AwaitingPhotoUpload
                => $"請在下方上傳照片（最多 {AfterSalesRequestService.MaxFileCount} 張），上傳完成後我會繼續為您處理。",
            AgentStopReason.AwaitingConfirmation => "請確認下方的申請內容，確認無誤再按「確認送出」。",
            _ => "目前沒有可以回覆的內容，請再說明一次您的需求。",
        };
        return (fallback, explanation, true);
    }

    /// <summary>「等待使用者回覆」的簡單判定：最後一段回覆含全形或半形問號（呼叫工具前的過場話不算）。</summary>
    private static bool AsksQuestion(IReadOnlyList<string> replies)
        => replies.Count > 0 && (replies[^1].Contains('？') || replies[^1].Contains('?'));

    private static string StopText(AgentStopReason reason, AfterSalesAgentSettings options)
        => AgentStopLabels.Text(reason, options.MaxSteps, options.MaxConsecutiveToolFailures);

    private async Task<(AIAgent Agent, TracingChatClient Tracing)> BuildAgentAsync(
        Turn turn, AfterSalesAgentGuard guard, AfterSalesAgentSettings options, CancellationToken ct)
    {
        var sessionKey = turn.Record.SessionKey;
        var toolContext = new AfterSalesToolContext
        {
            CustomerUserId = turn.UserId,
            SessionKey = sessionKey,
            Emit = turn.EmitAsync,
            ToolTimeout = TimeSpan.FromSeconds(options.ToolTimeoutSeconds),
            OnRequestCreated = turn.ConfirmationId is { } confirmationId
                ? requestId => sessionStore.MarkConfirmationAsync(confirmationId, approved: true, requestId)
                : null,
        };
        var tools = new AfterSalesAgentTools(
            shopOrderService,
            policyService,
            requestService,
            permissionChecker,
            currentUserService,
            photoInbox,
            timeProvider,
            loggerFactory.CreateLogger<AfterSalesAgentTools>(),
            toolContext);
        ConfigureToolsForTesting?.Invoke(tools);

        var tracing = new TracingChatClient(
            chatClientFactory.Create(TokenUsageOperations.AfterSalesAgent, sessionKey, options.GetApiStyle(), options.ReasoningEffort),
            turn.EmitAsync,
            timeProvider,
            logger);
        var chatClient = tracing.AsBuilder()
            .UseFunctionInvocation(configure: f =>
            {
                f.MaximumIterationsPerRequest = options.MaxSteps;
                f.MaximumConsecutiveErrorsPerRequest = options.MaxConsecutiveToolFailures;
            })
            .Build();
        var instructions = await promptProvider.GetAsync(PromptTemplateKeys.AfterSalesAgent, ct);
        var agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            ChatOptions = new ChatOptions
            {
                Instructions = instructions,
                Tools = [.. tools.CreateFunctions()],
                // 預設不送：推論模型不接受 temperature（AfterSalesAgentSettings.Temperature）。
                Temperature = (float?)options.Temperature,
                // 一次只允許一個工具呼叫：寫入工具需要確認時，框架會把同一回應裡的其他呼叫也變成核准請求；
                // 終止（Terminate）也只會停在目前這一個呼叫，不會留下沒有結果的呼叫。
                AllowMultipleToolCalls = false,
            },
        }, loggerFactory);

        return (agent.AsBuilder().Use(guard.InvokeAsync).Build(), tracing);
    }

    /// <summary>
    /// 模型提出寫入（核准請求）：先看 Guard 是否已封鎖寫入，再以程式重查組確認卡。
    /// 回傳顯示的卡片，或拒絕原因（交給模型續跑）。
    /// </summary>
    private async Task<(ConfirmationCard? Card, string Rejection)> PrepareConfirmationAsync(
        Turn turn, AfterSalesAgentGuard guard, ToolApprovalRequestContent approval)
    {
        if (approval.ToolCall is not FunctionCallContent call || call.Name != AfterSalesAgentTools.CreateAfterSalesRequestName)
        {
            logger.LogWarning("Unexpected agent approval request rejected. RequestId={RequestId}", approval.RequestId);
            return (null, RecheckFailedReason);
        }

        if (guard.WriteBlocked)
        {
            await guard.EmitBlockedAsync(ToolOutcome.Fail(ToolOutcomeStatus.Blocked, AfterSalesAgentGuard.BlockedMessage));
            logger.LogInformation("Agent write request blocked before showing the confirmation card. RequestId={RequestId}", approval.RequestId);
            return (null, AfterSalesAgentGuard.BlockedMessage);
        }

        var (draft, failure) = await BuildCardAsync(turn, approval.RequestId, call.Arguments);
        if (draft is null)
        {
            await turn.EmitAsync(AgentStepKind.ProgramCheck, AgentDataSource.Program,
                $"程式重新核對建立申請的內容：{failure}，不顯示確認卡。", Serialize(new { tool = call.Name, arguments = call.Arguments }));
            logger.LogInformation("Agent write request rejected by the confirmation re-check. RequestId={RequestId}", approval.RequestId);
            return (null, $"{RecheckFailedReason}：{failure}");
        }

        var confirmation = await sessionStore.AddConfirmationAsync(
            turn.Record.SessionKey, turn.UserId, approval.RequestId, call.Name, Serialize(call.Arguments) ?? "{}", draft.ToJson());
        var card = draft with { ConfirmationId = confirmation.Id };
        await turn.EmitAsync(AgentStepKind.ConfirmationShown, AgentDataSource.Program,
            $"程式重新查詢訂單與規則後顯示確認卡：{card.ProductLine}，{card.FromSpec}{(card.ToSpec is null ? string.Empty : " → " + card.ToSpec)}（{card.IssueLabel}）。按下確認前不會建立申請。",
            card.ToJson());
        return (card, string.Empty);
    }

    /// <summary>依模型提出的參數以程式重新查出確認卡內容；不是本人、查無、無規則、缺目標規格或資格不符時回失敗原因。</summary>
    private async Task<(ConfirmationCard? Card, string Failure)> BuildCardAsync(
        Turn turn, string approvalRequestId, IDictionary<string, object?>? args)
    {
        if (!int.TryParse(AfterSalesAgentGuard.Arg(args, "orderItemId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var orderItemId))
        {
            return (null, "沒有指定訂單商品");
        }

        if (!AfterSalesAgentTools.TryParseIssueType(AfterSalesAgentGuard.Arg(args, "issueType"), out var type))
        {
            return (null, "無法辨識的問題類型");
        }

        var orders = await shopOrderService.GetMyOrdersAsync(turn.UserId);
        var order = orders.FirstOrDefault(o => o.Items.Any(i => i.OrderItemId == orderItemId));
        var item = order?.Items.First(i => i.OrderItemId == orderItemId);
        if (order is null || item is null)
        {
            return (null, "查無這個訂單商品（或不是本人的訂單）");
        }

        var targetSize = AfterSalesAgentGuard.Arg(args, "targetSize");
        var targetColor = AfterSalesAgentGuard.Arg(args, "targetColor");
        var toSpec = type switch
        {
            AfterSalesType.SizeExchange when targetSize is not null => Spec(item.Color, targetSize),
            AfterSalesType.ColorExchange when targetColor is not null => Spec(targetColor, item.Size),
            AfterSalesType.SizeExchange => null,
            AfterSalesType.ColorExchange => null,
            _ => string.Empty,
        };
        if (toSpec is null)
        {
            return (null, type == AfterSalesType.SizeExchange ? "換尺碼沒有指定目標尺碼" : "換顏色沒有指定目標顏色");
        }

        var now = Now;
        var lookup = await policyService.GetEffectiveAsync(type, item.ProductCategory, now);
        if (lookup.Status != PolicyLookupStatus.Found)
        {
            return (null, lookup.Status == PolicyLookupStatus.Conflict ? "規則衝突" : "查無適用規則");
        }

        var policy = lookup.Policies[0];
        var shippedAt = type == AfterSalesType.ShipmentUrge
            ? (await shopOrderService.GetMyOrderDetailAsync(turn.UserId, order.OrderId))?.Shipment?.ShippedAt
            : null;
        var isWorn = bool.TryParse(AfterSalesAgentGuard.Arg(args, "isWorn"), out var worn) ? worn : (bool?)null;
        var photoCount = photoInbox.Count(turn.Record.SessionKey);
        var eligibility = AfterSalesEligibility.Evaluate(policy, order.DeliveredAt, isWorn, photoCount > 0, now, shippedAt);
        if (!eligibility.IsEligible)
        {
            var reasons = eligibility.UnmetReasons.ToList();
            if (eligibility.MissingInfo.Count > 0)
            {
                reasons.Add("尚缺：" + string.Join("、", eligibility.MissingInfo));
            }

            return (null, "不符合申請條件（" + string.Join("；", reasons) + "）");
        }

        var days = AfterSalesEligibility.DaysSinceDelivery(order.DeliveredAt, now);
        var facts = new List<string>
        {
            days is { } d ? $"簽收第 {d} 天（程式計算）" : "尚未簽收",
            isWorn switch { true => "你表示商品已穿過", false => "你表示商品未穿過", null => "未確認是否穿過" },
        };
        if (AfterSalesAgentGuard.Arg(args, "description") is { } description)
        {
            facts.Add($"問題描述：{description}");
        }

        var issueLabel = AfterSalesLabels.TypeText(type);
        return (new ConfirmationCard(
            0,
            approvalRequestId,
            $"確認{issueLabel}申請",
            $"{item.ProductName}（訂單 {order.OrderNo}）",
            Spec(item.Color, item.Size),
            toSpec.Length == 0 ? null : toSpec,
            issueLabel,
            $"依據：{policy.Title}（第 {policy.Version} 版，{policy.Source}）",
            facts,
            photoCount), string.Empty);
    }

    /// <summary>「黑 / 40」顯示成「黑色 / 40」。</summary>
    private static string Spec(string color, string size)
        => $"{(color.EndsWith('色') ? color : color + "色")} / {size}";

    private async Task SaveSessionAsync(Turn turn, AIAgent agent, AgentSession session, AgentStopReason reason, string? appendReply)
    {
        try
        {
            // 程式產生的回覆（固定說明、等待上傳的提示…）也放進對話紀錄：還原頁面時看得到，下一輪模型也知道顧客看過什麼。
            // 等待確認時不加：核准請求之後若插入其他訊息，框架補上的工具結果就不會緊接在呼叫後面。
            if (appendReply is not null && session.TryGetInMemoryChatHistory(out var history))
            {
                history.Add(new ChatMessage(ChatRole.Assistant, appendReply) { CreatedAt = timeProvider.GetUtcNow() });
                session.SetInMemoryChatHistory(history);
            }

            var state = await agent.SerializeSessionAsync(session);
            await sessionStore.SaveStateAsync(turn.Record.SessionKey, turn.UserId, state.GetRawText(), reason);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save the after-sales agent session. SessionKey={SessionKey}", turn.Record.SessionKey);
        }
    }

    private async Task EndRunAsync(Turn turn, AgentStopReason reason, int modelCalls, int toolCalls)
    {
        try
        {
            await traceService.EndRunAsync(turn.RunId, reason, modelCalls, toolCalls);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to end the after-sales agent run trace. RunId={RunId}", turn.RunId);
        }
    }

    private static IDictionary<string, object?> ParseArguments(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(json) ?? [];
        }
        catch (JsonException)
        {
            return new Dictionary<string, object?>();
        }
    }

    private string? Serialize(object? value)
    {
        try
        {
            return JsonSerializer.Serialize(value, RawJsonOptions);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to serialize an agent confirmation snapshot.");
            return null;
        }
    }

    /// <summary>
    /// 一輪執行的事件出口：先交給畫面（<c>onEvent</c>），再寫入 <see cref="AgentTraceService"/>，依序 await（單一寫入者，Sequence 才正確）。
    /// 畫面回呼失敗（例如瀏覽器已斷線）只記 Warning，不中斷執行。
    /// </summary>
    private sealed class Turn(
        AgentSessionRecord record,
        int userId,
        int? confirmationId,
        int runId,
        AgentTraceService traceService,
        Func<AgentTraceEvent, Task> onEvent,
        Func<string, Task> onTextDelta,
        TimeProvider timeProvider,
        ILogger logger)
    {
        public AgentSessionRecord Record => record;

        public int UserId => userId;

        public int? ConfirmationId => confirmationId;

        public int RunId => runId;

        public Task EmitAsync(AgentStepKind kind, AgentDataSource source, string plainText, string? rawJson)
            => EmitAsync(new AgentTraceEvent(kind, source, plainText, rawJson, timeProvider.GetLocalNow().DateTime));

        public async Task EmitAsync(AgentTraceEvent traceEvent)
        {
            try
            {
                await onEvent(traceEvent);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Agent trace event callback failed. RunId={RunId}, Kind={Kind}", runId, traceEvent.Kind);
            }

            await traceService.AppendStepAsync(runId, traceEvent.Kind, traceEvent.Source, traceEvent.PlainText, traceEvent.RawJson);
        }

        public async Task DeltaAsync(string text)
        {
            try
            {
                await onTextDelta(text);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Agent text delta callback failed. RunId={RunId}", runId);
            }
        }
    }

    /// <summary><see cref="GetHistoryAsync"/> 讀會話用的 Agent 底層；永遠不會被呼叫。</summary>
    private sealed class HistoryOnlyChatClient : IChatClient
    {
        public static readonly HistoryOnlyChatClient Instance = new();

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}

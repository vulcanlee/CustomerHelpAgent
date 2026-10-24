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
using CustomerHelpAgent.Models.Agents;
using CustomerHelpAgent.Models.Shop;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Agents;
using CustomerHelpAgent.Share.Demo;
using CustomerHelpAgent.Share.Shop;
using CustomerHelpAgent.Web.Ai;
using CustomerHelpAgent.Web.Configuration;
// MAF 1.25 也有一個 Microsoft.Agents.AI.AgentSessionStore（伺服器端會話存放抽象），這裡用的是本專案的資料服務。
using AgentSessionStore = CustomerHelpAgent.Business.Services.DataAccess.AgentSessionStore;

namespace CustomerHelpAgent.Web.Agents.AfterSales;

/// <summary>
/// 售後 Agent 的執行底座（Scoped，1.0.4 起）。每次呼叫都重新組一個 Agent，會話由 <see cref="AgentSessionStore"/> 的
/// <c>StateJson</c> 還原（MAF <c>DeserializeSessionAsync</c>），結束時再存回（1.0.5 起以讀入時的 <c>StateVersion</c> 做樂觀版本檢查，
/// 另一個分頁已先存回時不覆寫、拋 <see cref="AgentSessionConflictException"/>）。每輪最後一步發 <see cref="AgentStepKind.TurnResult"/>，
/// 重新整理頁面時以它重建對話（<see cref="GetHistoryAsync"/>），不讀會話原文。
///
/// 組裝：<c>AgentChatClientFactory.Create</c>（含 <see cref="AuditingChatClient"/>）→［錄製中才有 <see cref="RecordingChatClient"/>］
/// → <see cref="TracingChatClient"/> → <c>UseFunctionInvocation</c>（步數上限）→ <see cref="ChatClientAgent"/>（6 個工具）
/// → <see cref="AfterSalesAgentGuard"/>（函式中介層）。回放中最底層改為 <see cref="ReplayChatClient"/>（見下）。
///
/// 錄製（1.0.5 起）：示範模式開啟且為即時模式時（每輪開始判定一次），送出／確認／取消／上傳照片先記一個
/// <see cref="AgentRecordedTurnKind"/> 動作（同一會話的第一次建立錄製），模型呼叫由 <see cref="RecordingChatClient"/> 記下；
/// 已作廢的確認卡不記。錄製失敗只記 Warning，不影響對話。
///
/// 回放（1.0.5 起）：示範模式開啟、為回放模式且指定了錄製時（每輪開始判定一次），<b>不呼叫</b> <c>AgentChatClientFactory.Create</c>
/// （不需金鑰、不查額度、不記 Token 用量與 AI 對話紀錄），改以 <see cref="ReplayChatClient"/> 依 <see cref="ReplayCursorStore"/> 的游標
/// 逐筆送出錄製的回應；追蹤、函式呼叫、Guard、工具照常（工具會真的建立申請，所以回放前要先重置示範資料）。
/// <list type="bullet">
/// <item>每個顧客動作先與錄製的下一個動作核對（<see cref="GetReplayScriptAsync"/> 給頁面帶入）：種類不同、句子不同（去頭尾空白後逐字比對）
/// 或確認／取消與錄製時相反，回程式固定說明、停止原因 <see cref="AgentStopReason.ReplayActionMismatch"/>（停止說明「回放中請按照錄製時的步驟操作。」），
/// <b>不</b>呼叫模型、不寫軌跡、不前進游標、不動確認卡——顧客改送正確的動作就能繼續。錄製的動作已播完回 <see cref="AgentStopReason.ReplayFinished"/>。</item>
/// <item>模型請求摘要不同（資料或故障開關和錄製時不一樣）＝ <see cref="ReplayDivergedException"/> → <see cref="AgentStopReason.ReplayDiverged"/>；
/// 錄製的模型回應用完＝ <see cref="ReplayExhaustedException"/> → <see cref="AgentStopReason.ReplayFinished"/>。兩者都以固定說明取代回覆。</item>
/// <item>開新會話（<see cref="StartNewAsync"/>）與一鍵重置示範資料都會清掉游標；同一會話兩個分頁同時送出會各自前進游標，之後就會回放中止。</item>
/// </list>
///
/// 確認關卡（實測 MAF 1.25）：模型提出 <c>create_after_sales_request</c> 時工具不會執行，回應裡是 <see cref="ToolApprovalRequestContent"/>；
/// Runner 以程式重新查詢組成 <see cref="ConfirmationCard"/> 並寫入 <c>AgentConfirmation</c>；這段重查走與工具相同的外框
/// （<see cref="AfterSalesAgentTools.RunConfirmationCheckAsync"/>：工具逾時、身分與權限），逾時／權限失效／服務不可用時比照工具以
/// Guard 的固定說明與停止原因收尾，不出卡、不寫入。顧客確認時以同一個（還原的）會話送回
/// 核准回應：一律從還原的會話歷史找出 <c>RequestId</c> 相同的原始請求，以它的 <c>CreateResponse</c> 產生（回應帶原始呼叫的 CallId；
/// RequestId 是框架加上 <c>ficc_</c> 前綴的值，若拿它當 CallId，下一輪框架會因對不上而拋例外）。會話裡找不到原始請求時失敗關閉：
/// 作廢卡片、回「這張確認卡已失效，請重新送出您的需求。」，不送核准、不呼叫模型。
/// 顧客改打字而不按確認時，框架自動拒絕未回覆的請求，工具不執行。
///
/// 停止原因判定順序：Guard 設定者 → 顯示確認卡 → 確認請求屢遭程式拒絕 → 步數用完（<see cref="TracingChatClient.ForcedFinalCall"/>）
/// → 最後一段回覆含問號（？或 ?）＝等待回覆 → 已回答。例外：<see cref="AgentTurnBudgetExceededException"/>（每輪模型呼叫總數
/// <c>MaxSteps + 1</c>，跨被拒絕後的續跑累計）＝步數上限、<see cref="AiQuotaExceededException"/>＝額度、呼叫端取消＝取消、其他＝模型錯誤。
///
/// 標紅：本輪出現查無資料／逾時／規則衝突／無規則且未被同一工具後續成功化解（<see cref="AfterSalesAgentGuard.HadUncertainResult"/>），
/// 而最後的回覆是模型文字、沒有追問、沒有建議轉人工或洽客服，且停止原因不是規則衝突，收尾時另發一個 <see cref="AgentStepKind.Warning"/>（來源＝程式）。
/// </summary>
public sealed class AfterSalesAgentRunner
{
    /// <summary>上傳照片後續跑時代顧客送出的訊息。</summary>
    public const string PhotosUploadedMessageFormat = "（已上傳 {0} 張照片）";

    private const string RecheckFailedReason = "程式重核失敗";
    private const string CustomerCanceledReason = "顧客取消";
    private const string StaleConfirmationReply = "這張確認卡已經處理過或已失效，沒有重複送出申請。";
    private const string InvalidatedConfirmationReply = "這張確認卡已失效，請重新送出您的需求。";
    private const string CreatedButReplyFailedFormat = "申請已建立（{0}），但 AI 回覆暫時失敗，請到「我的申請」查看進度。";
    private const string ConsecutiveFailuresExplanation = "連續幾次查詢都沒有成功，先停在這裡，避免給您不確定的答覆。請補充更多資訊，或洽客服人員協助。";
    private const string DuplicateRequestFormat = "這個商品已有處理中的同類申請（{0}）";
    private const string CreatedButSessionConflictFormat = "申請已建立（{0}），請到「我的申請」查看進度。{1}";
    private const string ReplayDivergedExplanation = "回放中止：目前資料或故障開關和錄製時不同，請到展示控制台重置示範資料後再試。";
    private const string ReplayFinishedExplanation = "回放的錄製內容已播完。";
    private const string ReplayActionMismatchExplanation = "回放中請按照錄製時的步驟操作。";

    /// <summary>同一輪內確認請求被程式拒絕（封鎖、重核失敗）後，最多再讓模型續跑幾次。</summary>
    private const int MaxRejectedApprovals = 3;

    private static readonly JsonSerializerOptions RawJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary><see cref="TurnResultSnapshot"/> 的格式：小駝峰（<c>replyText</c>、<c>isProgramReply</c>…），中文不跳脫。</summary>
    private static readonly JsonSerializerOptions TurnResultJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
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
    private readonly DemoControlService demoControl;
    private readonly AgentRecordingService recordingService;
    private readonly ReplayCursorStore replayCursors;
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
        DemoControlService demoControl,
        AgentRecordingService recordingService,
        ReplayCursorStore replayCursors,
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
        this.demoControl = demoControl;
        this.recordingService = recordingService;
        this.replayCursors = replayCursors;
        this.currentUserService = currentUserService;
        this.settings = settings;
        this.timeProvider = timeProvider;
        this.loggerFactory = loggerFactory;
        logger = loggerFactory.CreateLogger<AfterSalesAgentRunner>();
    }

    /// <summary>測試接縫：每輪建立工具後呼叫（例如設定 <see cref="AfterSalesAgentTools.BeforeServiceCallForTesting"/> 模擬逾時）。</summary>
    internal Action<AfterSalesAgentTools>? ConfigureToolsForTesting { get; set; }

    /// <summary>回放串流時每段之間的間隔（模擬打字）；測試設為 0。</summary>
    internal TimeSpan ReplayUpdateDelay { get; set; } = TimeSpan.FromMilliseconds(30);

    private DateTime Now => timeProvider.GetLocalNow().DateTime;

    /// <summary>示範模式開啟且為即時模式時錄製（回放與正式環境都不錄）。</summary>
    private bool RecordingActive => demoControl.IsEnabled && demoControl.Mode == DemoMode.Live;

    /// <summary>回放中的錄製 Id：示範模式開啟、為回放模式且指定了錄製；否則 null（照常呼叫模型）。</summary>
    private int? ReplayRecordingId
        => demoControl.IsEnabled && demoControl.Mode == DemoMode.Replay ? demoControl.ActiveRecordingId : null;

    /// <summary>取最新一個未關閉的會話；沒有就建立新的。</summary>
    public async Task<Guid> StartOrResumeAsync()
    {
        var userId = RequireCustomer();
        var latest = await sessionStore.GetLatestOpenAsync(userId);
        return latest?.SessionKey ?? await sessionStore.CreateAsync(userId);
    }

    /// <summary>關閉目前的會話（清掉暫存照片與回放游標、作廢未決的確認卡）並建立新的（回放從錄製的第一個動作重新開始）。</summary>
    public async Task<Guid> StartNewAsync()
    {
        var userId = RequireCustomer();
        var latest = await sessionStore.GetLatestOpenAsync(userId);
        if (latest is not null)
        {
            await sessionStore.VoidPendingConfirmationAsync(latest.SessionKey, userId);
            await sessionStore.CloseAsync(latest.SessionKey, userId);
            photoInbox.Clear(latest.SessionKey);
            replayCursors.Reset(latest.SessionKey);
        }

        return await sessionStore.CreateAsync(userId);
    }

    /// <summary>顧客送出一句話。若有未決的確認卡，它就此作廢（框架也會自動拒絕那個核准請求，工具不執行）。</summary>
    public Task<AgentTurnResult> SendAsync(
        Guid sessionKey,
        string userText,
        Func<AgentTraceEvent, Task> onEvent,
        Func<string, Task> onTextDelta,
        CancellationToken ct)
        => SendCoreAsync(sessionKey, userText, AgentRecordedTurnKind.Send, onEvent, onTextDelta, ct);

    private async Task<AgentTurnResult> SendCoreAsync(
        Guid sessionKey,
        string userText,
        AgentRecordedTurnKind recordedKind,
        Func<AgentTraceEvent, Task> onEvent,
        Func<string, Task> onTextDelta,
        CancellationToken ct)
    {
        var userId = RequireCustomer();
        var record = await RequireSessionAsync(sessionKey, userId);
        var (replay, replayStop) = await PrepareReplayAsync(sessionKey, recordedKind, userText);
        if (replayStop is not null)
        {
            return replayStop;
        }

        replay?.Cursor.AdvanceTurn();
        var recording = await RecordTurnAsync(sessionKey, userId, recordedKind, userText);
        var turn = await BeginTurnAsync(record, userId, confirmationId: null, recording, replay, onEvent, onTextDelta);

        await turn.EmitAsync(AgentStepKind.UserMessage, AgentDataSource.Conversation, userText, null);
        // 條件式作廢：另一個分頁若已先按下確認（搶下決定），這裡不會把它改成拒絕。
        if (await sessionStore.VoidPendingConfirmationAsync(sessionKey, userId))
        {
            await turn.EmitAsync(AgentStepKind.ConfirmationDecided, AgentDataSource.Conversation,
                "你改以文字回覆、沒有按下確認，這張確認卡作廢，不會建立申請。", null);
        }

        return await RunTurnAsync(turn, _ => new ChatMessage(ChatRole.User, userText), ct);
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
        var kind = approve ? AgentRecordedTurnKind.Confirm : AgentRecordedTurnKind.Reject;
        ReplayPlan? replay = null;
        if (pending is not null && pending.Id == confirmationId)
        {
            // 回放中按了和錄製時相反的選擇：在搶下決定之前擋下，確認卡維持未決，顧客可以改按正確的選擇。
            (replay, var replayStop) = await PrepareReplayAsync(sessionKey, kind, customerText: null);
            if (replayStop is not null)
            {
                return replayStop;
            }

            // 會話裡沒有這張卡的原始核准請求（例如上一輪存回會話失敗）：一律失敗關閉——作廢卡片、回程式說明，
            // 不送任何核准、不呼叫模型、不建立申請。不可以 RequestId 自組請求：它不是原始呼叫的 CallId，框架會對不上而出錯。
            if (!await HasApprovalRequestAsync(record, pending.ApprovalRequestId))
            {
                await sessionStore.TryClaimConfirmationAsync(pending.Id, userId, approved: false);
                logger.LogWarning(
                    "Agent confirmation voided because its approval request is missing from the session. SessionKey={SessionKey}, ConfirmationId={ConfirmationId}",
                    sessionKey, confirmationId);
                return new AgentTurnResult(InvalidatedConfirmationReply, [], null, record.LastStopReason ?? AgentStopReason.Answered, null, null);
            }
        }

        // 先以條件式更新搶下決定：之後就不再是「未決」，重複按下或另一個分頁（即使同時讀到未決）都不會再送一次。
        if (pending is null || pending.Id != confirmationId
            || !await sessionStore.TryClaimConfirmationAsync(pending.Id, userId, approve))
        {
            logger.LogInformation(
                "Agent confirmation ignored because it is no longer pending. SessionKey={SessionKey}, ConfirmationId={ConfirmationId}",
                sessionKey, confirmationId);
            return new AgentTurnResult(StaleConfirmationReply, [], null, record.LastStopReason ?? AgentStopReason.Answered, null, null);
        }

        replay?.Cursor.AdvanceTurn();
        var recording = await RecordTurnAsync(sessionKey, userId, kind, customerText: null);
        var turn = await BeginTurnAsync(record, userId, approve ? pending.Id : null, recording, replay, onEvent, onTextDelta);
        await turn.EmitAsync(AgentStepKind.ConfirmationDecided, AgentDataSource.Conversation,
            approve ? "你按下「確認送出」，程式會再核對一次資格後才建立申請。" : "你按下「取消」，不建立申請。",
            JsonSerializer.Serialize(new { confirmationId = pending.Id, approved = approve }, RawJsonOptions));

        return await RunTurnAsync(turn, session =>
        {
            // 回應必須由會話裡框架記錄的原始請求產生：它帶原始呼叫的 CallId（RequestId 是框架另加前綴的值）。
            // 以 RequestId 自組的請求當輪雖能綁定執行，但存入歷史的回應 CallId 對不上，下一輪框架會拋例外。
            // 搶下決定前已確認同一份會話裡有這個請求（HasApprovalRequestAsync）。
            var request = FindApprovalRequest(session, pending.ApprovalRequestId)
                ?? throw new InvalidOperationException("The approval request is missing from the restored agent session.");
            var response = approve ? request.CreateResponse(true) : request.CreateResponse(false, CustomerCanceledReason);
            return new ChatMessage(ChatRole.User, [response]);
        }, ct);
    }

    /// <summary>顧客上傳照片後續跑：以使用者訊息「（已上傳 N 張照片）」送出。</summary>
    public Task<AgentTurnResult> NotifyPhotosUploadedAsync(
        Guid sessionKey,
        Func<AgentTraceEvent, Task> onEvent,
        Func<string, Task> onTextDelta,
        CancellationToken ct)
        => SendCoreAsync(
            sessionKey,
            string.Format(CultureInfo.InvariantCulture, PhotosUploadedMessageFormat, photoInbox.Count(sessionKey)),
            AgentRecordedTurnKind.PhotosUploaded,
            onEvent,
            onTextDelta,
            ct);

    /// <summary>
    /// 回放劇本：目前是否回放中，以及這個會話下一個待執行的錄製動作（頁面帶入下一句、或只開放錄製時按的確認／取消）；
    /// 不是回放時回 <see cref="AgentReplayScript.NotReplay"/>，播完時 <see cref="AgentReplayScript.NextTurn"/> 為 null。
    /// </summary>
    public async Task<AgentReplayScript> GetReplayScriptAsync(Guid sessionKey)
    {
        RequireCustomer();
        if (ReplayRecordingId is not { } recordingId)
        {
            return AgentReplayScript.NotReplay;
        }

        var recording = await recordingService.GetAsync(recordingId);
        var turns = recording?.Turns ?? [];
        var next = replayCursors.Get(sessionKey, recordingId).NextTurn;
        return new AgentReplayScript(true, next < turns.Count ? turns[next] : null, recording?.Recording.CreatedAt);
    }

    /// <summary>
    /// 還原頁面用：以執行軌跡重建對話——該會話所有執行的顧客 <see cref="AgentStepKind.UserMessage"/> 與每輪收尾的
    /// <see cref="AgentStepKind.TurnResult"/>（顧客當時看到的回覆、是否為程式說明、規則卡、停止說明），依時間排列；
    /// 最後附上未決的確認卡（<see cref="AgentStepKind.ConfirmationShown"/>）。不讀會話原文，被固定說明取代的模型文字不會再出現。
    /// 1.0.5 之前的會話（軌跡裡沒有任何 <see cref="AgentStepKind.TurnResult"/>）退回讀會話原文（只有文字，回覆一律當 Agent 文字）。
    /// </summary>
    public async Task<IReadOnlyList<AgentHistoryItem>> GetHistoryAsync(Guid sessionKey)
    {
        var userId = RequireCustomer();
        var record = await sessionStore.GetAsync(sessionKey, userId);
        if (record is null)
        {
            return [];
        }

        var steps = await traceService.GetSessionStepsAsync(sessionKey, userId);
        var items = steps.Any(x => x.Kind == AgentStepKind.TurnResult)
            ? HistoryFromTrace(steps)
            : await HistoryFromSessionStateAsync(record);

        if (await sessionStore.GetPendingConfirmationAsync(sessionKey, userId) is { } pending)
        {
            var card = ConfirmationCard.FromJson(pending.CardJson) with { ConfirmationId = pending.Id };
            items.Add(new AgentHistoryItem(
                AgentStepKind.ConfirmationShown, AgentDataSource.Program, card.Title, false, [], null, card, pending.CreatedAt));
        }

        return items;
    }

    /// <summary>還原幕後面板用：該會話所有執行的步驟（依時間），只限本人的會話。</summary>
    public async Task<IReadOnlyList<AgentTraceEvent>> GetTraceEventsAsync(Guid sessionKey)
    {
        var userId = RequireCustomer();
        var steps = await traceService.GetSessionStepsAsync(sessionKey, userId);
        return steps.Select(x => new AgentTraceEvent(x.Kind, x.Source, x.PlainText, x.RawJson, x.OccurredAt)).ToList();
    }

    private List<AgentHistoryItem> HistoryFromTrace(IReadOnlyList<AgentRunStepView> steps)
    {
        var items = new List<AgentHistoryItem>();
        foreach (var step in steps)
        {
            if (step.Kind == AgentStepKind.UserMessage)
            {
                items.Add(new AgentHistoryItem(step.Kind, step.Source, step.PlainText, false, [], null, null, step.OccurredAt));
            }
            else if (step.Kind == AgentStepKind.TurnResult && ReadTurnResult(step.RawJson) is { } result)
            {
                items.Add(new AgentHistoryItem(
                    step.Kind,
                    result.IsProgramReply ? AgentDataSource.Program : AgentDataSource.Conversation,
                    result.ReplyText,
                    result.IsProgramReply,
                    result.PolicyCards ?? [],
                    result.StopExplanation,
                    null,
                    step.OccurredAt));
            }
        }

        return items;
    }

    private TurnResultSnapshot? ReadTurnResult(string? rawJson)
    {
        try
        {
            return rawJson is null ? null : JsonSerializer.Deserialize<TurnResultSnapshot>(rawJson, TurnResultJsonOptions);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to read an agent turn result step; it is skipped when restoring the conversation.");
            return null;
        }
    }

    /// <summary>1.0.5 之前的會話：軌跡沒有 TurnResult，從會話原文讀出顧客與 Agent 的文字（分不出程式說明，一律當 Agent 文字）。</summary>
    private static async Task<List<AgentHistoryItem>> HistoryFromSessionStateAsync(AgentSessionRecord record)
    {
        var items = new List<AgentHistoryItem>();
        if (string.IsNullOrEmpty(record.StateJson))
        {
            return items;
        }

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

                items.Add(new AgentHistoryItem(
                    message.Role == ChatRole.User ? AgentStepKind.UserMessage : AgentStepKind.TurnResult,
                    AgentDataSource.Conversation,
                    text,
                    false,
                    [],
                    null,
                    null,
                    message.CreatedAt?.LocalDateTime ?? record.UpdatedAt));
            }
        }

        return items;
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

    /// <summary>錄製中就記下這個顧客動作（不拋例外），回傳本輪是否錄製。</summary>
    private async Task<bool> RecordTurnAsync(Guid sessionKey, int userId, AgentRecordedTurnKind kind, string? customerText)
    {
        if (!RecordingActive)
        {
            return false;
        }

        await recordingService.AppendTurnAsync(sessionKey, userId, kind, customerText);
        return true;
    }

    /// <summary>
    /// 回放中：核對這個顧客動作是不是錄製的下一個動作。不是回放回 (null, null)；動作已播完或與錄製不同時回程式固定回覆
    /// （不呼叫模型、不前進游標）；一致時回本輪要用的錄製呼叫與游標（呼叫端在動作確定執行時才 <see cref="ReplayCursor.AdvanceTurn"/>）。
    /// </summary>
    private async Task<(ReplayPlan? Plan, AgentTurnResult? Stop)> PrepareReplayAsync(
        Guid sessionKey, AgentRecordedTurnKind kind, string? customerText)
    {
        if (ReplayRecordingId is not { } recordingId)
        {
            return (null, null);
        }

        var cursor = replayCursors.Get(sessionKey, recordingId);
        var turns = (await recordingService.GetAsync(recordingId))?.Turns ?? [];
        if (cursor.NextTurn >= turns.Count)
        {
            logger.LogInformation(
                "Agent replay has no more recorded customer actions. SessionKey={SessionKey}, RecordingId={RecordingId}", sessionKey, recordingId);
            return (null, ReplayStop(AgentStopReason.ReplayFinished, ReplayFinishedExplanation));
        }

        var expected = turns[cursor.NextTurn];
        var textKind = kind is AgentRecordedTurnKind.Send or AgentRecordedTurnKind.PhotosUploaded;
        if (expected.Kind != kind
            || (textKind && !string.Equals(expected.CustomerText?.Trim(), customerText?.Trim(), StringComparison.Ordinal)))
        {
            logger.LogInformation(
                "Agent replay rejected a customer action that differs from the recording. SessionKey={SessionKey}, RecordingId={RecordingId}, ExpectedKind={ExpectedKind}, Kind={Kind}",
                sessionKey, recordingId, expected.Kind, kind);
            var reply = expected.Kind switch
            {
                AgentRecordedTurnKind.Confirm => "回放中請按下錄製時的選擇：確認送出",
                AgentRecordedTurnKind.Reject => "回放中請按下錄製時的選擇：取消",
                AgentRecordedTurnKind.PhotosUploaded => "回放中請上傳與錄製時相同張數的照片",
                _ => "回放中請送出錄製的句子",
            };
            return (null, ReplayStop(AgentStopReason.ReplayActionMismatch, reply, ReplayActionMismatchExplanation));
        }

        return (new ReplayPlan(recordingId, await recordingService.GetCallsAsync(recordingId), cursor), null);
    }

    /// <summary>回放的程式固定回覆（沒有執行這一輪，所以不寫軌跡、不存會話）。</summary>
    private static AgentTurnResult ReplayStop(AgentStopReason reason, string text, string? explanation = null)
        => new(text, [], null, reason, explanation ?? text, null);

    private async Task<Turn> BeginTurnAsync(
        AgentSessionRecord record,
        int userId,
        int? confirmationId,
        bool recording,
        ReplayPlan? replay,
        Func<AgentTraceEvent, Task> onEvent,
        Func<string, Task> onTextDelta)
    {
        // 回放的那一輪在軌跡上標出用的錄製（/agent-runs 的「回放」標籤），和即時執行分得出來。
        var runId = await traceService.StartRunAsync(record.SessionKey, userId, replay?.RecordingId);
        // 存回時要對上的版本＝讀入這份 StateJson 時的版本（同一筆讀取），中間若有別的分頁存回就會被擋下。
        return new Turn(record, userId, confirmationId, runId, record.StateVersion, recording, replay, traceService, onEvent, onTextDelta, timeProvider, logger);
    }

    /// <summary>組 Agent、還原會話、執行（含確認請求被程式拒絕後的續跑），判定停止原因並存回會話。</summary>
    private async Task<AgentTurnResult> RunTurnAsync(Turn turn, Func<AgentSession, ChatMessage> createInput, CancellationToken ct)
    {
        var options = settings.CurrentValue;
        var sessionKey = turn.Record.SessionKey;
        var guard = new AfterSalesAgentGuard(
            options.MaxConsecutiveToolFailures, turn.EmitAsync, timeProvider, loggerFactory.CreateLogger<AfterSalesAgentGuard>());
        TracingChatClient? tracing = null;
        AfterSalesAgentTools? tools = null;
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
            (agent, tracing, tools) = await BuildAgentAsync(turn, guard, options, ct);
            session = string.IsNullOrEmpty(turn.Record.StateJson)
                ? await agent.CreateSessionAsync(ct)
                : await agent.DeserializeSessionAsync(JsonSerializer.Deserialize<JsonElement>(turn.Record.StateJson), cancellationToken: ct);

            var rejected = 0;
            ChatMessage? next = createInput(session);
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

                var (shown, rejection, stopped) = await PrepareConfirmationAsync(turn, guard, tools, approval, ct);
                if (stopped)
                {
                    // 組卡的程式重查逾時、身分失效或服務不可用：Guard 已設停止原因與固定說明；不出卡、不續跑，
                    // 留在會話裡的請求下一輪會被框架自動拒絕。
                    break;
                }

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
        catch (AgentTurnBudgetExceededException)
        {
            // 確認請求被程式拒絕後續跑，累計的模型呼叫已達每輪上限：與步數用完同樣收尾（回覆用已完成子回合的模型文字或程式說明）。
            completed = true;
            reason = guard.StopReason ?? AgentStopReason.StepLimitReached;
        }
        catch (AiQuotaExceededException ex)
        {
            reason = AgentStopReason.QuotaExceeded;
            explanation = ex.Message;
        }
        catch (ReplayDivergedException)
        {
            reason = AgentStopReason.ReplayDiverged;
            explanation = ReplayDivergedExplanation;
        }
        catch (ReplayExhaustedException)
        {
            reason = AgentStopReason.ReplayFinished;
            explanation = ReplayFinishedExplanation;
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
                    + (reason is AgentStopReason.QuotaExceeded or AgentStopReason.ReplayDiverged or AgentStopReason.ReplayFinished
                        ? " " + explanation
                        : string.Empty);
            }
        }

        // 失敗的那一輪框架不會存入歷史；但申請已建立時要把程式回覆寫回會話，下一輪模型才知道已經送出（以及編號）。
        var appendReply = completed
            ? (programReply && card is null ? reply : null)
            : (guard.CreatedRequestNo is not null ? reply : null);
        AgentSessionConflictException? conflict = null;
        if (agent is not null && session is not null)
        {
            conflict = await SaveSessionAsync(turn, agent, session, reason, appendReply);
        }

        await turn.EmitAsync(AgentStepKind.ModelReply, programReply ? AgentDataSource.Program : AgentDataSource.Conversation, reply, null);
        // 規則衝突、或模型已建議轉人工／洽客服，都是照提示詞的正確收尾，不標紅。
        if (completed && !programReply && guard.HadUncertainResult && reason != AgentStopReason.PolicyConflict
            && !AsksQuestion(replies) && !SuggestsHuman(replies))
        {
            await turn.EmitAsync(AgentStepKind.Warning, AgentDataSource.Program, AgentStopLabels.UncertainConclusionWarning, null);
        }

        await turn.EmitAsync(AgentStepKind.Stop, AgentDataSource.Program,
            explanation is null ? StopText(reason, options) : $"{StopText(reason, options)} {explanation}",
            JsonSerializer.Serialize(new { stopReason = reason.ToString() }, RawJsonOptions));

        var policyCards = guard.PolicyCards.ToList();
        if (conflict is not null)
        {
            // 另一個分頁已先存回會話：這一輪沒有存進會話，顧客看到的是重新整理提示（申請已建立時要帶編號）。
            // 這一輪顯示的確認卡也一併作廢：會話裡沒有它的核准請求，留著會讓還原後的卡片無法正確送出。
            var notice = guard.CreatedRequestNo is { } no
                ? string.Format(CultureInfo.InvariantCulture, CreatedButSessionConflictFormat, no, conflict.Message)
                : conflict.Message;
            if (card is not null)
            {
                await sessionStore.TryClaimConfirmationAsync(card.ConfirmationId, turn.UserId, approved: false);
            }

            await EmitTurnResultAsync(turn, new TurnResultSnapshot(notice, true, reason.ToString(), null, [], guard.CreatedRequestNo), conflict: true, hasCard: false);
            await EndRunAsync(turn, reason, tracing?.ModelCallCount ?? 0, guard.ToolCallCount);
            logger.LogWarning(
                "After-sales agent turn was not saved because the session was updated elsewhere. SessionKey={SessionKey}, StopReason={StopReason}",
                sessionKey, reason);
            throw new AgentSessionConflictException(notice, guard.CreatedRequestNo);
        }

        await EmitTurnResultAsync(turn,
            new TurnResultSnapshot(reply, programReply, reason.ToString(), explanation, policyCards, guard.CreatedRequestNo),
            conflict: false, hasCard: card is not null);
        await EndRunAsync(turn, reason, tracing?.ModelCallCount ?? 0, guard.ToolCallCount);

        logger.LogInformation(
            "After-sales agent turn finished. SessionKey={SessionKey}, StopReason={StopReason}, ModelCalls={ModelCalls}, ToolCalls={ToolCalls}",
            sessionKey, reason, tracing?.ModelCallCount ?? 0, guard.ToolCallCount);
        return new AgentTurnResult(reply, policyCards, card, reason, explanation, guard.CreatedRequestNo);
    }

    /// <summary>
    /// 每輪最後一步：<see cref="AgentStepKind.TurnResult"/>（來源＝程式）。RawJson 是 <see cref="TurnResultSnapshot"/>（顧客看到的回覆、
    /// 規則卡、停止說明、申請編號，不含收件人／地址／手機），還原頁面據此重建對話；白話一句同時出現在幕後面板。
    /// </summary>
    private Task EmitTurnResultAsync(Turn turn, TurnResultSnapshot result, bool conflict, bool hasCard)
    {
        var parts = new List<string>
        {
            conflict ? "對話已在其他分頁更新，這一輪沒有存回，顯示重新整理提示"
            : result.IsProgramReply ? "回覆為程式說明" : "回覆為 Agent 文字",
        };
        if (result.PolicyCards.Count > 0)
        {
            parts.Add($"規則卡 {result.PolicyCards.Count} 張");
        }

        if (hasCard)
        {
            parts.Add("顯示確認卡");
        }

        if (result.CreatedRequestNo is { } no)
        {
            parts.Add($"已建立申請 {no}");
        }

        return turn.EmitAsync(AgentStepKind.TurnResult, AgentDataSource.Program,
            $"本輪結果：{string.Join("，", parts)}。",
            JsonSerializer.Serialize(result, TurnResultJsonOptions));
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

    /// <summary>最後一段回覆是否建議轉人工或洽客服。</summary>
    private static bool SuggestsHuman(IReadOnlyList<string> replies)
        => replies.Count > 0 && (replies[^1].Contains("轉人工", StringComparison.Ordinal) || replies[^1].Contains("客服", StringComparison.Ordinal));

    private static string StopText(AgentStopReason reason, AfterSalesAgentSettings options)
        => AgentStopLabels.Text(reason, options.MaxSteps, options.MaxConsecutiveToolFailures);

    private async Task<(AIAgent Agent, TracingChatClient Tracing, AfterSalesAgentTools Tools)> BuildAgentAsync(
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
            demoControl,
            timeProvider,
            loggerFactory.CreateLogger<AfterSalesAgentTools>(),
            toolContext);
        ConfigureToolsForTesting?.Invoke(tools);

        IChatClient modelClient;
        if (turn.Replay is { } replay)
        {
            // 回放：不建立模型 client（不驗證端點與金鑰、不查額度、不記用量），改送錄製的回應；位置與錄製時相同，摘要才比得起來。
            modelClient = new ReplayChatClient(replay.Calls, replay.Cursor, ReplayUpdateDelay, loggerFactory.CreateLogger<ReplayChatClient>());
        }
        else
        {
            modelClient = chatClientFactory.Create(TokenUsageOperations.AfterSalesAgent, sessionKey, options.GetApiStyle(), options.ReasoningEffort);
            if (turn.Recording)
            {
                modelClient = new RecordingChatClient(
                    modelClient, sessionKey, chatClientFactory.ConfiguredModel, recordingService, loggerFactory.CreateLogger<RecordingChatClient>());
            }
        }

        var tracing = new TracingChatClient(
            modelClient,
            turn.EmitAsync,
            // 單次執行最多 MaxSteps 次往返＋1 次不帶工具的收尾；被程式拒絕後的續跑也算在同一份額度裡。
            options.MaxSteps + 1,
            timeProvider,
            logger,
            turn.Replay?.RecordingId);
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

        return (agent.AsBuilder().Use(guard.InvokeAsync).Build(), tracing, tools);
    }

    /// <summary>
    /// 模型提出寫入（核准請求）：先看 Guard 是否已封鎖寫入，再以程式重查組確認卡。
    /// 回傳顯示的卡片，或拒絕原因（交給模型續跑）；重查本身失敗（逾時、身分失效、服務不可用）時 <c>Stopped</c> 為 true，
    /// Guard 已設好停止原因與固定說明，本輪就此結束。
    /// </summary>
    private async Task<(ConfirmationCard? Card, string Rejection, bool Stopped)> PrepareConfirmationAsync(
        Turn turn, AfterSalesAgentGuard guard, AfterSalesAgentTools tools, ToolApprovalRequestContent approval, CancellationToken ct)
    {
        if (approval.ToolCall is not FunctionCallContent call || call.Name != AfterSalesAgentTools.CreateAfterSalesRequestName)
        {
            logger.LogWarning("Unexpected agent approval request rejected. RequestId={RequestId}", approval.RequestId);
            return (null, RecheckFailedReason, false);
        }

        if (guard.WriteBlocked)
        {
            await guard.EmitBlockedAsync(ToolOutcome.Fail(ToolOutcomeStatus.Blocked, AfterSalesAgentGuard.BlockedMessage));
            logger.LogInformation("Agent write request blocked before showing the confirmation card. RequestId={RequestId}", approval.RequestId);
            return (null, AfterSalesAgentGuard.BlockedMessage, false);
        }

        (ConfirmationCard? Card, string Failure, bool Duplicate) built = (null, string.Empty, false);
        var check = await tools.RunConfirmationCheckAsync(async (customerUserId, checkCt) =>
        {
            built = await BuildCardAsync(turn, customerUserId, approval.RequestId, call.Arguments, checkCt);
            return ToolOutcome.Ok(null);
        }, ct);
        if (check.Status != ToolOutcomeStatus.Ok)
        {
            guard.StopOnConfirmationCheckFailure(check);
            await turn.EmitAsync(AgentStepKind.ProgramCheck, AgentDataSource.Program,
                $"程式重新核對建立申請的內容沒有完成（{check.Message.TrimEnd('。')}），不顯示確認卡、不建立申請，本輪停止。",
                Serialize(check.ToJsonElement()));
            logger.LogWarning(
                "Agent confirmation re-check failed; the turn stops without a card. RequestId={RequestId}, Status={Status}",
                approval.RequestId, check.Status);
            return (null, string.Empty, true);
        }

        var (draft, failure, duplicate) = built;
        if (draft is null)
        {
            await turn.EmitAsync(AgentStepKind.ProgramCheck, AgentDataSource.Program,
                $"程式重新核對建立申請的內容：{failure}，不顯示確認卡。", Serialize(new { tool = call.Name, arguments = call.Arguments }));
            logger.LogInformation(
                "Agent write request rejected by the confirmation re-check. RequestId={RequestId}, DuplicateOpenRequest={DuplicateOpenRequest}",
                approval.RequestId, duplicate);
            // 已有處理中的申請不是核對失敗：直接把原因（含申請編號）告訴模型，讓它照實轉告顧客。
            return (null, duplicate ? failure : $"{RecheckFailedReason}：{failure}", false);
        }

        var confirmation = await sessionStore.AddConfirmationAsync(
            turn.Record.SessionKey, turn.UserId, approval.RequestId, call.Name, Serialize(call.Arguments) ?? "{}", draft.ToJson());
        var card = draft with { ConfirmationId = confirmation.Id };
        await turn.EmitAsync(AgentStepKind.ConfirmationShown, AgentDataSource.Program,
            $"程式重新查詢訂單與規則後顯示確認卡：{card.ProductLine}，{card.FromSpec}{(card.ToSpec is null ? string.Empty : " → " + card.ToSpec)}（{card.IssueLabel}）。按下確認前不會建立申請。",
            card.ToJson());
        return (card, string.Empty, false);
    }

    /// <summary>
    /// 依模型提出的參數以程式重新查出確認卡內容；不是本人、查無、已有處理中的同類申請（<c>Duplicate</c>）、無規則、
    /// 缺目標規格或資格不符時回失敗原因。只在 <see cref="AfterSalesAgentTools.RunConfirmationCheckAsync"/> 裡呼叫：
    /// <paramref name="customerUserId"/> 已核對過身分與權限，服務呼叫受 <paramref name="ct"/>（工具逾時）約束。
    /// </summary>
    private async Task<(ConfirmationCard? Card, string Failure, bool Duplicate)> BuildCardAsync(
        Turn turn, int customerUserId, string approvalRequestId, IDictionary<string, object?>? args, CancellationToken ct)
    {
        if (!int.TryParse(AfterSalesAgentGuard.Arg(args, "orderItemId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var orderItemId))
        {
            return (null, "沒有指定訂單商品", false);
        }

        if (!AfterSalesAgentTools.TryParseIssueType(AfterSalesAgentGuard.Arg(args, "issueType"), out var type))
        {
            return (null, "無法辨識的問題類型", false);
        }

        var orders = await AfterSalesAgentTools.ObserveFault(shopOrderService.GetMyOrdersAsync(customerUserId)).WaitAsync(ct);
        var order = orders.FirstOrDefault(o => o.Items.Any(i => i.OrderItemId == orderItemId));
        var item = order?.Items.First(i => i.OrderItemId == orderItemId);
        if (order is null || item is null)
        {
            return (null, "查無這個訂單商品（或不是本人的訂單）", false);
        }

        // 與送件時的去重同一條規則：先擋下，不讓顧客按了確認才被拒。
        if (await AfterSalesAgentTools.ObserveFault(requestService.GetOpenRequestNoAsync(customerUserId, orderItemId, type)).WaitAsync(ct)
            is { } openRequestNo)
        {
            return (null, string.Format(CultureInfo.InvariantCulture, DuplicateRequestFormat, openRequestNo), true);
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
            return (null, type == AfterSalesType.SizeExchange ? "換尺碼沒有指定目標尺碼" : "換顏色沒有指定目標顏色", false);
        }

        var now = Now;
        var lookup = await AfterSalesAgentTools.ObserveFault(policyService.GetEffectiveAsync(type, item.ProductCategory, now)).WaitAsync(ct);
        if (lookup.Status != PolicyLookupStatus.Found)
        {
            return (null, lookup.Status == PolicyLookupStatus.Conflict ? "規則衝突" : "查無適用規則", false);
        }

        var policy = lookup.Policies[0];
        var shippedAt = type == AfterSalesType.ShipmentUrge
            ? (await AfterSalesAgentTools.ObserveFault(shopOrderService.GetMyOrderDetailAsync(customerUserId, order.OrderId)).WaitAsync(ct))?.Shipment?.ShippedAt
            : null;
        var isWorn = bool.TryParse(AfterSalesAgentGuard.Arg(args, "isWorn"), out var worn) ? worn : (bool?)null;
        var photoCount = photoInbox.Count(turn.Record.SessionKey);
        var eligibility = AfterSalesEligibility.Evaluate(policy, order.DeliveredAt, isWorn, photoCount > 0, now, shippedAt);
        if (!eligibility.IsEligible)
        {
            return (null, "不符合申請條件（" + AfterSalesEligibility.DescribeUnmet(eligibility) + "）", false);
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
            photoCount), string.Empty, false);
    }

    /// <summary>「黑 / 40」顯示成「黑色 / 40」。</summary>
    private static string Spec(string color, string size)
        => $"{(color.EndsWith('色') ? color : color + "色")} / {size}";

    /// <summary>存回會話（樂觀版本檢查）。版本衝突時回傳例外交給呼叫端收尾；其他失敗只記錄。</summary>
    private async Task<AgentSessionConflictException?> SaveSessionAsync(
        Turn turn, AIAgent agent, AgentSession session, AgentStopReason reason, string? appendReply)
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
            await sessionStore.SaveStateAsync(turn.Record.SessionKey, turn.UserId, state.GetRawText(), reason, turn.ExpectedStateVersion);
        }
        catch (AgentSessionConflictException ex)
        {
            return ex;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save the after-sales agent session. SessionKey={SessionKey}", turn.Record.SessionKey);
        }

        return null;
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

    /// <summary>在還原的會話歷史裡找出指定 RequestId 的核准請求（從最後一則往前找）。</summary>
    private static ToolApprovalRequestContent? FindApprovalRequest(AgentSession session, string requestId)
    {
        if (!session.TryGetInMemoryChatHistory(out var history))
        {
            return null;
        }

        for (var i = history.Count - 1; i >= 0; i--)
        {
            if (history[i].Contents.OfType<ToolApprovalRequestContent>().FirstOrDefault(x => x.RequestId == requestId) is { } request)
            {
                return request;
            }
        }

        return null;
    }

    /// <summary>會話原文（<c>StateJson</c>）裡是否有指定 RequestId 的核准請求；只讀出會話內容，不呼叫模型。</summary>
    private static async Task<bool> HasApprovalRequestAsync(AgentSessionRecord record, string requestId)
    {
        if (string.IsNullOrEmpty(record.StateJson))
        {
            return false;
        }

        var reader = new ChatClientAgent(HistoryOnlyChatClient.Instance);
        var session = await reader.DeserializeSessionAsync(JsonSerializer.Deserialize<JsonElement>(record.StateJson));
        return FindApprovalRequest(session, requestId) is not null;
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
        int expectedStateVersion,
        bool recording,
        ReplayPlan? replay,
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

        /// <summary>本輪開始時讀到的會話版本；本輪每次存回會話都以它做樂觀版本檢查。</summary>
        public int ExpectedStateVersion => expectedStateVersion;

        /// <summary>本輪是否錄製（開始時判定一次，模式中途切換不影響這一輪）。</summary>
        public bool Recording => recording;

        /// <summary>本輪回放用的錄製呼叫與游標；不是回放為 null（開始時判定一次）。</summary>
        public ReplayPlan? Replay => replay;

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

    /// <summary>
    /// <see cref="AgentStepKind.TurnResult"/> 的 RawJson：<c>{ replyText, isProgramReply, stopReason, stopExplanation, policyCards[], createdRequestNo }</c>。
    /// 規則卡只有條文與資格計算，不含個資。
    /// </summary>
    private sealed record TurnResultSnapshot(
        string ReplyText,
        bool IsProgramReply,
        string StopReason,
        string? StopExplanation,
        IReadOnlyList<AgentPolicyCard> PolicyCards,
        string? CreatedRequestNo);

    /// <summary>一輪回放要用的錄製（Id 與依 <c>(Sequence, Id)</c> 排序的模型呼叫）與這個會話的游標。</summary>
    private sealed record ReplayPlan(int RecordingId, IReadOnlyList<AgentRecordedCallView> Calls, ReplayCursor Cursor);

    /// <summary>1.0.5 之前會話的還原（<see cref="GetHistoryAsync"/> 讀會話原文）用的 Agent 底層；永遠不會被呼叫。</summary>
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

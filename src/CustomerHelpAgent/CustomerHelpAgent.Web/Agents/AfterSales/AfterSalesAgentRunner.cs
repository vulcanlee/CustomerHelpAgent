using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
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
using CustomerHelpAgent.Web.Agents.PreReview;
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
/// → <see cref="TracingChatClient"/> → <c>UseFunctionInvocation</c>（步數上限）→ <see cref="ChatClientAgent"/>（7 個工具；
/// M5 起掛 <see cref="CustomerMemoryContextProvider"/>，把顧客長期記憶以系統指示注入）→ <see cref="AfterSalesAgentGuard"/>（函式中介層）。
/// 回放中最底層改為 <see cref="ReplayChatClient"/>（見下）。
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
/// <item>M5：錄製記下會話模式（<c>AgentRecording.Mode</c>，<see cref="AgentReplayScript.Mode"/> 給頁面）；會話模式與錄製不同時每個動作都以
/// <see cref="AgentStopReason.ReplayActionMismatch"/> 擋下（不呼叫模型、不前進游標），頁面以 <c>StartNewAsync(錄製的模式)</c> 開新對話，
/// Runner 依會話模式重建相同的 Agent 結構（多 Agent 交接也只用 <see cref="ReplayChatClient"/>）。</item>
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
/// 記憶確認卡（M5，<c>remember_preference</c>）走同一條路，依工具名稱分流：組卡前以 <see cref="CustomerMemoryService.ValidateAsync"/>
/// 程式重核（同樣在工具外框內），不通過就不出卡、以拒絕回應交回模型；記憶只是偏好，不受 Guard 的封鎖寫入影響。
/// 顧客確認後框架執行工具，由 <see cref="CustomerMemoryService.UpsertAsync"/> 寫入（寫入時再重核一次）。
///
/// 停止原因判定順序：Guard 設定者 → 顯示確認卡 → 確認請求屢遭程式拒絕 → 步數用完（<see cref="TracingChatClient.ForcedFinalCall"/>）
/// → 最後一段回覆含問號（？或 ?）＝等待回覆 → 已回答。例外：<see cref="AgentTurnBudgetExceededException"/>（每輪模型呼叫總數
/// <c>MaxSteps + 1</c>，跨被拒絕後的續跑累計）＝步數上限、<see cref="AiQuotaExceededException"/>＝額度、呼叫端取消＝取消、其他＝模型錯誤。
///
/// 標紅：本輪出現查無資料／逾時／規則衝突／無規則且未被同一工具後續成功化解（<see cref="AfterSalesAgentGuard.HadUncertainResult"/>），
/// 而最後的回覆是模型文字、沒有追問、沒有建議轉人工或洽客服，且停止原因不是規則衝突，收尾時另發一個 <see cref="AgentStepKind.Warning"/>（來源＝程式）。
///
/// 多 Agent 交接模式（M5，會話的 <see cref="AgentSessionRecord.Mode"/>＝<see cref="AgentMode.Handoff"/>，見 <see cref="RunHandoffTurnAsync"/>）：
/// 每輪以 <see cref="HandoffTopologyFactory"/> 重建 MAF Handoff Workflow 並 <c>AsAIAgent</c> 包裝（checkpoint 存 SQLite），
/// 四位參與者共用同一條模型鏈、同一個 Guard 與記憶 provider，工具各自是子集。框架行為的限制由 <c>MafHandoffBehaviorTests</c> 釘住，Runner 依下列規則處理：
/// <list type="number">
/// <item>確認卡只取 <c>RawRepresentation is RequestInfoEvent</c> 的核准請求（外部 RequestId），原文存進 <c>AgentConfirmation.ApprovalRequestJson</c>；
/// 回覆前一律 <c>WorkflowSessionCheckpointRecovery.TryPrepare()</c>。</item>
/// <item>尚未回覆的外部核准請求記在會話的 StateBag（<see cref="OpenApprovalsKey"/>）：顧客改打字、組卡重查失敗或拒絕次數用完而留下的請求，
/// 下一次送出前先逐一明確拒絕（多一次模型呼叫），才送新訊息——Handoff 會話不會像單一模式那樣自動拒絕，直接送一般訊息會讓會話從此壞掉。</item>
/// <item>模型鏈的例外不會拋出，而是 <c>ErrorContent</c> update：依 <c>ExecutorFailedEvent.Data</c> 對應停止原因，回覆一律用程式說明（不顯示 ErrorContent 文字），
/// 而且<b>不存回會話</b>（保留上一輪的 StateJson）。取消也一樣不存回。</item>
/// <item>每輪存回成功後，只留下會話 <c>lastCheckpoint</c> 那一筆 checkpoint；開新對話時整批刪除。</item>
/// <item>交接沒有事件型別：Runner 從 update 裡作者為 X 的 <c>handoff_to_N</c> 呼叫（及發言者改變）發 <see cref="AgentStepKind.Handoff"/> 事件。</item>
/// <item>每位參與者外面包 <see cref="HandoffTopologyFactory.ScrubHistoryAttribution"/>：否則確認卡核准／拒絕後同一輪再交接，其他參與者第二次被呼叫時
/// 會送出沒有對應呼叫的工具結果（Responses API 回 400）。</item>
/// </list>
/// 「只建立一筆申請」靠 <c>AgentConfirmation</c> 的條件式認領保證，不依賴框架。
/// </summary>
public sealed class AfterSalesAgentRunner
{
    /// <summary>上傳照片後續跑時代顧客送出的訊息。</summary>
    public const string PhotosUploadedMessageFormat = "（已上傳 {0} 張照片）";

    private const string RecheckFailedReason = "程式重核失敗";
    private const string CustomerCanceledReason = "顧客取消";
    private const string MemoryCardTitle = "記住您的偏好";
    private const string StaleConfirmationReply = "這張確認卡已經處理過或已失效，沒有重複送出申請。";
    private const string InvalidatedConfirmationReply = "這張確認卡已失效，請重新送出您的需求。";
    private const string CreatedButReplyFailedFormat = "申請已建立（{0}），但 AI 回覆暫時失敗，請到「我的申請」查看進度。";
    private const string ConsecutiveFailuresExplanation = "連續幾次查詢都沒有成功，先停在這裡，避免給您不確定的答覆。請補充更多資訊，或洽客服人員協助。";
    private const string DuplicateRequestFormat = "這個商品已有處理中的同類申請（{0}）";
    private const string CreatedButSessionConflictFormat = "申請已建立（{0}），請到「我的申請」查看進度。{1}";
    private const string ReplayDivergedExplanation = "回放中止：目前資料或故障開關和錄製時不同，請到展示控制台重置示範資料後再試。";
    private const string ReplayFinishedExplanation = "回放的錄製內容已播完。";
    private const string ReplayActionMismatchExplanation = "回放中請按照錄製時的步驟操作。";
    private const string ModelErrorExplanation = "AI 服務暫時發生問題，這次沒有完成。請稍後再試，或洽客服人員協助。";
    private const string CanceledExplanation = "已取消這次的處理。";

    /// <summary>多 Agent 模式：顧客沒有按確認、改送其他訊息時，替留下的核准請求送出的拒絕理由（模型會看到）。</summary>
    internal const string AbandonedApprovalReason = "顧客未確認，改問其他問題";

    /// <summary>多 Agent 模式：顧客已確認、申請確實已建立（確認卡已連到申請），那一輪之後失敗回滾而留下的請求；{0} 是申請編號。</summary>
    internal const string RequestCreatedApprovalReasonFormat = "顧客先前已確認，申請已建立（{0}），請勿重複建立";

    /// <summary>多 Agent 模式：顧客已確認、偏好確實已記住，那一輪之後失敗回滾而留下的請求。</summary>
    internal const string MemorySavedApprovalReason = "顧客先前已確認，這個偏好已經記住，請勿重複執行";

    /// <summary>多 Agent 模式：顧客已確認、但那一輪在操作完成前中斷（結果無法確認）而留下的請求。不可說成「已執行」。</summary>
    internal const string UncertainApprovalReason = "顧客已確認，但這一輪中斷、結果不確定；請勿重複執行，請顧客到「我的申請」或「我的 AI 記憶」查看";

    /// <summary>多 Agent 會話存下的 checkpoint 已不存在（或會話無法還原）：失敗關閉，請顧客開新對話。</summary>
    internal const string UnrecoverableSessionExplanation = "這段對話的進度已無法接續（找不到上次的紀錄），請按「開新對話」重新開始。";

    /// <summary>
    /// 多 Agent 模式：替留下的核准請求送出拒絕後，模型一直又提出新的請求（達拒絕上限），顧客這句話始終沒有機會送出。
    /// 不可拿「連續查詢失敗」或模型針對舊請求的文字當成回覆；有待回覆請求時也不能硬送一般訊息（會話會壞）。
    /// </summary>
    internal const string CustomerMessageNotSentReply = "前面還有未完成的確認請求一直無法結束，您剛才這句話還沒有交給 AI 處理，請再送一次；若仍無法處理，請按「開新對話」重新開始。";

    /// <summary>多 Agent 模式：會話 StateBag 裡「尚未回覆的外部核准請求」的鍵（與框架的待決請求同步、隨會話一起存回或回滾）。</summary>
    internal const string OpenApprovalsKey = "AfterSalesOpenApprovals";

    /// <summary>交接理由（模型產生）寫進軌跡的上限字數。</summary>
    private const int MaxHandoffReasonLength = 200;

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

    /// <summary>會話 StateBag 裡 <see cref="OpenApprovalsState"/> 的格式（本專案自己的型別，以反射序列化）。</summary>
    private static readonly JsonSerializerOptions StateBagJsonOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    private readonly AgentChatClientFactory chatClientFactory;
    private readonly IAiSystemPromptProvider promptProvider;
    private readonly AgentSessionStore sessionStore;
    private readonly WorkflowCheckpointService checkpointService;
    private readonly AgentTraceService traceService;
    private readonly ShopOrderService shopOrderService;
    private readonly AfterSalesPolicyService policyService;
    private readonly AfterSalesRequestService requestService;
    private readonly CustomerMemoryService memoryService;
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
        WorkflowCheckpointService checkpointService,
        AgentTraceService traceService,
        ShopOrderService shopOrderService,
        AfterSalesPolicyService policyService,
        AfterSalesRequestService requestService,
        CustomerMemoryService memoryService,
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
        this.checkpointService = checkpointService;
        this.traceService = traceService;
        this.shopOrderService = shopOrderService;
        this.policyService = policyService;
        this.requestService = requestService;
        this.memoryService = memoryService;
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

    /// <summary>
    /// 關閉目前的會話（清掉暫存照片與回放游標、作廢未決的確認卡；多 Agent 會話另刪掉它的 checkpoint）並建立新的
    /// （回放從錄製的第一個動作重新開始）。<paramref name="mode"/>（M5）決定新會話是單一 Agent 或多 Agent，建立後不再改變。
    /// </summary>
    public async Task<Guid> StartNewAsync(AgentMode mode = AgentMode.Single)
    {
        var userId = RequireCustomer();
        var latest = await sessionStore.GetLatestOpenAsync(userId);
        if (latest is not null)
        {
            await sessionStore.VoidPendingConfirmationAsync(latest.SessionKey, userId);
            await sessionStore.CloseAsync(latest.SessionKey, userId);
            photoInbox.Clear(latest.SessionKey);
            replayCursors.Reset(latest.SessionKey);
            if (latest.Mode == AgentMode.Handoff && ReadCheckpoint(latest.StateJson) is { } checkpoint)
            {
                // 關閉的會話不會再續跑：它的 checkpoint 整批刪除（清理失敗只記 Warning）。
                await checkpointService.DeleteSessionCheckpointsAsync(checkpoint.SessionId, keepCheckpointId: null);
            }
        }

        return await sessionStore.CreateAsync(userId, mode);
    }

    /// <summary>會話的模式（M5）：頁面據此顯示單一／多 Agent；找不到（或不是本人的）會話回 <see cref="AgentMode.Single"/>。</summary>
    public async Task<AgentMode> GetModeAsync(Guid sessionKey)
    {
        var userId = RequireCustomer();
        return (await sessionStore.GetAsync(sessionKey, userId))?.Mode ?? AgentMode.Single;
    }

    /// <summary>
    /// 顧客送出一句話。若有未決的確認卡，它就此作廢（單一模式由框架自動拒絕那個核准請求；多 Agent 模式由 Runner 先明確拒絕，工具都不執行）。
    /// </summary>
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
        var (replay, replayStop) = await PrepareReplayAsync(record, recordedKind, userText);
        if (replayStop is not null)
        {
            return replayStop;
        }

        replay?.Cursor.AdvanceTurn();
        var recording = await RecordTurnAsync(sessionKey, userId, recordedKind, userText);
        var turn = await BeginTurnAsync(record, userId, confirmationId: null, recording, replay, onEvent, onTextDelta);

        await turn.EmitAsync(AgentStepKind.UserMessage, AgentDataSource.Conversation, userText, null);
        // 條件式作廢：另一個分頁若已先按下確認（搶下決定），這裡不會把它改成拒絕。回傳的工具名稱只用來決定說明文字。
        if (await sessionStore.VoidPendingConfirmationAsync(sessionKey, userId) is { } pendingTool)
        {
            await turn.EmitAsync(AgentStepKind.ConfirmationDecided, AgentDataSource.Conversation,
                pendingTool == AfterSalesAgentTools.RememberPreferenceName
                    ? "你改以文字回覆、沒有按下確認，這張記憶確認卡作廢，不會記住這個偏好。"
                    : "你改以文字回覆、沒有按下確認，這張確認卡作廢，不會建立申請。",
                null);
        }

        return turn.Record.Mode == AgentMode.Handoff
            ? await RunHandoffTurnAsync(turn, HandoffInput.Message(userText), ct)
            : await RunTurnAsync(turn, _ => new ChatMessage(ChatRole.User, userText), ct);
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
            (replay, var replayStop) = await PrepareReplayAsync(record, kind, customerText: null);
            if (replayStop is not null)
            {
                return replayStop;
            }

            // 會話裡沒有這張卡的原始核准請求（例如上一輪存回會話失敗）：一律失敗關閉——作廢卡片、回程式說明，
            // 不送任何核准、不呼叫模型、不建立申請。不可以 RequestId 自組請求：它不是原始呼叫的 CallId，框架會對不上而出錯。
            // 多 Agent 模式另外要求卡片存有原始請求（ApprovalRequestJson），且會話的待決請求裡還有它（否則送出的回應會被當成一般訊息，會話從此壞掉）。
            if (!await HasApprovalRequestAsync(record, pending))
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
        var decided = pending.ToolName == AfterSalesAgentTools.RememberPreferenceName
            ? (approve ? "你按下「記住」，程式會再核對一次內容後才儲存這個偏好。" : "你按下「不用記住」，不會儲存這個偏好。")
            : (approve ? "你按下「確認送出」，程式會再核對一次資格後才建立申請。" : "你按下「取消」，不建立申請。");
        await turn.EmitAsync(AgentStepKind.ConfirmationDecided, AgentDataSource.Conversation, decided,
            JsonSerializer.Serialize(new { confirmationId = pending.Id, approved = approve }, RawJsonOptions));

        if (record.Mode == AgentMode.Handoff)
        {
            // 搶下決定前已確認卡片存有原始請求、且仍在會話的待決請求裡（HasApprovalRequestAsync）。
            var approval = ReadApproval(pending.ApprovalRequestJson!)!;
            return await RunHandoffTurnAsync(turn, HandoffInput.Decision(approval, approve), ct);
        }

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
        return new AgentReplayScript(true, next < turns.Count ? turns[next] : null, recording?.Recording.CreatedAt, recording?.Recording.Mode);
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
        // 多 Agent 會話（M5 起才有）一律有 TurnResult；它的 StateJson 是 Workflow 會話，不能用單一 Agent 的方式讀。
        var items = steps.Any(x => x.Kind == AgentStepKind.TurnResult) || record.Mode == AgentMode.Handoff
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
                    step.OccurredAt,
                    result.Speaker,
                    result.Segments));
            }
            else if (step.Kind == AgentStepKind.Handoff)
            {
                // 多 Agent 模式的交接（M5）：頁面據接手者插入「已轉給品質專員」之類的系統訊息。
                items.Add(new AgentHistoryItem(
                    step.Kind, step.Source, step.PlainText, true, [], null, null, step.OccurredAt, HandoffEventData.FromRawJson(step.RawJson)?.To));
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
    /// 回放中：核對這個顧客動作是不是錄製的下一個動作。不是回放回 (null, null)；會話模式與錄製時不同（M5）、動作已播完或與錄製不同時
    /// 回程式固定回覆（不呼叫模型、不前進游標）；一致時回本輪要用的錄製呼叫與游標（呼叫端在動作確定執行時才 <see cref="ReplayCursor.AdvanceTurn"/>）。
    /// 模式必須相同，Runner 才會以錄製時的 Agent 結構重建（單一或多 Agent 交接），錄製的呼叫順序才對得上。
    /// </summary>
    private async Task<(ReplayPlan? Plan, AgentTurnResult? Stop)> PrepareReplayAsync(
        AgentSessionRecord record, AgentRecordedTurnKind kind, string? customerText)
    {
        if (ReplayRecordingId is not { } recordingId)
        {
            return (null, null);
        }

        var sessionKey = record.SessionKey;
        var recording = await recordingService.GetAsync(recordingId);
        if (recording is not null && recording.Recording.Mode != record.Mode)
        {
            logger.LogInformation(
                "Agent replay rejected a customer action because the session mode differs from the recording. SessionKey={SessionKey}, RecordingId={RecordingId}, RecordingMode={RecordingMode}, SessionMode={SessionMode}",
                sessionKey, recordingId, recording.Recording.Mode, record.Mode);
            return (null, ReplayStop(
                AgentStopReason.ReplayActionMismatch,
                $"這段錄製是以「{AgentModeLabels.Text(recording.Recording.Mode)}」模式錄的，回放中請按「開新對話」以相同模式重新開始。",
                ReplayActionMismatchExplanation));
        }

        var cursor = replayCursors.Get(sessionKey, recordingId);
        var turns = recording?.Turns ?? [];
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
            reason = CompletedStopReason(guard, card, approvalsExhausted, tracing, replies);
        }
        catch (AgentTurnBudgetExceededException)
        {
            // 確認請求被程式拒絕後續跑，累計的模型呼叫已達每輪上限：與步數用完同樣收尾（回覆用已完成子回合的模型文字或程式說明）。
            completed = true;
            reason = guard.StopReason ?? AgentStopReason.StepLimitReached;
        }
        catch (Exception ex)
        {
            bool unexpected;
            (reason, explanation, unexpected) = MapFailure(ex, guard, options.MaxSteps, ct);
            if (unexpected)
            {
                logger.LogError(ex, "After-sales agent turn failed. SessionKey={SessionKey}, StopReason={StopReason}", sessionKey, reason);
            }
        }

        return await FinishTurnAsync(
            turn,
            new TurnOutcome(guard, tracing, replies, card, reason, explanation, completed, Speaker: null),
            options,
            options.MaxSteps,
            // 失敗的那一輪框架不會存入歷史；但申請已建立時要把程式回覆寫回會話，下一輪模型才知道已經送出（以及編號）。
            (finalReason, appendReply) => agent is not null && session is not null
                ? SaveSessionAsync(turn, agent, session, finalReason, appendReply)
                : Task.FromResult<AgentSessionConflictException?>(null));
    }

    /// <summary>
    /// 一輪的收尾（兩種模式共用）：依停止原因決定回覆（<see cref="Finalize"/>，或失敗時的程式說明）、存回會話（<paramref name="saveAsync"/>，
    /// 參數為停止原因與要寫回會話歷史的程式回覆）、發 ModelReply／Warning／Stop／TurnResult 事件、結束軌跡；版本衝突時拋 <see cref="AgentSessionConflictException"/>。
    /// <paramref name="maxSteps"/> 是這個模式的步數上限（單一 <c>MaxSteps</c>、多 Agent <c>HandoffMaxSteps</c>），只影響說明文字。
    /// </summary>
    private async Task<AgentTurnResult> FinishTurnAsync(
        Turn turn,
        TurnOutcome outcome,
        AfterSalesAgentSettings options,
        int maxSteps,
        Func<AgentStopReason, string?, Task<AgentSessionConflictException?>> saveAsync)
    {
        var sessionKey = turn.Record.SessionKey;
        var (guard, tracing, replies, card, reason, explanation, completed, speaker, segments, fixedReply) = outcome;
        bool programReply;
        var reply = string.Join("\n\n", replies);
        if (completed)
        {
            (reply, explanation, programReply) = Finalize(reason, guard.FixedReply ?? fixedReply, reply, maxSteps, card?.Kind);
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

        var appendReply = completed
            ? (programReply && card is null ? reply : null)
            : (guard.CreatedRequestNo is not null ? reply : null);
        var conflict = await saveAsync(reason, appendReply);

        // 多 Agent 模式的模型回覆帶發言者（頁面標示「品質專員」）；程式說明不帶。單一模式一律 null（RawJson 不變）。
        var replySpeaker = programReply ? null : speaker;
        var replySegments = programReply ? null : SpeakerSegments(segments, reply);
        await turn.EmitAsync(AgentStepKind.ModelReply, programReply ? AgentDataSource.Program : AgentDataSource.Conversation, reply,
            replySpeaker is null ? null : JsonSerializer.Serialize(new { speaker = replySpeaker }, RawJsonOptions));
        // 規則衝突、或模型已建議轉人工／洽客服，都是照提示詞的正確收尾，不標紅。
        if (completed && !programReply && guard.HadUncertainResult && reason != AgentStopReason.PolicyConflict
            && !AsksQuestion(replies) && !SuggestsHuman(replies))
        {
            await turn.EmitAsync(AgentStepKind.Warning, AgentDataSource.Program, AgentStopLabels.UncertainConclusionWarning, null);
        }

        await turn.EmitAsync(AgentStepKind.Stop, AgentDataSource.Program,
            explanation is null ? StopText(reason, maxSteps, options) : $"{StopText(reason, maxSteps, options)} {explanation}",
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
            new TurnResultSnapshot(reply, programReply, reason.ToString(), explanation, policyCards, guard.CreatedRequestNo)
            {
                Speaker = replySpeaker,
                Segments = replySegments,
            },
            conflict: false, hasCard: card is not null);
        await EndRunAsync(turn, reason, tracing?.ModelCallCount ?? 0, guard.ToolCallCount);

        logger.LogInformation(
            "After-sales agent turn finished. SessionKey={SessionKey}, StopReason={StopReason}, ModelCalls={ModelCalls}, ToolCalls={ToolCalls}",
            sessionKey, reason, tracing?.ModelCallCount ?? 0, guard.ToolCallCount);
        return new AgentTurnResult(reply, policyCards, card, reason, explanation, guard.CreatedRequestNo, replySpeaker, replySegments);
    }

    /// <summary>
    /// 多 Agent 模式依發言者切開的回覆（頁面分段標示發言者、把「已轉給…」插在該位的回覆之前）：相鄰同一位合併；
    /// 只有一位發言者、或各段串起來不是顧客看到的回覆（被程式改寫）時回 null，頁面改以 <see cref="AgentTurnResult.Speaker"/> 標示整則回覆。
    /// </summary>
    private static List<AgentReplySegment>? SpeakerSegments(IReadOnlyList<AgentReplySegment>? segments, string reply)
    {
        if (segments is null || segments.Count < 2 || string.Join("\n\n", segments.Select(x => x.Text)) != reply)
        {
            return null;
        }

        var merged = new List<AgentReplySegment>();
        foreach (var segment in segments)
        {
            if (merged.Count > 0 && merged[^1].Speaker == segment.Speaker)
            {
                merged[^1] = merged[^1] with { Text = merged[^1].Text + "\n\n" + segment.Text };
            }
            else
            {
                merged.Add(segment);
            }
        }

        return merged.Count > 1 ? merged : null;
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
        AgentStopReason reason, string? fixedReply, string modelReply, int maxSteps, ConfirmationCardKind? cardKind)
    {
        if (fixedReply is not null)
        {
            // 逾時、權限、服務不可用：程式的固定說明取代模型輸出。
            return (fixedReply, fixedReply, true);
        }

        string? explanation = reason switch
        {
            AgentStopReason.StepLimitReached => StepLimitExplanation(maxSteps),
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
            AgentStopReason.AwaitingConfirmation when cardKind == ConfirmationCardKind.Memory => "要不要記住這個偏好，請按下方確認卡的按鈕。",
            AgentStopReason.AwaitingConfirmation => "請確認下方的申請內容，確認無誤再按「確認送出」。",
            _ => "目前沒有可以回覆的內容，請再說明一次您的需求。",
        };
        return (fallback, explanation, true);
    }

    /// <summary>完整跑完一輪後的停止原因（兩種模式共用）：Guard 設定者 → 顯示確認卡 → 確認請求屢遭程式拒絕 → 步數用完 → 追問 → 已回答。</summary>
    private static AgentStopReason CompletedStopReason(
        AfterSalesAgentGuard guard, ConfirmationCard? card, bool approvalsExhausted, TracingChatClient tracing, IReadOnlyList<string> replies)
        => guard.StopReason
            ?? (card is not null ? AgentStopReason.AwaitingConfirmation
            : approvalsExhausted ? AgentStopReason.ConsecutiveToolFailures
            : tracing.ForcedFinalCall ? AgentStopReason.StepLimitReached
            : AsksQuestion(replies) ? AgentStopReason.AwaitingUserReply
            : AgentStopReason.Answered);

    /// <summary>
    /// 一輪中途失敗的例外 → 停止原因與程式說明（兩種模式共用；多 Agent 模式的例外來自 <c>ExecutorFailedEvent.Data</c>，不是 catch）：
    /// 額度、回放分歧、回放播完、每輪模型呼叫上限、呼叫端取消；其他一律模型錯誤（Guard 已判定工具連續失敗時以它為準）。
    /// <c>Unexpected</c> 為 true 表示是非預期的錯誤，要記 Error。
    /// </summary>
    private static (AgentStopReason Reason, string Explanation, bool Unexpected) MapFailure(
        Exception? error, AfterSalesAgentGuard guard, int maxSteps, CancellationToken ct)
        => error switch
        {
            AiQuotaExceededException quota => (AgentStopReason.QuotaExceeded, quota.Message, false),
            ReplayDivergedException => (AgentStopReason.ReplayDiverged, ReplayDivergedExplanation, false),
            ReplayExhaustedException => (AgentStopReason.ReplayFinished, ReplayFinishedExplanation, false),
            AgentTurnBudgetExceededException => (AgentStopReason.StepLimitReached, StepLimitExplanation(maxSteps), false),
            OperationCanceledException when ct.IsCancellationRequested => (AgentStopReason.Canceled, CanceledExplanation, false),
            _ when guard.StopReason == AgentStopReason.ConsecutiveToolFailures
                => (AgentStopReason.ConsecutiveToolFailures, ConsecutiveFailuresExplanation, true),
            _ => (AgentStopReason.ModelError, ModelErrorExplanation, true),
        };

    /// <summary>「等待使用者回覆」的簡單判定：最後一段回覆含全形或半形問號（呼叫工具前的過場話不算）。</summary>
    private static bool AsksQuestion(IReadOnlyList<string> replies)
        => replies.Count > 0 && (replies[^1].Contains('？') || replies[^1].Contains('?'));

    /// <summary>最後一段回覆是否建議轉人工或洽客服。</summary>
    private static bool SuggestsHuman(IReadOnlyList<string> replies)
        => replies.Count > 0 && (replies[^1].Contains("轉人工", StringComparison.Ordinal) || replies[^1].Contains("客服", StringComparison.Ordinal));

    private static string StopText(AgentStopReason reason, int maxSteps, AfterSalesAgentSettings options)
        => AgentStopLabels.Text(reason, maxSteps, options.MaxConsecutiveToolFailures);

    private static string StepLimitExplanation(int maxSteps)
        => $"這次需要的步驟超過上限（{maxSteps} 步），先停在這裡，避免給您不確定的答覆。請補充說明，或洽客服人員協助。";

    private async Task<(AIAgent Agent, TracingChatClient Tracing, AfterSalesAgentTools Tools)> BuildAgentAsync(
        Turn turn, AfterSalesAgentGuard guard, AfterSalesAgentSettings options, CancellationToken ct)
    {
        var tools = CreateTools(turn, options);
        var modelClient = CreateModelClient(turn, options, TokenUsageOperations.AfterSalesAgent);
        var tracing = new TracingChatClient(
            modelClient,
            turn.EmitAsync,
            // 單次執行最多 MaxSteps 次往返＋1 次不帶工具的收尾；被程式拒絕後的續跑也算在同一份額度裡。
            options.MaxSteps + 1,
            timeProvider,
            logger,
            turn.Replay?.RecordingId);
        var chatClient = CreateFunctionInvokingClient(tracing, options);
        var instructions = await promptProvider.GetAsync(PromptTemplateKeys.AfterSalesAgent, ct);
        // 每輪一個實例、只帶顧客 Id：記憶以系統指示接在上面的指示之後，訊息不變（回放摘要不受影響）。
        var memoryProvider = new CustomerMemoryContextProvider(
            memoryService, turn.UserId, turn.EmitAsync, timeProvider, loggerFactory.CreateLogger<CustomerMemoryContextProvider>());
        var agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            AIContextProviders = [memoryProvider],
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
    /// 這一輪的工具（兩種模式共用；多 Agent 模式由各參與者各取子集）。<paramref name="shopPermission"/>：多 Agent 模式在呼叫端查好的「顧客商店」權限；
    /// 單一模式不給（工具每次即時查）。
    /// </summary>
    private AfterSalesAgentTools CreateTools(Turn turn, AfterSalesAgentSettings options, bool? shopPermission = null)
    {
        var sessionKey = turn.Record.SessionKey;
        var toolContext = new AfterSalesToolContext
        {
            CustomerUserId = turn.UserId,
            SessionKey = sessionKey,
            Emit = turn.EmitAsync,
            ToolTimeout = TimeSpan.FromSeconds(options.ToolTimeoutSeconds),
            ShopPermissionGranted = shopPermission,
            Channel = AfterSalesChannelExtensions.ForAgentMode(turn.Record.Mode),
            OnRequestCreated = turn.ConfirmationId is { } confirmationId
                ? requestId => sessionStore.MarkConfirmationAsync(confirmationId, approved: true, requestId)
                : null,
        };
        var tools = new AfterSalesAgentTools(
            shopOrderService,
            policyService,
            requestService,
            memoryService,
            permissionChecker,
            currentUserService,
            photoInbox,
            demoControl,
            timeProvider,
            loggerFactory.CreateLogger<AfterSalesAgentTools>(),
            toolContext);
        ConfigureToolsForTesting?.Invoke(tools);
        return tools;
    }

    /// <summary>
    /// 模型 client（兩種模式共用同一條鏈）：回放時是 <see cref="ReplayChatClient"/>（不建立真實模型 client），否則
    /// <c>AgentChatClientFactory.Create</c>（記帳作業 <paramref name="operation"/>）→［錄製中才有 <see cref="RecordingChatClient"/>，stream 一律 customer］。
    /// </summary>
    private IChatClient CreateModelClient(Turn turn, AfterSalesAgentSettings options, string operation)
    {
        var sessionKey = turn.Record.SessionKey;
        if (turn.Replay is { } replay)
        {
            // 回放：不建立模型 client（不驗證端點與金鑰、不查額度、不記用量），改送錄製的回應；位置與錄製時相同，摘要才比得起來。
            return new ReplayChatClient(replay.Calls, replay.Cursor, ReplayUpdateDelay, loggerFactory.CreateLogger<ReplayChatClient>());
        }

        var modelClient = chatClientFactory.Create(operation, sessionKey, options.GetApiStyle(), options.ReasoningEffort);
        if (turn.Recording)
        {
            modelClient = new RecordingChatClient(
                modelClient, sessionKey, chatClientFactory.ConfiguredModel, recordingService, loggerFactory.CreateLogger<RecordingChatClient>());
        }

        return modelClient;
    }

    private static IChatClient CreateFunctionInvokingClient(TracingChatClient tracing, AfterSalesAgentSettings options)
        => tracing.AsBuilder()
            .UseFunctionInvocation(configure: f =>
            {
                f.MaximumIterationsPerRequest = options.MaxSteps;
                f.MaximumConsecutiveErrorsPerRequest = options.MaxConsecutiveToolFailures;
            })
            .Build();

    /// <summary>
    /// 多 Agent 模式（M5）：以 <see cref="HandoffTopologyFactory"/> 建 Handoff Workflow 並包成 Agent。四位參與者共用同一條模型鏈
    /// （<see cref="TracingChatClient"/> 的每輪上限＝<c>HandoffMaxSteps + 1</c>、<c>UseFunctionInvocation</c> 每次往返上限仍是 <c>MaxSteps</c>）、
    /// 同一個 Guard（掛在每位參與者上）與同一個記憶 provider；工具各自是子集。固定的 Id／Name 讓每輪重建後 executor id 一致。
    /// 執行環境一律帶 SQLite checkpoint（否則整個 checkpoint 庫會塞進會話 JSON）。
    /// </summary>
    private async Task<(AIAgent Agent, TracingChatClient Tracing, AfterSalesAgentTools Tools)> BuildHandoffAgentAsync(
        Turn turn, AfterSalesAgentGuard guard, AfterSalesAgentSettings options, CancellationToken ct)
    {
        // 工具在 Workflow 的背景執行緒執行，而 IPermissionChecker 用的是 circuit 範圍的 DbContext（不可並行）：
        // 「顧客商店」權限在這裡（呼叫端執行緒）每輪查一次，工具改用這個結果；登入身分與展示的權限過期故障仍逐次檢查。
        var shopPermission = await AfterSalesAgentTools.HasShopPermissionAsync(permissionChecker, turn.UserId);
        var tools = CreateTools(turn, options, shopPermission);
        var tracing = new TracingChatClient(
            CreateModelClient(turn, options, TokenUsageOperations.AfterSalesAgentHandoff),
            turn.EmitAsync,
            options.HandoffMaxSteps + 1,
            timeProvider,
            logger,
            turn.Replay?.RecordingId);
        var chatClient = CreateFunctionInvokingClient(tracing, options);
        var memoryProvider = new CustomerMemoryContextProvider(
            memoryService, turn.UserId, turn.EmitAsync, timeProvider, loggerFactory.CreateLogger<CustomerMemoryContextProvider>());
        var instructions = new Dictionary<string, string>();
        foreach (var participant in HandoffTopologyFactory.Participants)
        {
            instructions[participant.Id] = await promptProvider.GetAsync(participant.PromptKey, ct);
        }

        var workflow = HandoffTopologyFactory.Build(participant => new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Id = participant.Id,
            Name = participant.Id,
            Description = participant.Description,
            AIContextProviders = [memoryProvider],
            ChatOptions = new ChatOptions
            {
                Instructions = instructions[participant.Id],
                Tools = [.. tools.CreateFunctions([.. participant.ToolNames])],
                Temperature = (float?)options.Temperature,
                AllowMultipleToolCalls = false,
            },
        }, loggerFactory).AsBuilder().Use(guard.InvokeAsync).Build());
        var environment = InProcessExecution.OffThread.WithCheckpointing(
            CheckpointManager.CreateJson(new SqliteWorkflowCheckpointStore(checkpointService)));
        var agent = workflow.AsAIAgent(
            id: HandoffTopologyFactory.WorkflowAgentId,
            name: HandoffTopologyFactory.WorkflowAgentId,
            executionEnvironment: environment);
        return (agent, tracing, tools);
    }

    /// <summary>
    /// 模型提出寫入（核准請求），依工具名稱分流：建立申請先看 Guard 是否已封鎖寫入，再以程式重查組確認卡；
    /// 記憶（<see cref="PrepareMemoryConfirmationAsync"/>）不受封鎖寫入影響，以程式重核內容組記憶確認卡。
    /// 回傳顯示的卡片，或拒絕原因（交給模型續跑）；重查本身失敗（逾時、身分失效、服務不可用）時 <c>Stopped</c> 為 true，
    /// Guard 已設好停止原因與固定說明，本輪就此結束。
    /// </summary>
    private async Task<(ConfirmationCard? Card, string Rejection, bool Stopped)> PrepareConfirmationAsync(
        Turn turn, AfterSalesAgentGuard guard, AfterSalesAgentTools tools, ToolApprovalRequestContent approval, CancellationToken ct,
        string? approvalRequestJson = null)
    {
        if (approval.ToolCall is FunctionCallContent { Name: AfterSalesAgentTools.RememberPreferenceName } memoryCall)
        {
            return await PrepareMemoryConfirmationAsync(turn, guard, tools, approval, memoryCall, ct, approvalRequestJson);
        }

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
            turn.Record.SessionKey, turn.UserId, approval.RequestId, call.Name, Serialize(call.Arguments) ?? "{}", draft.ToJson(), approvalRequestJson);
        var card = draft with { ConfirmationId = confirmation.Id };
        await turn.EmitAsync(AgentStepKind.ConfirmationShown, AgentDataSource.Program,
            $"程式重新查詢訂單與規則後顯示確認卡：{card.ProductLine}，{card.FromSpec}{(card.ToSpec is null ? string.Empty : " → " + card.ToSpec)}（{card.IssueLabel}）。按下確認前不會建立申請。",
            card.ToJson());
        return (card, string.Empty, false);
    }

    /// <summary>
    /// 記憶確認卡（M5）：在工具外框內（逾時、身分與權限）以 <see cref="CustomerMemoryService.ValidateAsync"/> 重核模型提出的內容
    /// （格式、個資、姓名與帳號），通過才寫入 <c>AgentConfirmation</c> 並出卡；不通過以拒絕原因交回模型（不出卡）。
    /// 外框本身失敗時比照建立申請，Guard 設停止原因與固定說明，本輪停止。卡片的文字全部由程式產生，不採模型文字。
    /// </summary>
    private async Task<(ConfirmationCard? Card, string Rejection, bool Stopped)> PrepareMemoryConfirmationAsync(
        Turn turn, AfterSalesAgentGuard guard, AfterSalesAgentTools tools, ToolApprovalRequestContent approval, FunctionCallContent call, CancellationToken ct,
        string? approvalRequestJson)
    {
        var kindArg = AfterSalesAgentGuard.Arg(call.Arguments, "kind");
        (MemoryKind Kind, string? Value, string? Reason) built = (default, null, null);
        var check = await tools.RunConfirmationCheckAsync(async (customerUserId, checkCt) =>
        {
            if (!AfterSalesAgentTools.TryParseMemoryKind(kindArg, out var kind))
            {
                built = (default, null, "無法辨識的偏好種類");
                return ToolOutcome.Ok(null);
            }

            var (ok, normalized, reason) = await AfterSalesAgentTools.ObserveFault(
                memoryService.ValidateAsync(customerUserId, kind, AfterSalesAgentGuard.Arg(call.Arguments, "value"))).WaitAsync(checkCt);
            built = ok ? (kind, normalized, null) : (kind, null, reason?.TrimEnd('。'));
            return ToolOutcome.Ok(null);
        }, ct);
        if (check.Status != ToolOutcomeStatus.Ok)
        {
            guard.StopOnConfirmationCheckFailure(check);
            await turn.EmitAsync(AgentStepKind.ProgramCheck, AgentDataSource.Program,
                $"程式重新核對要記住的內容沒有完成（{check.Message.TrimEnd('。')}），不顯示確認卡、不記住，本輪停止。",
                Serialize(check.ToJsonElement()));
            logger.LogWarning(
                "Agent memory confirmation re-check failed; the turn stops without a card. RequestId={RequestId}, Status={Status}",
                approval.RequestId, check.Status);
            return (null, string.Empty, true);
        }

        if (built.Value is not { } value)
        {
            // 不把模型提出的內容寫進軌跡：被拒絕的通常就是含個資的內容。
            await turn.EmitAsync(AgentStepKind.ProgramCheck, AgentDataSource.Program,
                $"程式重新核對要記住的內容：{built.Reason}，不顯示確認卡、不記住。", Serialize(new { tool = call.Name, kind = kindArg }));
            logger.LogInformation("Agent memory request rejected by the content re-check. RequestId={RequestId}", approval.RequestId);
            return (null, $"{RecheckFailedReason}：{built.Reason}", false);
        }

        var draft = new ConfirmationCard(
            0,
            approval.RequestId,
            MemoryCardTitle,
            string.Empty,
            string.Empty,
            null,
            MemoryLabels.KindText(built.Kind),
            string.Empty,
            [MemoryQuestion(built.Kind, value)],
            0,
            ConfirmationCardKind.Memory,
            built.Kind,
            value);
        var confirmation = await sessionStore.AddConfirmationAsync(
            turn.Record.SessionKey, turn.UserId, approval.RequestId, call.Name, Serialize(call.Arguments) ?? "{}", draft.ToJson(), approvalRequestJson);
        var card = draft with { ConfirmationId = confirmation.Id };
        await turn.EmitAsync(AgentStepKind.ConfirmationShown, AgentDataSource.Program,
            $"程式重新核對內容後顯示記憶確認卡：{card.Facts[0]}按下「記住」前不會儲存。",
            card.ToJson());
        return (card, string.Empty, false);
    }

    /// <summary>記憶確認卡的詢問句（程式產生），例如「要我記住：鞋子平常穿 41 碼嗎？之後的對話會先向您確認。」</summary>
    private static string MemoryQuestion(MemoryKind kind, string value)
        => kind switch
        {
            MemoryKind.ShoeSize => $"要我記住：鞋子平常穿 {value} 碼嗎？",
            MemoryKind.ClothingSize => $"要我記住：衣服平常穿 {value} 號嗎？",
            MemoryKind.PreferredColor => $"要我記住：您偏好的顏色是「{value}」嗎？",
            _ => $"要我記住這個溝通偏好：「{value}」嗎？",
        } + "之後的對話會先向您確認。";

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

    /// <summary>
    /// 多 Agent 模式的一輪（M5）：組 Handoff Workflow、還原會話，依序送出這一輪的輸入（先替留下的核准請求明確拒絕，再送顧客訊息；
    /// 或送出顧客對確認卡的決定），處理確認請求（與單一模式相同的組卡、程式重核與拒絕上限），判定停止原因；成功才存回會話。
    /// <list type="bullet">
    /// <item>模型鏈例外（額度、回放分歧／播完、每輪上限、其他）以 <c>ErrorContent</c> update 出現：依 <c>ExecutorFailedEvent.Data</c> 的型別對應停止原因
    /// （<see cref="MapHandoffFailure"/>），回覆一律是程式說明，<b>不存回會話</b>（出錯後的 Workflow 會話存回去，下一輪必定失敗）。</item>
    /// <item>取消（<paramref name="ct"/>）同樣不存回：會話維持這一輪開始前的狀態，之後可以照常續聊。</item>
    /// <item>存回成功後只保留會話 <c>lastCheckpoint</c> 那一筆 checkpoint。</item>
    /// </list>
    /// </summary>
    private async Task<AgentTurnResult> RunHandoffTurnAsync(Turn turn, HandoffInput input, CancellationToken ct)
    {
        var options = settings.CurrentValue;
        var sessionKey = turn.Record.SessionKey;
        var guard = new AfterSalesAgentGuard(
            options.MaxConsecutiveToolFailures, turn.EmitAsync, timeProvider, loggerFactory.CreateLogger<AfterSalesAgentGuard>(),
            HandoffTopologyFactory.DisplayNameOf);
        var speakers = new HandoffSpeakers(turn.EmitAsync);
        TracingChatClient? tracing = null;
        AIAgent? agent = null;
        AgentSession? session = null;
        var replies = new List<string>();
        var segments = new List<AgentReplySegment>();
        ConfirmationCard? card = null;
        var approvalsExhausted = false;
        // 程式固定回覆（取代模型文字）；目前只有「顧客的新訊息因留下的請求一直無法收尾而沒送出」會設定。
        string? fixedReply = null;
        AgentStopReason reason;
        string? explanation = null;
        var completed = false;

        try
        {
            // 存下的會話接不下去（checkpoint 已被刪掉、或 JSON 無法還原）：每輪都會失敗，不如明白請顧客開新對話。
            if (ReadCheckpoint(turn.Record.StateJson) is { } persisted
                && await checkpointService.GetPayloadAsync(persisted.SessionId, persisted.CheckpointId) is null)
            {
                throw new HandoffSessionUnrecoverableException("The last checkpoint of the handoff session is missing.");
            }

            AfterSalesAgentTools tools;
            (agent, tracing, tools) = await BuildHandoffAgentAsync(turn, guard, options, ct);
            try
            {
                session = string.IsNullOrEmpty(turn.Record.StateJson)
                    ? await agent.CreateSessionAsync(ct)
                    : await agent.DeserializeSessionAsync(JsonSerializer.Deserialize<JsonElement>(turn.Record.StateJson), cancellationToken: ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new HandoffSessionUnrecoverableException("The handoff session state cannot be restored.", ex);
            }

            var steps = new LinkedList<HandoffStep>();
            if (input.Approval is { } decided)
            {
                steps.AddLast(HandoffStep.Respond(decided, input.Approved, CustomerCanceledReason));
            }
            else
            {
                // ⚠️ Handoff 會話有待回覆的核准請求時絕不可直接送一般訊息（會話從此每輪失敗、模型不會被呼叫），先逐一明確拒絕。
                foreach (var open in OpenApprovals(session))
                {
                    var rejectReason = await LeftoverRejectionReasonAsync(turn, open);
                    await turn.EmitAsync(AgentStepKind.ProgramCheck, AgentDataSource.Program,
                        $"多 Agent 模式：先替尚未回覆的確認請求送出拒絕（{rejectReason}），工具不會執行，接著才處理你這句話。",
                        Serialize(new { requestId = open.RequestId, tool = (open.ToolCall as FunctionCallContent)?.Name }));
                    steps.AddLast(HandoffStep.Respond(open, approved: false, rejectReason));
                }

                steps.AddLast(HandoffStep.Send(input.UserText!));
            }

            var rejected = 0;
            HandoffStepResult? failedStep = null;
            while (steps.First is { } node)
            {
                steps.RemoveFirst();
                var run = await RunHandoffStepAsync(turn, agent, session, node.Value, speakers, ct);
                if (run.Failed)
                {
                    failedStep = run;
                    break;
                }

                replies.AddRange(run.Replies.Select(x => x.Text));
                segments.AddRange(run.Replies);
                if (run.Approval is not { } approval)
                {
                    continue;
                }

                if (steps.Count > 0)
                {
                    // 先送的拒絕讓模型又提出核准請求、顧客的新訊息還沒送：同樣先拒絕，才能送新訊息（計入拒絕上限）。
                    if (++rejected > MaxRejectedApprovals)
                    {
                        // 達上限時新訊息仍沒送出（最後的請求還待回覆，硬送會讓會話壞掉）：改回程式說明，請顧客再送一次；
                        // 請求留在會話（StateBag 記著），下一次送出前先明確拒絕。
                        approvalsExhausted = true;
                        fixedReply = CustomerMessageNotSentReply;
                        break;
                    }

                    steps.AddFirst(HandoffStep.Respond(approval, approved: false, AbandonedApprovalReason));
                    continue;
                }

                var (shown, rejection, stopped) = await PrepareConfirmationAsync(turn, guard, tools, approval, ct, WriteApproval(approval));
                if (stopped)
                {
                    // 組卡的程式重查失敗：Guard 已設停止原因與固定說明；請求留在會話（StateBag 記著），下一次送出前先明確拒絕。
                    break;
                }

                if (shown is not null)
                {
                    card = shown;
                    break;
                }

                if (++rejected > MaxRejectedApprovals)
                {
                    // 不再續跑；請求留在會話，下一次送出前先明確拒絕。
                    approvalsExhausted = true;
                    break;
                }

                steps.AddLast(HandoffStep.Respond(approval, approved: false, rejection));
            }

            if (failedStep is not null)
            {
                bool unexpected;
                (reason, explanation, unexpected) = MapFailure(failedStep.Error, guard, options.HandoffMaxSteps, ct);
                if (unexpected)
                {
                    logger.LogError(failedStep.Error, "After-sales handoff turn failed inside the workflow. SessionKey={SessionKey}", sessionKey);
                }
                else
                {
                    logger.LogWarning(
                        "After-sales handoff turn stopped by an error inside the workflow. SessionKey={SessionKey}, StopReason={StopReason}, ErrorType={ErrorType}",
                        sessionKey, reason, failedStep.Error?.GetType().Name);
                }
            }
            else
            {
                completed = true;
                reason = CompletedStopReason(guard, card, approvalsExhausted, tracing, replies);
            }
        }
        catch (HandoffSessionUnrecoverableException ex)
        {
            reason = AgentStopReason.ServiceUnavailable;
            explanation = UnrecoverableSessionExplanation;
            logger.LogWarning(ex, "After-sales handoff session cannot be resumed; the customer is asked to start a new conversation. SessionKey={SessionKey}", sessionKey);
        }
        catch (Exception ex)
        {
            bool unexpected;
            (reason, explanation, unexpected) = MapFailure(ex, guard, options.HandoffMaxSteps, ct);
            if (unexpected)
            {
                logger.LogError(ex, "After-sales handoff turn failed. SessionKey={SessionKey}, StopReason={StopReason}", sessionKey, reason);
            }
        }

        return await FinishTurnAsync(
            turn,
            new TurnOutcome(guard, tracing, replies, card, reason, explanation, completed, speakers.LastReplySpeaker, segments, fixedReply),
            options,
            options.HandoffMaxSteps,
            // 只有完整跑完的一輪才存回（出錯或取消時保留上一輪的 StateJson）；Workflow 會話無法插入程式回覆，appendReply 不適用。
            (finalReason, _) => completed && agent is not null && session is not null
                ? SaveHandoffSessionAsync(turn, agent, session, finalReason)
                : DiscardHandoffTurnAsync(turn, agent, session));
    }

    /// <summary>
    /// 出錯或取消、不存回的一輪：若這一輪用的是新的 Workflow 會話 Id（第一輪就失敗，每次重試都會產生新的 Id），它的 checkpoint 沒有任何會話會再用到，整批刪除。
    /// 沿用既有會話 Id 時不在這裡刪（另一個分頁可能正在同一個會話上執行），留給下一次成功存回時的清理。
    /// </summary>
    private async Task<AgentSessionConflictException?> DiscardHandoffTurnAsync(Turn turn, AIAgent? agent, AgentSession? session)
    {
        if (agent is null || session is null)
        {
            return null;
        }

        try
        {
            var state = (await agent.SerializeSessionAsync(session)).GetRawText();
            if (ReadSessionId(state) is { } sessionId && sessionId != ReadSessionId(turn.Record.StateJson))
            {
                await checkpointService.DeleteSessionCheckpointsAsync(sessionId, keepCheckpointId: null);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to clean up checkpoints of a discarded handoff turn. SessionKey={SessionKey}", turn.Record.SessionKey);
        }

        return null;
    }

    /// <summary>
    /// 送出一個輸入並讀完串流：轉送文字增量、偵測交接、依發言者切分回覆、取外部核准請求（只取 <c>RawRepresentation is RequestInfoEvent</c> 那一份；
    /// 參與者自己串流裡的那一份 RequestId 是內部 id，不能拿來回覆）、收集錯誤；最後同步會話 StateBag 的待回覆請求。
    /// 送出核准回應前先 <c>TryPrepare()</c>：不然包裝層會多送一個 TurnToken，與專員在同一個 superstep 更新共享狀態而失敗（工具其實已執行）。
    /// </summary>
    private async Task<HandoffStepResult> RunHandoffStepAsync(
        Turn turn, AIAgent agent, AgentSession session, HandoffStep step, HandoffSpeakers speakers, CancellationToken ct)
    {
        if (step.Resolves is not null)
        {
#pragma warning disable MAAI001 // 實驗性 API（MAF 1.25）：只續跑 checkpoint 中等待回應的工作，不開新的一輪；MafHandoffBehaviorTests 釘住其必要性。
            var prepared = session.GetService<WorkflowSessionCheckpointRecovery>()?.TryPrepare() == true;
#pragma warning restore MAAI001
            if (!prepared)
            {
                throw new InvalidOperationException("The handoff session has no checkpoint to deliver the approval response to.");
            }
        }

        var segments = new List<(string? Speaker, StringBuilder Text)>();
        ToolApprovalRequestContent? approval = null;
        var failed = false;
        Exception? executorError = null;
        Exception? workflowError = null;
        await foreach (var update in agent.RunStreamingAsync(step.Message, session, cancellationToken: ct))
        {
            if (update.RawRepresentation is RequestInfoEvent)
            {
                approval ??= update.Contents.OfType<ToolApprovalRequestContent>().FirstOrDefault();
                continue;
            }

            if (update.Contents.OfType<ErrorContent>().Any())
            {
                // 不採用 ErrorContent 的文字（不顯示給顧客）；原始例外在事件裡。
                failed = true;
                switch (update.RawRepresentation)
                {
                    case ExecutorFailedEvent executorFailed:
                        executorError ??= executorFailed.Data;
                        break;
                    case WorkflowErrorEvent workflowFailed:
                        workflowError ??= workflowFailed.Exception is System.Reflection.TargetInvocationException { InnerException: { } inner }
                            ? inner
                            : workflowFailed.Exception;
                        break;
                }

                continue;
            }

            await speakers.ObserveAsync(update);
            if (!string.IsNullOrEmpty(update.Text))
            {
                await turn.DeltaAsync(update.Text);
                if (segments.Count == 0 || segments[^1].Speaker != update.AuthorName)
                {
                    segments.Add((update.AuthorName, new StringBuilder()));
                }

                segments[^1].Text.Append(update.Text);
            }
        }

        // 取消時串流可能安靜地結束（不拋例外、也沒有錯誤事件）：一律當成取消，這一輪不存回。
        ct.ThrowIfCancellationRequested();

        // 與框架同步：送出的回應已讓框架移除那個待決請求；新出現的外部核准請求加入（之後組卡、拒絕或留到下一次送出前拒絕）。
        var open = OpenApprovals(session).Where(x => x.RequestId != step.Resolves?.RequestId).ToList();
        if (approval is not null && open.All(x => x.RequestId != approval.RequestId))
        {
            open.Add(approval);
        }

        SetOpenApprovals(session, open);

        var replies = segments
            .Select(x => new AgentReplySegment(HandoffTopologyFactory.DisplayNameOf(x.Speaker), x.Text.ToString().Trim()))
            .Where(x => x.Text.Length > 0)
            .ToList();
        if (replies.Count > 0)
        {
            speakers.LastReplySpeaker = replies[^1].Speaker;
        }

        return new HandoffStepResult(replies, approval, failed, executorError ?? workflowError);
    }

    /// <summary>
    /// 留下的核准請求要用什麼理由拒絕（模型會看到）：
    /// <list type="bullet">
    /// <item>顧客沒有按確認（改打字、卡片作廢、沒出卡的請求、按了取消）→ 顧客未確認。</item>
    /// <item>顧客按了確認，而且操作確實完成（確認卡已連到申請；或記憶裡已有這個偏好）→ 已完成、勿重做（申請帶編號）。</item>
    /// <item>顧客按了確認，但那一輪在操作完成前就中斷（取消、續跑失敗…）→ 結果不確定、勿重做，請顧客到頁面查看。不可說成「已執行」。</item>
    /// </list>
    /// </summary>
    private async Task<string> LeftoverRejectionReasonAsync(Turn turn, ToolApprovalRequestContent open)
    {
        var confirmation = await sessionStore.GetConfirmationByRequestIdAsync(turn.Record.SessionKey, turn.UserId, open.RequestId);
        if (confirmation?.Approved != true)
        {
            return AbandonedApprovalReason;
        }

        try
        {
            if (confirmation.ToolName == AfterSalesAgentTools.CreateAfterSalesRequestName && confirmation.AfterSalesRequestId is { } requestId
                && (await requestService.GetMyRequestsAsync(turn.UserId)).FirstOrDefault(x => x.Id == requestId) is { } request)
            {
                return string.Format(CultureInfo.InvariantCulture, RequestCreatedApprovalReasonFormat, request.RequestNo);
            }

            if (confirmation.ToolName == AfterSalesAgentTools.RememberPreferenceName
                && ConfirmationCard.FromJson(confirmation.CardJson) is { MemoryKind: { } kind, MemoryValue: { } value }
                && (await memoryService.GetAsync(turn.UserId)).Any(x => x.Kind == kind && x.Value == value))
            {
                return MemorySavedApprovalReason;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to check whether a confirmed agent action completed. SessionKey={SessionKey}", turn.Record.SessionKey);
        }

        return UncertainApprovalReason;
    }

    /// <summary>多 Agent 模式存回會話（樂觀版本檢查），成功後只保留會話 <c>lastCheckpoint</c> 那一筆 checkpoint。版本衝突回傳例外；其他失敗只記錄。</summary>
    private async Task<AgentSessionConflictException?> SaveHandoffSessionAsync(Turn turn, AIAgent agent, AgentSession session, AgentStopReason reason)
    {
        string state;
        try
        {
            state = (await agent.SerializeSessionAsync(session)).GetRawText();
            await sessionStore.SaveStateAsync(turn.Record.SessionKey, turn.UserId, state, reason, turn.ExpectedStateVersion);
        }
        catch (AgentSessionConflictException ex)
        {
            return ex;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save the after-sales handoff session. SessionKey={SessionKey}", turn.Record.SessionKey);
            return null;
        }

        if (ReadCheckpoint(state) is { } checkpoint)
        {
            await checkpointService.DeleteSessionCheckpointsAsync(checkpoint.SessionId, checkpoint.CheckpointId);
        }

        return null;
    }

    /// <summary>多 Agent 會話 JSON 的 <c>sessionId</c>（Workflow 會話 Id，也是 checkpoint 的 SessionId）；沒有或讀不到回 null。</summary>
    private static string? ReadSessionId(string? stateJson)
    {
        if (string.IsNullOrEmpty(stateJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(stateJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("sessionId", out var sessionId) && sessionId.ValueKind == JsonValueKind.String
                ? sessionId.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>多 Agent 會話 JSON 的 <c>lastCheckpoint</c>（<c>{ sessionId, checkpointId }</c>）；沒有或讀不到回 null。</summary>
    private static HandoffCheckpoint? ReadCheckpoint(string? stateJson)
    {
        if (string.IsNullOrEmpty(stateJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(stateJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("lastCheckpoint", out var checkpoint)
                && checkpoint.ValueKind == JsonValueKind.Object
                && checkpoint.TryGetProperty("sessionId", out var sessionId) && sessionId.GetString() is { Length: > 0 } sid
                && checkpoint.TryGetProperty("checkpointId", out var checkpointId) && checkpointId.GetString() is { Length: > 0 } cid
                ? new HandoffCheckpoint(sid, cid)
                : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>會話 StateBag 記錄的待回覆外部核准請求（多 Agent 模式）。</summary>
    private static List<ToolApprovalRequestContent> OpenApprovals(AgentSession session)
        => session.StateBag.TryGetValue<OpenApprovalsState>(OpenApprovalsKey, out var state, StateBagJsonOptions) && state is not null
            ? [.. state.Requests.Select(ReadApproval).OfType<ToolApprovalRequestContent>()]
            : [];

    private static void SetOpenApprovals(AgentSession session, IReadOnlyCollection<ToolApprovalRequestContent> approvals)
    {
        if (approvals.Count == 0)
        {
            session.StateBag.TryRemoveValue(OpenApprovalsKey);
            return;
        }

        session.StateBag.SetValue(OpenApprovalsKey, new OpenApprovalsState { Requests = [.. approvals.Select(WriteApproval)] }, StateBagJsonOptions);
    }

    /// <summary>不建 Agent、直接從會話原文讀出 StateBag 記錄的待回覆外部核准請求（確認前的失敗關閉檢查用）。</summary>
    private static List<ToolApprovalRequestContent> ReadOpenApprovals(string stateJson)
    {
        try
        {
            using var document = JsonDocument.Parse(stateJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("stateBag", out var bagJson))
            {
                return [];
            }

            var bag = AgentSessionStateBag.Deserialize(bagJson.Clone());
            return bag.TryGetValue<OpenApprovalsState>(OpenApprovalsKey, out var state, StateBagJsonOptions) && state is not null
                ? [.. state.Requests.Select(ReadApproval).OfType<ToolApprovalRequestContent>()]
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>外部核准請求的保存格式（<c>AIJsonUtilities.DefaultOptions</c>，含原始呼叫的 CallId），應用程式重啟後仍可 <c>CreateResponse</c>。</summary>
    private static string WriteApproval(ToolApprovalRequestContent approval)
        => JsonSerializer.Serialize<AIContent>(approval, AIJsonUtilities.DefaultOptions);

    private static ToolApprovalRequestContent? ReadApproval(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<AIContent>(json, AIJsonUtilities.DefaultOptions) as ToolApprovalRequestContent;
        }
        catch (JsonException)
        {
            return null;
        }
    }

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

    /// <summary>
    /// 會話原文（<c>StateJson</c>）裡是否有這張確認卡的核准請求；只讀出會話內容，不呼叫模型。
    /// 多 Agent 模式：卡片存有原始請求（<c>ApprovalRequestJson</c>，Id 相符），且它仍在會話 StateBag 記錄的待回覆請求裡。
    /// </summary>
    private static async Task<bool> HasApprovalRequestAsync(AgentSessionRecord record, AgentConfirmation pending)
    {
        var requestId = pending.ApprovalRequestId;
        if (string.IsNullOrEmpty(record.StateJson))
        {
            return false;
        }

        if (record.Mode == AgentMode.Handoff)
        {
            return pending.ApprovalRequestJson is { } json
                && ReadApproval(json)?.RequestId == requestId
                && ReadOpenApprovals(record.StateJson).Any(x => x.RequestId == requestId);
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
    /// ⚠️ 多 Agent 模式（M5）的 Workflow 在背景執行緒跑（<c>InProcessExecution.OffThread</c>）：Guard、模型鏈、記憶 provider、工具的事件在背景執行緒發出，
    /// 同時 Runner 在讀串流的這一邊發交接事件與文字增量。所有事件與增量一律經 <c>gate</c> 排隊，維持單一寫入者
    /// （<see cref="AgentTraceService.AppendStepAsync"/> 以「最大 Sequence＋1」編號，並行寫入會重號；畫面回呼也不必處理並行）。單一模式沒有並行，閘門不會等待。
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
        private readonly SemaphoreSlim gate = new(1, 1);

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
            await gate.WaitAsync();
            try
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
            finally
            {
                gate.Release();
            }
        }

        public async Task DeltaAsync(string text)
        {
            await gate.WaitAsync();
            try
            {
                await onTextDelta(text);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Agent text delta callback failed. RunId={RunId}", runId);
            }
            finally
            {
                gate.Release();
            }
        }

    }

    /// <summary>
    /// <see cref="AgentStepKind.TurnResult"/> 的 RawJson：<c>{ replyText, isProgramReply, stopReason, stopExplanation, policyCards[], createdRequestNo }</c>
    /// （多 Agent 模式另有 <c>speaker</c>）。
    /// 規則卡只有條文與資格計算，不含個資。
    /// </summary>
    private sealed record TurnResultSnapshot(
        string ReplyText,
        bool IsProgramReply,
        string StopReason,
        string? StopExplanation,
        IReadOnlyList<AgentPolicyCard> PolicyCards,
        string? CreatedRequestNo)
    {
        /// <summary>多 Agent 模式（M5）：回覆的發言者顯示名稱；單一模式與程式說明為 null，且不寫進 JSON（單一模式的 RawJson 與 M4 相同）。</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Speaker { get; init; }

        /// <summary>多 Agent 模式（M5）同一輪兩位以上發言時依發言者切開的各段；其他情況為 null，且不寫進 JSON。</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public IReadOnlyList<AgentReplySegment>? Segments { get; init; }
    }

    /// <summary>
    /// 一輪執行的結果（交給 <see cref="FinishTurnAsync"/> 收尾）。<see cref="Speaker"/>：多 Agent 模式最後一段回覆的發言者顯示名稱；
    /// <see cref="Segments"/>：多 Agent 模式依發言者切開的各段（單一模式為 null）；
    /// <see cref="FixedReply"/>：Runner 自己決定的程式固定回覆（Guard 的固定說明優先），取代模型文字。
    /// </summary>
    private sealed record TurnOutcome(
        AfterSalesAgentGuard Guard,
        TracingChatClient? Tracing,
        IReadOnlyList<string> Replies,
        ConfirmationCard? Card,
        AgentStopReason Reason,
        string? Explanation,
        bool Completed,
        string? Speaker,
        IReadOnlyList<AgentReplySegment>? Segments = null,
        string? FixedReply = null);

    /// <summary>一輪回放要用的錄製（Id 與依 <c>(Sequence, Id)</c> 排序的模型呼叫）與這個會話的游標。</summary>
    private sealed record ReplayPlan(int RecordingId, IReadOnlyList<AgentRecordedCallView> Calls, ReplayCursor Cursor);

    /// <summary>多 Agent 模式一輪的輸入：顧客訊息，或顧客對確認卡的決定（<see cref="Approval"/> 是卡片保存的外部核准請求）。</summary>
    private sealed record HandoffInput(string? UserText, ToolApprovalRequestContent? Approval, bool Approved)
    {
        public static HandoffInput Message(string text) => new(text, null, false);

        public static HandoffInput Decision(ToolApprovalRequestContent approval, bool approved) => new(null, approval, approved);
    }

    /// <summary>多 Agent 模式一次送出的訊息；<see cref="Resolves"/> 不為 null 表示它是那個核准請求的回應（送出前要 TryPrepare）。</summary>
    private sealed record HandoffStep(ChatMessage Message, ToolApprovalRequestContent? Resolves)
    {
        public static HandoffStep Send(string text) => new(new ChatMessage(ChatRole.User, text), null);

        public static HandoffStep Respond(ToolApprovalRequestContent approval, bool approved, string rejectReason)
            => new(new ChatMessage(ChatRole.User, [approved ? approval.CreateResponse(true) : approval.CreateResponse(false, rejectReason)]), approval);
    }

    /// <summary>一次送出的結果：依發言者切分的回覆、外部核准請求、是否出錯（與原始例外）。</summary>
    private sealed record HandoffStepResult(IReadOnlyList<AgentReplySegment> Replies, ToolApprovalRequestContent? Approval, bool Failed, Exception? Error);

    /// <summary>多 Agent 會話 JSON 的 <c>lastCheckpoint</c>。</summary>
    private sealed record HandoffCheckpoint(string SessionId, string CheckpointId);

    /// <summary>存下的多 Agent 會話無法接續（checkpoint 遺失或 JSON 無法還原）。</summary>
    private sealed class HandoffSessionUnrecoverableException(string message, Exception? inner = null) : Exception(message, inner);

    /// <summary>會話 StateBag 裡的待回覆外部核准請求（每筆是 <see cref="WriteApproval"/> 的 JSON）。</summary>
    private sealed class OpenApprovalsState
    {
        public List<string> Requests { get; set; } = [];
    }

    /// <summary>
    /// 多 Agent 模式的發言者與交接偵測（每輪一個實例）：update 裡作者為 X 的 <c>handoff_to_N</c> 呼叫 → 以拓樸表對應接手者，
    /// 發 <see cref="AgentStepKind.Handoff"/> 事件（來源＝程式，白話「分流 Agent → 品質專員：理由」，RawJson <c>{ from, to, fromId, toId, reason }</c>）；
    /// 沒看到交接呼叫卻換了發言者（理論上不會發生）也記一筆、理由留空。下一輪由上一位專員直接接手不算交接。
    /// </summary>
    private sealed class HandoffSpeakers(Func<AgentStepKind, AgentDataSource, string, string?, Task> emit)
    {
        private readonly HashSet<string> handoffCalls = [];
        private string? current;
        private string? expected;

        /// <summary>這一輪最後一段回覆的發言者顯示名稱。</summary>
        public string? LastReplySpeaker { get; set; }

        public async Task ObserveAsync(AgentResponseUpdate update)
        {
            var author = update.AuthorName;
            if (author is null || HandoffTopologyFactory.Find(author) is null)
            {
                return;
            }

            foreach (var call in update.Contents.OfType<FunctionCallContent>())
            {
                if (HandoffTopologyFactory.TargetOf(author, call.Name) is { } target && handoffCalls.Add(call.CallId))
                {
                    current = author;
                    expected = target;
                    await EmitAsync(author, target, AfterSalesAgentGuard.Arg(call.Arguments, "reasonForHandoff"));
                    return;
                }
            }

            if (author == current)
            {
                return;
            }

            if (current is not null && author != expected)
            {
                await EmitAsync(current, author, null);
            }

            current = author;
            expected = null;
        }

        private Task EmitAsync(string from, string to, string? reason)
        {
            var fromName = HandoffTopologyFactory.DisplayNameOf(from) ?? from;
            var toName = HandoffTopologyFactory.DisplayNameOf(to) ?? to;
            if (reason is { Length: > MaxHandoffReasonLength })
            {
                reason = reason[..MaxHandoffReasonLength];
            }

            return emit(
                AgentStepKind.Handoff,
                AgentDataSource.Program,
                reason is null ? $"{fromName} → {toName}" : $"{fromName} → {toName}：{reason}",
                JsonSerializer.Serialize(new { from = fromName, to = toName, fromId = from, toId = to, reason }, RawJsonOptions));
        }
    }

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

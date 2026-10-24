namespace CustomerHelpAgent.Share.Agents;

/// <summary>
/// 工具結果信封的狀態。序列化給模型時一律小寫蛇形（例：<c>not_found</c>、<c>no_policy</c>）。
/// </summary>
public enum ToolOutcomeStatus
{
    Ok,
    NotFound,
    Timeout,
    Forbidden,
    Unavailable,
    NoPolicy,
    Conflict,
    Blocked,
    Invalid,
}

/// <summary>一輪 Agent 執行停下來的原因。</summary>
public enum AgentStopReason
{
    Answered,
    AwaitingUserReply,
    AwaitingConfirmation,
    AwaitingPhotoUpload,
    StepLimitReached,
    ConsecutiveToolFailures,
    Forbidden,
    ServiceUnavailable,
    Timeout,
    PolicyConflict,
    QuotaExceeded,
    ModelError,
    Canceled,

    /// <summary>回放（1.0.5 起）：這次送給模型的內容與錄製時不同（資料或故障開關不一致）；要重置示範資料後重來。</summary>
    ReplayDiverged,

    /// <summary>回放（1.0.5 起）：錄製的模型回應或顧客動作已經播完。</summary>
    ReplayFinished,

    /// <summary>
    /// 回放（1.0.5 起）：顧客的動作與錄製的下一個動作不同（句子、確認／取消、照片張數）。這一輪沒有執行（不呼叫模型、不前進游標），
    /// 改送錄製時的動作就能繼續，與 <see cref="ReplayDiverged"/> 不同。
    /// </summary>
    ReplayActionMismatch,
}

/// <summary>執行軌跡的步驟類型。</summary>
public enum AgentStepKind
{
    UserMessage,
    ModelContext,
    ToolRequest,
    ProgramCheck,
    ToolResult,
    ModelReply,
    ConfirmationShown,
    ConfirmationDecided,
    Stop,

    /// <summary>程式提醒（標紅）：例如模型在查無資料／逾時／規則衝突後直接下結論。來源一律是 <see cref="AgentDataSource.Program"/>。</summary>
    Warning,

    /// <summary>
    /// 一輪的收尾結果（每輪最後一步，來源一律是 <see cref="AgentDataSource.Program"/>）：顧客最後看到的回覆、是否為程式說明、
    /// 停止原因與說明、規則卡、建立的申請編號。重新整理頁面時以它還原對話，不讀會話原文（被固定說明取代的模型文字不會再出現）。
    /// </summary>
    TurnResult,

    /// <summary>
    /// 多 Agent 模式（M5）的交接：發言的 Agent 換人（例如分流 Agent → 品質專員）。來源一律是 <see cref="AgentDataSource.Program"/>，
    /// 白話「分流 Agent → 品質專員：{理由}」；RawJson <c>{ from, to, fromId, toId, reason }</c>（from／to 是顯示名稱，fromId／toId 是參與者 Id）。
    /// </summary>
    Handoff,
}

/// <summary>
/// 售後 Agent 的會話模式（M5）。⚠️ 數值存在 <c>AgentSessionRecord.Mode</c>（M5 之前的會話一律是 <see cref="Single"/>），不可改號。
/// </summary>
public enum AgentMode
{
    /// <summary>單一售後 Agent（M2 起的 <c>ChatClientAgent</c>）。</summary>
    Single = 1,

    /// <summary>多 Agent 交接（MAF Handoff 編排）：分流 Agent → 換退貨／品質／物流專員。</summary>
    Handoff = 2,
}

/// <summary>執行軌跡步驟的資料來源。</summary>
public enum AgentDataSource
{
    Conversation,
    Database,
    Policy,
    Memory,
    Program,
}

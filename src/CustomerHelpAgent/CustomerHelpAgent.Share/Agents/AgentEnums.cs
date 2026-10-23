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

using CustomerHelpAgent.Share.Agents;

namespace CustomerHelpAgent.Models.Agents;

/// <summary>執行軌跡清單的一列（顧客姓名以關掉軟刪除過濾器的明確查詢取得）。</summary>
public sealed record AgentRunSummary(
    int Id,
    Guid SessionKey,
    string CustomerName,
    DateTime StartedAt,
    DateTime? EndedAt,
    AgentStopReason? StopReason,
    int ModelCallCount,
    int ToolCallCount);

/// <summary>執行軌跡的一個步驟（白話層 <see cref="PlainText"/>＋原始層 <see cref="RawJson"/>）。</summary>
public sealed record AgentRunStepView(
    int Sequence,
    AgentStepKind Kind,
    AgentDataSource Source,
    string PlainText,
    string? RawJson,
    DateTime OccurredAt);

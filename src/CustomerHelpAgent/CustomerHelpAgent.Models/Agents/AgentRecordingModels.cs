using CustomerHelpAgent.Share.Demo;

namespace CustomerHelpAgent.Models.Agents;

/// <summary>錄製清單的一列（顧客姓名以關掉軟刪除過濾器的明確查詢取得）。</summary>
public sealed record AgentRecordingSummary(
    int Id,
    Guid SessionKey,
    string Title,
    string CustomerName,
    bool IsGolden,
    string? ScenarioCode,
    string ModelName,
    decimal? TotalCostTwd,
    int TurnCount,
    int CallCount,
    DateTime CreatedAt);

/// <summary>錄製的一個顧客動作。</summary>
public sealed record AgentRecordedTurnView(
    int Sequence,
    AgentRecordedTurnKind Kind,
    string? CustomerText,
    DateTime CreatedAt);

/// <summary>一段錄製與它的顧客動作（依順序）。</summary>
public sealed record AgentRecordingDetail(
    AgentRecordingSummary Recording,
    IReadOnlyList<AgentRecordedTurnView> Turns);

/// <summary>錄製的一次模型呼叫（請求摘要＋回應 JSON），回放依 <see cref="Sequence"/> 取用。</summary>
public sealed record AgentRecordedCallView(
    int Sequence,
    string RequestDigest,
    string ResponseJson,
    string? UsageJson,
    DateTime CreatedAt);

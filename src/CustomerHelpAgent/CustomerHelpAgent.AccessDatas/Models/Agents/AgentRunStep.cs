using CustomerHelpAgent.Share.Agents;

namespace CustomerHelpAgent.AccessDatas.Models;

/// <summary>一輪執行中的一個步驟（白話層 <see cref="PlainText"/>＋原始層 <see cref="RawJson"/>）。不含地址、手機。</summary>
public class AgentRunStep
{
    public int Id { get; set; }
    public int AgentRunTraceId { get; set; }
    public AgentRunTrace? AgentRunTrace { get; set; }

    /// <summary>同一輪內的順序（由 1 起遞增）。</summary>
    public int Sequence { get; set; }

    public AgentStepKind Kind { get; set; }
    public AgentDataSource Source { get; set; }
    public string PlainText { get; set; } = string.Empty;
    public string? RawJson { get; set; }
    public DateTime OccurredAt { get; set; }
}

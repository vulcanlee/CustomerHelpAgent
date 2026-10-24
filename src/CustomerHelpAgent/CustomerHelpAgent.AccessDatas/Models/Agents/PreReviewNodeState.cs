using CustomerHelpAgent.Share.Agents;

namespace CustomerHelpAgent.AccessDatas.Models;

/// <summary>初審一輪中單一節點的狀態（畫面顯示進度用）。</summary>
public class PreReviewNodeState
{
    public int Id { get; set; }
    public int PreReviewRunId { get; set; }
    public PreReviewRun? PreReviewRun { get; set; }
    public PreReviewNode Node { get; set; }
    public NodeStatus Status { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public string? Note { get; set; }
}

using CustomerHelpAgent.Share.Agents;

namespace CustomerHelpAgent.AccessDatas.Models;

/// <summary>
/// AI 初審的一輪執行（M4）：一筆售後申請可有多輪（要求補件後重新送件為新一輪，<see cref="Attempt"/> 遞增）。
/// <see cref="WorkflowSessionId"/> 同時是 Workflow checkpoint 的 SessionId。
/// </summary>
public class PreReviewRun
{
    public int Id { get; set; }
    public int AfterSalesRequestId { get; set; }
    public AfterSalesRequest? AfterSalesRequest { get; set; }

    /// <summary>第幾輪（由 1 起）；與 <see cref="AfterSalesRequestId"/> 組成唯一索引。</summary>
    public int Attempt { get; set; }

    /// <summary>案件關聯鍵，複製自申請的 <c>CaseKey</c>；背景模型呼叫以此作 ConversationId。</summary>
    public Guid CaseKey { get; set; }

    /// <summary>Workflow 會話識別（<c>prereview-{requestId}-{attempt}</c>，唯一）。</summary>
    public string WorkflowSessionId { get; set; } = string.Empty;

    public PreReviewStatus Status { get; set; }

    public string? PhotoJson { get; set; }
    public string? RuleJson { get; set; }
    public string? HistoryJson { get; set; }
    public string? SummaryJson { get; set; }

    /// <summary>摘要結論（建議），供佇列頁顯示。</summary>
    public string? Recommendation { get; set; }
    public bool HasWarning { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>回放：這一輪用哪一份錄製重播模型回應；刻意不設外鍵。即時執行為 null。</summary>
    public int? ReplayRecordingId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public List<PreReviewNodeState> Nodes { get; set; } = [];
}

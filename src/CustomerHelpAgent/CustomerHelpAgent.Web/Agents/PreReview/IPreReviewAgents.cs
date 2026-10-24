namespace CustomerHelpAgent.Web.Agents.PreReview;

// 初審 Workflow 的三個 AI 節點（照片、歷史紀錄、摘要）。實作只讀、不寫入；
// 例外直接往外拋即可，由 executor 吞下並改為 Failed 結論（摘要改用程式產生的固定摘要），Workflow 一定會走到人工決策。

/// <summary>照片初審：看照片與顧客描述是否一致。只有申請附了照片才會被呼叫。</summary>
public interface IPhotoReviewer
{
    Task<PreReviewFinding> ReviewAsync(PreReviewCase reviewCase, CancellationToken cancellationToken);
}

/// <summary>歷史紀錄初審：此顧客過去的申請與顧客端 Agent 的對話重點。</summary>
public interface IHistoryReviewer
{
    Task<PreReviewFinding> ReviewAsync(PreReviewCase reviewCase, CancellationToken cancellationToken);
}

/// <summary>摘要：讀三個初審結論，給客服建議。建議核准但規則核對不符資格時，executor 會改為需人工判斷。</summary>
public interface IPreReviewSummarizer
{
    Task<PreReviewSummary> SummarizeAsync(PreReviewCase reviewCase, IReadOnlyList<PreReviewFinding> findings, CancellationToken cancellationToken);
}

using CustomerHelpAgent.Share.Agents;

namespace CustomerHelpAgent.Models.Agents;

/// <summary>
/// 審核佇列「AI 初審」欄需要的最新一輪初審（<c>PreReviewRunService.GetLatestByRequestIdsAsync</c>）：
/// 只有狀態、建議與警示旗標，不載入四個節點結論 JSON。
/// </summary>
public sealed record PreReviewQueueStatus(PreReviewStatus Status, string? Recommendation, bool HasWarning);

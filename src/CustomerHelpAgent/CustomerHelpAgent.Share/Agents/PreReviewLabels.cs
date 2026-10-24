namespace CustomerHelpAgent.Share.Agents;

/// <summary>
/// AI 初審的中文標籤（審核頁的流程圖、AI 建議卡與佇列標籤共用，避免各檔各寫一份而漂移）。
/// 建議值（approve／reject／need_more_info／manual）與 Web 專案的 <c>PreReviewRecommendations</c> 一致；Share 不相依 Web，所以在這裡重複字面值。
/// </summary>
public static class PreReviewLabels
{
    /// <summary>節點由誰執行：AI、程式或人（流程圖每格的徽章）。</summary>
    public const string ActorAi = "AI";
    public const string ActorProgram = "程式";
    public const string ActorHuman = "人";

    public static string NodeText(PreReviewNode node) => node switch
    {
        PreReviewNode.Intake => "進件",
        PreReviewNode.PhotoReview => "照片初審",
        PreReviewNode.RuleCheck => "規則核對",
        PreReviewNode.HistoryReview => "歷史紀錄",
        PreReviewNode.Summary => "摘要",
        PreReviewNode.StaffDecision => "人工決策",
        PreReviewNode.Execute => "執行",
        PreReviewNode.Notify => "通知",
        _ => node.ToString(),
    };

    public static string NodeActor(PreReviewNode node) => node switch
    {
        PreReviewNode.PhotoReview or PreReviewNode.HistoryReview or PreReviewNode.Summary => ActorAi,
        PreReviewNode.StaffDecision => ActorHuman,
        _ => ActorProgram,
    };

    public static string NodeStatusText(NodeStatus status) => status switch
    {
        NodeStatus.Waiting => "等待中",
        NodeStatus.Running => "進行中",
        NodeStatus.Done => "完成",
        NodeStatus.Failed => "失敗",
        NodeStatus.Skipped => "略過",
        _ => status.ToString(),
    };

    public static string RecommendationText(string? recommendation) => recommendation switch
    {
        "approve" => "建議核准",
        "reject" => "建議駁回",
        "need_more_info" => "建議補件",
        _ => "需人工判斷",
    };

    /// <summary>佇列「AI 初審」欄的標籤：初審中、建議核准／駁回／補件、需人工判斷、初審失敗、已由人工處理。</summary>
    public static string QueueTagText(PreReviewStatus status, string? recommendation) => status switch
    {
        PreReviewStatus.Pending or PreReviewStatus.Running => "初審中",
        PreReviewStatus.AwaitingDecision => RecommendationText(recommendation),
        PreReviewStatus.Failed => "初審失敗",
        _ => "已由人工處理",
    };
}

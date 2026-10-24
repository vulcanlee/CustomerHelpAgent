using CustomerHelpAgent.Share.Agents;
using CustomerHelpAgent.Web.Agents.PreReview;
using CustomerHelpAgent.Web.Components.Commons;

namespace CustomerHelpAgent.Web.Components.Views.AfterSales;

/// <summary>AI 初審標籤的語氣色（佇列欄與 AI 建議卡共用）。文字在 <see cref="PreReviewLabels"/>。</summary>
internal static class PreReviewTones
{
    public static StatusTone OfRecommendation(string? recommendation) => recommendation switch
    {
        PreReviewRecommendations.Approve => StatusTone.Positive,
        PreReviewRecommendations.Reject => StatusTone.Muted,
        _ => StatusTone.Warning,
    };

    public static StatusTone OfQueueTag(PreReviewStatus status, string? recommendation) => status switch
    {
        PreReviewStatus.Pending or PreReviewStatus.Running => StatusTone.Accent,
        PreReviewStatus.AwaitingDecision => OfRecommendation(recommendation),
        PreReviewStatus.Failed => StatusTone.Warning,
        _ => StatusTone.Neutral,
    };
}

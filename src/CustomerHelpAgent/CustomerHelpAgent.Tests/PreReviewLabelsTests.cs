using CustomerHelpAgent.Share.Agents;
using CustomerHelpAgent.Web.Agents.PreReview;

namespace CustomerHelpAgent.Tests;

/// <summary>Share 的 <see cref="PreReviewLabels"/> 重複了 Web 的建議字面值（Share 不相依 Web）；這裡守住兩邊不漂移。</summary>
public sealed class PreReviewLabelsTests
{
    [Fact]
    public void RecommendationText_CoversEveryRecommendation_WithoutDrift()
    {
        Assert.Equal("建議核准", PreReviewLabels.RecommendationText(PreReviewRecommendations.Approve));
        Assert.Equal("建議駁回", PreReviewLabels.RecommendationText(PreReviewRecommendations.Reject));
        Assert.Equal("建議補件", PreReviewLabels.RecommendationText(PreReviewRecommendations.NeedMoreInfo));
        Assert.Equal("需人工判斷", PreReviewLabels.RecommendationText(PreReviewRecommendations.Manual));
        Assert.Equal(PreReviewRecommendations.All.Count, PreReviewRecommendations.All.Select(PreReviewLabels.RecommendationText).Distinct().Count());
    }
}

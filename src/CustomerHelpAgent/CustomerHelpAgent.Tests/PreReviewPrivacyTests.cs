using CustomerHelpAgent.Web.Agents.PreReview;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 送模型前的個資樣式遮蔽（AI 初審、客服 AI 助手、交接摘要共用 <see cref="PreReviewPrivacy.Redact"/>）：
/// 顧客自己打出的電話不論本地格式或 +886 國際格式都要遮蔽。
/// </summary>
public sealed class PreReviewPrivacyTests
{
    [Theory]
    [InlineData("請打 0912-345-678 找我")]
    [InlineData("請打 +886-912-345-678 找我")]
    [InlineData("請打 +886 912 345 678 找我")]
    [InlineData("請打 +886912345678 找我")]
    [InlineData("請打 886-912-345-678 找我")]
    [InlineData("請打 +886-2-2345-6789 找我")]
    [InlineData("請打 +886 (2) 2345 6789 找我")]
    public void Redact_MasksLocalAndInternationalPhones(string text)
    {
        var redacted = PreReviewPrivacy.Redact(text, [], []);

        Assert.Equal($"請打 {PreReviewPrivacy.Masked} 找我", redacted);
    }
}

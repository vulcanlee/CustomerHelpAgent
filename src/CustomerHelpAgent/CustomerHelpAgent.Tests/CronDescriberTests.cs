using CustomerHelpAgent.Web.Scheduling;

namespace CustomerHelpAgent.Tests;

/// <summary>「資料保留與清理」頁把 cron 轉成白話（0.9.133 起）；認不得的寫法原樣顯示，不硬翻。</summary>
public sealed class CronDescriberTests
{
    [Theory]
    [InlineData("0 3 * * *", "每天 03:00")]
    [InlineData("30 2 * * *", "每天 02:30")]
    [InlineData("0 8 * * 1", "每週一 08:00")]
    [InlineData("0 8 * * 0", "每週日 08:00")]
    [InlineData("0 8 * * 7", "每週日 08:00")]
    [InlineData("15 4 1 * *", "每月 1 日 04:15")]
    [InlineData("5 * * * *", "每小時第 5 分")]
    public void Describe_ShouldTranslateCommonSchedules(string cron, string expected)
        => Assert.Equal(expected, CronDescriber.Describe(cron));

    [Theory]
    [InlineData("*/5 * * * *")]
    [InlineData("0 3 * * 1-5")]
    [InlineData("0 3 1 1 *")]
    [InlineData("0 3 * *")]
    [InlineData("0 25 * * *")]
    public void Describe_ShouldKeepUnknownPatternsAsIs(string cron)
        => Assert.Equal(cron, CronDescriber.Describe(cron));
}

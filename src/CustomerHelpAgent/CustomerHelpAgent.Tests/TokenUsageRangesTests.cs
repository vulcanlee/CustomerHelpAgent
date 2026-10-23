using CustomerHelpAgent.Models.Systems;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 「最近 N 天」的定義：<b>含今天往前數 N 日</b>。
///
/// 這幾個數字看起來瑣碎，但摘要卡的標題（「近 7 天」）與它實際查出來的區間
/// 必須是同一件事。這裡釘死之後，畫面與趨勢都只能走 <see cref="TokenUsageRanges"/>，
/// 不會出現「卡片說近 7 天、趨勢畫了 8 格」。
/// </summary>
public sealed class TokenUsageRangesTests
{
    private static readonly DateTime Today = new(2026, 9, 23);

    [Fact]
    public void Recent_OneDay_ShouldBeTodayOnly()
    {
        var (start, end) = TokenUsageRanges.Recent(Today, TokenUsageRanges.RecentDay);

        Assert.Equal(Today, start);
        Assert.Equal(Today, end);
    }

    [Theory]
    [InlineData(TokenUsageRanges.RecentWeek, -6)]
    [InlineData(TokenUsageRanges.RecentMonth, -29)]
    public void Recent_ShouldCountTodayAsTheFirstDay(int days, int expectedStartOffset)
    {
        var (start, end) = TokenUsageRanges.Recent(Today, days);

        Assert.Equal(Today.AddDays(expectedStartOffset), start);
        Assert.Equal(Today, end);
        // 含頭含尾，所以天數正好是 days，不是 days + 1。
        Assert.Equal(days, (end - start).Days + 1);
    }

    [Fact]
    public void Recent_ShouldIgnoreTimeOfDay()
    {
        // 呼叫端傳 DateTime.Now 也要得到乾淨的日期，否則起訖日會帶著時分秒進篩選器。
        var (start, end) = TokenUsageRanges.Recent(Today.AddHours(17).AddMinutes(42), TokenUsageRanges.RecentWeek);

        Assert.Equal(Today.AddDays(-6), start);
        Assert.Equal(Today, end);
    }
}

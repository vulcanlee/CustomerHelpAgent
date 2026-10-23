using System.Globalization;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Web.Ai;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 「趨勢圖」的計算：分桶、Y 軸、座標字串。畫面的 SVG 與 PDF 折線圖共用這一份，
/// 這裡釘住之後兩邊畫出來的點與刻度必然一致。
/// </summary>
public sealed class TokenUsageTrendChartTests
{
    [Fact]
    public void Bucket_ShouldKeepOnePointPerDay_UpTo92Days()
    {
        var points = TokenUsageTrendChart.Bucket(Days(new DateTime(2026, 6, 3), 92));

        Assert.Equal(TokenUsageTrendGrain.Day, TokenUsageTrendChart.GrainFor(92));
        Assert.Equal(92, points.Count);
        Assert.All(points, x => Assert.Equal(x.Start, x.End));
    }

    /// <summary>
    /// ⭐ 超過 92 天改按週，週一起算；頭尾不滿一週的桶子寫出<b>實際</b>涵蓋的日期。
    /// 2026-06-03 是週三、93 天後的最後一天 2026-09-03 是週四。
    /// </summary>
    [Fact]
    public void Bucket_ShouldGroupByMondayWeek_Over92Days()
    {
        var days = Days(new DateTime(2026, 6, 3), 93);
        var points = TokenUsageTrendChart.Bucket(days);

        Assert.Equal(TokenUsageTrendGrain.Week, TokenUsageTrendChart.GrainFor(93));
        // 頭 5 天（週三～週日）＋ 12 個整週 ＋ 尾 4 天（週一～週四）。
        Assert.Equal(14, points.Count);

        Assert.Equal(new DateTime(2026, 6, 3), points[0].Start);
        Assert.Equal(new DateTime(2026, 6, 7), points[0].End);
        Assert.Equal(new DateTime(2026, 6, 8), points[1].Start);
        Assert.Equal(DayOfWeek.Monday, points[1].Start.DayOfWeek);
        Assert.Equal(new DateTime(2026, 8, 31), points[^1].Start);
        Assert.Equal(new DateTime(2026, 9, 3), points[^1].End);
    }

    /// <summary>分桶不能漏也不能重複算：各欄位的總和與逐日相同。</summary>
    [Fact]
    public void Bucket_ShouldPreserveTotals_WhenGroupedByWeek()
    {
        var days = Days(new DateTime(2026, 6, 3), 120);
        var points = TokenUsageTrendChart.Bucket(days);

        Assert.Equal(days.Sum(x => x.TotalCount), points.Sum(x => x.TotalCount));
        Assert.Equal(days.Sum(x => x.CostTwd), points.Sum(x => x.CostTwd), 6);
        Assert.Equal(days.Sum(x => x.CostUsd), points.Sum(x => x.CostUsd), 6);
        Assert.Equal(days.Sum(x => x.UnpricedCount), points.Sum(x => x.UnpricedCount));
        Assert.Equal(days.Sum(x => x.CallCount), points.Sum(x => x.CallCount));
    }

    [Fact]
    public void Bucket_ShouldReturnEmpty_WhenNoDays()
        => Assert.Empty(TokenUsageTrendChart.Bucket([]));

    [Theory]
    [InlineData(2026, 9, 7, 2026, 9, 7)]   // 週一
    [InlineData(2026, 9, 6, 2026, 8, 31)]  // 週日 → 前一個週一
    [InlineData(2026, 9, 23, 2026, 9, 21)] // 週三
    public void WeekStart_ShouldBeMonday(int y, int m, int d, int ey, int em, int ed)
        => Assert.Equal(new DateTime(ey, em, ed), TokenUsageTrendChart.WeekStart(new DateTime(y, m, d)));

    /// <summary>Y 軸上限要整齊（1／2／5×10ⁿ 的倍數）、不小於資料最大值，且刻度剛好落在頂端。</summary>
    [Theory]
    [InlineData(38.888, 40, 10)]
    [InlineData(1234567, 1500000, 500000)]
    [InlineData(0.0042, 0.006, 0.002)]
    [InlineData(100, 100, 50)]
    public void Axis_ShouldRoundUpToNiceStep(double max, double expectedMax, double expectedStep)
    {
        var axis = TokenUsageTrendChart.Axis(max);

        Assert.Equal(expectedMax, axis.Max, 9);
        Assert.Equal(expectedStep, axis.Step, 9);
        Assert.True(axis.Max >= max);
        Assert.Equal(axis.Max, axis.Ticks().Last(), 9);
    }

    /// <summary>⭐ 整段期間沒花錢（或全部未定價）時最大值是 0，Y 軸仍要能畫、不可除以零。</summary>
    [Fact]
    public void Axis_ShouldFallBackToUnit_WhenMaxIsZero()
    {
        var axis = TokenUsageTrendChart.Axis(0);

        Assert.Equal(1, axis.Max);
        Assert.Equal(100, TokenUsageTrendChart.Y(0, axis.Max, 0, 100));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(92)]
    [InlineData(27)]
    public void LabelStep_ShouldKeepLabelsWithinLimit(int count)
    {
        var step = TokenUsageTrendChart.LabelStep(count);
        var labels = (int)Math.Ceiling(count / (double)step);

        Assert.True(labels <= TokenUsageTrendChart.MaxAxisLabels);
    }

    [Fact]
    public void X_ShouldCenterSinglePoint_AndSpanWidthOtherwise()
    {
        Assert.Equal(60, TokenUsageTrendChart.X(0, 1, 10, 100));
        Assert.Equal(10, TokenUsageTrendChart.X(0, 5, 10, 100));
        Assert.Equal(110, TokenUsageTrendChart.X(4, 5, 10, 100));
    }

    /// <summary>
    /// ⭐ 以逗號為小數點的地區，座標字串仍然要用點 ——
    /// 否則 <c>points="12,5 30,7"</c> 解析失敗，整條線靜默消失。
    /// </summary>
    [Fact]
    public void Polyline_ShouldUseInvariantDecimalPoint_UnderCommaCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            var points = TokenUsageTrendChart.Polyline([0.5, 1.0, 0.25], 1, 10, 0, 3, 3);

            Assert.Equal("10,1.5 11.5,0 13,2.25", points);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void PeriodLabel_ShouldShowRange_OnlyForMultiDayBuckets()
    {
        var day = new TokenUsageTrendPoint(new DateTime(2026, 9, 7), new DateTime(2026, 9, 7), 0, 0, 0, 0, 0);
        var week = day with { End = new DateTime(2026, 9, 13) };

        Assert.StartsWith("2026-09-07", TokenUsageTrendChart.PeriodLabel(day));
        Assert.DoesNotContain("～", TokenUsageTrendChart.PeriodLabel(day));
        Assert.Equal("2026-09-07 ～ 2026-09-13", TokenUsageTrendChart.PeriodLabel(week));
    }

    private static List<TokenUsageDailyRow> Days(DateTime start, int count) =>
        [.. Enumerable.Range(0, count).Select(index => new TokenUsageDailyRow
        {
            Date = start.AddDays(index),
            TotalCount = 1000 + index,
            CostTwd = 1.5 * (index % 4),
            CostUsd = 1.5 * (index % 4) / 31.5,
            UnpricedCount = index % 7 == 0 ? 1 : 0,
            CallCount = index % 3,
        })];
}

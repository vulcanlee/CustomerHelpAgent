using System.Globalization;
using CustomerHelpAgent.Web.Benefit;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 效益對照頁的長條圖座標（<see cref="BenefitBarChart"/>）與數字標籤（<see cref="BenefitMetricDisplay"/>，M6）。
/// </summary>
public sealed class BenefitBarChartTests
{
    [Fact]
    public void BarChart_ScalesAndLabels()
    {
        // 9、2、無資料：軸上限取整齊的 10，長條高度依比例，缺資料的那根不畫。
        var values = new double?[] { 9, 2, null };
        var axisMax = BenefitBarChart.AxisMax(values);
        Assert.Equal(10, axisMax);

        var bars = BenefitBarChart.Layout(values, axisMax);
        Assert.Equal(3, bars.Count);
        Assert.Equal(BenefitBarChart.PlotHeight * 0.9, bars[0].Height, 6);
        Assert.Equal(BenefitBarChart.PlotHeight * 0.2, bars[1].Height, 6);
        Assert.Equal(BenefitBarChart.Baseline - bars[0].Height, bars[0].Y, 6);
        Assert.False(bars[2].HasValue);
        Assert.Equal(string.Empty, BenefitBarChart.BarPath(bars[2]));

        // 每根置中在自己的格子裡、由左到右、不超過最大寬度、不互相重疊。
        Assert.True(bars[0].CenterX < bars[1].CenterX && bars[1].CenterX < bars[2].CenterX);
        Assert.All(bars, x => Assert.True(x.Width <= BenefitBarChart.MaxBarWidth));
        Assert.True(bars[0].X + bars[0].Width < bars[1].X);
        Assert.Equal((BenefitBarChart.PlotLeft + BenefitBarChart.PlotRight) / 2, bars[1].CenterX, 6);

        // 底部貼齊基準線；座標字串不受地區設定影響（逗號小數點的地區也一樣）。
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var path = BenefitBarChart.BarPath(bars[0]);
            Assert.StartsWith($"M{BenefitBarChart.Num(bars[0].X)},{BenefitBarChart.Num(BenefitBarChart.Baseline)} ", path, StringComparison.Ordinal);
            Assert.EndsWith($"V{BenefitBarChart.Num(BenefitBarChart.Baseline)} Z", path, StringComparison.Ordinal);
            Assert.DoesNotContain("NaN", path, StringComparison.Ordinal);
            Assert.Equal("12.5", BenefitBarChart.Num(12.5));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }

        // 全部是 0 或沒有值：軸上限 1，不除以零；0 的長條有值但不畫。
        var zeros = BenefitBarChart.Layout([0d, null], BenefitBarChart.AxisMax([0d, null]));
        Assert.True(zeros[0].HasValue);
        Assert.Equal(0, zeros[0].Height);
        Assert.Equal(string.Empty, BenefitBarChart.BarPath(zeros[0]));

        // 標籤：次數最多一位小數、金額 NT$ 兩位小數、月度金額千分位、沒有值寫「—」。
        Assert.Equal("2", BenefitMetricDisplay.Format(2, isCurrency: false));
        Assert.Equal("2.5", BenefitMetricDisplay.Format(2.5, isCurrency: false));
        Assert.Equal("NT$0.50", BenefitMetricDisplay.Format(0.5, isCurrency: true));
        Assert.Equal("—", BenefitMetricDisplay.Format(null, isCurrency: true));
        Assert.Equal("NT$247,000", BenefitMetricDisplay.FormatMonthlyTwd(247000));
        Assert.Equal("−NT$1,200", BenefitMetricDisplay.FormatMonthlyTwd(-1200));

        // 先四捨五入再決定正負號：趨近 0 的負數不寫成「−NT$0」「−0」。
        Assert.Equal("NT$0", BenefitMetricDisplay.FormatMonthlyTwd(-0.4));
        Assert.Equal("0", BenefitMetricDisplay.FormatMonthly(-0.04));
        Assert.Equal("−0.1", BenefitMetricDisplay.FormatMonthly(-0.06));
    }

    [Fact]
    public void Delta_PercentWithText_NotColourAlone()
    {
        var better = BenefitMetricDisplay.Delta(9, 2, BenefitDirection.LowerIsBetter);
        Assert.Equal("−78%，改善", better!.Value.Text);
        Assert.Equal("better", better.Value.Tone);

        var worse = BenefitMetricDisplay.Delta(4, 5, BenefitDirection.LowerIsBetter);
        Assert.Equal("+25%，變差", worse!.Value.Text);
        Assert.Equal("worse", worse.Value.Tone);

        Assert.Equal("+50%", BenefitMetricDisplay.Delta(2, 3, BenefitDirection.Neutral)!.Value.Text);
        Assert.Equal("持平", BenefitMetricDisplay.Delta(3, 3, BenefitDirection.LowerIsBetter)!.Value.Text);

        // 傳統是 0（例如不呼叫 AI）或沒有值：無法算百分比。
        Assert.Null(BenefitMetricDisplay.Delta(0, 0.5, BenefitDirection.Neutral));
        Assert.Null(BenefitMetricDisplay.Delta(null, 2, BenefitDirection.LowerIsBetter));
        Assert.Null(BenefitMetricDisplay.Delta(4, null, BenefitDirection.LowerIsBetter));
    }
}

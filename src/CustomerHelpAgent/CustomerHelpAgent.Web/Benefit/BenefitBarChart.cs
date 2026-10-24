using CustomerHelpAgent.Web.Ai;

namespace CustomerHelpAgent.Web.Benefit;

/// <summary>
/// 一根長條的座標（SVG 使用者座標）。<see cref="HasValue"/> 為 false 代表這個流程沒有資料：不畫長條，改寫「無資料」。
/// </summary>
public readonly record struct BenefitBarGeometry(double X, double Y, double Width, double Height, double CenterX, bool HasValue);

/// <summary>
/// 效益對照頁的長條圖（M6）：每個指標一張小圖，三根長條依序是傳統、單一 Agent、多 Agent。
/// 手寫 SVG、不引入圖表套件（同 <see cref="TokenUsageTrendChart"/> 的作法）；這裡只算座標，顏色在檢視的 scoped CSS。
///
/// 每張圖各自一條 Y 軸（從 0 起算），不同指標的單位不同，不可以畫在同一張圖上比較高低。
/// </summary>
public static class BenefitBarChart
{
    public const double ChartWidth = 260;
    public const double ChartHeight = 170;
    public const double PlotLeft = 12;
    public const double PlotRight = 248;

    /// <summary>長條頂端上方要留給數值標籤的空間。</summary>
    public const double PlotTop = 24;

    /// <summary>長條的底部（基準線）。</summary>
    public const double Baseline = 130;

    /// <summary>流程名稱標籤的基線。</summary>
    public const double CategoryLabelY = 152;

    public const double MaxBarWidth = 44;

    /// <summary>長條頂端的圓角；底部貼齊基準線、不做圓角。</summary>
    public const double CornerRadius = 4;

    public static double PlotHeight => Baseline - PlotTop;

    /// <summary>
    /// Y 軸上限：取有值且大於 0 的最大值，再取整齊的刻度上限（1／2／5×10ⁿ，同 <see cref="TokenUsageTrendChart.Axis"/>）。
    /// 全部沒有值或都是 0 時回 1，呼叫端不必另外擋除以零。
    /// </summary>
    public static double AxisMax(IEnumerable<double?> values)
    {
        var max = values
            .Where(x => x is { } value && double.IsFinite(value) && value > 0)
            .Select(x => x!.Value)
            .DefaultIfEmpty(0)
            .Max();
        return TokenUsageTrendChart.Axis(max).Max;
    }

    /// <summary>依序排出每根長條：每根各佔一格、置中，寬度不超過 <see cref="MaxBarWidth"/>。負值當 0。</summary>
    public static IReadOnlyList<BenefitBarGeometry> Layout(IReadOnlyList<double?> values, double axisMax)
    {
        if (values.Count == 0)
        {
            return [];
        }

        var slot = (PlotRight - PlotLeft) / values.Count;
        var width = Math.Min(MaxBarWidth, slot * 0.6);
        var result = new List<BenefitBarGeometry>(values.Count);
        for (var index = 0; index < values.Count; index++)
        {
            var center = PlotLeft + slot * (index + 0.5);
            var value = values[index];
            var hasValue = value is { } number && double.IsFinite(number);
            var height = hasValue && axisMax > 0 ? Math.Max(0, value!.Value) / axisMax * PlotHeight : 0;
            result.Add(new BenefitBarGeometry(center - width / 2, Baseline - height, width, height, center, hasValue));
        }

        return result;
    }

    /// <summary>
    /// 長條的 SVG path：底部貼齊基準線、頂端兩角圓角。沒有值或高度為 0 時回空字串（不畫）。
    /// 長條比圓角還矮時，圓角縮成長條高度，不會畫到基準線下面。
    /// </summary>
    public static string BarPath(BenefitBarGeometry bar)
    {
        if (bar.HasValue == false || bar.Height <= 0)
        {
            return string.Empty;
        }

        var radius = Math.Min(CornerRadius, Math.Min(bar.Height, bar.Width / 2));
        var left = bar.X;
        var right = bar.X + bar.Width;
        var top = bar.Y;
        return $"M{Num(left)},{Num(Baseline)} V{Num(top + radius)} Q{Num(left)},{Num(top)} {Num(left + radius)},{Num(top)} "
            + $"H{Num(right - radius)} Q{Num(right)},{Num(top)} {Num(right)},{Num(top + radius)} V{Num(Baseline)} Z";
    }

    /// <summary>SVG 座標字串；一律不受地區設定影響（見 <see cref="TokenUsageTrendChart.Num"/>）。</summary>
    public static string Num(double value) => TokenUsageTrendChart.Num(value);
}

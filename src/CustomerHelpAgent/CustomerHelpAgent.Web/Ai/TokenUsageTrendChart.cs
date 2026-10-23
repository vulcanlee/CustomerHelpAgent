using System.Globalization;
using CustomerHelpAgent.Models.Systems;

namespace CustomerHelpAgent.Web.Ai;

/// <summary>趨勢圖上一個點代表多長的時間。</summary>
public enum TokenUsageTrendGrain
{
    Day,
    Week,
}

/// <summary>
/// 趨勢圖上的一個點。按週加總時 <see cref="Start"/>／<see cref="End"/> 是這個點<b>實際涵蓋</b>的日期 ——
/// 區間頭尾不滿一週的桶子不會假裝成完整的一週。
/// </summary>
public sealed record TokenUsageTrendPoint(
    DateTime Start,
    DateTime End,
    long TotalCount,
    double CostTwd,
    double CostUsd,
    int UnpricedCount,
    int CallCount);

/// <summary>Y 軸：上限與刻度間距。上限一定是間距的整數倍，刻度才會剛好落在頂端。</summary>
public readonly record struct TokenUsageTrendAxis(double Max, double Step)
{
    public IEnumerable<double> Ticks()
    {
        var count = (int)Math.Round(Max / Step);
        for (var index = 0; index <= count; index++)
        {
            yield return index * Step;
        }
    }
}

/// <summary>
/// 「趨勢圖」頁籤與 PDF 折線圖共用的計算：分桶、Y 軸刻度、座標換算。
/// 兩邊走同一份程式，畫出來的點與刻度才必然一致。
///
/// ⚠️ 輸入是畫面上的 <c>dailyRows</c>：已補零、由舊到新、最多 180 天。這裡不再補零也不再排序。
/// </summary>
public static class TokenUsageTrendChart
{
    /// <summary>不超過這麼多天就一天一點（約三個月）；超過改按週加總。</summary>
    public const int MaxDailyPoints = 92;

    /// <summary>X 軸最多印幾個日期標籤。點多時每隔幾個點才印一個，否則字會疊在一起。</summary>
    public const int MaxAxisLabels = 8;

    public static TokenUsageTrendGrain GrainFor(int dayCount)
        => dayCount <= MaxDailyPoints ? TokenUsageTrendGrain.Day : TokenUsageTrendGrain.Week;

    public static string DescribeGrain(TokenUsageTrendGrain grain)
        => grain == TokenUsageTrendGrain.Day ? "每點 = 1 天" : "每點 = 1 週（週一起算）";

    public static List<TokenUsageTrendPoint> Bucket(IReadOnlyList<TokenUsageDailyRow> days)
    {
        if (GrainFor(days.Count) == TokenUsageTrendGrain.Day)
        {
            return [.. days.Select(x => new TokenUsageTrendPoint(
                x.Date, x.Date, x.TotalCount, x.CostTwd, x.CostUsd, x.UnpricedCount, x.CallCount))];
        }

        return [.. days
            .GroupBy(x => WeekStart(x.Date))
            .Select(week => new TokenUsageTrendPoint(
                week.Min(x => x.Date),
                week.Max(x => x.Date),
                week.Sum(x => x.TotalCount),
                week.Sum(x => x.CostTwd),
                week.Sum(x => x.CostUsd),
                week.Sum(x => x.UnpricedCount),
                week.Sum(x => x.CallCount)))];
    }

    /// <summary>該日所在那一週的週一。</summary>
    public static DateTime WeekStart(DateTime date)
    {
        // DayOfWeek 的週日是 0；往回退到週一要退 (6, 0, 1, 2, 3, 4, 5) 天。
        var offset = ((int)date.DayOfWeek + 6) % 7;
        return date.Date.AddDays(-offset);
    }

    /// <summary>
    /// 依資料最大值算出整齊的 Y 軸：間距取 1／2／5×10ⁿ，大約分成 4 格。
    /// 最大值 ≤ 0（整段期間沒花錢，或全部未定價）時回傳 0～1，呼叫端不必另外擋除以零。
    /// </summary>
    public static TokenUsageTrendAxis Axis(double max)
    {
        if (max <= 0 || double.IsFinite(max) == false)
        {
            return new TokenUsageTrendAxis(1, 0.25);
        }

        var rough = max / 4;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(rough)));
        var normalized = rough / magnitude;
        var step = (normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10) * magnitude;

        return new TokenUsageTrendAxis(Math.Ceiling(max / step - 1e-9) * step, step);
    }

    /// <summary>X 軸每隔幾個點印一個標籤，讓標籤數不超過 <see cref="MaxAxisLabels"/>。</summary>
    public static int LabelStep(int pointCount)
        => Math.Max(1, (int)Math.Ceiling(pointCount / (double)MaxAxisLabels));

    /// <summary>第 <paramref name="index"/> 個點的 X。只有一個點時放在正中間。</summary>
    public static double X(int index, int count, double left, double width)
        => count <= 1 ? left + width / 2 : left + width * index / (count - 1);

    /// <summary>值換成 Y（向下為正）。</summary>
    public static double Y(double value, double axisMax, double top, double height)
        => top + height - value / axisMax * height;

    /// <summary>
    /// SVG 座標用的數字字串。
    ///
    /// ⚠️ 一定要 <see cref="CultureInfo.InvariantCulture"/> —— 以逗號為小數點的地區，
    /// <c>points="12,5 30,7"</c> 會變成無法解析的字串，整條線消失而且不會有任何錯誤訊息。
    /// </summary>
    public static string Num(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>SVG <c>&lt;polyline points&gt;</c> 的內容。</summary>
    public static string Polyline(
        IReadOnlyList<double> values, double axisMax, double left, double top, double width, double height)
        => string.Join(' ', values.Select((value, index) =>
            $"{Num(X(index, values.Count, left, width))},{Num(Y(value, axisMax, top, height))}"));

    /// <summary>X 軸標籤：按日印月／日，按週印週的起始日。</summary>
    public static string AxisLabel(TokenUsageTrendPoint point) => point.Start.ToString("MM/dd");

    /// <summary>滑鼠提示與報表共用的期間字串。按週且頭尾不同天時寫出起訖。</summary>
    public static string PeriodLabel(TokenUsageTrendPoint point)
        => point.Start == point.End
            ? point.Start.ToString("yyyy-MM-dd (ddd)")
            : $"{point.Start:yyyy-MM-dd} ～ {point.End:yyyy-MM-dd}";
}

using System.Globalization;

namespace CustomerHelpAgent.Web.Diagnostics;

/// <summary>
/// Token 數量的顯示格式。
///
/// 大數字以 K／M 精簡呈現（15.67M、867.33K），否則統計頁上一排 8 位數會完全讀不出量級差異。
/// </summary>
public static class TokenUsageFormat
{
    private const long Million = 1_000_000;
    private const long Thousand = 1_000;

    /// <summary>精簡格式：≥1M 顯示 M、≥1K 顯示 K，其餘顯示原數字。</summary>
    public static string Compact(long value)
    {
        if (value >= Million)
        {
            return (value / (double)Million).ToString("0.##", CultureInfo.InvariantCulture) + "M";
        }

        if (value >= Thousand)
        {
            return (value / (double)Thousand).ToString("0.##", CultureInfo.InvariantCulture) + "K";
        }

        return value.ToString("N0", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 明細列的欄位格式。null 代表「這次 API 沒有回傳這個欄位」，
    /// 顯示破折號而不是 0 —— 兩者意義完全不同。
    /// </summary>
    public static string Cell(int? value)
        => value.HasValue ? Compact(value.Value) : "—";

    /// <summary>
    /// 明細列的台幣費用。單筆呼叫常常小於 0.01 元，位數不夠會全部顯示成 0。
    /// null 代表未定價（不是免費），顯示破折號。
    /// </summary>
    public static string CostTwdCell(double? value)
        => value.HasValue ? value.Value.ToString("N4", CultureInfo.InvariantCulture) : "—";

    /// <summary>明細列的美金費用，位數再多兩位（單價本身就是每百萬 token 計的）。</summary>
    public static string CostUsdCell(double? value)
        => value.HasValue ? value.Value.ToString("N6", CultureInfo.InvariantCulture) : "—";

    /// <summary>合計與分組統計的台幣費用。金額量級大，不需要明細那麼細。</summary>
    public static string CostTwdTotal(double value)
        => value.ToString("N2", CultureInfo.InvariantCulture);

    /// <summary>合計與分組統計的美金費用。</summary>
    public static string CostUsdTotal(double value)
        => value.ToString("N4", CultureInfo.InvariantCulture);

    /// <summary>
    /// 匯率。用「有幾位顯示幾位」而不是固定小數位 —— 匯率是 31.5 就顯示 31.5，
    /// 套用金額那種 6 位小數格式會變成 31.500000，讀起來像雜訊。
    /// </summary>
    public static string ExchangeRate(double? value)
        => value.HasValue ? value.Value.ToString("0.####", CultureInfo.InvariantCulture) : "—";
}

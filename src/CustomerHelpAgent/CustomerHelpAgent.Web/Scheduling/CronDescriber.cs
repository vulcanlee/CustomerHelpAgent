namespace CustomerHelpAgent.Web.Scheduling;

/// <summary>
/// 把常見的 cron（分 時 日 月 週）轉成白話，給「資料保留與清理」頁顯示「多久清一次」（0.9.133 起）。
/// 只認得單一數值的幾種寫法；其他（範圍、間隔、清單）原樣顯示 cron，不硬翻成可能錯的句子。
/// </summary>
public static class CronDescriber
{
    private static readonly string[] WeekDays = ["日", "一", "二", "三", "四", "五", "六", "日"];

    public static string Describe(string cron)
    {
        var parts = cron.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5 || !IsNumber(parts[0], 0, 59, out var minute))
        {
            return cron;
        }

        var (hourText, dayText, monthText, weekText) = (parts[1], parts[2], parts[3], parts[4]);
        if (hourText == "*" && dayText == "*" && monthText == "*" && weekText == "*")
        {
            return $"每小時第 {minute} 分";
        }

        if (!IsNumber(hourText, 0, 23, out var hour) || monthText != "*")
        {
            return cron;
        }

        var time = $"{hour:00}:{minute:00}";
        return (dayText, weekText) switch
        {
            ("*", "*") => $"每天 {time}",
            ("*", _) when IsNumber(weekText, 0, 7, out var week) => $"每週{WeekDays[week]} {time}",
            (_, "*") when IsNumber(dayText, 1, 31, out var day) => $"每月 {day} 日 {time}",
            _ => cron,
        };
    }

    private static bool IsNumber(string text, int min, int max, out int value)
        => int.TryParse(text, out value) && value >= min && value <= max;
}

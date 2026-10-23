using Cronos;
using CustomerHelpAgent.Web.Configuration;

namespace CustomerHelpAgent.Web.Scheduling;

/// <summary>
/// 排程時間的計算（純函式，0.9.96 起）。cron 一律依伺服器本地時區（<see cref="TimeProvider.LocalTimeZone"/>）解讀。
///
/// ⚠️ Cronos 只接受 <see cref="DateTimeKind.Utc"/> 的時間；EF 從 SQLite 讀回來的是 Unspecified，
/// 所以所有從資料庫來的時間都先經 <see cref="AsUtc"/>。
/// </summary>
public static class ScheduleCalculator
{
    /// <summary>只接受 5 欄位的標準 cron（不含秒）。</summary>
    public static bool TryParse(string? cron, out CronExpression? expression)
    {
        expression = null;
        return !string.IsNullOrWhiteSpace(cron) && CronExpression.TryParse(cron.Trim(), CronFormat.Standard, out expression);
    }

    public static CronExpression Parse(string cron) => CronExpression.Parse(cron.Trim(), CronFormat.Standard);

    /// <summary>作業實際使用的 cron：設定檔有覆寫就用覆寫，否則用預設。</summary>
    public static string GetEffectiveCron(ScheduledJobDescriptor descriptor, ScheduledJobSettings settings)
        => settings.Jobs.TryGetValue(descriptor.Name, out var jobOverride) && !string.IsNullOrWhiteSpace(jobOverride.Cron)
            ? jobOverride.Cron.Trim()
            : descriptor.DefaultCron;

    public static DateTime AsUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    /// <summary>嚴格晚於 <paramref name="fromUtc"/> 的下一個時段；永遠不會觸發的 cron 回 null。</summary>
    public static DateTime? NextOccurrenceUtc(CronExpression cron, DateTime fromUtc, TimeZoneInfo zone)
        => cron.GetNextOccurrence(AsUtc(fromUtc), zone);

    /// <summary>
    /// 補跑的起點：上次搶到的時段與「建立或最後一次切換啟用」兩者取晚的。
    /// 所以全新安裝、新加入的作業、重新啟用的作業都不會回頭補跑之前的時段。
    /// </summary>
    public static DateTime Anchor(DateTime? lastScheduledForUtc, DateTime updatedAtUtc)
    {
        var updated = AsUtc(updatedAtUtc);
        return lastScheduledForUtc is { } last && AsUtc(last) > updated ? AsUtc(last) : updated;
    }

    /// <summary>
    /// 從錨點（不含）到現在（含）之間最近的一個時段；沒有錯過任何時段時回 null。
    /// 錯過好幾個時段也只回最近一個 —— 清理類作業補跑一次就夠了。Cronos 沒有「上一次」的 API，所以列舉後取最後一個。
    /// </summary>
    public static DateTime? CatchUpSlotUtc(CronExpression cron, DateTime anchorUtc, DateTime nowUtc, TimeZoneInfo zone)
    {
        var from = AsUtc(anchorUtc);
        var to = AsUtc(nowUtc);
        if (from >= to)
        {
            return null;
        }

        DateTime? last = null;
        foreach (var occurrence in cron.GetOccurrences(from, to, zone, fromInclusive: false, toInclusive: true))
        {
            last = occurrence;
        }

        return last;
    }
}

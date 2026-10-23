namespace CustomerHelpAgent.Web.DataRetention;

/// <summary>資料表存時間的方式，決定「自訂清除」的門檻怎麼換算。</summary>
public enum RetentionTimeBasis
{
    /// <summary>本地時間（例外紀錄、已刪除資料）。</summary>
    Local,

    /// <summary>UTC（稽核紀錄、站內通知、排程執行紀錄）。</summary>
    Utc,

    /// <summary>本地時間、以日為單位，門檻是當天 0 點（Token 用量、AI 對話紀錄，與它們的自動過期一致）。</summary>
    LocalDate,
}

/// <summary>
/// 「資料保留與清理」頁的「自訂清除」條件（0.9.134 起）：二選一 ——
/// 只留最近 <see cref="KeepDays"/> 天，或刪除 <see cref="BeforeDate"/> 這一天之前（不含當天）。只影響這一次，不改系統參數。
/// </summary>
public sealed record CustomPurgeCutoff(int? KeepDays, DateOnly? BeforeDate)
{
    public const int MaxKeepDays = 3650;

    /// <summary>不合法時回給使用者看的原因；合法回 null。</summary>
    public string? Validate(DateOnly todayLocal) => (KeepDays, BeforeDate) switch
    {
        ({ } days, null) when days is >= 1 and <= MaxKeepDays => null,
        ({ }, null) => $"保留天數要在 1～{MaxKeepDays} 天之間。",
        (null, { } date) when date <= todayLocal => null,
        (null, { }) => "日期不能晚於今天。",
        _ => "請選擇「只留最近 N 天」或「刪除這一天之前」其中一種。",
    };

    /// <summary>
    /// 換算成與資料表同一個時間基準的門檻：早於它的資料會被刪。
    /// 「只留最近 N 天」＝現在往前 N 天（<see cref="RetentionTimeBasis.LocalDate"/> 取當天 0 點，同自動過期）；
    /// 「刪除這一天之前」＝那天本地 0 點，UTC 的資料再換成 UTC。
    /// </summary>
    public DateTime ToThreshold(RetentionTimeBasis basis, TimeProvider timeProvider)
    {
        if (KeepDays is { } days)
        {
            return basis switch
            {
                RetentionTimeBasis.Utc => timeProvider.GetUtcNow().UtcDateTime.AddDays(-days),
                RetentionTimeBasis.LocalDate => timeProvider.GetLocalNow().DateTime.Date.AddDays(-days),
                _ => timeProvider.GetLocalNow().DateTime.AddDays(-days),
            };
        }

        var localMidnight = BeforeDate!.Value.ToDateTime(TimeOnly.MinValue);
        return basis == RetentionTimeBasis.Utc
            ? TimeZoneInfo.ConvertTimeToUtc(localMidnight, timeProvider.LocalTimeZone)
            : localMidnight;
    }

    public string Describe() => KeepDays is { } days
        ? $"只留最近 {days} 天"
        : $"刪除 {BeforeDate:yyyy-MM-dd} 之前（不含當天）";

    /// <summary>稽核 detail：只記條件與筆數，不記任何內容。</summary>
    public string AuditDetail(int rows) => KeepDays is { } days
        ? $"source=data-retention; mode=keepDays; value={days}; rows={rows}"
        : $"source=data-retention; mode=beforeDate; value={BeforeDate:yyyy-MM-dd}; rows={rows}";
}

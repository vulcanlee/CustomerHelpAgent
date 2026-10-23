namespace CustomerHelpAgent.Web.Configuration;

/// <summary>
/// 排程作業的設定（<c>ScheduledJobSettings</c> 區段，0.9.96 起）。啟動時由 <c>ScheduledJobSettingsValidator</c> 驗證。
///
/// 各作業的啟用／停用在管理頁「排程作業」切換（存在資料庫），這裡只管執行時間與總開關。
/// 執行中修改這個區段會在下一分鐘生效；改壞的話排程沿用上一份合法的設定並記錄錯誤。
/// </summary>
public class ScheduledJobSettings
{
    public const string SectionName = "ScheduledJobSettings";

    /// <summary>
    /// 總開關：false 時不依排程執行、也不補跑，但管理頁仍可「立即執行」。
    /// 用途：維護期間暫停所有排程、或多台機器只讓其中一台跑排程。
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>執行紀錄保留天數；0＝不清除。</summary>
    public int JobRunRetentionDays { get; set; } = 90;

    /// <summary>
    /// 覆寫個別作業的執行時間，鍵為作業名稱（不分大小寫）。沒有列出的作業使用程式內建的預設時間。
    /// </summary>
    public Dictionary<string, ScheduledJobOverride> Jobs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>單一作業的覆寫設定。</summary>
public class ScheduledJobOverride
{
    /// <summary>5 欄位 cron（分 時 日 月 星期），依伺服器本地時區。例：<c>0 3 * * *</c> = 每天 03:00。</summary>
    public string? Cron { get; set; }
}

using System.ComponentModel.DataAnnotations;

namespace CustomerHelpAgent.Models.Systems;

/// <summary>站內通知（0.9.100 起）。可在「系統參數」頁覆寫。</summary>
public class NotificationSettings
{
    public const string SectionName = "NotificationSettings";

    /// <summary>通知保留天數（從建立時間起算，不論已讀未讀），超過的由排程作業「站內通知清理」刪除；0＝不自動清除。</summary>
    [Range(0, 3650)]
    public int RetentionDays { get; set; } = 90;

    /// <summary>已讀通知保留天數（0.9.125 起，從標為已讀的時間起算），超過的由同一個排程作業提早刪除；0＝不提早刪除。</summary>
    [Range(0, 3650)]
    public int ReadRetentionDays { get; set; } = 7;
}

using System.ComponentModel.DataAnnotations;

namespace CustomerHelpAgent.AccessDatas.Models;

/// <summary>
/// 排程作業的一次執行紀錄（0.9.96 起）。純歷史：依 <c>ScheduledJobSettings.JobRunRetentionDays</c> 清除，
/// 任何邏輯都不可以依賴它還在 —— 補跑的錨點在 <see cref="ScheduledJobState.LastScheduledForUtc"/>。
///
/// 時間一律存 UTC（欄位名稱帶 Utc）。<see cref="Trigger"/> 與 <see cref="Status"/> 的值見 <c>ScheduledJobConstants</c>。
/// </summary>
public class JobRun
{
    public int Id { get; set; }

    [Required]
    [MaxLength(64)]
    public string JobName { get; set; } = string.Empty;

    [Required]
    [MaxLength(16)]
    public string Trigger { get; set; } = string.Empty;

    /// <summary>手動執行的操作者帳號；排程與補跑為 null。</summary>
    [MaxLength(100)]
    public string? TriggeredByAccount { get; set; }

    /// <summary>這次執行對應的排程時段；手動執行為 null。</summary>
    public DateTime? ScheduledForUtc { get; set; }

    public DateTime StartedAtUtc { get; set; }

    public DateTime? FinishedAtUtc { get; set; }

    [Required]
    [MaxLength(16)]
    public string Status { get; set; } = string.Empty;

    /// <summary>結果摘要（已截斷）。不放原始例外 —— 細節以 <see cref="TraceId"/> 到系統例外紀錄查。</summary>
    [MaxLength(1000)]
    public string? Message { get; set; }

    public long? DurationMs { get; set; }

    [MaxLength(16)]
    public string? TraceId { get; set; }
}

using System.ComponentModel.DataAnnotations;

namespace CustomerHelpAgent.AccessDatas.Models;

/// <summary>
/// 每個排程作業一列的狀態（0.9.96 起），不會被清除。
///
/// <see cref="LastScheduledForUtc"/> 同時是「跨行程只跑一次」的權威：執行前以一條 UPDATE 搶占時段
/// （<c>LastScheduledForUtc &lt; 這個時段</c> 才搶得到），兩個行程同一時段只有一個成功。
/// 它也是補跑的錨點之一（另一個是 <see cref="UpdatedAtUtc"/>：建立或最後一次切換啟用的時間）。
/// </summary>
public class ScheduledJobState
{
    [Key]
    [MaxLength(64)]
    public string JobName { get; set; } = string.Empty;

    /// <summary>管理頁的啟用開關；沒有這一列時視為啟用（啟動時會補建）。</summary>
    public bool IsEnabled { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>建立或最後一次切換啟用的時間；補跑不會回溯到這之前（重新啟用不補跑停用期間的時段）。</summary>
    public DateTime UpdatedAtUtc { get; set; }

    [MaxLength(100)]
    public string? UpdatedBy { get; set; }

    /// <summary>最近一次被搶占的排程時段（UTC）；手動執行不會改它。</summary>
    public DateTime? LastScheduledForUtc { get; set; }
}

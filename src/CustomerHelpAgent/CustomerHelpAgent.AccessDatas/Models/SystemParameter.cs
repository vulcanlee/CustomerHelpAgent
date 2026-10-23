using System.ComponentModel.DataAnnotations;

namespace CustomerHelpAgent.AccessDatas.Models;

/// <summary>
/// 系統參數的覆寫值（0.9.98 起）。一列＝管理員在「系統參數」頁覆寫的一個設定鍵；**沒有這一列就用 appsettings 的值**。
///
/// 只存值，不存型別、範圍與說明：那些的唯一來源是 <c>CustomerHelpAgent.Web/Configuration/Parameters/SystemParameterCatalog</c>
/// 與各設定類別本身。不在目錄裡的鍵即使存在這張表，也不會被套用到設定。
///
/// <see cref="ConcurrencyStamp"/> 由 <c>SystemParameterService</c> 以條件式 UPDATE／DELETE 比對（刻意不實作
/// <c>IConcurrencyStamped</c>：那一套是給「整筆覆蓋的編輯畫面」用的，這裡一次只改一個值）。
/// </summary>
public class SystemParameter
{
    /// <summary>完整設定鍵，例如 <c>LogRetentionSettings:AuditLogDays</c>。</summary>
    [Key]
    [MaxLength(200)]
    public string ParameterKey { get; set; } = string.Empty;

    /// <summary>以不變文化格式化的值；空字串是合法的覆寫（例如把系統簡介設為空白）。</summary>
    [MaxLength(2000)]
    public string Value { get; set; } = string.Empty;

    [MaxLength(32)]
    public string ConcurrencyStamp { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    [MaxLength(100)]
    public string? UpdatedBy { get; set; }
}

using System.ComponentModel.DataAnnotations;

namespace CustomerHelpAgent.Models.Systems;

/// <summary>
/// 登入失敗鎖定（0.9.101 起；之前寫死在 <c>MyUserServiceLogin</c>：5 次、15 分鐘）。可在「系統參數」頁覆寫。
/// ⚠️ 次數最少 1，不提供「關閉鎖定」—— 那會讓密碼可以被無限次猜測。
/// </summary>
public class LockoutSettings
{
    public const string SectionName = "LockoutSettings";

    /// <summary>連續輸錯幾次就鎖定。</summary>
    [Range(1, 100)]
    public int MaxFailedAttempts { get; set; } = 5;

    /// <summary>鎖定幾分鐘。</summary>
    [Range(1, 1440)]
    public int LockoutMinutes { get; set; } = 15;
}

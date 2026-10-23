using System.ComponentModel.DataAnnotations;

namespace CustomerHelpAgent.Models.Systems;

/// <summary>
/// 忘記密碼的時效設定（<c>PasswordResetSettings</c> 區段，0.9.60 起）。
///
/// 放在 Models 而不是 Web/Configuration：讀它的是 Business 層的 <c>PasswordResetService</c>，
/// Business 參照不到 Web。以 <c>ValidateOnStart</c> 驗證範圍，寫壞就啟動失敗。
/// </summary>
public class PasswordResetSettings
{
    public const string SectionName = "PasswordResetSettings";

    /// <summary>重設連結的有效分鐘數。</summary>
    [Range(5, 1440)]
    public int TokenLifetimeMinutes { get; set; } = 30;

    /// <summary>同一帳號兩次申請之間的最短秒數；0 表示不限制。冷卻期間的申請不寄信，畫面訊息照舊。</summary>
    [Range(0, 3600)]
    public int RequestCooldownSeconds { get; set; } = 60;
}

using System.ComponentModel.DataAnnotations;

namespace CustomerHelpAgent.Models.Systems;

/// <summary>
/// 密碼原則（0.9.101 起）。所有設定密碼的路徑（管理員建立與修改、變更密碼頁、忘記密碼重設）都經 <c>IPasswordPolicy</c> 套用。
/// 可在「系統參數」頁覆寫；改了之後只影響之後設定的密碼（既有密碼不會因此被要求變更，到期天數除外）。
/// </summary>
public class PasswordPolicySettings
{
    public const string SectionName = "PasswordPolicySettings";

    /// <summary>最少字元數。</summary>
    [Range(6, 64)]
    public int MinimumLength { get; set; } = 8;

    /// <summary>必須包含英文字母（A–Z 或 a–z）。</summary>
    public bool RequireLetter { get; set; } = true;

    /// <summary>必須包含數字（0–9）。</summary>
    public bool RequireDigit { get; set; } = true;

    /// <summary>必須包含大寫英文字母。</summary>
    public bool RequireUppercase { get; set; }

    /// <summary>必須包含符號（英數字與空白以外的字元）。</summary>
    public bool RequireSymbol { get; set; }

    /// <summary>不可與最近幾次使用過的密碼相同（含目前的）；0＝不檢查。</summary>
    [Range(0, 12)]
    public int HistoryCount { get; set; } = 3;

    /// <summary>密碼幾天後到期、登入後必須變更；0＝不過期。</summary>
    [Range(0, 3650)]
    public int ExpiryDays { get; set; }
}

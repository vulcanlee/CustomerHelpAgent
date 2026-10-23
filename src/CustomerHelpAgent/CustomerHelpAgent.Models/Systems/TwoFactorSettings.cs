using System.ComponentModel.DataAnnotations;

namespace CustomerHelpAgent.Models.Systems;

/// <summary>兩步驟驗證（0.9.104 起）。可在「系統參數」頁覆寫。個別角色是否強制，在角色管理勾選「需要兩步驟驗證」。</summary>
public class TwoFactorSettings
{
    public const string SectionName = "TwoFactorSettings";

    /// <summary>管理員必須啟用兩步驟驗證（support 帳號與只用 Google 登入的帳號除外）。</summary>
    public bool RequireForAdmins { get; set; }

    /// <summary>「記住這台裝置」幾天內不再要求驗證碼；0＝不提供這個選項。</summary>
    [Range(0, 365)]
    public int RememberDeviceDays { get; set; } = 30;
}

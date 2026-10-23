namespace CustomerHelpAgent.AccessDatas.Models;

/// <summary>
/// 使用者最近用過的密碼（0.9.101 起），供「不可重複最近 N 次的密碼」檢查。
/// 存的是與 <see cref="MyUser.Password"/> 相同的雜湊字串（PBKDF2，鹽值在雜湊裡），絕不存明文；
/// 只由 <c>IPasswordPolicy.Apply</c> 寫入並裁到 <c>PasswordPolicySettings.HistoryCount</c> 筆。
/// </summary>
public class PasswordHistory
{
    public int Id { get; set; }

    public int MyUserId { get; set; }

    public MyUser MyUser { get; set; } = null!;

    public string PasswordHash { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }
}

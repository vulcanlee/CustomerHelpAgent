namespace CustomerHelpAgent.AccessDatas.Models;

/// <summary>
/// 兩步驟驗證的備用碼（0.9.104 起）：手機不在身邊時代替 6 位數驗證碼登入，每組只能用一次。
/// 只存雜湊（HMAC-SHA256，以使用者 Id 為鹽），原文只在產生當下顯示一次；重新產生會刪掉舊的整組。
/// </summary>
public class TwoFactorBackupCode
{
    public int Id { get; set; }

    public int MyUserId { get; set; }

    public MyUser MyUser { get; set; } = null!;

    public string CodeHash { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>用掉的時間；null 代表還能用。以條件式 UPDATE 標記，兩個請求同時用同一組只有一個成功。</summary>
    public DateTime? UsedAtUtc { get; set; }
}

using System.ComponentModel.DataAnnotations;

namespace CustomerHelpAgent.AccessDatas.Models;

/// <summary>
/// 使用者
/// </summary>
public class MyUser : IConcurrencyStamped, ISoftDeletable
{
    public MyUser()
    {
    }
    public int Id { get; set; }
    [Required(ErrorMessage = "帳號 不可為空白")]
    public string Account { get; set; } = String.Empty;
    [Required(ErrorMessage = "密碼 不可為空白")]
    public string Password { get; set; } = String.Empty;
    [Required(ErrorMessage = "名稱 不可為空白")]
    public string Name { get; set; } = String.Empty;
    public string? Salt { get; set; }
    public bool Status { get; set; } = true;
    public string? Email { get; set; }
    public bool IsAdmin { get; set; } = false;
    public DateTime CreateAt { get; set; } = DateTime.Now;
    public int? RoleViewId { get; set; }
    public DateTime UpdateAt { get; set; } = DateTime.Now;
    /// <summary>
    /// 外部身分驗證提供者，例如 "Google"；本地帳號為 null 或 "Local"
    /// </summary>
    public string? OAuthProvider { get; set; }
    /// <summary>
    /// 外部身分驗證的使用者唯一識別碼（Google 的 sub）
    /// </summary>
    public string? GoogleId { get; set; }
    /// <summary>
    /// 連續登入失敗次數；成功登入後歸零。
    /// </summary>
    public int AccessFailedCount { get; set; } = 0;
    /// <summary>
    /// 帳號鎖定截止時間（UTC）；為 null 或已過期表示未鎖定。
    /// </summary>
    public DateTime? LockoutEndUtc { get; set; }
    /// <summary>
    /// 是否啟用二階段驗證（TOTP）；預設關閉。
    /// </summary>
    public bool TwoFactorEnabled { get; set; } = false;
    /// <summary>
    /// TOTP Base32 密鑰；未綁定時為 null。0.9.104 起以 Data Protection 加密後存放（<c>ITwoFactorSecretProtector</c>），⚠️ 絕不寫進日誌。
    /// </summary>
    public string? TwoFactorSecret { get; set; }
    /// <summary>
    /// 最近一次接受的 TOTP 時間步（0.9.104 起，防重放）：同一組 6 位數在有效期間內只能用一次。
    /// </summary>
    public long? TwoFactorLastStep { get; set; }
    /// <summary>
    /// 下次登入必須先變更密碼（0.9.101 起，取代以密碼 123456 當哨兵的做法）。管理員建立帳號或替使用者設密碼時預設勾選。
    /// </summary>
    public bool MustChangePassword { get; set; }
    /// <summary>
    /// 最近一次設定密碼的時間（UTC，0.9.101 起），用來計算密碼到期；null 表示不會到期（沒有本機密碼或升級前的資料）。
    /// </summary>
    public DateTime? PasswordChangedAtUtc { get; set; }
    /// <summary>
    /// 工作階段版本（0.9.103 起）。換掉它，這位使用者所有已登入的瀏覽器與 API refresh token 都會失效。
    /// 只由 <c>SecurityStamps.New()</c> 產生；空字串代表「尚未設定」（重疊回收時舊版程式新增的列），登入時補上，比對時一律視為不符。
    /// ⚠️ 與 <see cref="ConcurrencyStamp"/> 無關，換它不會讓別人的編輯窗衝突；不可放進畫面模型。
    /// </summary>
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");
    public RoleView? RoleView { get; set; }

    /// <summary>樂觀並行的版本號（見 <see cref="IConcurrencyStamped"/>）。</summary>
    public string ConcurrencyStamp { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>軟刪除（見 <see cref="ISoftDeletable"/>，0.9.95 起）。</summary>
    public bool IsDeleted { get; set; }

    public DateTime? DeletedAt { get; set; }

    public string? DeletedBy { get; set; }
}

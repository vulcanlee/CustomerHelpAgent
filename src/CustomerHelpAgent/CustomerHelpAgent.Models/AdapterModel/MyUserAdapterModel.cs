using CustomerHelpAgent.Models.Admins;
using System.ComponentModel.DataAnnotations;

namespace CustomerHelpAgent.Models.AdapterModel;

public class MyUserAdapterModel : ICloneable
{
    /// <summary>樂觀並行的版本號：開啟編輯時的值，存檔時用來比對資料是否已被別人修改（0.9.93 起）。</summary>
    public string ConcurrencyStamp { get; set; } = string.Empty;

    /// <summary>軟刪除狀態（0.9.95 起，只由實體對應過來，供「顯示已刪除」清單使用）。</summary>
    public bool IsDeleted { get; set; }

    public DateTime? DeletedAt { get; set; }

    public string? DeletedBy { get; set; }

    public int Id { get; set; }
    [Required(ErrorMessage = "帳號 不可為空白")]
    public string Account { get; set; } = String.Empty;
    public string Password { get; set; } = String.Empty;
    [Required(ErrorMessage = "名稱 不可為空白")]
    public string Name { get; set; } = String.Empty;
    public string? Salt { get; set; }
    public bool Status { get; set; } = true;
    public string? Email { get; set; }
    public bool IsAdmin { get; set; } = false;
    public DateTime CreateAt { get; set; } = DateTime.Now;
    public DateTime UpdateAt { get; set; } = DateTime.Now;
    [Required(ErrorMessage = "角色 不可為空白")]
    public int? RoleViewId { get; set; }
    public string? OAuthProvider { get; set; }
    public string? GoogleId { get; set; }
    /// <summary>下次登入須變更密碼（0.9.101 起）。管理員新增使用者時預設勾選；編輯時輸入新密碼也會自動勾選。</summary>
    public bool MustChangePassword { get; set; }
    /// <summary>最近一次設定密碼的時間（UTC，唯讀；0.9.101 起）。</summary>
    public DateTime? PasswordChangedAtUtc { get; set; }
    /// <summary>鎖定截止時間（UTC，唯讀；0.9.101 起在使用者清單顯示與解鎖）。</summary>
    public DateTime? LockoutEndUtc { get; set; }
    /// <summary>是否有本機密碼（只用 Google 登入的帳號沒有；唯讀）。<see cref="Password"/> 讀出時一律清空，所以另外對應。</summary>
    public bool HasLocalPassword { get; set; }
    /// <summary>已啟用兩步驟驗證（唯讀；0.9.104 起）。只能由本人設定、管理員重設，不經編輯表單寫回。</summary>
    public bool TwoFactorEnabled { get; set; }
    public RoleViewAdapterModel? RoleView { get; set; }
    /// <summary>額外角色（主要角色 RoleViewId 之外）；與主要角色一起寫入 UserRole（多角色）。</summary>
    public List<int> AdditionalRoleIds { get; set; } = new();
    /// <summary>直接綁在使用者的團隊名稱；寫入 UserTeam（團隊綁使用者）。</summary>
    public List<string> TeamNames { get; set; } = new();
    public string RoleViewName => RoleView?.Name ?? string.Empty;
    public string StatusText => Status ? "啟用" : "停用";
    public string IsAdminText => IsAdmin ? "是" : "否";
    public bool IsGoogleAccount => string.Equals(OAuthProvider, "Google", StringComparison.OrdinalIgnoreCase);

    public MyUserAdapterModel Clone()
    {
        return (MyUserAdapterModel)((ICloneable)this).Clone();
    }
    object ICloneable.Clone()
    {
        return this.MemberwiseClone();
    }
}

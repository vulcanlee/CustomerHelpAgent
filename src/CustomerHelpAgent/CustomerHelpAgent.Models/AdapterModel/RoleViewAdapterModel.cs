using CustomerHelpAgent.Models.Admins;
using System.ComponentModel.DataAnnotations;

namespace CustomerHelpAgent.Models.AdapterModel;

public class RoleViewAdapterModel : ICloneable
{
    /// <summary>樂觀並行的版本號：開啟編輯時的值，存檔時用來比對資料是否已被別人修改（0.9.93 起）。</summary>
    public string ConcurrencyStamp { get; set; } = string.Empty;

    /// <summary>軟刪除狀態（0.9.95 起，只由實體對應過來，供「顯示已刪除」清單使用）。</summary>
    public bool IsDeleted { get; set; }

    public DateTime? DeletedAt { get; set; }

    public string? DeletedBy { get; set; }

    public int Id { get; set; }
    [Required(ErrorMessage = "名稱 不可為空白")]
    public string Name { get; set; } = String.Empty;
    public string TabViewJson { get; set; } = string.Empty;
    public DateTime CreateAt { get; set; } = DateTime.Now;
    public DateTime UpdateAt { get; set; } = DateTime.Now;
    public RolePermission RolePermission { get; set; } = new();
    /// <summary>角色預設團隊（UI 綁定用）</summary>
    public List<string> DefaultTeams { get; set; } = [];
    /// <summary>有這個角色的人必須使用兩步驟驗證（0.9.104 起）。</summary>
    public bool RequireTwoFactor { get; set; }

    public RoleViewAdapterModel Clone()
    {
        return (RoleViewAdapterModel)((ICloneable)this).Clone();
    }
    object ICloneable.Clone()
    {
        // 手寫逐欄複製（RolePermission、DefaultTeams 要深複製）：新增屬性時務必補上，
        // AdapterModelCloneTests 會抓漏。0.9.93 加上 ConcurrencyStamp 時漏了它，角色存檔全被當成並行衝突。
        return new RoleViewAdapterModel
        {
            ConcurrencyStamp = ConcurrencyStamp,
            IsDeleted = IsDeleted,
            DeletedAt = DeletedAt,
            DeletedBy = DeletedBy,
            Id = Id,
            Name = Name,
            TabViewJson = TabViewJson,
            CreateAt = CreateAt,
            UpdateAt = UpdateAt,
            RolePermission = RolePermission.Clone(),
            DefaultTeams = new List<string>(DefaultTeams),
            RequireTwoFactor = RequireTwoFactor
        };
    }
}

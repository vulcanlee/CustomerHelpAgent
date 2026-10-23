using System.ComponentModel.DataAnnotations;

namespace CustomerHelpAgent.AccessDatas.Models;

/// <summary>
/// 使用者
/// </summary>
public class RoleView : IConcurrencyStamped, ISoftDeletable
{
    public RoleView()
    {
    }
    public int Id { get; set; }
    [Required(ErrorMessage = "名稱 不可為空白")]
    public string Name { get; set; } = string.Empty;
    [Required(ErrorMessage = "頁面可視權限 Json 不可為空白")]
    public string TabViewJson { get; set; } = string.Empty;
    /// <summary>角色預設團隊（JSON 字串陣列，例如 ["團隊A","團隊B"]）</summary>
    public string DefaultTeamsJson { get; set; } = "[]";
    /// <summary>擁有這個角色（主要或額外）的人必須啟用兩步驟驗證（0.9.104 起）；未啟用者登入後先被帶去設定。</summary>
    public bool RequireTwoFactor { get; set; }
    public DateTime CreateAt { get; set; } = DateTime.Now;
    public DateTime UpdateAt { get; set; } = DateTime.Now;

    /// <summary>樂觀並行的版本號（見 <see cref="IConcurrencyStamped"/>）。</summary>
    public string ConcurrencyStamp { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>軟刪除（見 <see cref="ISoftDeletable"/>，0.9.95 起）。</summary>
    public bool IsDeleted { get; set; }

    public DateTime? DeletedAt { get; set; }

    public string? DeletedBy { get; set; }
}

using System.ComponentModel.DataAnnotations;

namespace CustomerHelpAgent.Models.AdapterModel;

public class CategoryAdapterModel : ICloneable
{
    /// <summary>軟刪除狀態（0.9.94 起，只由實體對應過來，供「顯示已刪除」清單使用）。</summary>
    public bool IsDeleted { get; set; }

    public DateTime? DeletedAt { get; set; }

    public string? DeletedBy { get; set; }

    /// <summary>樂觀並行的版本號：開啟編輯時的值，存檔時用來比對資料是否已被別人修改（0.9.93 起）。</summary>
    public string ConcurrencyStamp { get; set; } = string.Empty;

    public int Id { get; set; }

    [Required(ErrorMessage = "分類名稱 不可為空白")]
    [StringLength(100, ErrorMessage = "名稱長度不可超過 100 字元")]
    public string Name { get; set; } = string.Empty;

    [StringLength(2000, ErrorMessage = "描述長度不可超過 2000 字元")]
    public string? Description { get; set; }

    /// <summary>
    /// 適用團隊（未設定表示所有團隊皆可使用）。
    /// 注意：Clone() 走 MemberwiseClone()，這個清單是淺複製，
    /// 因此異動時一律「指派新清單」，不可對既有清單 Add/Remove。
    /// </summary>
    public List<string> Teams { get; set; } = [];

    public string TeamsText => string.Join("、", Teams);

    public bool IsEnabled { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public CategoryAdapterModel Clone()
    {
        return (CategoryAdapterModel)((ICloneable)this).Clone();
    }

    object ICloneable.Clone()
    {
        return MemberwiseClone();
    }
}

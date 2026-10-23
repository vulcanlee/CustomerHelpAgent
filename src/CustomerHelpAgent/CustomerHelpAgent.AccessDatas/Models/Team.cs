using System.ComponentModel.DataAnnotations;

namespace CustomerHelpAgent.AccessDatas.Models;

/// <summary>
/// 團隊（主資料）。0.9.105 起可設定上層部門（<see cref="ParentId"/>）組成部門樹。
/// </summary>
public class Team : IConcurrencyStamped, ISoftDeletable
{
    public int Id { get; set; }

    [Required(ErrorMessage = "團隊名稱 不可為空白")]
    public string Name { get; set; } = string.Empty;

    /// <summary>團隊代號（選填，有填則需唯一）</summary>
    public string? Code { get; set; }

    public string? Description { get; set; }

    /// <summary>
    /// 上層部門（0.9.105 起，null＝最上層）。上層看得到所有下屬部門的資料；不可指向自己、自己的下屬或已刪除的部門。
    /// 沒有導覽屬性：階層一律經 <c>ITeamTreeCache</c> 或明確查詢走訪。
    /// </summary>
    public int? ParentId { get; set; }

    /// <summary>是否啟用</summary>
    public bool IsEnabled { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    /// <summary>樂觀並行的版本號（見 <see cref="IConcurrencyStamped"/>）。</summary>
    public string ConcurrencyStamp { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>軟刪除旗標（見 <see cref="ISoftDeletable"/>）。</summary>
    public bool IsDeleted { get; set; }

    public DateTime? DeletedAt { get; set; }

    public string? DeletedBy { get; set; }
}

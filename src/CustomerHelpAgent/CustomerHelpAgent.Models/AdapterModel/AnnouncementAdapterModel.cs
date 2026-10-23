namespace CustomerHelpAgent.Models.AdapterModel;

/// <summary>
/// 公告（0.9.100 起）。時間為<b>伺服器本地時間</b>（畫面直接顯示與編輯）；存進資料庫時轉成 UTC。
/// </summary>
public class AnnouncementAdapterModel
{
    public int Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public DateTime StartAt { get; set; }

    public DateTime? EndAt { get; set; }

    /// <summary><c>All</c>／<c>Role</c>／<c>Team</c>。</summary>
    public string TargetKind { get; set; } = "All";

    public int? TargetId { get; set; }

    /// <summary>對象名稱（管理頁顯示用）；角色或團隊已刪除時附註「（已刪除）」。</summary>
    public string? TargetName { get; set; }

    public string? CreatedByAccount { get; set; }

    public DateTime UpdatedAt { get; set; }

    public string ConcurrencyStamp { get; set; } = string.Empty;
}

using System.ComponentModel.DataAnnotations;

namespace CustomerHelpAgent.AccessDatas.Models;

/// <summary>
/// 公告（0.9.100 起）：管理員發布，在起訖時間內顯示在每個登入後頁面的上方，使用者可以各自關閉。
/// 內容是純文字（以換行保留格式顯示，不經 Markdown／HTML）。對象為全體、單一角色或單一團隊；對象 id 不設外鍵，
/// 角色或團隊被永久刪除後公告仍在，只是不再對任何人顯示（管理頁標示「已刪除」）。
///
/// <see cref="ConcurrencyStamp"/> 以條件式 UPDATE 比對（刻意不實作 <c>IConcurrencyStamped</c>：那一套要搭配軟刪除的 <c>ProtectFlags</c>）。
/// </summary>
public class Announcement
{
    public int Id { get; set; }

    [MaxLength(100)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string Content { get; set; } = string.Empty;

    public DateTime StartAtUtc { get; set; }

    /// <summary>結束時間（不含）；null 表示一直顯示到管理員刪除。</summary>
    public DateTime? EndAtUtc { get; set; }

    /// <summary><c>All</c>／<c>Role</c>／<c>Team</c>。</summary>
    [MaxLength(16)]
    public string TargetKind { get; set; } = AnnouncementTargetKinds.All;

    /// <summary>角色或團隊的 Id（<see cref="TargetKind"/> 為 <c>All</c> 時為 null）。</summary>
    public int? TargetId { get; set; }

    [MaxLength(100)]
    public string? CreatedByAccount { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    [MaxLength(32)]
    public string ConcurrencyStamp { get; set; } = string.Empty;
}

public static class AnnouncementTargetKinds
{
    public const string All = "All";
    public const string Role = "Role";
    public const string Team = "Team";
}

/// <summary>某位使用者關閉了某則公告（0.9.100 起）。</summary>
public class AnnouncementDismissal
{
    public int AnnouncementId { get; set; }

    public Announcement Announcement { get; set; } = null!;

    public int MyUserId { get; set; }

    public MyUser MyUser { get; set; } = null!;

    public DateTime DismissedAtUtc { get; set; }
}

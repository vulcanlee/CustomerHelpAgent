using System.ComponentModel.DataAnnotations;

namespace CustomerHelpAgent.AccessDatas.Models;

/// <summary>
/// 站內通知（0.9.100 起）：一位收件人一列，發送當下就把收件人展開（之後角色或團隊異動不會改寫已發出的通知）。
/// 由程式經 <c>INotificationSender</c> 發出；內容一律是純文字。依 <c>NotificationSettings.RetentionDays</c> 自動清除。
/// </summary>
public class Notification
{
    public int Id { get; set; }

    public int RecipientUserId { get; set; }

    public MyUser RecipientUser { get; set; } = null!;

    /// <summary>來源分類，例如 <c>JobFailed</c>、<c>AccountPending</c>、<c>AccountLocked</c>。</summary>
    [MaxLength(32)]
    public string Category { get; set; } = string.Empty;

    [MaxLength(100)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string? Body { get; set; }

    /// <summary>站內相對網址（例如 <c>/scheduled-jobs</c>）；點選時經 <c>ReturnUrlGuard</c> 檢查才導向。</summary>
    [MaxLength(200)]
    public string? Link { get; set; }

    /// <summary>去重鍵（例如 <c>AccountPending:12</c>）；實際去重看 <see cref="NotificationDispatch"/>。</summary>
    [MaxLength(100)]
    public string? SourceKey { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? ReadAtUtc { get; set; }
}

/// <summary>
/// 帶去重鍵的通知「已經發給誰」（0.9.136 起）。和通知本身分開存：使用者刪掉通知、或已讀的被提早清除，
/// 這一列仍在，同一個鍵不會再發一次。和通知一樣依 <c>NotificationSettings.RetentionDays</c>（建立時間）清除。
/// </summary>
public class NotificationDispatch
{
    [MaxLength(100)]
    public string SourceKey { get; set; } = string.Empty;

    public int RecipientUserId { get; set; }

    public MyUser RecipientUser { get; set; } = null!;

    public DateTime SentAtUtc { get; set; }
}

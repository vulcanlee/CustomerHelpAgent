namespace CustomerHelpAgent.AccessDatas.Models;

/// <summary>
/// 稽核軌跡：記錄「誰、何時、對什麼、做了什麼、結果」。
/// </summary>
public class AuditLog
{
    public int Id { get; set; }
    /// <summary>
    /// 事件發生時間（UTC）。
    /// </summary>
    public DateTime OccurredAt { get; set; }
    /// <summary>
    /// 操作者使用者 Id；系統或匿名事件可為 null。
    /// </summary>
    public int? ActorUserId { get; set; }
    /// <summary>
    /// 操作者帳號（快照，避免使用者刪除後無法追溯）。
    /// </summary>
    public string? ActorAccount { get; set; }
    /// <summary>
    /// 動作代碼，例如 Login.Success / Login.Failed / User.Create。
    /// </summary>
    public string Action { get; set; } = string.Empty;
    /// <summary>
    /// 目標實體類型，例如 MyUser、RoleView。
    /// </summary>
    public string? TargetType { get; set; }
    /// <summary>
    /// 目標實體識別（通常為主鍵字串）。
    /// </summary>
    public string? TargetId { get; set; }
    /// <summary>
    /// 補充摘要（非敏感資訊）。
    /// </summary>
    public string? Detail { get; set; }
    /// <summary>
    /// 該操作是否成功。
    /// </summary>
    public bool Success { get; set; } = true;
    /// <summary>
    /// 來源 IP（0.9.117 起）。排程作業與之前的舊紀錄為 null；IPv4-mapped IPv6 已轉回 IPv4。
    /// </summary>
    public string? ClientIp { get; set; }
    /// <summary>
    /// 詳細內容檔的相對路徑（0.9.131 起），例如 <c>202610/3f2a….txt</c>。
    /// 修改前後差異、完整查詢條件這類長內容寫在 <c>AuditContentPath</c> 底下的檔案，不進資料庫；沒有長內容時為 null。
    /// </summary>
    public string? ContentFile { get; set; }
}

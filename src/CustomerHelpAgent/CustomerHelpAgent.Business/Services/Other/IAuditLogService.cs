namespace CustomerHelpAgent.Business.Services.Other;

/// <summary>
/// 稽核軌跡寫入服務；寫入失敗不應中斷主要業務流程。
/// </summary>
public interface IAuditLogService
{
    /// <param name="detail">寫進資料庫的短摘要（非敏感、可搜尋），例如 <c>name=分類A</c>。</param>
    /// <param name="content">
    /// 長內容（0.9.131 起）：修改前後差異、完整查詢條件。寫成檔案、資料庫只存路徑，
    /// 只在 /audit-logs 明細窗顯示，<b>不進日誌</b>。沒有就傳 null。
    /// </param>
    Task WriteAsync(
        string action,
        bool success = true,
        int? actorUserId = null,
        string? actorAccount = null,
        string? targetType = null,
        string? targetId = null,
        string? detail = null,
        string? content = null);
}

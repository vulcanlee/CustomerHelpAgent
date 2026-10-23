using System.Globalization;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.Other;

namespace CustomerHelpAgent.Web.Components.Commons;

/// <summary>
/// 檢視層寫稽核的共用樣板（LOG-14）：操作者一律取自 <see cref="CurrentUserService"/>。
///
/// 只在動作<b>成功之後</b>呼叫。Detail 進資料庫，只放非敏感短摘要（筆數、天數、匯出格式、名稱），
/// 不放查詢原文或匯出內容；長內容（完整查詢條件、修改差異）走 <c>content</c>，寫成詳細內容檔（0.9.131 起），
/// 只在 /audit-logs 明細窗顯示、不進日誌。
/// <see cref="IAuditLogService"/> 寫入失敗只記 Warning、不拋出 —— 不推翻「已經完成」的動作。
/// </summary>
public static class ViewAudit
{
    public static Task WriteAsync(
        IAuditLogService auditLogService,
        CurrentUserService currentUserService,
        string action,
        string targetType,
        string? targetId,
        string? detail,
        string? content = null)
    {
        var user = currentUserService.CurrentUser;
        return auditLogService.WriteAsync(
            action,
            success: true,
            actorUserId: user.Id > 0 ? user.Id : null,
            actorAccount: user.Id > 0 ? user.Account : null,
            targetType: targetType,
            targetId: targetId,
            detail: detail,
            content: content);
    }

    /// <summary>
    /// 按「搜尋／查詢」的稽核（0.9.131 起）。
    ///
    /// 全部條件（含搜尋原文）寫進詳細內容檔。Detail 只放短摘要：使用者自由輸入的文字（<see cref="AuditSearchCondition.Text"/>：
    /// 關鍵字、帳號、IP、追蹤碼）只記「有沒有填」，其他條件（勾選、日期、下拉選單）記值。
    /// </summary>
    public static Task WriteSearchAsync(
        IAuditLogService auditLogService,
        CurrentUserService currentUserService,
        string action,
        string targetType,
        params AuditSearchCondition[] conditions)
    {
        var detail = string.Join("; ", conditions.Select(c => c.IsFreeText
            ? $"has{Capitalize(c.Name)}={string.IsNullOrWhiteSpace(c.Value as string) == false}"
            : $"{c.Name}={Format(c.Value)}"));

        var content = string.Join("\n", conditions.Select(c => $"{c.Name}: {Format(c.Value)}").Prepend("查詢條件"));

        return WriteAsync(auditLogService, currentUserService, action, targetType, null, detail, content);
    }

    private static string Format(object? value) => value switch
    {
        null => AuditDiff.Empty,
        string text when string.IsNullOrWhiteSpace(text) => AuditDiff.Empty,
        DateTime time => time.ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture),
        bool flag => flag ? "true" : "false",
        IEnumerable<string> items => items.Any() ? string.Join(", ", items) : AuditDiff.Empty,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? AuditDiff.Empty,
    };

    private static string Capitalize(string name)
        => name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..];
}

/// <summary>一個查詢條件（<see cref="ViewAudit.WriteSearchAsync"/>）。</summary>
public sealed record AuditSearchCondition(string Name, object? Value, bool IsFreeText)
{
    /// <summary>使用者自由輸入的文字：原文只進詳細內容檔，資料庫摘要只記有沒有填。</summary>
    public static AuditSearchCondition Text(string name, string? value) => new(name, value, true);

    /// <summary>勾選、日期、下拉選單這類條件：摘要直接記值。</summary>
    public static AuditSearchCondition Of(string name, object? value) => new(name, value, false);
}

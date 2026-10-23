using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.AdapterModel;
using CustomerHelpAgent.Models.Systems;

namespace CustomerHelpAgent.Web.Export;

/// <summary>一次匯出的結果：成功時帶檔案內容與檔名，超過上限時帶說明。</summary>
public sealed record ExportOutcome(byte[]? Content, string? FileName, int Rows, string? Error);

/// <summary>
/// 業務頁的「匯出 Excel」（0.9.107 起）：專案、分類、團隊、使用者。
/// 資料一律經畫面同一個查詢（服務的 <c>GetAsync</c>／<c>GetDeletedAsync</c>）—— 搜尋、篩選、排序、「顯示已刪除」與團隊範圍都與畫面一致，
/// 看不到的資料不會出現在匯出檔裡。看得到清單就能匯出（使用者決定，沒有另外的匯出權限）。
/// </summary>
public static class BusinessExports
{
    /// <summary>一次匯出的上限（與稽核紀錄、AI 對話紀錄的匯出相同）。</summary>
    public const int MaxRows = 10000;

    public static string TooManyMessage(int count)
        => $"符合條件的資料有 {count} 筆，超過一次匯出的上限 {MaxRows} 筆；請縮小搜尋或篩選條件後再匯出。";

    /// <summary>以畫面目前的條件查詢（第一頁、上限筆數），超過上限就不產生檔案。</summary>
    public static async Task<ExportOutcome> BuildAsync<T>(
        Func<DataRequest, Task<DataRequestResult<T>>> query,
        DataRequest current,
        string sheetName,
        string fileKey,
        IReadOnlyList<ExportColumn<T>> columns)
    {
        var result = await query(ForExport(current));
        if (result.Count > MaxRows)
        {
            return new ExportOutcome(null, null, result.Count, TooManyMessage(result.Count));
        }

        return FromRows(result.Result.ToList(), sheetName, fileKey, columns);
    }

    /// <summary>已經在記憶體裡的資料（團隊清單的樹狀模式一次載入全部）。</summary>
    public static ExportOutcome FromRows<T>(IReadOnlyList<T> rows, string sheetName, string fileKey, IReadOnlyList<ExportColumn<T>> columns)
        => rows.Count > MaxRows
            ? new ExportOutcome(null, null, rows.Count, TooManyMessage(rows.Count))
            : new ExportOutcome(TabularExport.ToXlsx(sheetName, columns, rows), $"CustomerHelpAgent.Web-{fileKey}-{DateTime.Now:yyyyMMdd-HHmmss}.xlsx", rows.Count, null);

    /// <summary>寫匯出稽核（筆數，不記內容）。</summary>
    public static Task WriteAuditAsync(IAuditLogService auditLogService, CurrentUserService currentUserService, string action, string targetType, int rows, bool deleted)
    {
        var user = currentUserService.CurrentUser;
        return auditLogService.WriteAsync(
            action,
            success: true,
            actorUserId: user.Id > 0 ? user.Id : null,
            actorAccount: user.Id > 0 ? user.Account : null,
            targetType: targetType,
            targetId: "*",
            detail: $"format=xlsx; rows={rows}" + (deleted ? "; deleted=true" : string.Empty));
    }

    internal static DataRequest ForExport(DataRequest current) => new()
    {
        Search = current.Search,
        SortField = current.SortField,
        SortDescending = current.SortDescending,
        CategoryFilters = current.CategoryFilters,
        TeamFilters = current.TeamFilters,
        CurrentPage = 1,
        PageSize = MaxRows,
        Take = 1,
    };

    public static readonly IReadOnlyList<ExportColumn<ProjectAdapterModel>> ProjectColumns =
    [
        new("標題", x => x.Title, Width: 28),
        new("描述", x => x.Description, Width: 40),
        new("開始日期", x => x.StartDate, TabularExport.DateFormat, 12),
        new("結束日期", x => x.EndDate, TabularExport.DateFormat, 12),
        new("狀態", x => x.Status, Width: 10),
        new("優先級", x => x.Priority, Width: 8),
        new("完成百分比", x => x.CompletionPercentage, Width: 10),
        new("負責人", x => x.Owner, Width: 12),
        new("分類", x => x.CategoriesText, Width: 24),
        new("團隊", x => x.TeamsText, Width: 24),
        new("建立時間", x => x.CreatedAt, TabularExport.DateTimeFormat, 17),
        new("更新時間", x => x.UpdatedAt, TabularExport.DateTimeFormat, 17),
        new("刪除時間", x => x.DeletedAt, TabularExport.DateTimeFormat, 17),
        new("刪除者", x => x.DeletedBy, Width: 12),
    ];

    public static readonly IReadOnlyList<ExportColumn<CategoryAdapterModel>> CategoryColumns =
    [
        new("名稱", x => x.Name, Width: 24),
        new("描述", x => x.Description, Width: 40),
        new("適用團隊", x => string.Join("、", x.Teams), Width: 24),
        new("啟用", x => x.IsEnabled, Width: 8),
        new("建立時間", x => x.CreatedAt, TabularExport.DateTimeFormat, 17),
        new("更新時間", x => x.UpdatedAt, TabularExport.DateTimeFormat, 17),
        new("刪除時間", x => x.DeletedAt, TabularExport.DateTimeFormat, 17),
        new("刪除者", x => x.DeletedBy, Width: 12),
    ];

    public static IReadOnlyList<ExportColumn<TeamAdapterModel>> TeamColumns(Func<int?, string?> parentName) =>
    [
        new("名稱", x => x.Name, Width: 24),
        new("上層部門", x => parentName(x.ParentId), Width: 24),
        new("代號", x => x.Code, Width: 12),
        new("描述", x => x.Description, Width: 40),
        new("啟用", x => x.IsEnabled, Width: 8),
        new("建立時間", x => x.CreatedAt, TabularExport.DateTimeFormat, 17),
        new("更新時間", x => x.UpdatedAt, TabularExport.DateTimeFormat, 17),
        new("刪除時間", x => x.DeletedAt, TabularExport.DateTimeFormat, 17),
        new("刪除者", x => x.DeletedBy, Width: 12),
    ];

    /// <summary>⚠️ 不含密碼、鹽、兩步驟驗證密鑰、Google 識別碼（UsersExport_ShouldNotContainSecrets 守門）。</summary>
    public static readonly IReadOnlyList<ExportColumn<MyUserAdapterModel>> UserColumns =
    [
        new("帳號", x => x.Account, Width: 18),
        new("名稱", x => x.Name, Width: 14),
        new("Email", x => x.Email, Width: 28),
        new("角色", x => x.RoleViewName, Width: 14),
        new("狀態", x => x.StatusText, Width: 8),
        new("管理員", x => x.IsAdmin, Width: 8),
        new("兩步驟驗證", x => x.TwoFactorEnabled, Width: 10),
        new("鎖定至", x => x.LockoutEndUtc is { } end && end > DateTime.UtcNow ? end.ToLocalTime() : null, TabularExport.DateTimeFormat, 17),
        new("建立時間", x => x.CreateAt, TabularExport.DateTimeFormat, 17),
        new("更新時間", x => x.UpdateAt, TabularExport.DateTimeFormat, 17),
        new("刪除時間", x => x.DeletedAt, TabularExport.DateTimeFormat, 17),
        new("刪除者", x => x.DeletedBy, Width: 12),
    ];
}

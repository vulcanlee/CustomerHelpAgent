namespace CustomerHelpAgent.AccessDatas.Models;

/// <summary>
/// 軟刪除的實體（0.9.94 起：Project、Category、Team；0.9.95 起：MyUser、RoleView）。
///
/// <c>BackendDBContext</c> 為實作者套用具名全域過濾器 <see cref="FilterName"/>（<c>!IsDeleted</c>），
/// 所有查詢預設看不到已刪除的資料；「顯示已刪除」、還原、永久刪除要用 <c>IgnoreQueryFilters([FilterName])</c>。
///
/// ⚠️ 關聯表（UserTeam、UserRole、RolePermissionMap、PasswordResetToken、ProjectFile…）一律用明確 Join 查主體，不要 Include 或經由導覽屬性存取有過濾器的主體 ——
/// 從相依端經必要導覽查詢時，EF 會產生 INNER JOIN 接過濾後的主體，相依的資料列會靜默消失。
/// </summary>
public interface ISoftDeletable
{
    public const string FilterName = "SoftDelete";

    bool IsDeleted { get; set; }

    /// <summary>刪除時間（本地時間，與實體的 CreatedAt／UpdatedAt 慣例一致）。</summary>
    DateTime? DeletedAt { get; set; }

    /// <summary>刪除者的帳號。</summary>
    string? DeletedBy { get; set; }
}

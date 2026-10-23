using CustomerHelpAgent.AccessDatas;

namespace CustomerHelpAgent.Business.Services.Other;

/// <summary>
/// 在角色/使用者編輯時，同步維護 RBAC 關聯表（雙寫），使新表與舊模型一致。
/// 所有方法皆為協調式（reconcile）：新增缺少的、移除多餘的。
/// 帶 <see cref="BackendDBContext"/> 的多載（0.9.136 起）寫進呼叫端的 context，讓呼叫端把主表與關聯表包在同一個交易裡；
/// 不帶的多載用本服務注入的 context，各自存檔。
/// </summary>
public interface IRbacWriteService
{
    /// <summary>依權限鍵集合同步某角色的 RolePermissionMap（缺少的 Permission 會自動建立）。</summary>
    Task SyncRolePermissionsAsync(int roleViewId, IEnumerable<string> permissionKeys);

    /// <inheritdoc cref="SyncRolePermissionsAsync(int, IEnumerable{string})"/>
    Task SyncRolePermissionsAsync(BackendDBContext context, int roleViewId, IEnumerable<string> permissionKeys);

    /// <summary>同步某使用者的 UserRole。</summary>
    Task SyncUserRolesAsync(int userId, IEnumerable<int> roleViewIds);

    /// <inheritdoc cref="SyncUserRolesAsync(int, IEnumerable{int})"/>
    Task SyncUserRolesAsync(BackendDBContext context, int userId, IEnumerable<int> roleViewIds);

    /// <summary>同步某使用者的 UserTeam。</summary>
    Task SyncUserTeamsAsync(int userId, IEnumerable<int> teamIds);

    /// <inheritdoc cref="SyncUserTeamsAsync(int, IEnumerable{int})"/>
    Task SyncUserTeamsAsync(BackendDBContext context, int userId, IEnumerable<int> teamIds);
}

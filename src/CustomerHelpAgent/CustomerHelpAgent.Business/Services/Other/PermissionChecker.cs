using Microsoft.EntityFrameworkCore;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.Share.Helpers;
using Microsoft.Extensions.Logging;

namespace CustomerHelpAgent.Business.Services.Other;

public sealed class PermissionChecker : IPermissionChecker
{
    private readonly BackendDBContext context;
    private readonly ILogger<PermissionChecker> logger;

    public PermissionChecker(BackendDBContext context, ILogger<PermissionChecker> logger)
    {
        this.context = context;
        this.logger = logger;
    }

    public async Task<bool> HasPermissionAsync(int userId, string permissionKey)
    {
        var user = await context.MyUser
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == userId);

        if (user is null)
        {
            logger.LogWarning(
                "Permission check failed because the user does not exist. UserId={UserId}, PermissionKey={PermissionKey}",
                userId, permissionKey);
            return false;
        }

        if (user.IsAdmin)
        {
            logger.LogDebug(
                "Permission granted by administrator short-circuit. UserId={UserId}, PermissionKey={PermissionKey}",
                userId, permissionKey);
            return true;
        }

        var keys = await GetKeysForRolesAsync(await GetRoleIdsAsync(user.Id, user.RoleViewId));
        if (keys.Contains(permissionKey))
        {
            return true;
        }

        // 舊制相容：擁有裸頁面鍵者，視為具備該頁全部動作。
        var page = PermissionKey.PageOf(permissionKey);
        return page is not null && keys.Contains(page);
    }

    public async Task<IReadOnlyCollection<string>> GetEffectivePermissionKeysAsync(int userId)
    {
        var user = await context.MyUser
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == userId);

        return user is null
            ? []
            : await GetKeysForRolesAsync(await GetRoleIdsAsync(user.Id, user.RoleViewId));
    }

    /// <summary>使用者的角色來自 UserRole 關聯（多角色）；並容錯併入 legacy RoleViewId。</summary>
    private async Task<List<int>> GetRoleIdsAsync(int userId, int? legacyRoleViewId)
    {
        var roleIds = await context.UserRole
            .AsNoTracking()
            .Where(x => x.MyUserId == userId)
            .Select(x => x.RoleViewId)
            .ToListAsync();

        if (legacyRoleViewId.HasValue && !roleIds.Contains(legacyRoleViewId.Value))
        {
            roleIds.Add(legacyRoleViewId.Value);
        }

        return roleIds;
    }

    private async Task<HashSet<string>> GetKeysForRolesAsync(List<int> roleIds)
    {
        if (roleIds.Count == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        // ⚠️ 一定要經 context.RoleView（套用軟刪除過濾器）Join（0.9.95 起）：UserRole 與 legacy RoleViewId 都不看角色是否已刪除，
        // 只在這裡過濾一次，兩個來源就都涵蓋；少了這一步，已刪除的角色會繼續把權限給 UI 與 API。
        var keys = await context.RolePermissionMap
            .AsNoTracking()
            .Where(m => roleIds.Contains(m.RoleViewId))
            .Join(context.RoleView, m => m.RoleViewId, r => r.Id, (m, r) => m)
            .Join(context.Permission, m => m.PermissionId, p => p.Id, (m, p) => p.Key)
            .Distinct()
            .ToListAsync();

        return keys.ToHashSet(StringComparer.Ordinal);
    }
}

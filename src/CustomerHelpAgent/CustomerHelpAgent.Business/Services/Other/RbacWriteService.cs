using Microsoft.EntityFrameworkCore;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using Microsoft.Extensions.Logging;

namespace CustomerHelpAgent.Business.Services.Other;

public sealed class RbacWriteService : IRbacWriteService
{
    private readonly BackendDBContext context;
    private readonly ILogger<RbacWriteService> logger;

    public RbacWriteService(BackendDBContext context, ILogger<RbacWriteService> logger)
    {
        this.context = context;
        this.logger = logger;
    }

    public Task SyncRolePermissionsAsync(int roleViewId, IEnumerable<string> permissionKeys)
        => SyncRolePermissionsAsync(context, roleViewId, permissionKeys);

    public async Task SyncRolePermissionsAsync(BackendDBContext context, int roleViewId, IEnumerable<string> permissionKeys)
    {
        var desiredKeys = permissionKeys
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .ToHashSet(StringComparer.Ordinal);

        // 權限異動是安全相關的變更，記在 Info 讓日常巡檢就看得到。
        logger.LogInformation(
            "Syncing role permissions. RoleViewId={RoleViewId}, PermissionKeyCount={PermissionKeyCount}",
            roleViewId, desiredKeys.Count);

        var permissionIdByKey = await EnsurePermissionsAsync(context, desiredKeys);
        var desiredIds = desiredKeys.Select(k => permissionIdByKey[k]).ToHashSet();

        var existing = await context.RolePermissionMap
            .Where(x => x.RoleViewId == roleViewId)
            .ToListAsync();
        var existingIds = existing.Select(x => x.PermissionId).ToHashSet();

        foreach (var removed in existing.Where(x => !desiredIds.Contains(x.PermissionId)))
        {
            context.RolePermissionMap.Remove(removed);
        }

        foreach (var addId in desiredIds.Where(id => !existingIds.Contains(id)))
        {
            context.RolePermissionMap.Add(new RolePermissionMap { RoleViewId = roleViewId, PermissionId = addId });
        }

        await context.SaveChangesAsync();
    }

    public Task SyncUserRolesAsync(int userId, IEnumerable<int> roleViewIds)
        => SyncUserRolesAsync(context, userId, roleViewIds);

    public async Task SyncUserRolesAsync(BackendDBContext context, int userId, IEnumerable<int> roleViewIds)
    {
        var desired = roleViewIds.ToHashSet();

        var existing = await context.UserRole.Where(x => x.MyUserId == userId).ToListAsync();
        var existingIds = existing.Select(x => x.RoleViewId).ToHashSet();

        // 指向已軟刪除角色的關聯一律保留（0.9.95 起，與 SyncUserTeamsAsync 相同）：編輯畫面看不到已刪除的角色，
        // 若照樣刪掉，角色還原後這個人的額外角色就回不來了。權限判斷另經 context.RoleView 過濾，保留的關聯不會生效。
        var activeExistingIds = (await context.RoleView
                .Where(r => existingIds.Contains(r.Id))
                .Select(r => r.Id)
                .ToListAsync())
            .ToHashSet();

        foreach (var removed in existing.Where(x => activeExistingIds.Contains(x.RoleViewId) && !desired.Contains(x.RoleViewId)))
        {
            context.UserRole.Remove(removed);
        }

        foreach (var addId in desired.Where(id => !existingIds.Contains(id)))
        {
            context.UserRole.Add(new UserRole { MyUserId = userId, RoleViewId = addId });
        }

        await context.SaveChangesAsync();
    }

    public Task SyncUserTeamsAsync(int userId, IEnumerable<int> teamIds)
        => SyncUserTeamsAsync(context, userId, teamIds);

    public async Task SyncUserTeamsAsync(BackendDBContext context, int userId, IEnumerable<int> teamIds)
    {
        var desired = teamIds.ToHashSet();

        var existing = await context.UserTeam.Where(x => x.MyUserId == userId).ToListAsync();
        var existingIds = existing.Select(x => x.TeamId).ToHashSet();

        // 指向已軟刪除團隊的關聯一律保留（0.9.94 起）：編輯畫面看不到已刪除的團隊，送來的 desired 自然不含它，
        // 若照樣刪掉，團隊還原後成員關係就回不來了。只在「有效團隊」之間計算差異（context.Team 套用軟刪除過濾）。
        var activeExistingIds = (await context.Team
                .Where(t => existingIds.Contains(t.Id))
                .Select(t => t.Id)
                .ToListAsync())
            .ToHashSet();

        foreach (var removed in existing.Where(x => activeExistingIds.Contains(x.TeamId) && !desired.Contains(x.TeamId)))
        {
            context.UserTeam.Remove(removed);
        }

        foreach (var addId in desired.Where(id => !existingIds.Contains(id)))
        {
            context.UserTeam.Add(new UserTeam { MyUserId = userId, TeamId = addId });
        }

        await context.SaveChangesAsync();
    }

    private static async Task<Dictionary<string, int>> EnsurePermissionsAsync(BackendDBContext context, IReadOnlyCollection<string> keys)
    {
        var map = await context.Permission
            .Where(p => keys.Contains(p.Key))
            .ToDictionaryAsync(p => p.Key, p => p.Id, StringComparer.Ordinal);

        var missing = keys.Where(k => !map.ContainsKey(k)).ToList();
        if (missing.Count > 0)
        {
            foreach (var key in missing)
            {
                context.Permission.Add(new Permission { Key = key, DisplayName = key });
            }

            await context.SaveChangesAsync();

            foreach (var permission in await context.Permission.Where(p => missing.Contains(p.Key)).ToListAsync())
            {
                map[permission.Key] = permission.Id;
            }
        }

        return map;
    }
}

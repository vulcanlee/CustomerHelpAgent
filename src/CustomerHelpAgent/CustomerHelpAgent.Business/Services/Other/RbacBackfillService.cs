using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Helpers;

namespace CustomerHelpAgent.Business.Services.Other;

public sealed class RbacBackfillService : IRbacBackfillService
{
    private readonly BackendDBContext context;
    private readonly RolePermissionService rolePermissionService;
    private readonly ILogger<RbacBackfillService> logger;

    public RbacBackfillService(
        BackendDBContext context,
        RolePermissionService rolePermissionService,
        ILogger<RbacBackfillService> logger)
    {
        this.context = context;
        this.rolePermissionService = rolePermissionService;
        this.logger = logger;
    }

    public async Task RunAsync()
    {
        await NormalizePermissionKeysAsync();
        await BackfillPermissionCatalogAsync();
        await BackfillRolePermissionsAsync();
        await BackfillUserRolesAsync();
        await BackfillUserTeamsAsync();
        logger.LogInformation("RBAC backfill completed.");
    }

    /// <summary>
    /// 把既有資料中權限鍵的前後空白清掉，讓它與 <c>MagicObjectHelper</c> 的常數對得上。
    ///
    /// 背景：0.4.32 之前 <c>角色_角色管理</c>／<c>角色_登出</c> 兩個常數帶尾隨空白，
    /// 該字串已隨 RBAC 回填寫進 <c>Permission.Key</c> 與 <c>RoleView.TabViewJson</c>。
    /// 常數修好之後，既有部署的資料若不一併正規化，授權就會對不上（Ordinal 比對）。
    ///
    /// <c>Permission.Key</c> 有唯一索引，因此去空白後若與現有列撞鍵，必須合併而非直接改寫：
    /// 把重複列的 <c>RolePermissionMap</c> 轉掛到保留列，再刪掉重複列。
    /// 本方法為冪等，資料已正規化時不會產生任何異動。
    /// </summary>
    private async Task NormalizePermissionKeysAsync()
    {
        await NormalizePermissionCatalogAsync();
        await NormalizeRoleTabViewJsonAsync();
    }

    private async Task NormalizePermissionCatalogAsync()
    {
        var permissions = await context.Permission.ToListAsync();
        if (permissions.Count == 0)
        {
            return;
        }

        var changed = false;

        foreach (var group in permissions.GroupBy(x => x.Key.Trim(), StringComparer.Ordinal))
        {
            var trimmedKey = group.Key;

            // 已是正規化鍵的那一列優先保留，其次取最小 Id，讓結果不受載入順序影響。
            var survivor = group
                .OrderByDescending(x => string.Equals(x.Key, trimmedKey, StringComparison.Ordinal))
                .ThenBy(x => x.Id)
                .First();

            foreach (var duplicate in group.Where(x => x.Id != survivor.Id))
            {
                await MergePermissionAsync(duplicate, survivor);
                context.Permission.Remove(duplicate);
                changed = true;

                logger.LogInformation(
                    "Merged duplicate permission row into normalized key. PermissionKey={PermissionKey}",
                    trimmedKey);
            }

            if (!string.Equals(survivor.Key, trimmedKey, StringComparison.Ordinal))
            {
                survivor.Key = trimmedKey;
                changed = true;
            }

            var displayName = survivor.DisplayName?.Trim();
            if (!string.Equals(survivor.DisplayName, displayName, StringComparison.Ordinal))
            {
                survivor.DisplayName = displayName;
                changed = true;
            }

            var groupName = survivor.GroupName?.Trim();
            if (!string.Equals(survivor.GroupName, groupName, StringComparison.Ordinal))
            {
                survivor.GroupName = groupName;
                changed = true;
            }
        }

        if (changed)
        {
            await context.SaveChangesAsync();
            logger.LogInformation("Normalized permission keys.");
        }
    }

    /// <summary>
    /// 把重複權限列的角色對應轉掛到保留列；(RoleViewId, PermissionId) 有唯一索引，
    /// 因此保留列已有的對應要直接刪除而非轉掛。
    /// </summary>
    private async Task MergePermissionAsync(Permission duplicate, Permission survivor)
    {
        var survivorRoleIds = (await context.RolePermissionMap
            .Where(x => x.PermissionId == survivor.Id)
            .Select(x => x.RoleViewId)
            .ToListAsync())
            .ToHashSet();

        var maps = await context.RolePermissionMap
            .Where(x => x.PermissionId == duplicate.Id)
            .ToListAsync();

        foreach (var map in maps)
        {
            if (survivorRoleIds.Add(map.RoleViewId))
            {
                map.PermissionId = survivor.Id;
            }
            else
            {
                context.RolePermissionMap.Remove(map);
            }
        }
    }

    private async Task NormalizeRoleTabViewJsonAsync()
    {
        // 已刪除的角色也一併正規化與回填（0.9.95 起），還原後資料才一致。
        var roles = await context.RoleView.IgnoreQueryFilters([ISoftDeletable.FilterName]).ToListAsync();
        var changed = false;

        foreach (var role in roles)
        {
            var names = DeserializePermissionNames(role.TabViewJson);
            if (names.Count == 0)
            {
                continue;
            }

            var normalized = names
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (normalized.SequenceEqual(names, StringComparer.Ordinal))
            {
                continue;
            }

            role.TabViewJson = JsonSerializer.Serialize(normalized);
            changed = true;
        }

        if (changed)
        {
            await context.SaveChangesAsync();
            logger.LogInformation("Normalized role permission names in TabViewJson.");
        }
    }

    private async Task BackfillPermissionCatalogAsync()
    {
        var existingKeys = (await context.Permission.Select(x => x.Key).ToListAsync())
            .ToHashSet(StringComparer.Ordinal);

        foreach (var permission in rolePermissionService.CreatePermissionCatalog())
        {
            if (existingKeys.Add(permission.Key))
            {
                context.Permission.Add(permission);
            }
        }

        await context.SaveChangesAsync();
    }

    private async Task BackfillRolePermissionsAsync()
    {
        var permissionByKey = await context.Permission
            .ToDictionaryAsync(x => x.Key, x => x.Id, StringComparer.Ordinal);

        var existing = (await context.RolePermissionMap
            .Select(x => new { x.RoleViewId, x.PermissionId })
            .ToListAsync())
            .Select(x => (x.RoleViewId, x.PermissionId))
            .ToHashSet();

        var roles = await context.RoleView.IgnoreQueryFilters([ISoftDeletable.FilterName]).AsNoTracking().ToListAsync();
        foreach (var role in roles)
        {
            foreach (var name in DeserializePermissionNames(role.TabViewJson))
            {
                if (permissionByKey.TryGetValue(name, out var permissionId)
                    && existing.Add((role.Id, permissionId)))
                {
                    context.RolePermissionMap.Add(new RolePermissionMap
                    {
                        RoleViewId = role.Id,
                        PermissionId = permissionId,
                    });
                }
            }
        }

        await context.SaveChangesAsync();
    }

    private async Task BackfillUserRolesAsync()
    {
        var existing = (await context.UserRole
            .Select(x => new { x.MyUserId, x.RoleViewId })
            .ToListAsync())
            .Select(x => (x.MyUserId, x.RoleViewId))
            .ToHashSet();

        var users = await context.MyUser.AsNoTracking()
            .Where(x => x.RoleViewId != null)
            .ToListAsync();

        foreach (var user in users)
        {
            var roleViewId = user.RoleViewId!.Value;
            if (existing.Add((user.Id, roleViewId)))
            {
                context.UserRole.Add(new UserRole
                {
                    MyUserId = user.Id,
                    RoleViewId = roleViewId,
                });
            }
        }

        await context.SaveChangesAsync();
    }

    private async Task BackfillUserTeamsAsync()
    {
        var teamIdByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var team in await context.Team.AsNoTracking().ToListAsync())
        {
            teamIdByName.TryAdd(team.Name, team.Id);
        }

        var rolesById = await context.RoleView.AsNoTracking().ToDictionaryAsync(x => x.Id);

        var existing = (await context.UserTeam
            .Select(x => new { x.MyUserId, x.TeamId })
            .ToListAsync())
            .Select(x => (x.MyUserId, x.TeamId))
            .ToHashSet();

        var users = await context.MyUser.AsNoTracking()
            .Where(x => x.RoleViewId != null)
            .ToListAsync();

        foreach (var user in users)
        {
            if (!rolesById.TryGetValue(user.RoleViewId!.Value, out var role))
            {
                continue;
            }

            foreach (var teamName in TeamJsonHelper.Deserialize(role.DefaultTeamsJson, logger))
            {
                if (teamIdByName.TryGetValue(teamName, out var teamId)
                    && existing.Add((user.Id, teamId)))
                {
                    context.UserTeam.Add(new UserTeam
                    {
                        MyUserId = user.Id,
                        TeamId = teamId,
                    });
                }
            }
        }

        await context.SaveChangesAsync();
    }

    private List<string> DeserializePermissionNames(string? tabViewJson)
    {
        if (string.IsNullOrWhiteSpace(tabViewJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(tabViewJson) ?? [];
        }
        catch (Exception ex)
        {
            // 解析失敗等於「這個角色沒有任何權限」，回填會略過它，必須留下紀錄（LOG-08）。
            logger.LogWarning(ex, "Failed to parse role permission JSON during RBAC backfill. JsonLength={JsonLength}", tabViewJson.Length);
            return [];
        }
    }
}

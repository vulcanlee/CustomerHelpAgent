using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Systems;

namespace CustomerHelpAgent.Business.Startup;

/// <summary>
/// 售後展示的示範角色與帳號（<see cref="DemoSeedData.Roles"/>、<see cref="DemoSeedData.Accounts"/>；<c>DemoSettings:Enabled</c> 時才執行）。
/// <list type="bullet">
/// <item>角色依名稱、帳號依 <c>Account</c> 查找，不存在才建立。<b>角色的權限鍵每次啟動都對齊 <see cref="DemoSeedData.Roles"/></b>
/// （舊示範資料庫才拿得到新頁面的權限；管理員對示範角色手動加減的權限會被蓋回）；
/// 已存在帳號的密碼、姓名不動。</item>
/// <item>查找連已刪除的一起找、優先取未刪除的；找到已刪除的就還原（同 <see cref="SupportUserSeeder"/>），避免建出重複的列。</item>
/// <item>每個帳號都指定 <c>RoleViewId</c>：沒有主要角色的使用者每次換頁都會被登出。</item>
/// </list>
/// </summary>
public sealed class DemoAccountSeeder : IDatabaseSeeder
{
    private readonly BackendDBContext dbContext;
    private readonly IRbacWriteService rbacWriteService;
    private readonly DemoSettings demoSettings;
    private readonly ILogger<DemoAccountSeeder> logger;

    public DemoAccountSeeder(
        BackendDBContext dbContext,
        IRbacWriteService rbacWriteService,
        IOptions<DemoSettings> demoSettings,
        ILogger<DemoAccountSeeder> logger)
    {
        this.dbContext = dbContext;
        this.rbacWriteService = rbacWriteService;
        this.demoSettings = demoSettings.Value;
        this.logger = logger;
    }

    public int Order => 50;

    public string Name => "售後展示示範帳號";

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        if (!demoSettings.Enabled)
        {
            return;
        }

        // 角色、帳號與 RBAC 關聯表包在同一個交易：中途失敗時不會留下「有帳號、沒權限」的半套資料。
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var roleIdByName = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var demoRole in DemoSeedData.Roles)
        {
            roleIdByName[demoRole.Name] = await EnsureRoleAsync(demoRole, cancellationToken);
        }

        foreach (var account in DemoSeedData.Accounts)
        {
            await EnsureUserAsync(account, roleIdByName[account.RoleName], cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<int> EnsureRoleAsync(DemoSeedData.DemoRole demoRole, CancellationToken cancellationToken)
    {
        var role = await dbContext.RoleView
            .IgnoreQueryFilters([ISoftDeletable.FilterName])
            .Where(x => x.Name == demoRole.Name)
            .OrderBy(x => x.IsDeleted)
            .ThenBy(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (role is not null)
        {
            if (role.IsDeleted)
            {
                SoftDeleteHelper.Restore(role);
                await dbContext.SaveChangesAsync(cancellationToken);
                logger.LogWarning("Demo role was soft-deleted; restored it. RoleViewId={RoleViewId}", role.Id);
            }

            // 每次啟動都把權限鍵對齊 DemoSeedData：新版新增的頁面權限，舊的示範資料庫才拿得到。
            role.TabViewJson = Newtonsoft.Json.JsonConvert.SerializeObject(demoRole.PermissionKeys);
            await dbContext.SaveChangesAsync(cancellationToken);
            await rbacWriteService.SyncRolePermissionsAsync(dbContext, role.Id, demoRole.PermissionKeys);
            return role.Id;
        }

        role = new RoleView
        {
            Name = demoRole.Name,
            TabViewJson = Newtonsoft.Json.JsonConvert.SerializeObject(demoRole.PermissionKeys),
        };
        dbContext.RoleView.Add(role);
        await dbContext.SaveChangesAsync(cancellationToken);
        await rbacWriteService.SyncRolePermissionsAsync(dbContext, role.Id, demoRole.PermissionKeys);
        logger.LogInformation("Seeded demo role. RoleViewId={RoleViewId}", role.Id);
        return role.Id;
    }

    private async Task EnsureUserAsync(DemoSeedData.DemoAccount account, int roleViewId, CancellationToken cancellationToken)
    {
        var user = await dbContext.MyUser
            .IgnoreQueryFilters([ISoftDeletable.FilterName])
            .Where(x => x.Account == account.Account)
            .OrderBy(x => x.IsDeleted)
            .ThenBy(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (user is not null)
        {
            if (user.IsDeleted)
            {
                SoftDeleteHelper.Restore(user);
                await dbContext.SaveChangesAsync(cancellationToken);
                logger.LogWarning("Demo user was soft-deleted; restored it. UserId={UserId}", user.Id);
            }

            return;
        }

        user = new MyUser
        {
            Account = account.Account,
            Name = account.Name,
            IsAdmin = false,
            Salt = Guid.NewGuid().ToString(),
            Status = true,
            RoleViewId = roleViewId,
            Password = SecurePasswordHasher.HashPassword(demoSettings.DemoUserPassword),
        };
        dbContext.MyUser.Add(user);
        await dbContext.SaveChangesAsync(cancellationToken);
        await rbacWriteService.SyncUserRolesAsync(dbContext, user.Id, [roleViewId]);
        logger.LogInformation("Seeded demo user. UserId={UserId}", user.Id);
    }
}

using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Share.Helpers;

namespace CustomerHelpAgent.Tests;

public sealed class RbacBackfillServiceTests
{
    [Fact]
    public async Task RunAsync_ShouldCreatePermissionCatalog()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService();

        await service.RunAsync();

        var keys = await fixture.Context.Permission.AsNoTracking().Select(x => x.Key).ToListAsync();
        Assert.Contains(MagicObjectHelper.角色_首頁, keys);
        Assert.Contains(MagicObjectHelper.角色_專案項目, keys);
        Assert.Contains(MagicObjectHelper.角色_分類清單, keys);

        // 0.4.32 起「系統管理」群組改為管理員專屬（比照「統計與分析」），
        // 權限鍵不上架角色矩陣，因此也不會種進權限目錄。見 AdminOnlyPermissionTests。
        Assert.DoesNotContain(MagicObjectHelper.角色_使用者管理, keys);
        Assert.DoesNotContain(MagicObjectHelper.角色_角色管理, keys);
    }

    [Fact]
    public async Task RunAsync_ShouldLinkRolePermissionsFromTabViewJson()
    {
        await using var fixture = await Fixture.CreateAsync();
        var role = await fixture.AddRoleAsync(
            "檢視員",
            new[] { MagicObjectHelper.角色_首頁, MagicObjectHelper.角色_專案項目 });
        var service = fixture.CreateService();

        await service.RunAsync();

        var linkedKeys = await fixture.Context.RolePermissionMap.AsNoTracking()
            .Where(x => x.RoleViewId == role.Id)
            .Join(fixture.Context.Permission, m => m.PermissionId, p => p.Id, (m, p) => p.Key)
            .ToListAsync();

        Assert.Equal(2, linkedKeys.Count);
        Assert.Contains(MagicObjectHelper.角色_首頁, linkedKeys);
        Assert.Contains(MagicObjectHelper.角色_專案項目, linkedKeys);
    }

    [Fact]
    public async Task RunAsync_ShouldCreateUserRoleFromRoleViewId()
    {
        await using var fixture = await Fixture.CreateAsync();
        var role = await fixture.AddRoleAsync("一般", new[] { MagicObjectHelper.角色_首頁 });
        var user = await fixture.AddUserAsync("alice", role.Id);
        var service = fixture.CreateService();

        await service.RunAsync();

        var userRole = await fixture.Context.UserRole.AsNoTracking()
            .SingleAsync(x => x.MyUserId == user.Id);
        Assert.Equal(role.Id, userRole.RoleViewId);
    }

    [Fact]
    public async Task RunAsync_ShouldCreateUserTeamsFromRoleDefaultTeams()
    {
        await using var fixture = await Fixture.CreateAsync();
        var team = await fixture.AddTeamAsync("團隊A");
        var role = await fixture.AddRoleAsync("甲", new[] { MagicObjectHelper.角色_首頁 }, defaultTeams: new[] { "團隊A" });
        var user = await fixture.AddUserAsync("bob", role.Id);
        var service = fixture.CreateService();

        await service.RunAsync();

        var userTeam = await fixture.Context.UserTeam.AsNoTracking()
            .SingleAsync(x => x.MyUserId == user.Id);
        Assert.Equal(team.Id, userTeam.TeamId);
    }

    [Fact]
    public async Task RunAsync_ShouldBeIdempotent()
    {
        await using var fixture = await Fixture.CreateAsync();
        var team = await fixture.AddTeamAsync("團隊A");
        var role = await fixture.AddRoleAsync("甲", new[] { MagicObjectHelper.角色_首頁 }, defaultTeams: new[] { "團隊A" });
        var user = await fixture.AddUserAsync("bob", role.Id);
        var service = fixture.CreateService();

        await service.RunAsync();
        await service.RunAsync();

        Assert.Equal(1, await fixture.Context.UserRole.CountAsync(x => x.MyUserId == user.Id));
        Assert.Equal(1, await fixture.Context.UserTeam.CountAsync(x => x.MyUserId == user.Id));
        Assert.Equal(1, await fixture.Context.RolePermissionMap.CountAsync(x => x.RoleViewId == role.Id));
        Assert.Equal(
            1,
            await fixture.Context.Permission.CountAsync(x => x.Key == MagicObjectHelper.角色_首頁));
    }

    /// <summary>
    /// 0.4.32 之前 <c>角色_登出</c> 常數是 "登出 "（帶尾隨空白），該字串已寫進既有部署的
    /// <c>Permission.Key</c>。常數修好後，回填必須把舊資料一併正規化，
    /// 且**不能弄丟角色既有的授權**。
    /// </summary>
    [Fact]
    public async Task RunAsync_ShouldTrimLegacyPermissionKey_AndKeepRoleGrant()
    {
        await using var fixture = await Fixture.CreateAsync();
        var role = await fixture.AddRoleAsync("舊角色", new[] { "登出 " });
        var legacy = await fixture.AddPermissionAsync("登出 ");
        await fixture.AddRolePermissionAsync(role.Id, legacy.Id);
        var service = fixture.CreateService();

        await service.RunAsync();

        var keys = await fixture.Context.Permission.AsNoTracking().Select(x => x.Key).ToListAsync();
        Assert.Contains("登出", keys);
        Assert.DoesNotContain("登出 ", keys);

        // 授權仍在，且指向正規化後的那一列。
        var grantedKeys = await fixture.Context.RolePermissionMap.AsNoTracking()
            .Where(x => x.RoleViewId == role.Id)
            .Join(fixture.Context.Permission, m => m.PermissionId, p => p.Id, (_, p) => p.Key)
            .ToListAsync();
        Assert.Contains("登出", grantedKeys);
    }

    /// <summary>
    /// Permission.Key 有唯一索引，因此去空白後撞鍵時必須「合併」而非直接改寫。
    /// 兩個角色分別掛在髒鍵與乾淨鍵上，合併後兩者的授權都要保住。
    /// </summary>
    [Fact]
    public async Task RunAsync_ShouldMergeDuplicatePermissionKeys_WithoutLosingGrants()
    {
        await using var fixture = await Fixture.CreateAsync();
        var dirtyRole = await fixture.AddRoleAsync("髒鍵角色", new[] { "登出 " });
        var cleanRole = await fixture.AddRoleAsync("乾淨角色", new[] { "登出" });
        var dirty = await fixture.AddPermissionAsync("登出 ");
        var clean = await fixture.AddPermissionAsync("登出");
        await fixture.AddRolePermissionAsync(dirtyRole.Id, dirty.Id);
        await fixture.AddRolePermissionAsync(cleanRole.Id, clean.Id);
        var service = fixture.CreateService();

        await service.RunAsync();

        Assert.Equal(1, await fixture.Context.Permission.CountAsync(x => x.Key == "登出"));
        Assert.Equal(0, await fixture.Context.Permission.CountAsync(x => x.Key == "登出 "));

        var survivorId = await fixture.Context.Permission.Where(x => x.Key == "登出").Select(x => x.Id).SingleAsync();
        foreach (var roleId in new[] { dirtyRole.Id, cleanRole.Id })
        {
            Assert.Equal(
                1,
                await fixture.Context.RolePermissionMap.CountAsync(x => x.RoleViewId == roleId && x.PermissionId == survivorId));
        }
    }

    [Fact]
    public async Task RunAsync_ShouldTrimPermissionNamesInTabViewJson()
    {
        await using var fixture = await Fixture.CreateAsync();
        var role = await fixture.AddRoleAsync("舊角色", new[] { "登出 ", "登出", MagicObjectHelper.角色_首頁 });
        var service = fixture.CreateService();

        await service.RunAsync();

        var stored = await fixture.Context.RoleView.AsNoTracking()
            .Where(x => x.Id == role.Id)
            .Select(x => x.TabViewJson)
            .SingleAsync();
        var names = JsonSerializer.Deserialize<List<string>>(stored!)!;

        // 去空白後與既有項目重複者一併去重。
        Assert.Equal(new[] { "登出", MagicObjectHelper.角色_首頁 }, names);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly ILoggerFactory loggerFactory;

        private Fixture(SqliteConnection connection, BackendDBContext context)
        {
            this.connection = connection;
            Context = context;
            loggerFactory = LoggerFactory.Create(_ => { });
        }

        public BackendDBContext Context { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<BackendDBContext>()
                .UseSqlite(connection)
                .Options;
            var context = new BackendDBContext(options);
            await context.Database.EnsureCreatedAsync();
            return new Fixture(connection, context);
        }

        public RbacBackfillService CreateService()
            => new(Context, new RolePermissionService(), loggerFactory.CreateLogger<RbacBackfillService>());

        public async Task<Permission> AddPermissionAsync(string key)
        {
            var permission = new Permission { Key = key, DisplayName = key, GroupName = key, SortOrder = 0 };
            Context.Permission.Add(permission);
            await Context.SaveChangesAsync();
            return permission;
        }

        public async Task AddRolePermissionAsync(int roleViewId, int permissionId)
        {
            Context.RolePermissionMap.Add(new RolePermissionMap { RoleViewId = roleViewId, PermissionId = permissionId });
            await Context.SaveChangesAsync();
        }

        public async Task<RoleView> AddRoleAsync(string name, string[] permissions, string[]? defaultTeams = null)
        {
            var role = new RoleView
            {
                Name = name,
                TabViewJson = JsonSerializer.Serialize(permissions),
                DefaultTeamsJson = JsonSerializer.Serialize(defaultTeams ?? Array.Empty<string>()),
            };
            Context.RoleView.Add(role);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
            return role;
        }

        public async Task<MyUser> AddUserAsync(string account, int roleViewId)
        {
            var user = new MyUser
            {
                Account = account,
                Name = account,
                Password = "x",
                Status = true,
                RoleViewId = roleViewId,
            };
            Context.MyUser.Add(user);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
            return user;
        }

        public async Task<Team> AddTeamAsync(string name)
        {
            var team = new Team { Name = name, IsEnabled = true };
            Context.Team.Add(team);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
            return team;
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
            loggerFactory.Dispose();
        }
    }
}

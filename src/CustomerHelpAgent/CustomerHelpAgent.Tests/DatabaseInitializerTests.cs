using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Business.Startup;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Helpers;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 啟動時的資料庫準備（<see cref="DatabaseInitializer"/> 與三個內建 seeder，0.9.91）。
///
/// 0.9.90 之前這段寫在 Program.cs、完全沒有測試。這裡用暫存的**檔案**資料庫跑真正的 migration，
/// 每個 <see cref="Host"/> 代表一個「行程」：同一個檔案、各自的 DI 容器。
/// </summary>
public sealed class DatabaseInitializerTests : IDisposable
{
    private const string SupportPassword = "support-test-password";

    private readonly string directory = Path.Combine(Path.GetTempPath(), "CustomerHelpAgentDatabaseInitializer", Guid.NewGuid().ToString("N"));
    private readonly string connectionString;

    public DatabaseInitializerTests()
    {
        Directory.CreateDirectory(directory);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, MagicObjectHelper.SQLiteDatabaseFilename),
            ForeignKeys = true,
        }.ToString();
    }

    [Fact]
    public async Task FreshDatabase_ShouldMigrateAndSeedEverything()
    {
        using var host = CreateHost();
        await host.InitializeAsync();

        await using var db = host.CreateDbContext();
        var allPages = new RolePermissionService().GetRolePermissionAllName();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal(allPages.Order(), (await RolePagesAsync(db)).Order());
        Assert.Equal(allPages.Order(), (await RoleMapKeysAsync(db)).Order());
        Assert.Equal(allPages.Order(), (await db.Permission.Select(x => x.Key).ToListAsync()).Order());

        var support = await db.MyUser.SingleAsync(x => x.Account == "support");
        Assert.True(support.IsAdmin);
        Assert.Equal(PasswordVerificationOutcome.Success, SecurePasswordHasher.VerifyPassword(SupportPassword, support.Password, support.Salt));

        Assert.Equal("wal", await JournalModeAsync(db));
    }

    [Fact]
    public async Task RunningTwice_ShouldBeIdempotent()
    {
        using var host = CreateHost();
        await host.InitializeAsync();
        var before = await CountsAsync(host);

        await host.InitializeAsync();

        Assert.Equal(before, await CountsAsync(host));
    }

    [Fact]
    public async Task PageRemovedByAdmin_ShouldStayRemovedAfterRestart()
    {
        using var host = CreateHost();
        await host.InitializeAsync();

        // 管理員在角色管理取消勾選：TabViewJson 與權限列都移除（RbacWriteService.SyncRolePermissionsAsync 的效果）。
        await RemovePageFromDefaultRoleAsync(host, MagicObjectHelper.角色_分類清單);

        await host.InitializeAsync();

        await using var db = host.CreateDbContext();
        Assert.DoesNotContain(MagicObjectHelper.角色_分類清單, await RolePagesAsync(db));
        Assert.DoesNotContain(MagicObjectHelper.角色_分類清單, await RoleMapKeysAsync(db));
    }

    [Fact]
    public async Task NewPage_ShouldBeAddedToDefaultRole()
    {
        using var host = CreateHost();
        await host.InitializeAsync();

        // 模擬「這個頁面是新版程式才加入的」：權限目錄裡沒有它，角色也還沒有它。
        await RemovePageFromDefaultRoleAsync(host, MagicObjectHelper.角色_團隊清單);
        await using (var db = host.CreateDbContext())
        {
            await db.Permission.Where(x => x.Key == MagicObjectHelper.角色_團隊清單).ExecuteDeleteAsync();
        }

        await host.InitializeAsync();

        await using var verify = host.CreateDbContext();
        Assert.Contains(MagicObjectHelper.角色_團隊清單, await RolePagesAsync(verify));
        Assert.Contains(MagicObjectHelper.角色_團隊清單, await RoleMapKeysAsync(verify));
    }

    [Fact]
    public async Task LegacyKeyWithTrailingSpace_ShouldNotBeTreatedAsNewPage()
    {
        using var host = CreateHost();
        await host.InitializeAsync();

        // 0.4.32 之前的資料庫：權限目錄裡的鍵帶尾端空白；管理員已從預設角色移除這個頁面。
        await RemovePageFromDefaultRoleAsync(host, MagicObjectHelper.角色_登出);
        await using (var db = host.CreateDbContext())
        {
            await db.Permission.Where(x => x.Key == MagicObjectHelper.角色_登出)
                .ExecuteUpdateAsync(x => x.SetProperty(p => p.Key, MagicObjectHelper.角色_登出 + " "));
        }

        await host.InitializeAsync();

        await using var verify = host.CreateDbContext();
        Assert.DoesNotContain(MagicObjectHelper.角色_登出, await RolePagesAsync(verify));
        Assert.Equal(1, await verify.Permission.CountAsync(x => x.Key.Trim() == MagicObjectHelper.角色_登出));
    }

    [Fact]
    public async Task SoftDeletedSupportAndDefaultRole_ShouldBeRestoredInsteadOfDuplicated()
    {
        // 0.9.95 起使用者與角色是軟刪除。seeder 若只看有過濾的集合，會把它們當成不存在而建出第二份
        // （預設角色還會帶著全部權限重建）。服務層禁止刪除這兩者，這裡測的是最後一道防線。
        using var host = CreateHost();
        await host.InitializeAsync();
        await using (var db = host.CreateDbContext())
        {
            await db.MyUser.Where(x => x.Account == "support").ExecuteUpdateAsync(x => x.SetProperty(u => u.IsDeleted, true));
            await db.RoleView.Where(x => x.Name == MagicObjectHelper.預設角色).ExecuteUpdateAsync(x => x.SetProperty(r => r.IsDeleted, true));
        }

        await host.InitializeAsync();

        await using var verify = host.CreateDbContext();
        var supports = await verify.MyUser.IgnoreQueryFilters([ISoftDeletable.FilterName]).Where(x => x.Account == "support").ToListAsync();
        Assert.False(Assert.Single(supports).IsDeleted);
        var roles = await verify.RoleView.IgnoreQueryFilters([ISoftDeletable.FilterName]).Where(x => x.Name == MagicObjectHelper.預設角色).ToListAsync();
        Assert.False(Assert.Single(roles).IsDeleted);
    }

    [Fact]
    public async Task ActiveSupportWithOlderDeletedDuplicate_ShouldKeepTheActiveOne()
    {
        // 已刪除的那筆 Id 較小：查找若沒有「優先取未刪除」，會把它還原，變成兩個有效的 support。
        using var host = CreateHost();
        await host.InitializeAsync();
        await using (var db = host.CreateDbContext())
        {
            var original = await db.MyUser.SingleAsync(x => x.Account == "support");
            original.IsDeleted = true;
            db.MyUser.Add(new MyUser
            {
                Account = "support",
                Name = "support",
                Salt = Guid.NewGuid().ToString(),
                Password = SecurePasswordHasher.HashPassword(SupportPassword),
                RoleViewId = original.RoleViewId,
            });
            await db.SaveChangesAsync();
        }

        await host.InitializeAsync();

        await using var verify = host.CreateDbContext();
        var supports = await verify.MyUser.IgnoreQueryFilters([ISoftDeletable.FilterName]).Where(x => x.Account == "support").OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(2, supports.Count);
        Assert.True(supports[0].IsDeleted, "較舊的已刪除重複列被還原了 —— seeder 沒有優先取未刪除的列。");
        Assert.False(supports[1].IsDeleted);
        Assert.True(supports[1].IsAdmin);
    }

    [Fact]
    public async Task ChangedSupportPassword_ShouldBeResetToConfiguredValue()
    {
        using var host = CreateHost();
        await host.InitializeAsync();
        await using (var db = host.CreateDbContext())
        {
            var support = await db.MyUser.SingleAsync(x => x.Account == "support");
            support.Password = SecurePasswordHasher.HashPassword("changed-by-someone");
            support.IsAdmin = false;
            await db.SaveChangesAsync();
        }

        await host.InitializeAsync();

        await using var verify = host.CreateDbContext();
        var reset = await verify.MyUser.SingleAsync(x => x.Account == "support");
        Assert.True(reset.IsAdmin);
        Assert.Equal(PasswordVerificationOutcome.Success, SecurePasswordHasher.VerifyPassword(SupportPassword, reset.Password, reset.Salt));
    }

    [Fact]
    public async Task TwoProcessesStartingTogether_ShouldNotDuplicateSeedData()
    {
        // 內建 seeder 很快，加上 EF 的 migration 鎖會讓兩個行程自然錯開，競態幾乎重現不了。
        // 用一個「先查、等一下、再寫」的慢 seeder 把時間窗放大：沒有跨行程鎖時兩邊都會寫入。
        using var first = CreateHost(extraSeeder: typeof(SlowCheckThenInsertSeeder));
        using var second = CreateHost(extraSeeder: typeof(SlowCheckThenInsertSeeder));

        await Task.WhenAll(
            Task.Run(() => first.InitializeAsync()),
            Task.Run(() => second.InitializeAsync()));

        await using var db = first.CreateDbContext();
        Assert.Equal(1, await db.RoleView.CountAsync(x => x.Name == SlowCheckThenInsertSeeder.RoleName));
        Assert.Equal(1, await db.RoleView.CountAsync(x => x.Name == MagicObjectHelper.預設角色));
        Assert.Equal(1, await db.MyUser.CountAsync(x => x.Account == "support"));
    }

    [Fact]
    public async Task LockHeldByAnotherProcess_ShouldTimeOut()
    {
        var lockPath = Path.Combine(directory, MagicObjectHelper.SQLiteDatabaseFilename) + ".migration.lock";
        using var heldByOtherProcess = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var host = CreateHost(lockTimeout: TimeSpan.FromSeconds(1));

        var exception = await Assert.ThrowsAsync<TimeoutException>(() => host.InitializeAsync());

        Assert.Contains(lockPath, exception.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        // 連線池佔著 .db／-wal／-shm 時刪不掉目錄。
        SqliteConnection.ClearPool(new SqliteConnection(connectionString));
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // 暫存目錄清不掉不影響測試結果；系統的暫存清理會處理。
        }
    }

    private Host CreateHost(TimeSpan? lockTimeout = null, Type? extraSeeder = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<BackendDBContext>(options => options.UseSqlite(connectionString));
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<BackendDBContext>>().CreateDbContext());
        services.AddScoped<RolePermissionService>();
        services.AddScoped<IRbacBackfillService, RbacBackfillService>();
        services.AddSingleton(Options.Create(new BootstrapSettings { SupportPassword = SupportPassword }));
        services.AddSingleton(Options.Create(new DatabaseInitializerOptions { LockTimeout = lockTimeout ?? TimeSpan.FromSeconds(30) }));
        services.AddSingleton<IDatabaseInitializer, DatabaseInitializer>();
        services.AddScoped<IDatabaseSeeder, DefaultRoleViewSeeder>();
        services.AddScoped<IDatabaseSeeder, SupportUserSeeder>();
        services.AddScoped<IDatabaseSeeder, RbacBackfillSeeder>();
        if (extraSeeder is not null)
        {
            services.AddScoped(typeof(IDatabaseSeeder), extraSeeder);
        }

        return new Host(services.BuildServiceProvider());
    }

    private static async Task RemovePageFromDefaultRoleAsync(Host host, string page)
    {
        await using var db = host.CreateDbContext();
        var role = await db.RoleView.SingleAsync(x => x.Name == MagicObjectHelper.預設角色);
        var pages = Newtonsoft.Json.JsonConvert.DeserializeObject<List<string>>(role.TabViewJson!)!;
        pages.Remove(page);
        role.TabViewJson = Newtonsoft.Json.JsonConvert.SerializeObject(pages);
        await db.SaveChangesAsync();

        await db.RolePermissionMap
            .Where(x => x.RoleViewId == role.Id && x.Permission!.Key == page)
            .ExecuteDeleteAsync();
    }

    private static async Task<List<string>> RolePagesAsync(BackendDBContext db)
    {
        var json = await db.RoleView.Where(x => x.Name == MagicObjectHelper.預設角色).Select(x => x.TabViewJson).SingleAsync();
        return Newtonsoft.Json.JsonConvert.DeserializeObject<List<string>>(json!)!;
    }

    private static Task<List<string>> RoleMapKeysAsync(BackendDBContext db)
        => db.RolePermissionMap
            .Where(x => x.RoleView!.Name == MagicObjectHelper.預設角色)
            .Select(x => x.Permission!.Key)
            .ToListAsync();

    private static async Task<string?> JournalModeAsync(BackendDBContext db)
    {
        await db.Database.OpenConnectionAsync();
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "PRAGMA journal_mode;";
            return (string?)await command.ExecuteScalarAsync();
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static async Task<(int Roles, int Users, int Permissions, int Maps, int UserRoles)> CountsAsync(Host host)
    {
        await using var db = host.CreateDbContext();
        return (
            await db.RoleView.CountAsync(),
            await db.MyUser.CountAsync(),
            await db.Permission.CountAsync(),
            await db.RolePermissionMap.CountAsync(),
            await db.UserRole.CountAsync());
    }

    /// <summary>
    /// 典型的「先檢查是否存在、不存在才新增」種子寫法，中間刻意停頓。
    /// RoleView.Name 沒有唯一索引，兩個行程同時執行而沒有鎖時，會各新增一筆。
    /// </summary>
    private sealed class SlowCheckThenInsertSeeder : IDatabaseSeeder
    {
        public const string RoleName = "慢速種子測試角色";

        private readonly BackendDBContext db;

        public SlowCheckThenInsertSeeder(BackendDBContext db)
        {
            this.db = db;
        }

        public int Order => 15;

        public string Name => "慢速種子（測試）";

        public async Task SeedAsync(CancellationToken cancellationToken)
        {
            if (await db.RoleView.AnyAsync(x => x.Name == RoleName, cancellationToken))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            db.RoleView.Add(new AccessDatas.Models.RoleView { Name = RoleName, TabViewJson = "[]" });
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>一個「行程」：自己的 DI 容器，連到同一個資料庫檔。</summary>
    private sealed class Host : IDisposable
    {
        private readonly ServiceProvider provider;

        public Host(ServiceProvider provider)
        {
            this.provider = provider;
        }

        public Task InitializeAsync()
            => provider.GetRequiredService<IDatabaseInitializer>().InitializeAsync(CancellationToken.None);

        public BackendDBContext CreateDbContext()
            => provider.GetRequiredService<IDbContextFactory<BackendDBContext>>().CreateDbContext();

        public void Dispose() => provider.Dispose();
    }
}

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
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
/// 售後展示的示範帳號（<see cref="DemoAccountSeeder"/>）與示範商品、訂單、規則（<see cref="DemoCatalogSeeder"/>）。
/// 依正式啟動的順序跑 10／20／30／50／60 五個 seeder，權限一律經 <see cref="PermissionChecker"/> 驗證（UI 與 API 的單一權威來源）。
/// </summary>
public sealed class DemoSeederTests
{
    private const string DemoPassword = "demo1234";

    /// <summary>本地時間（UTC+8）2026/10/23 10:00。</summary>
    private static readonly DateTimeOffset UtcNow = new(2026, 10, 23, 2, 0, 0, TimeSpan.Zero);

    private static readonly DateTime LocalNow = new(2026, 10, 23, 10, 0, 0);

    [Fact]
    public async Task DemoSeeders_WhenEnabled_ShouldCreateAccountsWithPermissions()
    {
        await using var fixture = await Fixture.CreateAsync();

        await fixture.RunAllSeedersAsync(Enabled());

        await using var db = fixture.CreateDbContext();
        var checker = new PermissionChecker(db, NullLogger<PermissionChecker>.Instance);

        var customer = await KeysAsync(db, checker, "customer1");
        Assert.Contains(MagicObjectHelper.角色_顧客商店, customer);
        Assert.DoesNotContain(MagicObjectHelper.角色_首頁, customer);

        var agent = await KeysAsync(db, checker, "agent1");
        Assert.Contains(MagicObjectHelper.角色_售後審核, agent);
        Assert.DoesNotContain(MagicObjectHelper.角色_售後規則, agent);

        var merchant = await KeysAsync(db, checker, "merchant1");
        Assert.Contains(MagicObjectHelper.角色_售後規則, merchant);

        foreach (var account in new[] { "customer1", "customer2", "agent1", "merchant1" })
        {
            var user = await db.MyUser.SingleAsync(x => x.Account == account);
            Assert.NotNull(user.RoleViewId);
            Assert.True(user.Status);
            Assert.False(user.IsAdmin);
            Assert.Equal(PasswordVerificationOutcome.Success, SecurePasswordHasher.VerifyPassword(DemoPassword, user.Password, user.Salt));
        }

        Assert.Equal("王大同", (await db.MyUser.SingleAsync(x => x.Account == "customer1")).Name);
    }

    [Fact]
    public async Task DemoCatalogSeeder_ShouldSeedFlagshipOrders()
    {
        await using var fixture = await Fixture.CreateAsync();

        await fixture.RunAllSeedersAsync(Enabled());

        await using var db = fixture.CreateDbContext();
        var customer1 = await db.MyUser.SingleAsync(x => x.Account == "customer1");
        var orders = await db.SalesOrder
            .Include(x => x.Items).ThenInclude(x => x.ProductVariant).ThenInclude(x => x!.Product)
            .Where(x => x.CustomerUserId == customer1.Id)
            .ToListAsync();
        Assert.Equal(2, orders.Count);

        var black40 = orders.Single(o => o.Items.Any(i => i.ProductVariant!.Color == "黑" && i.ProductVariant.Size == "40"));
        var white41 = orders.Single(o => o.Items.Any(i => i.ProductVariant!.Color == "白" && i.ProductVariant.Size == "41"));
        Assert.Equal(LocalNow.AddDays(-3), black40.DeliveredAt);
        Assert.Equal(LocalNow.AddDays(-2), white41.DeliveredAt);
        Assert.All(orders.SelectMany(o => o.Items), i => Assert.Equal("SH-RUN01", i.ProductVariant!.Product!.Code));

        var policies = await db.AfterSalesPolicy.ToListAsync();
        Assert.Equal(5, policies.Count);
        Assert.All(policies, p => Assert.Equal(1, p.Version));
        Assert.All(policies, p => Assert.Equal(LocalNow.AddDays(-90), p.EffectiveFrom));

        var shipment = await db.Shipment.Include(x => x.Events).SingleAsync();
        Assert.Equal(LocalNow.AddDays(-4), shipment.ShippedAt);
        Assert.Equal(3, shipment.Events.Count);
        var shippedOrder = await db.SalesOrder.SingleAsync(x => x.Id == shipment.SalesOrderId);
        Assert.Null(shippedOrder.DeliveredAt);

        Assert.Equal(2 * 7 + 2 * 4, await db.ProductVariant.CountAsync());
        Assert.Equal(0, (await db.ProductVariant.SingleAsync(x => x.Product!.Code == "SH-RUN01" && x.Color == "白" && x.Size == "42")).Stock);
        Assert.Equal(5, (await db.ProductVariant.SingleAsync(x => x.Product!.Code == "SH-RUN01" && x.Color == "黑" && x.Size == "41")).Stock);
    }

    [Fact]
    public async Task DemoSeeders_RunTwice_ShouldNotDuplicate()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.RunAllSeedersAsync(Enabled());
        var before = await fixture.CountsAsync();

        await fixture.RunAllSeedersAsync(Enabled());

        Assert.Equal(before, await fixture.CountsAsync());
    }

    [Fact]
    public async Task DemoSeeders_WhenDisabled_ShouldDoNothing()
    {
        await using var fixture = await Fixture.CreateAsync();

        await fixture.RunAllSeedersAsync(new DemoSettings { Enabled = false, DemoUserPassword = string.Empty });

        await using var db = fixture.CreateDbContext();
        Assert.Equal(new[] { "support" }, await db.MyUser.Select(x => x.Account).ToListAsync());
        Assert.Equal(new[] { MagicObjectHelper.預設角色 }, await db.RoleView.Select(x => x.Name).ToListAsync());
        Assert.False(await db.Product.AnyAsync());
        Assert.False(await db.SalesOrder.AnyAsync());
        Assert.False(await db.AfterSalesPolicy.AnyAsync());
    }

    private static DemoSettings Enabled() => new() { Enabled = true, DemoUserPassword = DemoPassword };

    private static async Task<IReadOnlyCollection<string>> KeysAsync(BackendDBContext db, PermissionChecker checker, string account)
    {
        var user = await db.MyUser.SingleAsync(x => x.Account == account);
        return await checker.GetEffectivePermissionKeysAsync(user.Id);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly TestDbContextFactory factory;

        private Fixture(SqliteConnection connection)
        {
            this.connection = connection;
            factory = new TestDbContextFactory(connection);
        }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var fixture = new Fixture(connection);
            await using var db = fixture.CreateDbContext();
            await db.Database.EnsureCreatedAsync();
            return fixture;
        }

        public BackendDBContext CreateDbContext() => factory.CreateDbContext();

        /// <summary>與 <see cref="DatabaseInitializer"/> 相同：所有 seeder 共用一個 context，依 Order 執行，之間清空追蹤狀態。</summary>
        public async Task RunAllSeedersAsync(DemoSettings demoSettings)
        {
            await using var db = CreateDbContext();
            var demoOptions = Options.Create(demoSettings);
            var rolePermissionService = new RolePermissionService();
            IDatabaseSeeder[] seeders =
            [
                new DefaultRoleViewSeeder(db, rolePermissionService, NullLogger<DefaultRoleViewSeeder>.Instance),
                new SupportUserSeeder(db, Options.Create(new BootstrapSettings { SupportPassword = "support-test-password" }), NullLogger<SupportUserSeeder>.Instance),
                new RbacBackfillSeeder(new RbacBackfillService(db, rolePermissionService, NullLogger<RbacBackfillService>.Instance), NullLogger<RbacBackfillSeeder>.Instance),
                new DemoAccountSeeder(db, new RbacWriteService(db, NullLogger<RbacWriteService>.Instance), demoOptions, NullLogger<DemoAccountSeeder>.Instance),
                new DemoCatalogSeeder(db, demoOptions, new ManualTimeProvider(UtcNow), NullLogger<DemoCatalogSeeder>.Instance),
            ];

            foreach (var seeder in seeders.OrderBy(x => x.Order))
            {
                await seeder.SeedAsync(CancellationToken.None);
                db.ChangeTracker.Clear();
            }
        }

        public async Task<string> CountsAsync()
        {
            await using var db = CreateDbContext();
            var counts = new[]
            {
                await db.RoleView.CountAsync(),
                await db.MyUser.CountAsync(),
                await db.UserRole.CountAsync(),
                await db.Permission.CountAsync(),
                await db.RolePermissionMap.CountAsync(),
                await db.Product.CountAsync(),
                await db.ProductVariant.CountAsync(),
                await db.SalesOrder.CountAsync(),
                await db.SalesOrderItem.CountAsync(),
                await db.Shipment.CountAsync(),
                await db.ShipmentEvent.CountAsync(),
                await db.AfterSalesPolicy.CountAsync(),
            };
            return string.Join(",", counts);
        }

        public async ValueTask DisposeAsync() => await connection.DisposeAsync();
    }
}

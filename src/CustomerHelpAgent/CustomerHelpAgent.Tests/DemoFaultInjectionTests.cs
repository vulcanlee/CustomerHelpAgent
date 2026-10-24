using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Business.Startup;
using CustomerHelpAgent.Models.Others;
using CustomerHelpAgent.Models.Shop;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Agents;
using CustomerHelpAgent.Share.Demo;
using CustomerHelpAgent.Share.Shop;
using CustomerHelpAgent.Web.Agents.AfterSales;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 7 種故障模擬注入模擬業務服務與 Agent 工具（展示用）：服務層故障讓 Agent 與傳統流程同受影響；
/// 權限過期只影響 Agent 工具（傳統流程的登入失效由真實驗證機制處理）。資料取自 <see cref="DemoSeedData"/>。
/// </summary>
public sealed class DemoFaultInjectionTests : IAsyncLifetime
{
    /// <summary>本地時間（UTC+8）2026/10/23 10:00。</summary>
    private static readonly DateTimeOffset UtcNow = new(2026, 10, 23, 2, 0, 0, TimeSpan.Zero);

    private readonly string fileRoot = Path.Combine(Path.GetTempPath(), "DemoFaultTests-" + Guid.NewGuid().ToString("N"));
    private readonly RecordingAuditLogService audit = new();
    private readonly AgentPhotoInbox inbox = new();
    private readonly Guid sessionKey = Guid.NewGuid();
    private SqliteConnection connection = null!;
    private TestDbContextFactory factory = null!;
    private BackendDBContext permissionContext = null!;
    private DemoControlService demo = null!;
    private int customer1;
    private int customer2;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(fileRoot);
        connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        factory = new TestDbContextFactory(connection);
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            var demoOptions = Options.Create(new DemoSettings { Enabled = true, DemoUserPassword = "demo1234" });
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

            customer1 = (await db.MyUser.SingleAsync(x => x.Account == DemoSeedData.Customer1)).Id;
            customer2 = (await db.MyUser.SingleAsync(x => x.Account == DemoSeedData.Customer2)).Id;
        }

        permissionContext = factory.CreateDbContext();
        demo = TestDemoControl.Create(factory, enabled: true, new ManualTimeProvider(UtcNow));
        await demo.LoadAsync();
    }

    public async Task DisposeAsync()
    {
        await permissionContext.DisposeAsync();
        await connection.DisposeAsync();
        if (Directory.Exists(fileRoot))
        {
            Directory.Delete(fileRoot, recursive: true);
        }
    }

    private (ShopOrderService Orders, AfterSalesPolicyService Policies, AfterSalesRequestService Requests, CurrentUserService CurrentUser) CreateServices(int userId)
    {
        var time = new ManualTimeProvider(UtcNow);
        var currentUser = new CurrentUserService { CurrentUser = new CurrentUser { Id = userId, Account = "customer" } };
        var settings = new SystemSettings();
        settings.ExternalFileSystem.AfterSalesFilePath = fileRoot;
        var policyService = new AfterSalesPolicyService(factory, audit, currentUser, demo, time, NullLogger<AfterSalesPolicyService>.Instance);
        var orderService = new ShopOrderService(factory, demo, NullLogger<ShopOrderService>.Instance);
        var requestService = new AfterSalesRequestService(
            factory,
            policyService,
            orderService,
            new AfterSalesFileStore(Options.Create(settings), NullLogger<AfterSalesFileStore>.Instance),
            audit,
            currentUser,
            time,
            NullLogger<AfterSalesRequestService>.Instance);
        return (orderService, policyService, requestService, currentUser);
    }

    private AfterSalesAgentTools CreateTools(int? userId = null, TimeSpan? toolTimeout = null)
    {
        var id = userId ?? customer1;
        var (orders, policies, requests, currentUser) = CreateServices(id);
        return new AfterSalesAgentTools(
            orders,
            policies,
            requests,
            new CustomerMemoryService(factory, new RecordingAuditLogService(), new ManualTimeProvider(UtcNow), NullLogger<CustomerMemoryService>.Instance),
            new PermissionChecker(permissionContext, NullLogger<PermissionChecker>.Instance),
            currentUser,
            inbox,
            demo,
            new ManualTimeProvider(UtcNow),
            NullLogger<AfterSalesAgentTools>.Instance,
            new AfterSalesToolContext
            {
                CustomerUserId = id,
                SessionKey = sessionKey,
                ToolTimeout = toolTimeout ?? TimeSpan.FromSeconds(15),
            });
    }

    private static async Task<ToolOutcome> InvokeAsync(AfterSalesAgentTools tools, string name, Dictionary<string, object?>? args = null)
    {
        var result = await tools.CreateFunctions().Single(x => x.Name == name).InvokeAsync(new AIFunctionArguments(args ?? []));
        Assert.True(ToolOutcome.TryParse(result, out var outcome));
        return outcome!;
    }

    private async Task<int> ItemIdAsync(string orderNo)
    {
        await using var db = factory.CreateDbContext();
        return await db.SalesOrderItem.Where(x => x.SalesOrder!.OrderNo == orderNo).Select(x => x.Id).SingleAsync();
    }

    private async Task<int> OrderIdAsync(string orderNo)
    {
        await using var db = factory.CreateDbContext();
        return await db.SalesOrder.Where(x => x.OrderNo == orderNo).Select(x => x.Id).SingleAsync();
    }

    private static JsonElement Data(ToolOutcome outcome) => Assert.IsType<JsonElement>(outcome.Data);

    [Fact]
    public async Task Fault_OrderServiceTimeout_AgentToolReturnsTimeout()
    {
        demo.FaultDelay = TimeSpan.FromSeconds(5);
        await demo.SetFaultAsync(DemoFault.OrderServiceTimeout, true);
        var tools = CreateTools(toolTimeout: TimeSpan.FromMilliseconds(200));

        var stopwatch = Stopwatch.StartNew();
        var outcome = await InvokeAsync(tools, AfterSalesAgentTools.QueryMyOrdersName);
        stopwatch.Stop();

        Assert.Equal(ToolOutcomeStatus.Timeout, outcome.Status);
        Assert.True(outcome.Retryable);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(4), $"工具應在工具逾時就停止等待，實際 {stopwatch.Elapsed}。");
    }

    /// <summary>
    /// 工具逾時後放棄等待的服務工作稍後會以 <see cref="TimeoutException"/> 結束；沒人觀察的話，GC 時會觸發
    /// <see cref="TaskScheduler.UnobservedTaskException"/>，被 <c>ProcessExceptionHooks</c> 記成一筆 Error（甚至告警）。
    /// </summary>
    [Fact]
    public async Task Fault_OrderServiceTimeout_OrphanedTaskIsObserved()
    {
        var unobserved = new List<Exception>();
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            if (e.Exception.Flatten().InnerExceptions.Any(x => x is TimeoutException && x.Message.Contains("故障模擬", StringComparison.Ordinal)))
            {
                lock (unobserved)
                {
                    unobserved.Add(e.Exception);
                }
            }
        }

        demo.FaultDelay = TimeSpan.FromMilliseconds(300);
        await demo.SetFaultAsync(DemoFault.OrderServiceTimeout, true);
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            await RunTimedOutToolAsync();

            // 等被放棄的服務工作結束（FaultDelay 之後拋出），再強制回收讓未觀察的例外浮現。
            await Task.Delay(TimeSpan.FromMilliseconds(800));
            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            lock (unobserved)
            {
                Assert.Empty(unobserved);
            }
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    /// <summary>獨立方法：回傳後不留任何指向被放棄工作的區域變數。</summary>
    private async Task RunTimedOutToolAsync()
    {
        var outcome = await InvokeAsync(CreateTools(toolTimeout: TimeSpan.FromMilliseconds(50)), AfterSalesAgentTools.QueryMyOrdersName);
        Assert.Equal(ToolOutcomeStatus.Timeout, outcome.Status);
    }

    [Fact]
    public async Task Fault_OrderServiceTimeout_ServiceThrowsAfterDelayInsteadOfHanging()
    {
        demo.FaultDelay = TimeSpan.FromMilliseconds(20);
        await demo.SetFaultAsync(DemoFault.OrderServiceTimeout, true);
        var orders = CreateServices(customer1).Orders;
        var orderId = await OrderIdAsync("SO-DEMO-0001");
        var itemId = await ItemIdAsync("SO-DEMO-0001");

        foreach (var call in new Func<Task>[]
        {
            () => orders.GetMyOrdersAsync(customer1),
            () => orders.GetMyOrderDetailAsync(customer1, orderId),
            () => orders.GetMyOrderItemAsync(customer1, itemId),
            () => orders.GetVariantOptionsAsync(customer1, itemId),
            () => orders.GetVariantStockAsync(customer1, itemId),
        })
        {
            var exception = await Assert.ThrowsAsync<TimeoutException>(call);
            Assert.Contains("故障模擬", exception.Message);
        }
    }

    [Fact]
    public async Task Fault_OrdersNotFound_ToolNotFoundAndServiceReturnsNothing()
    {
        await demo.SetFaultAsync(DemoFault.OrdersNotFound, true);
        var orders = CreateServices(customer1).Orders;
        var itemId = await ItemIdAsync("SO-DEMO-0001");

        var outcome = await InvokeAsync(CreateTools(), AfterSalesAgentTools.QueryMyOrdersName);

        Assert.Equal(ToolOutcomeStatus.NotFound, outcome.Status);
        Assert.Empty(await orders.GetMyOrdersAsync(customer1));
        Assert.Null(await orders.GetMyOrderDetailAsync(customer1, await OrderIdAsync("SO-DEMO-0001")));
        Assert.Null(await orders.GetMyOrderItemAsync(customer1, itemId));
    }

    [Fact]
    public async Task Fault_ShipmentAnomaly_TrackShipmentShowsAnomaly()
    {
        await demo.SetFaultAsync(DemoFault.ShipmentAnomaly, true);
        var greyL = await ItemIdAsync("SO-DEMO-0004");

        var outcome = await InvokeAsync(CreateTools(customer2), AfterSalesAgentTools.TrackShipmentName, new() { ["orderItemId"] = greyL });

        Assert.Equal(ToolOutcomeStatus.Ok, outcome.Status);
        var shipment = Data(outcome);
        Assert.Equal("配送異常", shipment.GetProperty("status_text").GetString());
        Assert.Contains(shipment.GetProperty("events").EnumerateArray(), e => e.GetString()!.Contains("配送異常，物流中心查詢中", StringComparison.Ordinal));

        var detail = await CreateServices(customer2).Orders.GetMyOrderDetailAsync(customer2, await OrderIdAsync("SO-DEMO-0004"));
        Assert.Equal("配送異常", detail!.Shipment!.StatusText);
        Assert.Equal("配送異常，物流中心查詢中", detail.Shipment.Events[^1].Description);

        // 只改讀出的結果，不寫回資料庫。
        await using var db = factory.CreateDbContext();
        Assert.Equal("運送中", await db.Shipment.Select(x => x.StatusText).SingleAsync());
    }

    [Fact]
    public async Task Fault_PolicyServiceUnavailable_ToolUnavailableAndTraditionalSubmitFails()
    {
        await demo.SetFaultAsync(DemoFault.PolicyServiceUnavailable, true);
        var black40 = await ItemIdAsync("SO-DEMO-0001");

        var outcome = await InvokeAsync(CreateTools(), AfterSalesAgentTools.GetEffectivePolicyName, new()
        {
            ["orderItemId"] = black40,
            ["issueType"] = "size_exchange",
        });
        Assert.Equal(ToolOutcomeStatus.Unavailable, outcome.Status);

        var requests = CreateServices(customer1).Requests;
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => requests.SubmitAsync(customer1, new SubmitAfterSalesInput
        {
            CaseKey = Guid.NewGuid(),
            OrderItemId = black40,
            Type = AfterSalesType.SizeExchange,
            TargetSize = "41",
            IsWorn = false,
            Description = "尺碼太小",
            Channel = AfterSalesChannel.Traditional,
        }, []));
        Assert.Contains("故障模擬", exception.Message);

        await using var db = factory.CreateDbContext();
        Assert.False(await db.AfterSalesRequest.AnyAsync());
    }

    [Fact]
    public async Task Fault_PolicyConflict_ToolConflictWithSyntheticVersion()
    {
        await demo.SetFaultAsync(DemoFault.PolicyConflict, true);
        var black40 = await ItemIdAsync("SO-DEMO-0001");
        int policyCount;
        await using (var db = factory.CreateDbContext())
        {
            policyCount = await db.AfterSalesPolicy.CountAsync();
        }

        var outcome = await InvokeAsync(CreateTools(), AfterSalesAgentTools.GetEffectivePolicyName, new()
        {
            ["orderItemId"] = black40,
            ["issueType"] = "size_exchange",
        });
        Assert.Equal(ToolOutcomeStatus.Conflict, outcome.Status);

        var lookup = await CreateServices(customer1).Policies.GetEffectiveAsync(
            AfterSalesType.SizeExchange, ProductCategories.Shoes, new DateTime(2026, 10, 23, 10, 0, 0));
        Assert.Equal(PolicyLookupStatus.Conflict, lookup.Status);
        Assert.Equal(2, lookup.Policies.Count);
        var real = Assert.Single(lookup.Policies, x => x.Id > 0);
        var synthetic = Assert.Single(lookup.Policies, x => x.Id <= 0);
        Assert.Equal(real.Code, synthetic.Code);
        Assert.Equal(real.Version + 1, synthetic.Version);
        Assert.EndsWith("（衝突版本）", synthetic.Title, StringComparison.Ordinal);

        await using (var db = factory.CreateDbContext())
        {
            Assert.Equal(policyCount, await db.AfterSalesPolicy.CountAsync());
        }
    }

    [Fact]
    public async Task Fault_PolicyNotFound_ToolNoPolicy()
    {
        await demo.SetFaultAsync(DemoFault.PolicyNotFound, true);
        var black40 = await ItemIdAsync("SO-DEMO-0001");

        var outcome = await InvokeAsync(CreateTools(), AfterSalesAgentTools.GetEffectivePolicyName, new()
        {
            ["orderItemId"] = black40,
            ["issueType"] = "size_exchange",
        });

        Assert.Equal(ToolOutcomeStatus.NoPolicy, outcome.Status);
        var lookup = await CreateServices(customer1).Policies.GetEffectiveAsync(
            AfterSalesType.SizeExchange, ProductCategories.Shoes, new DateTime(2026, 10, 23, 10, 0, 0));
        Assert.Equal(PolicyLookupStatus.NoPolicy, lookup.Status);
        Assert.Empty(lookup.Policies);
    }

    [Fact]
    public async Task Fault_PermissionExpired_ToolForbidden()
    {
        await demo.SetFaultAsync(DemoFault.PermissionExpired, true);

        var outcome = await InvokeAsync(CreateTools(), AfterSalesAgentTools.QueryMyOrdersName);

        Assert.Equal(ToolOutcomeStatus.Forbidden, outcome.Status);
        // 只影響 Agent：傳統流程照常讀得到訂單。
        Assert.NotEmpty(await CreateServices(customer1).Orders.GetMyOrdersAsync(customer1));
    }

    [Fact]
    public async Task Fault_WhenDemoDisabled_IsNotInjected()
    {
        await demo.SetFaultAsync(DemoFault.OrdersNotFound, true);
        await demo.SetFaultAsync(DemoFault.PolicyServiceUnavailable, true);
        demo = TestDemoControl.Off(factory);
        await demo.LoadAsync();
        var black40 = await ItemIdAsync("SO-DEMO-0001");

        var orders = await InvokeAsync(CreateTools(), AfterSalesAgentTools.QueryMyOrdersName);
        var policy = await InvokeAsync(CreateTools(), AfterSalesAgentTools.GetEffectivePolicyName, new()
        {
            ["orderItemId"] = black40,
            ["issueType"] = "size_exchange",
        });

        Assert.Equal(ToolOutcomeStatus.Ok, orders.Status);
        Assert.Equal(ToolOutcomeStatus.Ok, policy.Status);
    }
}

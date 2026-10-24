using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Business.Startup;
using CustomerHelpAgent.Models.Others;
using CustomerHelpAgent.Models.Shop;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Agents;
using CustomerHelpAgent.Share.Shop;
using CustomerHelpAgent.Web.Agents.AfterSales;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 一鍵重置示範資料（<see cref="DemoResetService"/>）：以正式的 seeder 建立示範資料，再以真實服務製造申請、規則新版本、會話，
/// 驗證重置後業務資料回到初始狀態（相對時間以重置當下計算），而執行軌跡、流程度量、Token 用量、AI 對話紀錄與稽核保留。
/// </summary>
public sealed class DemoResetServiceTests : IAsyncLifetime
{
    /// <summary>本地時間（UTC+8）2026/10/23 10:00＝初次寫入示範資料的時間。</summary>
    private static readonly DateTimeOffset SeedUtc = new(2026, 10, 23, 2, 0, 0, TimeSpan.Zero);

    /// <summary>重置時間＝寫入後 5 天（本地 2026/10/28 10:00）。</summary>
    private static readonly DateTime ResetLocal = new(2026, 10, 28, 10, 0, 0);

    private readonly string fileRoot = Path.Combine(Path.GetTempPath(), "DemoResetTests-" + Guid.NewGuid().ToString("N"));
    private readonly RecordingAuditLogService audit = new();
    private readonly AgentPhotoInbox inbox = new();
    private SqliteConnection connection = null!;
    private TestDbContextFactory factory = null!;
    private int customer1;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(fileRoot);
        connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        factory = new TestDbContextFactory(connection);
        await using var db = factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        var demoOptions = Options.Create(new DemoSettings { Enabled = true, DemoUserPassword = "demo1234" });
        var rolePermissionService = new RolePermissionService();
        IDatabaseSeeder[] seeders =
        [
            new DefaultRoleViewSeeder(db, rolePermissionService, NullLogger<DefaultRoleViewSeeder>.Instance),
            new SupportUserSeeder(db, Options.Create(new BootstrapSettings { SupportPassword = "support-test-password" }), NullLogger<SupportUserSeeder>.Instance),
            new RbacBackfillSeeder(new RbacBackfillService(db, rolePermissionService, NullLogger<RbacBackfillService>.Instance), NullLogger<RbacBackfillSeeder>.Instance),
            new DemoAccountSeeder(db, new RbacWriteService(db, NullLogger<RbacWriteService>.Instance), demoOptions, NullLogger<DemoAccountSeeder>.Instance),
            new DemoCatalogSeeder(db, demoOptions, new ManualTimeProvider(SeedUtc), NullLogger<DemoCatalogSeeder>.Instance),
        ];
        foreach (var seeder in seeders.OrderBy(x => x.Order))
        {
            await seeder.SeedAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
        }

        customer1 = (await db.MyUser.SingleAsync(x => x.Account == DemoSeedData.Customer1)).Id;
    }

    public async Task DisposeAsync()
    {
        await connection.DisposeAsync();
        if (Directory.Exists(fileRoot))
        {
            Directory.Delete(fileRoot, recursive: true);
        }
    }

    private CurrentUserService Staff() => new() { CurrentUser = new CurrentUser { Id = 1, Account = "support" } };

    private AfterSalesFileStore CreateFileStore()
    {
        var settings = new SystemSettings();
        settings.ExternalFileSystem.AfterSalesFilePath = fileRoot;
        return new AfterSalesFileStore(Options.Create(settings), NullLogger<AfterSalesFileStore>.Instance);
    }

    private DemoResetService CreateResetService(bool enabled = true)
        => new(
            factory,
            CreateFileStore(),
            Options.Create(new DemoSettings { Enabled = enabled, DemoUserPassword = "demo1234" }),
            new ManualTimeProvider(SeedUtc.AddDays(5)),
            audit,
            Staff(),
            [inbox],
            NullLogger<DemoResetService>.Instance);

    private AfterSalesPolicyService CreatePolicyService(TimeProvider time)
        => new(factory, audit, Staff(), TestDemoControl.Off(factory), time, NullLogger<AfterSalesPolicyService>.Instance);

    private AfterSalesRequestService CreateRequestService()
    {
        var time = new ManualTimeProvider(SeedUtc);
        var currentUser = new CurrentUserService { CurrentUser = new CurrentUser { Id = customer1, Account = DemoSeedData.Customer1 } };
        return new AfterSalesRequestService(
            factory,
            CreatePolicyService(time),
            new ShopOrderService(factory, TestDemoControl.Off(factory), NullLogger<ShopOrderService>.Instance),
            CreateFileStore(),
            audit,
            currentUser,
            time,
            NullLogger<AfterSalesRequestService>.Instance);
    }

    private AgentSessionStore CreateSessionStore()
        => new(factory, new ManualTimeProvider(SeedUtc), NullLogger<AgentSessionStore>.Instance);

    private async Task<int> OrderItemIdAsync(string orderNo)
    {
        await using var db = factory.CreateDbContext();
        return await db.SalesOrderItem.Where(x => x.SalesOrder!.OrderNo == orderNo).Select(x => x.Id).SingleAsync();
    }

    private async Task SubmitWithPhotoAsync()
    {
        var submitted = await CreateRequestService().SubmitAsync(customer1, new SubmitAfterSalesInput
        {
            CaseKey = Guid.NewGuid(),
            OrderItemId = await OrderItemIdAsync("SO-DEMO-0001"),
            Type = AfterSalesType.SizeExchange,
            TargetSize = "41",
            IsWorn = false,
            Description = "尺寸偏小",
        }, [Photo()]);
        Assert.True(submitted.Success, submitted.Message);
    }

    /// <summary>每張目錄資料表的 Id（依 Id 排序），以及可辨識內容的鍵，用來比對兩次寫入是否完全相同。</summary>
    private async Task<string> CatalogIdsAsync()
    {
        await using var db = factory.CreateDbContext();
        var parts = new[]
        {
            string.Join(",", await db.Product.OrderBy(x => x.Id).Select(x => x.Id + ":" + x.Code).ToListAsync()),
            string.Join(",", await db.ProductVariant.OrderBy(x => x.Id).Select(x => x.Id + ":" + x.ProductId + x.Color + x.Size).ToListAsync()),
            string.Join(",", await db.SalesOrder.OrderBy(x => x.Id).Select(x => x.Id + ":" + x.OrderNo).ToListAsync()),
            string.Join(",", await db.SalesOrderItem.OrderBy(x => x.Id).Select(x => x.Id + ":" + x.SalesOrderId + "/" + x.ProductVariantId).ToListAsync()),
            string.Join(",", await db.Shipment.OrderBy(x => x.Id).Select(x => x.Id + ":" + x.SalesOrderId).ToListAsync()),
            string.Join(",", await db.ShipmentEvent.OrderBy(x => x.Id).Select(x => x.Id + ":" + x.ShipmentId + x.Location).ToListAsync()),
            string.Join(",", await db.AfterSalesPolicy.OrderBy(x => x.Id).Select(x => x.Id + ":" + x.Code + "v" + x.Version).ToListAsync()),
        };
        return string.Join(" | ", parts);
    }

    private bool FileRootIsEmpty() => !Directory.EnumerateFiles(fileRoot, "*", SearchOption.AllDirectories).Any();

    private static AfterSalesUploadFile Photo() => new()
    {
        FileName = "photo.jpg",
        FileSize = 16,
        Content = new MemoryStream(new byte[16]),
    };

    [Fact]
    public async Task Reset_RestoresFlagshipOrdersRelativeToNow()
    {
        // 展示過程中改過的資料：庫存歸零、簽收日被改。
        await using (var db = factory.CreateDbContext())
        {
            await db.ProductVariant
                .Where(x => x.Product!.Code == DemoSeedData.RunningShoesCode && x.Color == "黑" && x.Size == "41")
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Stock, 0));
            await db.SalesOrder
                .Where(x => x.OrderNo == "SO-DEMO-0001")
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.DeliveredAt, new DateTime(2026, 1, 1)));
        }

        var result = await CreateResetService().ResetAsync();

        Assert.True(result.Success, result.Message);
        await using var after = factory.CreateDbContext();
        var orders = await after.SalesOrder
            .Include(x => x.Items).ThenInclude(x => x.ProductVariant)
            .Where(x => x.CustomerUserId == customer1)
            .ToListAsync();
        Assert.Equal(2, orders.Count);
        var black40 = orders.Single(o => o.Items.Any(i => i.ProductVariant!.Color == "黑" && i.ProductVariant.Size == "40"));
        var white41 = orders.Single(o => o.Items.Any(i => i.ProductVariant!.Color == "白" && i.ProductVariant.Size == "41"));
        Assert.Equal(ResetLocal.AddDays(-3), black40.DeliveredAt);
        Assert.Equal(ResetLocal.AddDays(-2), white41.DeliveredAt);

        var shipment = await after.Shipment.Include(x => x.Events).SingleAsync();
        Assert.Equal(ResetLocal.AddDays(-4), shipment.ShippedAt);
        Assert.Equal(3, shipment.Events.Count);

        Assert.Equal(2, await after.Product.CountAsync());
        Assert.Equal(2 * 7 + 2 * 4, await after.ProductVariant.CountAsync());
        Assert.Equal(5, (await after.ProductVariant.SingleAsync(x => x.Product!.Code == DemoSeedData.RunningShoesCode && x.Color == "黑" && x.Size == "41")).Stock);

        Assert.Contains(audit.Entries, e => e.Action == AuditActions.Demo.Reset && e.Success);
    }

    [Fact]
    public async Task Reset_RemovesRequestsAndFiles()
    {
        await SubmitWithPhotoAsync();
        Assert.False(FileRootIsEmpty());
        var pendingKey = Guid.NewGuid();
        Assert.True(inbox.Add(pendingKey, "pending.jpg", new byte[8]));

        var result = await CreateResetService().ResetAsync();

        Assert.True(result.Success, result.Message);
        Assert.Equal(1, result.DeletedRequests);
        Assert.True(FileRootIsEmpty());
        Assert.Equal(0, inbox.Count(pendingKey));
        await using var db = factory.CreateDbContext();
        Assert.Empty(db.AfterSalesRequest);
        Assert.Empty(db.AfterSalesAttachment);
    }

    [Fact]
    public async Task Reset_RemovesFulfilmentsAndAfterSalesNotifications()
    {
        await SubmitWithPhotoAsync();
        int requestId;
        await using (var db = factory.CreateDbContext())
        {
            requestId = await db.AfterSalesRequest.Select(x => x.Id).SingleAsync();
        }

        var staff = new CurrentUserService { CurrentUser = new CurrentUser { Id = 1, Account = "support" } };
        var decision = TestDecisionService.Create(factory, audit, staff, new ManualTimeProvider(SeedUtc));
        var approved = await decision.ApplyAsync(requestId, StaffDecisionKind.Approve, null, 1);
        Assert.True(approved.Success, approved.Message);
        await using (var db = factory.CreateDbContext())
        {
            db.Notification.AddRange(
                new Notification { RecipientUserId = customer1, Category = NotificationCategories.AfterSalesUpdated, Title = "t", SourceKey = "aftersales:AS1:Approved", CreatedAtUtc = SeedUtc.UtcDateTime },
                new Notification { RecipientUserId = customer1, Category = NotificationCategories.Test, Title = "keep", CreatedAtUtc = SeedUtc.UtcDateTime });
            db.NotificationDispatch.Add(new NotificationDispatch { SourceKey = "aftersales:AS1:Approved", RecipientUserId = customer1, SentAtUtc = SeedUtc.UtcDateTime });
            await db.SaveChangesAsync();
        }

        var result = await CreateResetService().ResetAsync();

        Assert.True(result.Success, result.Message);
        await using var after = factory.CreateDbContext();
        Assert.Empty(after.AfterSalesFulfillment);
        Assert.Equal(["keep"], await after.Notification.Select(x => x.Title).ToListAsync());
        Assert.Empty(after.NotificationDispatch);
    }

    [Fact]
    public async Task Reset_RemovesRunsAndCheckpoints()
    {
        await SubmitWithPhotoAsync();
        int requestId;
        await using (var db = factory.CreateDbContext())
        {
            requestId = await db.AfterSalesRequest.Select(x => x.Id).SingleAsync();
        }

        var runService = new PreReviewRunService(factory, new ManualTimeProvider(SeedUtc), NullLogger<PreReviewRunService>.Instance);
        var run = (await runService.CreateAsync(requestId))!;
        await runService.SetNodeAsync(run.Id, PreReviewNode.Intake, NodeStatus.Done);
        await new WorkflowCheckpointService(factory, new ManualTimeProvider(SeedUtc), NullLogger<WorkflowCheckpointService>.Instance)
            .AddAsync(run.WorkflowSessionId, "cp1", null, "{}");

        var result = await CreateResetService().ResetAsync();

        Assert.True(result.Success, result.Message);
        await using var after = factory.CreateDbContext();
        Assert.Empty(after.PreReviewNodeState);
        Assert.Empty(after.PreReviewRun);
        Assert.Empty(after.WorkflowCheckpoint);
        Assert.Empty(after.AfterSalesRequest);
    }

    [Fact]
    public async Task Reset_RestoresPolicyVersionsToV1()
    {
        var policyService = CreatePolicyService(new ManualTimeProvider(SeedUtc.AddDays(1)));
        var current = (await policyService.GetVersionsAsync(includeExpired: false)).First(x => x.Code == "SIZE-EXCHANGE");
        current.BaseVersion = current.Version;
        current.WindowDays = 3;
        var added = await policyService.AddVersionAsync(current);
        Assert.True(added.Success, added.Message);

        var result = await CreateResetService().ResetAsync();

        Assert.True(result.Success, result.Message);
        await using var db = factory.CreateDbContext();
        var policies = await db.AfterSalesPolicy.ToListAsync();
        Assert.Equal(5, policies.Count);
        Assert.All(policies, p => Assert.Equal(1, p.Version));
        Assert.All(policies, p => Assert.Null(p.EffectiveTo));
        Assert.All(policies, p => Assert.Equal(ResetLocal.AddDays(-90), p.EffectiveFrom));
        Assert.Equal(7, policies.Single(x => x.Code == "SIZE-EXCHANGE").WindowDays);
    }

    [Fact]
    public async Task Reset_KeepsTracesMetricsAndUsage()
    {
        var sessionKey = Guid.NewGuid();
        await using (var db = factory.CreateDbContext())
        {
            db.ProcessMetric.Add(new ProcessMetric
            {
                CaseKey = sessionKey,
                Channel = AfterSalesChannel.Agent,
                Actor = ProcessActor.Customer,
                Action = "Submit",
                UserId = customer1,
                AfterSalesRequestId = 12345,
                OccurredAt = ResetLocal.AddDays(-1),
            });
            db.AgentRunTrace.Add(new AgentRunTrace
            {
                SessionKey = sessionKey,
                CustomerUserId = customer1,
                StartedAt = ResetLocal.AddDays(-1),
                Steps = [new AgentRunStep { Sequence = 1, Kind = AgentStepKind.UserMessage, Source = AgentDataSource.Conversation, PlainText = "hi", OccurredAt = ResetLocal.AddDays(-1) }],
            });
            db.TokenUsageLog.Add(new TokenUsageLog { OccurredAt = ResetLocal.AddDays(-1), Operation = "AfterSalesAgent", CallKind = "chat", Provider = "AzureOpenAI", Model = "m", Success = true });
            db.AiCallLog.Add(new AiCallLog { CallId = Guid.NewGuid(), OccurredAt = ResetLocal.AddDays(-1), Operation = "AfterSalesAgent", Provider = "AzureOpenAI", Model = "m", Success = true });
            db.AuditLog.Add(new AuditLog { OccurredAt = ResetLocal.AddDays(-1), Action = AuditActions.Demo.FaultChanged });
            await db.SaveChangesAsync();
        }

        var result = await CreateResetService().ResetAsync();

        Assert.True(result.Success, result.Message);
        await using var after = factory.CreateDbContext();
        Assert.Equal(1, await after.ProcessMetric.CountAsync());
        Assert.Equal(1, await after.AgentRunTrace.CountAsync());
        Assert.Equal(1, await after.AgentRunStep.CountAsync());
        Assert.Equal(1, await after.TokenUsageLog.CountAsync());
        Assert.Equal(1, await after.AiCallLog.CountAsync());
        Assert.Equal(1, await after.AuditLog.CountAsync());
    }

    [Fact]
    public async Task Reset_ThenOldSessionConfirm_IsStale()
    {
        var store = CreateSessionStore();
        var key = await store.CreateAsync(customer1);
        var pending = await store.AddConfirmationAsync(key, customer1, "ficc_1", "create_after_sales_request", "{}", "{}");

        var result = await CreateResetService().ResetAsync();

        Assert.True(result.Success, result.Message);
        Assert.Equal(1, result.DeletedSessions);
        // 會話與確認卡都被刪除：Runner 的 RequireSessionAsync 會拒絕（找不到這段對話），確認卡也搶不下來。
        Assert.Null(await store.GetAsync(key, customer1));
        Assert.Null(await store.GetLatestOpenAsync(customer1));
        Assert.Null(await store.GetPendingConfirmationAsync(key, customer1));
        Assert.False(await store.TryClaimConfirmationAsync(pending.Id, customer1, approved: true));
    }

    [Fact]
    public async Task Reset_WhenDemoDisabled_Fails()
    {
        var store = CreateSessionStore();
        var key = await store.CreateAsync(customer1);
        await using (var db = factory.CreateDbContext())
        {
            await db.SalesOrder
                .Where(x => x.OrderNo == "SO-DEMO-0001")
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.DeliveredAt, new DateTime(2026, 1, 1)));
        }

        var result = await CreateResetService(enabled: false).ResetAsync();

        Assert.False(result.Success);
        Assert.Equal(0, result.DeletedRequests);
        Assert.Equal(0, result.DeletedSessions);
        Assert.NotNull(await store.GetAsync(key, customer1));
        await using var after = factory.CreateDbContext();
        Assert.Equal(new DateTime(2026, 1, 1), (await after.SalesOrder.SingleAsync(x => x.OrderNo == "SO-DEMO-0001")).DeliveredAt);
        Assert.DoesNotContain(audit.Entries, e => e.Action == AuditActions.Demo.Reset && e.Success);
    }

    /// <summary>控制台的「上次重置」：最新一筆成功的 <c>Demo.Reset</c> 稽核（UTC）換成本地時間；失敗的不算，沒有就回 null。</summary>
    [Fact]
    public async Task LastResetAt_ReturnsLatestSuccessfulResetInLocalTime()
    {
        var service = CreateResetService();
        Assert.Null(await service.GetLastResetAtAsync());

        await using (var db = factory.CreateDbContext())
        {
            db.AuditLog.AddRange(
                new AuditLog { Action = AuditActions.Demo.Reset, Success = true, OccurredAt = new DateTime(2026, 10, 27, 1, 0, 0) },
                new AuditLog { Action = AuditActions.Demo.Reset, Success = true, OccurredAt = new DateTime(2026, 10, 28, 2, 30, 0) },
                new AuditLog { Action = AuditActions.Demo.Reset, Success = false, OccurredAt = new DateTime(2026, 10, 28, 3, 0, 0) },
                new AuditLog { Action = AuditActions.Demo.FaultChanged, Success = true, OccurredAt = new DateTime(2026, 10, 29, 0, 0, 0) });
            await db.SaveChangesAsync();
        }

        Assert.Equal(new DateTime(2026, 10, 28, 10, 30, 0), await service.GetLastResetAtAsync());
    }

    [Fact]
    public async Task Reset_TwiceProducesIdenticalIds()
    {
        var fresh = await CatalogIdsAsync();
        Assert.StartsWith("1:SH-RUN01,2:AP-JKT01 | 1:1黑38,", fresh);
        Assert.Contains("1:SO-DEMO-0001,2:SO-DEMO-0002,3:SO-DEMO-0003,4:SO-DEMO-0004", fresh);
        Assert.Contains("1:4 | ", fresh);
        Assert.EndsWith("1:SIZE-EXCHANGEv1,2:COLOR-EXCHANGEv1,3:RETURN-REFUNDv1,4:QUALITY-ISSUEv1,5:SHIPMENT-URGEv1", fresh);

        // 展示過程中新增的規則版本（自動 Id 6）與申請，不影響重置後的固定 Id。
        var policyService = CreatePolicyService(new ManualTimeProvider(SeedUtc.AddDays(1)));
        var current = (await policyService.GetVersionsAsync(includeExpired: false)).First(x => x.Code == "SIZE-EXCHANGE");
        current.BaseVersion = current.Version;
        Assert.True((await policyService.AddVersionAsync(current)).Success);
        await SubmitWithPhotoAsync();

        Assert.True((await CreateResetService().ResetAsync()).Success);
        var first = await CatalogIdsAsync();
        Assert.True((await CreateResetService().ResetAsync()).Success);
        var second = await CatalogIdsAsync();

        Assert.Equal(fresh, first);
        Assert.Equal(fresh, second);
        Assert.Equal(1, await OrderItemIdAsync("SO-DEMO-0001"));
        Assert.Equal(2, await OrderItemIdAsync("SO-DEMO-0002"));
    }

    [Fact]
    public async Task Reset_FromDatabaseWithOtherIds_RestoresFixedIds()
    {
        var fresh = await CatalogIdsAsync();
        // 模擬舊版資料庫：目錄是以自動遞增 Id 寫入的（從 51 開始）。
        await using (var db = factory.CreateDbContext())
        {
            await db.ShipmentEvent.ExecuteDeleteAsync();
            await db.Shipment.ExecuteDeleteAsync();
            await db.SalesOrderItem.ExecuteDeleteAsync();
            await db.SalesOrder.ExecuteDeleteAsync();
            await db.ProductVariant.ExecuteDeleteAsync();
            await db.Product.ExecuteDeleteAsync();
            await db.AfterSalesPolicy.ExecuteDeleteAsync();
            db.Product.Add(new Product { Id = 50, Code = "PLACEHOLDER", Name = "x", Category = ProductCategories.Shoes, Price = 1m });
            await db.SaveChangesAsync();
            await db.Product.ExecuteDeleteAsync();

            var ids = await db.MyUser
                .Where(x => x.Account == DemoSeedData.Customer1 || x.Account == DemoSeedData.Customer2)
                .ToDictionaryAsync(x => x.Account, x => x.Id);
            var catalog = DemoSeedData.CreateCatalog(ResetLocal, ids);
            foreach (var product in catalog.Products)
            {
                product.Id = 0;
                product.Variants.ForEach(v => v.Id = 0);
            }

            foreach (var order in catalog.Orders)
            {
                order.Id = 0;
                order.Items.ForEach(i => i.Id = 0);
            }

            foreach (var shipment in catalog.Shipments)
            {
                shipment.Id = 0;
                shipment.Events.ForEach(e => e.Id = 0);
            }

            foreach (var policy in catalog.Policies)
            {
                policy.Id = 0;
            }

            db.Product.AddRange(catalog.Products);
            db.SalesOrder.AddRange(catalog.Orders);
            db.Shipment.AddRange(catalog.Shipments);
            db.AfterSalesPolicy.AddRange(catalog.Policies);
            await db.SaveChangesAsync();
        }

        Assert.True(await OrderItemIdAsync("SO-DEMO-0001") > 4);

        Assert.True((await CreateResetService().ResetAsync()).Success);

        Assert.Equal(fresh, await CatalogIdsAsync());
    }

    [Fact]
    public async Task Reset_FailureMidway_RollsBackAndKeepsFiles()
    {
        await SubmitWithPhotoAsync();
        var store = CreateSessionStore();
        var key = await store.CreateAsync(customer1);
        Assert.True(inbox.Add(key, "pending.jpg", new byte[8]));
        await using (var db = factory.CreateDbContext())
        {
            // 重新寫入時找不到示範帳號 → WriteCatalogAsync 在刪除之後拋例外。
            await db.MyUser
                .Where(x => x.Account == DemoSeedData.Customer2)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Account, "customer2-renamed"));
        }

        var ids = await CatalogIdsAsync();

        var result = await CreateResetService().ResetAsync();

        Assert.False(result.Success);
        Assert.Equal(ids, await CatalogIdsAsync());
        Assert.False(FileRootIsEmpty());
        Assert.Equal(1, inbox.Count(key));
        Assert.NotNull(await store.GetAsync(key, customer1));
        await using var after = factory.CreateDbContext();
        Assert.Equal(1, await after.AfterSalesRequest.CountAsync());
        Assert.Equal(1, await after.AfterSalesAttachment.CountAsync());
        Assert.Contains(audit.Entries, e => e.Action == AuditActions.Demo.Reset && !e.Success);
        Assert.DoesNotContain(audit.Entries, e => e.Action == AuditActions.Demo.Reset && e.Success);
    }
}

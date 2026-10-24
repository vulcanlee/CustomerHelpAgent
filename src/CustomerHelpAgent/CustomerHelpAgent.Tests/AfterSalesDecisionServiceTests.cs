using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Others;
using CustomerHelpAgent.Models.Shop;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Shop;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 售後決策執行（<see cref="AfterSalesDecisionService"/>）：核准時在同一交易內開處理單並扣庫存／加物流事件，
/// 庫存不足整筆回滾，併發核准只有一個成功，commit 後通知顧客。
/// 用檔案型 SQLite（每個操作各自一條連線），才能真實驗證兩位客服同時核准。
/// </summary>
public sealed class AfterSalesDecisionServiceTests : IAsyncLifetime
{
    // UTC 02:00 = 本地（UTC+8）10:00
    private static readonly DateTimeOffset UtcNow = new(2026, 10, 23, 2, 0, 0, TimeSpan.Zero);
    private const int StaffUserId = 900;

    private readonly string dbPath = Path.Combine(Path.GetTempPath(), "DecisionTests-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly RecordingAuditLogService audit = new();
    private readonly RecordingNotificationSender sender = new();
    private FileDbContextFactory factory = null!;
    private int alice;
    private int blackSize26;
    private int blackSize27;
    private int blackSize28OutOfStock;
    private int aliceOrderId;
    private int aliceItemId;
    private int shipmentId;

    public async Task InitializeAsync()
    {
        factory = new FileDbContextFactory(dbPath);
        await using var ctx = factory.CreateDbContext();
        await ctx.Database.EnsureCreatedAsync();

        var user = new MyUser { Account = "alice", Password = "x", Name = "Alice" };
        ctx.MyUser.Add(user);
        var product = new Product { Code = "P1", Name = "跑鞋", Category = ProductCategories.Shoes, Price = 1999m };
        var v26 = new ProductVariant { Product = product, Color = "黑", Size = "26", Stock = 5 };
        var v27 = new ProductVariant { Product = product, Color = "黑", Size = "27", Stock = 2 };
        var v28 = new ProductVariant { Product = product, Color = "黑", Size = "28", Stock = 0 };
        var white26 = new ProductVariant { Product = product, Color = "白", Size = "26", Stock = 1 };
        ctx.ProductVariant.AddRange(v26, v27, v28, white26);
        await ctx.SaveChangesAsync();
        alice = user.Id;
        blackSize26 = v26.Id;
        blackSize27 = v27.Id;
        blackSize28OutOfStock = v28.Id;

        var order = new SalesOrder
        {
            OrderNo = "A1",
            CustomerUserId = alice,
            OrderedAt = Now().AddDays(-5),
            DeliveredAt = Now().AddDays(-2),
            RecipientName = "王小明",
            ShippingAddress = "台北市某路1號",
            RecipientPhone = "0912345678",
            Items = [new SalesOrderItem { ProductVariantId = v26.Id, Quantity = 1, UnitPrice = 1999m }],
        };
        ctx.SalesOrder.Add(order);
        await ctx.SaveChangesAsync();
        aliceOrderId = order.Id;
        aliceItemId = order.Items[0].Id;

        var shipment = new Shipment
        {
            SalesOrderId = order.Id,
            Carrier = "宅配",
            TrackingNo = "T1",
            StatusText = "配送中",
            ShippedAt = Now().AddDays(-4),
            Events = [new ShipmentEvent { OccurredAt = Now().AddDays(-3), Location = "桃園轉運站", Description = "包裹已抵達轉運站" }],
        };
        ctx.Shipment.Add(shipment);
        await ctx.SaveChangesAsync();
        shipmentId = shipment.Id;
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { dbPath, dbPath + "-wal", dbPath + "-shm", dbPath + "-journal" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        return Task.CompletedTask;
    }

    private static DateTime Now() => new(2026, 10, 23, 10, 0, 0);

    private AfterSalesDecisionService CreateService(INotificationSender? notificationSender = null)
    {
        var currentUser = new CurrentUserService { CurrentUser = new CurrentUser { Id = StaffUserId, Account = "agent1" } };
        return TestDecisionService.Create(factory, audit, currentUser, new ManualTimeProvider(UtcNow), notificationSender ?? sender);
    }

    private async Task<int> AddRequestAsync(
        AfterSalesType type, string? targetSize = null, string? targetColor = null, AfterSalesStatus status = AfterSalesStatus.Submitted)
    {
        await using var ctx = factory.CreateDbContext();
        var row = new AfterSalesRequest
        {
            RequestNo = Guid.NewGuid().ToString("N"),
            CaseKey = Guid.NewGuid(),
            CustomerUserId = alice,
            SalesOrderItemId = aliceItemId,
            Type = type,
            Status = status,
            Channel = AfterSalesChannel.Traditional,
            TargetSize = targetSize,
            TargetColor = targetColor,
            Description = "測試",
            CreatedAt = Now(),
            UpdatedAt = Now(),
        };
        ctx.AfterSalesRequest.Add(row);
        await ctx.SaveChangesAsync();
        row.RequestNo = $"AS20261023{row.Id:D4}";
        await ctx.SaveChangesAsync();
        return row.Id;
    }

    private async Task<int> StockAsync(int variantId)
    {
        await using var ctx = factory.CreateDbContext();
        return await ctx.ProductVariant.Where(x => x.Id == variantId).Select(x => x.Stock).SingleAsync();
    }

    [Fact]
    public async Task Approve_SizeExchange_CreatesExchangeOrderAndDecrementsStock()
    {
        var id = await AddRequestAsync(AfterSalesType.SizeExchange, targetSize: "27");

        var result = await CreateService().ApplyAsync(id, StaffDecisionKind.Approve, "同意換貨", StaffUserId);

        Assert.True(result.Success, result.Message);
        Assert.Equal(AfterSalesStatus.Approved, result.NewStatus);
        Assert.Matches(@"^EX20261023\d{4}$", result.FulfillmentNo);
        await using var ctx = factory.CreateDbContext();
        var fulfillment = await ctx.AfterSalesFulfillment.SingleAsync();
        Assert.Equal((id, FulfillmentKind.ExchangeOrder, blackSize27, 1, result.FulfillmentNo), (fulfillment.AfterSalesRequestId, fulfillment.Kind, fulfillment.ProductVariantId, fulfillment.Quantity, fulfillment.ReferenceNo));
        Assert.Null(fulfillment.RefundAmount);
        Assert.Equal(1, await StockAsync(blackSize27));
        Assert.Equal(5, await StockAsync(blackSize26));
        var row = await ctx.AfterSalesRequest.SingleAsync();
        Assert.Equal((AfterSalesStatus.Approved, "同意換貨", StaffUserId, Now()), (row.Status, row.StaffNote, row.DecidedByUserId, row.DecidedAt));
        Assert.Contains(audit.Entries, e => e.Action == AuditActions.Shop.RequestDecide && e.ActorUserId == StaffUserId && e.ActorAccount == "agent1" && e.Detail == "status=Approved");
        Assert.Contains(audit.Entries, e => e.Action == AuditActions.AfterSales.Fulfilled && e.Detail!.Contains(result.FulfillmentNo!));

        var view = await CreateService().GetFulfillmentAsync(id);
        Assert.NotNull(view);
        Assert.Equal((result.FulfillmentNo, "黑 / 27"), (view.ReferenceNo, view.VariantText));
        Assert.Null(await CreateService().GetFulfillmentAsync(id + 1000));
    }

    [Fact]
    public async Task GetFulfillments_Batch_ReturnsOnlyRequestsWithFulfillment()
    {
        var approved = await AddRequestAsync(AfterSalesType.ReturnRefund);
        var pending = await AddRequestAsync(AfterSalesType.ReturnRefund);
        var result = await CreateService().ApplyAsync(approved, StaffDecisionKind.Approve, null, StaffUserId);

        var map = await CreateService().GetFulfillmentsAsync([approved, pending, approved + 1000]);

        var entry = Assert.Single(map);
        Assert.Equal(approved, entry.Key);
        Assert.Equal(result.FulfillmentNo, entry.Value.ReferenceNo);
        Assert.Empty(await CreateService().GetFulfillmentsAsync([]));
    }

    [Fact]
    public async Task Approve_ReturnRefund_CreatesRefundRecordNoStockChange()
    {
        var id = await AddRequestAsync(AfterSalesType.ReturnRefund);

        var result = await CreateService().ApplyAsync(id, StaffDecisionKind.Approve, null, StaffUserId);

        Assert.True(result.Success, result.Message);
        Assert.StartsWith("RF20261023", result.FulfillmentNo);
        await using var ctx = factory.CreateDbContext();
        var fulfillment = await ctx.AfterSalesFulfillment.SingleAsync();
        Assert.Equal((FulfillmentKind.RefundRecord, 1999m), (fulfillment.Kind, fulfillment.RefundAmount));
        Assert.Equal(5, await StockAsync(blackSize26));
        Assert.Equal(2, await StockAsync(blackSize27));
    }

    [Fact]
    public async Task Approve_QualityIssue_CreatesReplacementOrder()
    {
        var id = await AddRequestAsync(AfterSalesType.QualityIssue);

        var result = await CreateService().ApplyAsync(id, StaffDecisionKind.Approve, null, StaffUserId);

        Assert.True(result.Success, result.Message);
        Assert.StartsWith("RP20261023", result.FulfillmentNo);
        await using var ctx = factory.CreateDbContext();
        var fulfillment = await ctx.AfterSalesFulfillment.SingleAsync();
        Assert.Equal((FulfillmentKind.ReplacementOrder, blackSize26), (fulfillment.Kind, fulfillment.ProductVariantId));
        Assert.Equal(4, await StockAsync(blackSize26));
    }

    [Fact]
    public async Task Approve_ShipmentUrge_AddsShipmentEvent()
    {
        var id = await AddRequestAsync(AfterSalesType.ShipmentUrge);

        var result = await CreateService().ApplyAsync(id, StaffDecisionKind.Approve, null, StaffUserId);

        Assert.True(result.Success, result.Message);
        Assert.StartsWith("SF20261023", result.FulfillmentNo);
        await using var ctx = factory.CreateDbContext();
        Assert.Equal(FulfillmentKind.ShipmentFollowUp, (await ctx.AfterSalesFulfillment.SingleAsync()).Kind);
        var events = await ctx.ShipmentEvent.Where(x => x.ShipmentId == shipmentId).OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(2, events.Count);
        Assert.Equal("客服已催件，物流中心處理中", events[^1].Description);
        Assert.Equal(5, await StockAsync(blackSize26));
    }

    [Fact]
    public async Task Approve_ExchangeOutOfStock_FailsWithoutSideEffects()
    {
        var id = await AddRequestAsync(AfterSalesType.SizeExchange, targetSize: "28");

        var result = await CreateService().ApplyAsync(id, StaffDecisionKind.Approve, "同意", StaffUserId);

        Assert.False(result.Success);
        Assert.Equal("目標規格已無庫存，無法核准；請改駁回或聯繫顧客。", result.Message);
        Assert.Null(result.NewStatus);
        await using var ctx = factory.CreateDbContext();
        Assert.Empty(await ctx.AfterSalesFulfillment.ToListAsync());
        var row = await ctx.AfterSalesRequest.SingleAsync();
        Assert.Equal((AfterSalesStatus.Submitted, null, null), (row.Status, row.StaffNote, row.DecidedAt));
        Assert.Equal(0, await StockAsync(blackSize28OutOfStock));
        Assert.Empty(sender.Requests);
        Assert.Empty(audit.Entries);
    }

    /// <summary>品質問題補寄的是同規格：同規格沒貨時的訊息不可說成「目標規格」。</summary>
    [Fact]
    public async Task Approve_QualityIssueOutOfStock_ReportsSameSpecMessage()
    {
        var id = await AddRequestAsync(AfterSalesType.QualityIssue);
        await using (var ctx = factory.CreateDbContext())
        {
            await ctx.ProductVariant.Where(x => x.Id == blackSize26).ExecuteUpdateAsync(s => s.SetProperty(x => x.Stock, 0));
        }

        var result = await CreateService().ApplyAsync(id, StaffDecisionKind.Approve, null, StaffUserId);

        Assert.False(result.Success);
        Assert.Equal(AfterSalesDecisionService.SameSpecOutOfStockMessage, result.Message);
        Assert.Equal("同規格已無庫存，無法補寄；請改駁回或聯繫顧客。", result.Message);
        await using var check = factory.CreateDbContext();
        Assert.Empty(await check.AfterSalesFulfillment.ToListAsync());
        Assert.Equal(AfterSalesStatus.Submitted, (await check.AfterSalesRequest.SingleAsync()).Status);
    }

    [Fact]
    public async Task Decide_Concurrent_OnlyOneFulfilment()
    {
        var id = await AddRequestAsync(AfterSalesType.SizeExchange, targetSize: "27");

        var results = await Task.WhenAll(Enumerable.Range(0, 2)
            .Select(_ => Task.Run(() => CreateService().ApplyAsync(id, StaffDecisionKind.Approve, null, StaffUserId))));

        Assert.Single(results, r => r.Success);
        Assert.Single(results, r => !r.Success && r.Message == AfterSalesRequestService.StatusChangedMessage);
        await using var ctx = factory.CreateDbContext();
        Assert.Single(await ctx.AfterSalesFulfillment.ToListAsync());
        Assert.Equal(1, await StockAsync(blackSize27));
        Assert.Single(sender.Requests);
    }

    [Fact]
    public async Task Decide_SendsCustomerNotificationWithLink()
    {
        var id = await AddRequestAsync(AfterSalesType.ReturnRefund);

        var result = await CreateService().ApplyAsync(id, StaffDecisionKind.Approve, null, StaffUserId);

        var request = Assert.Single(sender.Requests);
        Assert.Equal(NotificationCategories.AfterSalesUpdated, request.Category);
        Assert.Equal($"售後申請 AS20261023{id:D4} 已核准", request.Title);
        Assert.Contains(result.FulfillmentNo!, request.Body);
        Assert.Equal("/shop/after-sales", request.Link);
        Assert.Equal([alice], request.Target.UserIds);
        Assert.False(request.AlsoEmail);
        Assert.Equal($"aftersales:AS20261023{id:D4}:Approved:{Now().Ticks}", request.SourceKey);
    }

    [Fact]
    public async Task Decide_NotificationThrows_StillSucceeds()
    {
        var id = await AddRequestAsync(AfterSalesType.ReturnRefund);

        var result = await CreateService(new ThrowingNotificationSender()).ApplyAsync(id, StaffDecisionKind.Approve, null, StaffUserId);

        Assert.True(result.Success, result.Message);
        await using var ctx = factory.CreateDbContext();
        Assert.Single(await ctx.AfterSalesFulfillment.ToListAsync());
    }

    [Fact]
    public async Task Reject_NoFulfilment_NotifiesCustomer()
    {
        var id = await AddRequestAsync(AfterSalesType.SizeExchange, targetSize: "27");

        var result = await CreateService().ApplyAsync(id, StaffDecisionKind.Reject, "已穿著", StaffUserId);

        Assert.True(result.Success, result.Message);
        Assert.Equal((AfterSalesStatus.Rejected, (string?)null), (result.NewStatus, result.FulfillmentNo));
        await using var ctx = factory.CreateDbContext();
        Assert.Empty(await ctx.AfterSalesFulfillment.ToListAsync());
        Assert.Equal(2, await StockAsync(blackSize27));
        var request = Assert.Single(sender.Requests);
        Assert.EndsWith("已駁回", request.Title);
        Assert.Equal("已穿著", request.Body);
        Assert.DoesNotContain(audit.Entries, e => e.Action == AuditActions.AfterSales.Fulfilled);
    }

    [Fact]
    public async Task NeedMoreInfo_RequiresNote_AndKeepsDecisionColumnsEmpty()
    {
        var id = await AddRequestAsync(AfterSalesType.QualityIssue);

        Assert.False((await CreateService().ApplyAsync(id, StaffDecisionKind.NeedMoreInfo, "  ", StaffUserId)).Success);
        var result = await CreateService().ApplyAsync(id, StaffDecisionKind.NeedMoreInfo, "請補照片", StaffUserId);

        Assert.True(result.Success, result.Message);
        Assert.Equal(AfterSalesStatus.NeedMoreInfo, result.NewStatus);
        await using var ctx = factory.CreateDbContext();
        var row = await ctx.AfterSalesRequest.SingleAsync();
        Assert.Equal(("請補照片", null), (row.StaffNote, row.DecidedAt));
        Assert.EndsWith("需要補件", Assert.Single(sender.Requests).Title);
    }

    [Fact]
    public async Task NeedMoreInfo_ResubmitThenNeedMoreInfoAgain_NotifiesCustomerTwice()
    {
        var id = await AddRequestAsync(AfterSalesType.QualityIssue);
        var time = new ManualTimeProvider(UtcNow);
        var currentUser = new CurrentUserService { CurrentUser = new CurrentUser { Id = StaffUserId, Account = "agent1" } };
        var fileRoot = Path.Combine(Path.GetTempPath(), "DecisionFiles-" + Guid.NewGuid().ToString("N"));
        var settings = new SystemSettings();
        settings.ExternalFileSystem.AfterSalesFilePath = fileRoot;
        var decisions = TestDecisionService.Create(factory, audit, currentUser, time, sender);
        var requestService = new AfterSalesRequestService(
            factory,
            new AfterSalesPolicyService(factory, audit, currentUser, TestDemoControl.Off(factory), time, NullLogger<AfterSalesPolicyService>.Instance),
            new ShopOrderService(factory, TestDemoControl.Off(factory), NullLogger<ShopOrderService>.Instance),
            new AfterSalesFileStore(Options.Create(settings), NullLogger<AfterSalesFileStore>.Instance),
            audit,
            currentUser,
            time,
            NullLogger<AfterSalesRequestService>.Instance);

        Assert.True((await decisions.ApplyAsync(id, StaffDecisionKind.NeedMoreInfo, "請補照片", StaffUserId)).Success);
        var resubmit = await requestService.ResubmitAsync(alice, id, null, "已補充", []);
        Assert.True(resubmit.Success, resubmit.Message);
        time.Advance(TimeSpan.FromMinutes(5));
        var second = await decisions.ApplyAsync(id, StaffDecisionKind.NeedMoreInfo, "還要鞋底照片", StaffUserId);

        Assert.True(second.Success, second.Message);
        Assert.Equal(2, sender.Requests.Count(x => x.Category == NotificationCategories.AfterSalesUpdated && x.Title.EndsWith("需要補件")));
        Assert.Equal(2, sender.Requests.Select(x => x.SourceKey).Distinct().Count());
        Assert.Equal(2, audit.Entries.Count(e => e.Action == AuditActions.Shop.RequestMoreInfo));
    }

    [Fact]
    public async Task Decide_AlreadyDecided_ReturnsStatusChanged()
    {
        var id = await AddRequestAsync(AfterSalesType.ReturnRefund, status: AfterSalesStatus.Rejected);

        var result = await CreateService().ApplyAsync(id, StaffDecisionKind.Approve, null, StaffUserId);

        Assert.False(result.Success);
        Assert.Equal(AfterSalesRequestService.StatusChangedMessage, result.Message);
        Assert.Empty(sender.Requests);
    }

    private sealed class ThrowingNotificationSender : INotificationSender
    {
        public Task<NotificationSendResult> SendAsync(NotificationRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("boom");
    }

    private sealed class FileDbContextFactory(string path) : IDbContextFactory<BackendDBContext>
    {
        public BackendDBContext CreateDbContext()
            => new(new DbContextOptionsBuilder<BackendDBContext>()
                .UseSqlite($"Data Source={path};Pooling=False")
                .Options);
    }
}

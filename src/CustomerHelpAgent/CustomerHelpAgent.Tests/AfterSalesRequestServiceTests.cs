using System.Text.RegularExpressions;
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

public sealed class AfterSalesRequestServiceTests : IAsyncLifetime
{
    // UTC 02:00 = 本地（UTC+8）10:00
    private static readonly DateTimeOffset UtcNow = new(2026, 10, 23, 2, 0, 0, TimeSpan.Zero);
    private static readonly DateTime LocalNow = new(2026, 10, 23, 10, 0, 0);
    private const int StaffUserId = 900;

    private readonly string fileRoot = Path.Combine(Path.GetTempPath(), "AfterSalesTests-" + Guid.NewGuid().ToString("N"));
    private SqliteConnection connection = null!;
    private readonly RecordingAuditLogService audit = new();
    private int alice;
    private int bob;
    private int aliceItemId;
    private int bobItemId;
    private int sizePolicyId;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(fileRoot);
        connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var ctx = new BackendDBContext(new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connection).Options);
        await ctx.Database.EnsureCreatedAsync();

        var aliceUser = new MyUser { Account = "alice", Password = "x", Name = "Alice" };
        var bobUser = new MyUser { Account = "bob", Password = "x", Name = "Bob" };
        ctx.MyUser.AddRange(aliceUser, bobUser);
        var product = new Product { Code = "P1", Name = "跑鞋", Category = ProductCategories.Shoes, Price = 1999m };
        var variant = new ProductVariant { Product = product, Color = "黑", Size = "26", Stock = 5 };
        ctx.ProductVariant.Add(variant);
        await ctx.SaveChangesAsync();
        alice = aliceUser.Id;
        bob = bobUser.Id;

        var aliceOrder = Order("A1", alice, variant.Id);
        var bobOrder = Order("B1", bob, variant.Id);
        ctx.SalesOrder.AddRange(aliceOrder, bobOrder);
        var policy = new AfterSalesPolicy
        {
            Code = "SIZE-EXCHANGE",
            Type = AfterSalesType.SizeExchange,
            Category = ProductCategories.Shoes,
            Title = "換尺碼",
            Content = "內容",
            Source = "來源",
            Version = 1,
            EffectiveFrom = LocalNow.AddDays(-30),
            WindowDays = 7,
            RequiresUnworn = true,
            CreatedAt = LocalNow.AddDays(-30),
        };
        ctx.AfterSalesPolicy.Add(policy);
        await ctx.SaveChangesAsync();
        aliceItemId = aliceOrder.Items[0].Id;
        bobItemId = bobOrder.Items[0].Id;
        sizePolicyId = policy.Id;
    }

    public async Task DisposeAsync()
    {
        await connection.DisposeAsync();
        if (Directory.Exists(fileRoot))
        {
            Directory.Delete(fileRoot, recursive: true);
        }
    }

    private static SalesOrder Order(string no, int userId, int variantId) => new()
    {
        OrderNo = no,
        CustomerUserId = userId,
        OrderedAt = LocalNow.AddDays(-5),
        DeliveredAt = LocalNow.AddDays(-2),
        RecipientName = "王小明",
        ShippingAddress = "台北市某路1號",
        RecipientPhone = "0912345678",
        Items = [new SalesOrderItem { ProductVariantId = variantId, Quantity = 1, UnitPrice = 1999m }],
    };

    private AfterSalesFileStore CreateFileStore()
    {
        var settings = new SystemSettings();
        settings.ExternalFileSystem.AfterSalesFilePath = fileRoot;
        return new AfterSalesFileStore(Options.Create(settings), NullLogger<AfterSalesFileStore>.Instance);
    }

    private AfterSalesRequestService CreateService()
    {
        var factory = new TestDbContextFactory(connection);
        var time = new ManualTimeProvider(UtcNow);
        var currentUser = new CurrentUserService { CurrentUser = new CurrentUser { Id = StaffUserId, Account = "agent1" } };
        return new AfterSalesRequestService(
            factory,
            new AfterSalesPolicyService(factory, audit, currentUser, time, NullLogger<AfterSalesPolicyService>.Instance),
            new ShopOrderService(factory, NullLogger<ShopOrderService>.Instance),
            CreateFileStore(),
            audit,
            currentUser,
            time,
            NullLogger<AfterSalesRequestService>.Instance);
    }

    private SubmitAfterSalesInput SizeExchange(int itemId, string? targetSize = "27") => new()
    {
        CaseKey = Guid.NewGuid(),
        OrderItemId = itemId,
        Type = AfterSalesType.SizeExchange,
        TargetSize = targetSize,
        IsWorn = false,
        Description = "尺寸偏小",
    };

    private static AfterSalesUploadFile Photo(string name = "photo.jpg", int size = 16) => new()
    {
        FileName = name,
        FileSize = size,
        Content = new MemoryStream(new byte[size]),
    };

    private BackendDBContext NewContext() =>
        new(new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connection).Options);

    private bool FileRootIsEmpty() => !Directory.EnumerateFiles(fileRoot, "*", SearchOption.AllDirectories).Any();

    [Fact]
    public async Task SubmitAsync_OtherCustomersItem_ShouldFail()
    {
        var result = await CreateService().SubmitAsync(alice, SizeExchange(bobItemId), [Photo()]);

        Assert.False(result.Success);
        Assert.Equal("找不到這筆訂單", result.Message);
        Assert.Null(result.RequestId);
        Assert.True(FileRootIsEmpty());
        await using var ctx = NewContext();
        Assert.Empty(ctx.AfterSalesRequest);
    }

    [Fact]
    public async Task SubmitAsync_SizeExchangeWithoutTarget_ShouldFail()
    {
        var result = await CreateService().SubmitAsync(alice, SizeExchange(aliceItemId, targetSize: " "), []);

        Assert.False(result.Success);
        await using var ctx = NewContext();
        Assert.Empty(ctx.AfterSalesRequest);
    }

    [Fact]
    public async Task SubmitAsync_WithDisallowedFile_ShouldFailWithoutWritingFile()
    {
        var service = CreateService();

        var exe = await service.SubmitAsync(alice, SizeExchange(aliceItemId), [Photo(), Photo("tool.exe")]);
        var tooLarge = await service.SubmitAsync(alice, SizeExchange(aliceItemId), [Photo(), Photo("big.png", 11 * 1024 * 1024)]);
        var tooMany = await service.SubmitAsync(alice, SizeExchange(aliceItemId), [Photo(), Photo(), Photo(), Photo()]);

        Assert.False(exe.Success);
        Assert.False(tooLarge.Success);
        Assert.False(tooMany.Success);
        Assert.True(FileRootIsEmpty());
        await using var ctx = NewContext();
        Assert.Empty(ctx.AfterSalesRequest);
    }

    [Fact]
    public async Task SubmitAsync_Valid_ShouldStoreAppliedPolicyAndRequestNo()
    {
        var input = SizeExchange(aliceItemId);

        var result = await CreateService().SubmitAsync(alice, input, [Photo("IMG_01.JPG")]);

        Assert.True(result.Success, result.Message);
        Assert.NotNull(result.RequestId);
        Assert.Matches(new Regex(@"^AS\d{8}\d{4}$"), result.RequestNo!);
        Assert.Equal($"AS20261023{result.RequestId:D4}", result.RequestNo);

        await using var ctx = NewContext();
        var row = await ctx.AfterSalesRequest.Include(x => x.Attachments).SingleAsync();
        Assert.Equal(result.RequestNo, row.RequestNo);
        Assert.Equal(sizePolicyId, row.AppliedPolicyId);
        Assert.Equal(AfterSalesStatus.Submitted, row.Status);
        Assert.Equal(AfterSalesChannel.Traditional, row.Channel);
        Assert.Equal(input.CaseKey, row.CaseKey);
        Assert.Equal(alice, row.CustomerUserId);
        Assert.Equal("27", row.TargetSize);
        Assert.Equal(LocalNow, row.CreatedAt);

        var attachment = Assert.Single(row.Attachments);
        Assert.Equal("IMG_01.JPG", attachment.OriginalFileName);
        Assert.Equal("image/jpeg", attachment.ContentType);
        Assert.StartsWith("2026/10/", attachment.RelativePath);
        Assert.True(File.Exists(Path.Combine(fileRoot, attachment.RelativePath)));

        Assert.Contains(audit.Entries, e => e.Action == AuditActions.Shop.RequestSubmit && e.Success && e.ActorUserId == alice);

        var mine = await CreateService().GetMyRequestsAsync(alice);
        var view = Assert.Single(mine);
        Assert.Equal("A1", view.OrderNo);
        Assert.Equal("跑鞋", view.ProductName);
        Assert.Equal("Alice", view.CustomerName);
        Assert.Single(view.Attachments);
        Assert.Empty(await CreateService().GetMyRequestsAsync(bob));
    }

    [Fact]
    public async Task RequestMoreInfo_ThenResubmit_ShouldReturnToSubmittedWithSupplement()
    {
        var service = CreateService();
        var submitted = await service.SubmitAsync(alice, SizeExchange(aliceItemId), []);
        var id = submitted.RequestId!.Value;

        var moreInfo = await service.RequestMoreInfoAsync(id, "請提供鞋底照片");
        Assert.True(moreInfo.Success, moreInfo.Message);
        Assert.Equal(AfterSalesStatus.NeedMoreInfo, (await service.GetReviewDetailAsync(id))!.Status);

        var byOther = await service.ResubmitAsync(bob, id, null, "不是我的", []);
        Assert.False(byOther.Success);

        var resubmit = await service.ResubmitAsync(alice, id, true, "已附上照片", [Photo("sole.png")]);

        Assert.True(resubmit.Success, resubmit.Message);
        var detail = await service.GetReviewDetailAsync(id);
        Assert.NotNull(detail);
        Assert.Equal(AfterSalesStatus.Submitted, detail.Status);
        Assert.Equal("尺寸偏小\n［補充］已附上照片", detail.Description);
        Assert.Equal("請提供鞋底照片", detail.StaffNote);
        Assert.True(detail.IsWorn);
        Assert.Single(detail.Attachments);

        var again = await service.ResubmitAsync(alice, id, null, "再補一次", []);
        Assert.False(again.Success);

        Assert.Contains(audit.Entries, e => e.Action == AuditActions.Shop.RequestMoreInfo && e.ActorUserId == StaffUserId);
        Assert.Contains(audit.Entries, e => e.Action == AuditActions.Shop.RequestResubmit && e.ActorUserId == alice);
    }

    [Fact]
    public async Task DecideAsync_WhenStatusChanged_ShouldFail()
    {
        var service = CreateService();
        var submitted = await service.SubmitAsync(alice, SizeExchange(aliceItemId), []);
        var id = submitted.RequestId!.Value;
        await service.RequestMoreInfoAsync(id, "請補照片");

        var result = await service.DecideAsync(id, approve: true, note: null);

        Assert.False(result.Success);
        Assert.Equal("這筆申請的狀態已經變更，請重新整理後再操作。", result.Message);
        await using var ctx = NewContext();
        var row = await ctx.AfterSalesRequest.SingleAsync();
        Assert.Equal(AfterSalesStatus.NeedMoreInfo, row.Status);
        Assert.Null(row.DecidedAt);
    }

    [Fact]
    public async Task DecideAsync_WhenSubmitted_ShouldRecordDecision()
    {
        var service = CreateService();
        var submitted = await service.SubmitAsync(alice, SizeExchange(aliceItemId), []);
        var id = submitted.RequestId!.Value;

        var result = await service.DecideAsync(id, approve: false, note: "已穿著");

        Assert.True(result.Success, result.Message);
        await using var ctx = NewContext();
        var row = await ctx.AfterSalesRequest.SingleAsync();
        Assert.Equal(AfterSalesStatus.Rejected, row.Status);
        Assert.Equal("已穿著", row.StaffNote);
        Assert.Equal(LocalNow, row.DecidedAt);
        Assert.Equal(StaffUserId, row.DecidedByUserId);
        Assert.Contains(audit.Entries, e => e.Action == AuditActions.Shop.RequestDecide && e.ActorUserId == StaffUserId);
        Assert.False((await service.DecideAsync(id, approve: true, note: null)).Success);
    }

    [Fact]
    public async Task GetReviewQueueAsync_ShouldFilterByStatusAndPage()
    {
        var service = CreateService();
        var first = await service.SubmitAsync(alice, SizeExchange(aliceItemId), []);
        var second = await service.SubmitAsync(bob, SizeExchange(bobItemId), []);
        await service.SubmitAsync(alice, SizeExchange(aliceItemId), []);
        await service.RequestMoreInfoAsync(second.RequestId!.Value, "補件");

        var submitted = await service.GetReviewQueueAsync(new DataRequest { CurrentPage = 1, PageSize = 1, Take = 1 }, AfterSalesStatus.Submitted);
        var all = await service.GetReviewQueueAsync(new DataRequest { CurrentPage = 1, PageSize = 10 }, null);

        Assert.Equal(2, submitted.Count);
        Assert.Equal(first.RequestId, Assert.Single(submitted.Result).Id);
        Assert.Equal(3, all.Count);
        Assert.Equal(3, all.Result.Count());
        Assert.Contains(all.Result, x => x.CustomerName == "Bob" && x.DeliveredAt == LocalNow.AddDays(-2));
    }

    [Fact]
    public async Task GetReviewQueueAsync_SoftDeletedCustomer_ShouldStillListRequest()
    {
        var service = CreateService();
        await service.SubmitAsync(alice, SizeExchange(aliceItemId), []);
        var bobs = await service.SubmitAsync(bob, SizeExchange(bobItemId), []);
        await using (var ctx = NewContext())
        {
            var bobUser = await ctx.MyUser.SingleAsync(x => x.Id == bob);
            SoftDeleteHelper.MarkDeleted(bobUser, "support");
            await ctx.SaveChangesAsync();
        }

        var all = await service.GetReviewQueueAsync(new DataRequest { CurrentPage = 1, PageSize = 10 }, null);
        var detail = await service.GetReviewDetailAsync(bobs.RequestId!.Value);

        Assert.Equal(2, all.Count);
        Assert.Equal(2, all.Result.Count());
        Assert.Contains(all.Result, x => x.Id == bobs.RequestId && x.CustomerName == "Bob");
        Assert.NotNull(detail);
        Assert.Equal("Bob", detail.CustomerName);
    }

    [Fact]
    public async Task GetAttachmentFor_OtherCustomer_ShouldReturnNull()
    {
        var service = CreateService();
        await service.SubmitAsync(alice, SizeExchange(aliceItemId), [Photo("a.webp")]);
        await using var ctx = NewContext();
        var attachmentId = (await ctx.AfterSalesAttachment.SingleAsync()).Id;

        var byBob = await service.GetAttachmentForAsync(attachmentId, bob, canReview: false);
        var byAlice = await service.GetAttachmentForAsync(attachmentId, alice, canReview: false);
        var byStaff = await service.GetAttachmentForAsync(attachmentId, StaffUserId, canReview: true);

        Assert.Null(byBob);
        Assert.NotNull(byAlice);
        Assert.Equal("image/webp", byAlice.Value.ContentType);
        Assert.Equal("a.webp", byAlice.Value.FileName);
        Assert.True(File.Exists(byAlice.Value.FullPath));
        Assert.NotNull(byStaff);
    }

    [Fact]
    public void FileStore_TryResolveFullPath_ShouldRefuseEscapingRoot()
    {
        var store = CreateFileStore();

        Assert.Null(store.TryResolveFullPath(@"..\..\Windows\win.ini"));
        Assert.Null(store.TryResolveFullPath(null));
        Assert.NotNull(store.TryResolveFullPath("2026/10/a.jpg"));
        Assert.False(store.Delete(@"..\outside.txt"));
    }
}

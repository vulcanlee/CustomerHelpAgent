using AntDesign;
using Bunit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Share.Agents;
using CustomerHelpAgent.Web.Components.Views.Shop;
using CustomerHelpAgent.Web.Configuration;

namespace CustomerHelpAgent.Tests.Components;

/// <summary>
/// 「我的 AI 記憶」頁（/shop/memory）：只列本人的記憶、單筆與全部刪除都先經確認窗、沒有記憶時顯示空狀態。
/// 確認窗由 ModalService 開在 <see cref="AntContainer"/> 底下，所以需要刪除的測試先渲染它。
/// </summary>
public sealed class ShopMemoryViewTests : ShopViewTestBase
{
    private readonly CustomerMemoryService memoryService;

    public ShopMemoryViewTests()
    {
        memoryService = new CustomerMemoryService(
            Factory,
            new RecordingAuditLogService(),
            TimeProvider.System,
            NullLogger<CustomerMemoryService>.Instance);
        Services.AddSingleton(memoryService);

        // 錯誤通知的「複製」按鈕需要的服務（CurrentUserService 已由基底註冊，不能覆蓋）。
        Services.AddSingleton(Options.Create(new SecuritySettings()));
        Services.AddSingleton<IWebHostEnvironment>(new ToastCopyButtonTests.TestWebHostEnvironment { EnvironmentName = "Production" });
    }

    [Fact]
    public async Task Memory_ListsOnlyOwnMemories()
    {
        var otherId = SeedOtherCustomer();
        await memoryService.UpsertAsync(CustomerId, MemoryKind.ShoeSize, "42", null);
        await memoryService.UpsertAsync(otherId, MemoryKind.PreferredColor, "紫紅", null);

        var cut = Render<ShopMemoryView>();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".shop-memory-item")));
        Assert.Contains("鞋碼", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("42", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("紫紅", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Memory_Delete_RemovesAndRefreshes()
    {
        await memoryService.UpsertAsync(CustomerId, MemoryKind.ShoeSize, "42", null);
        await memoryService.UpsertAsync(CustomerId, MemoryKind.PreferredColor, "黑", null);
        var container = Render<AntContainer>();
        var cut = Render<ShopMemoryView>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".shop-memory-item").Count));

        cut.Find(".shop-memory-item .shop-memory-delete").Click();
        container.WaitForElement(".ant-modal-confirm .ant-btn-dangerous").Click();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".shop-memory-item")));
        Assert.Single(await memoryService.GetAsync(CustomerId));
    }

    [Fact]
    public async Task Memory_DeleteAll_AsksConfirm()
    {
        await memoryService.UpsertAsync(CustomerId, MemoryKind.ShoeSize, "42", null);
        await memoryService.UpsertAsync(CustomerId, MemoryKind.PreferredColor, "黑", null);
        var container = Render<AntContainer>();
        var cut = Render<ShopMemoryView>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".shop-memory-item").Count));

        // 先取消：沒有任何記憶被刪。
        cut.Find(".shop-memory-delete-all").Click();
        container.WaitForElement(".ant-modal-confirm .ant-btn:not(.ant-btn-dangerous)").Click();
        container.WaitForAssertion(() => Assert.Empty(container.FindAll(".ant-modal-confirm .ant-btn-dangerous")));
        Assert.Equal(2, (await memoryService.GetAsync(CustomerId)).Count);

        // 再確認：全部刪除並顯示空狀態。
        cut.Find(".shop-memory-delete-all").Click();
        container.WaitForElement(".ant-modal-confirm .ant-btn-dangerous").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".shop-memory-empty")));
        Assert.Empty(await memoryService.GetAsync(CustomerId));
    }

    [Fact]
    public async Task Memory_DeleteAll_DbError_ShowsErrorNotification()
    {
        await memoryService.UpsertAsync(CustomerId, MemoryKind.ShoeSize, "42", null);
        var container = Render<AntContainer>();
        var cut = Render<ShopMemoryView>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".shop-memory-item")));

        // 模擬資料庫故障：刪除時資料表已不存在，服務會把例外往外丟。
        await using (var context = Factory.CreateDbContext())
        {
            await context.Database.ExecuteSqlRawAsync("DROP TABLE CustomerMemory");
        }

        cut.Find(".shop-memory-delete-all").Click();
        container.WaitForElement(".ant-modal-confirm .ant-btn-dangerous").Click();

        container.WaitForAssertion(() => Assert.Contains(
            "刪除記憶失敗",
            container.Find(".ant-notification-notice-description").TextContent,
            StringComparison.Ordinal));
    }

    [Fact]
    public void Memory_Empty_ShowsHint()
    {
        var cut = Render<ShopMemoryView>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".shop-memory-empty")));
        Assert.Contains("目前沒有", cut.Find(".shop-memory-empty").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".shop-memory-delete-all"));
    }

    private int SeedOtherCustomer()
    {
        using var context = Factory.CreateDbContext();
        var user = new MyUser { Account = "customer2", Name = "顧客二", Salt = Guid.NewGuid().ToString(), Password = "x", Status = true };
        context.MyUser.Add(user);
        context.SaveChanges();
        return user.Id;
    }
}

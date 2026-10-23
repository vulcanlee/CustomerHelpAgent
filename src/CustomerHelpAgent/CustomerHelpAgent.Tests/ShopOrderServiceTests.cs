using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Share.Shop;

namespace CustomerHelpAgent.Tests;

public sealed class ShopOrderServiceTests
{
    private static readonly DateTime Base = new(2026, 10, 1, 9, 0, 0);

    private static async Task<(SqliteConnection Conn, BackendDBContext Ctx)> CreateDbAsync()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var ctx = new BackendDBContext(new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(conn).Options);
        await ctx.Database.EnsureCreatedAsync();
        return (conn, ctx);
    }

    private static ShopOrderService Create(SqliteConnection conn) =>
        new(new TestDbContextFactory(conn), NullLogger<ShopOrderService>.Instance);

    private static MyUser User(string account) => new() { Account = account, Password = "x", Name = account };

    private static async Task<(int Alice, int Bob, ProductVariant Variant)> SeedAsync(BackendDBContext ctx)
    {
        var alice = User("alice");
        var bob = User("bob");
        ctx.MyUser.AddRange(alice, bob);
        var product = new Product { Code = "P1", Name = "跑鞋", Category = ProductCategories.Shoes, Price = 1999m };
        var variant = new ProductVariant { Product = product, Color = "黑", Size = "26", Stock = 5 };
        ctx.ProductVariant.Add(variant);
        await ctx.SaveChangesAsync();
        return (alice.Id, bob.Id, variant);
    }

    private static SalesOrder Order(string no, int userId, ProductVariant v, DateTime at, DateTime? delivered = null) => new()
    {
        OrderNo = no,
        CustomerUserId = userId,
        OrderedAt = at,
        DeliveredAt = delivered,
        RecipientName = "王小明",
        ShippingAddress = "台北市某路1號",
        RecipientPhone = "0912345678",
        Items = [new SalesOrderItem { ProductVariantId = v.Id, Quantity = 1, UnitPrice = 1999m }],
    };

    [Fact]
    public async Task GetMyOrders_ShouldOnlyReturnOwnOrders()
    {
        var (conn, ctx) = await CreateDbAsync();
        await using var connection = conn;
        await using var context = ctx;
        var (alice, bob, v) = await SeedAsync(ctx);
        ctx.SalesOrder.AddRange(
            Order("A1", alice, v, Base),
            Order("A2", alice, v, Base.AddDays(1), Base.AddDays(3)),
            Order("B1", bob, v, Base.AddDays(2)));
        await ctx.SaveChangesAsync();
        var shipped = Order("A3", alice, v, Base.AddDays(-1));
        ctx.SalesOrder.Add(shipped);
        await ctx.SaveChangesAsync();
        ctx.Shipment.Add(new Shipment { SalesOrderId = shipped.Id, Carrier = "黑貓", TrackingNo = "T1", StatusText = "運送中", ShippedAt = Base });
        await ctx.SaveChangesAsync();

        var orders = await Create(conn).GetMyOrdersAsync(alice);

        Assert.Equal(["A2", "A1", "A3"], orders.Select(x => x.OrderNo));
        Assert.Equal(["已簽收", "處理中", "配送中"], orders.Select(x => x.StatusText));
        var item = Assert.Single(orders[0].Items);
        Assert.Equal("跑鞋", item.ProductName);
        Assert.Equal(ProductCategories.Shoes, item.ProductCategory);
        Assert.Equal("黑", item.Color);
        Assert.Equal("26", item.Size);
        Assert.Equal(1999m, item.UnitPrice);
    }

    [Fact]
    public async Task GetMyOrderDetail_OtherCustomer_ShouldReturnNull()
    {
        var (conn, ctx) = await CreateDbAsync();
        await using var connection = conn;
        await using var context = ctx;
        var (alice, bob, v) = await SeedAsync(ctx);
        var order = Order("B1", bob, v, Base);
        ctx.SalesOrder.Add(order);
        await ctx.SaveChangesAsync();
        var service = Create(conn);

        Assert.Null(await service.GetMyOrderDetailAsync(alice, order.Id));
        Assert.NotNull(await service.GetMyOrderDetailAsync(bob, order.Id));
        Assert.Null(await service.GetMyOrderItemAsync(alice, order.Items[0].Id));
        Assert.NotNull(await service.GetMyOrderItemAsync(bob, order.Items[0].Id));
    }

    [Fact]
    public async Task GetMyOrderDetail_ShouldIncludeShipmentEventsInTimeOrder()
    {
        var (conn, ctx) = await CreateDbAsync();
        await using var connection = conn;
        await using var context = ctx;
        var (alice, _, v) = await SeedAsync(ctx);
        var order = Order("A1", alice, v, Base);
        ctx.SalesOrder.Add(order);
        await ctx.SaveChangesAsync();
        ctx.Shipment.Add(new Shipment
        {
            SalesOrderId = order.Id,
            Carrier = "黑貓",
            TrackingNo = "T1",
            StatusText = "運送中",
            ShippedAt = Base,
            Events =
            [
                new ShipmentEvent { OccurredAt = Base.AddHours(5), Location = "B站", Description = "第二" },
                new ShipmentEvent { OccurredAt = Base.AddHours(1), Location = "A站", Description = "第一" },
            ],
        });
        await ctx.SaveChangesAsync();

        var detail = await Create(conn).GetMyOrderDetailAsync(alice, order.Id);

        Assert.NotNull(detail);
        Assert.Equal("王小明", detail.RecipientName);
        Assert.Equal("配送中", detail.Summary.StatusText);
        Assert.NotNull(detail.Shipment);
        Assert.Equal("運送中", detail.Shipment.StatusText);
        Assert.Equal(["第一", "第二"], detail.Shipment.Events.Select(x => x.Description));
    }

    [Fact]
    public async Task GetVariantOptions_ShouldReturnSameProductVariantsInIdOrder_AndEmptyForOtherCustomer()
    {
        var (conn, ctx) = await CreateDbAsync();
        await using var connection = conn;
        await using var context = ctx;
        var (alice, bob, v) = await SeedAsync(ctx);
        ctx.ProductVariant.AddRange(
            new ProductVariant { ProductId = v.ProductId, Color = "黑", Size = "27", Stock = 0 },
            new ProductVariant { ProductId = v.ProductId, Color = "白", Size = "26", Stock = 3 },
            new ProductVariant { Product = new Product { Code = "P2", Name = "外套", Category = ProductCategories.Apparel, Price = 2990m }, Color = "灰", Size = "M", Stock = 1 });
        var order = Order("A1", alice, v, Base);
        ctx.SalesOrder.Add(order);
        await ctx.SaveChangesAsync();
        var service = Create(conn);

        var options = await service.GetVariantOptionsAsync(alice, order.Items[0].Id);

        Assert.Equal([("黑", "26"), ("黑", "27"), ("白", "26")], options);
        Assert.Empty(await service.GetVariantOptionsAsync(bob, order.Items[0].Id));
        Assert.Empty(await service.GetVariantOptionsAsync(alice, 999));
    }

    [Fact]
    public void ShopOrderModels_ShouldNotExposePhone()
    {
        var types = typeof(CustomerHelpAgent.Models.Shop.ShopOrderSummary).Assembly.GetTypes()
            .Where(t => t.IsPublic && t.Namespace == "CustomerHelpAgent.Models.Shop" && t.Name.StartsWith("Shop", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(types);
        foreach (var type in types)
        {
            Assert.DoesNotContain(type.GetProperties(), p => p.Name.Contains("Phone", StringComparison.OrdinalIgnoreCase));
        }
    }
}

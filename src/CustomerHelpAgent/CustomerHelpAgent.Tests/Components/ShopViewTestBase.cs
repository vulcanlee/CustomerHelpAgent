using System.Security.Claims;
using AutoMapper;
using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Share.Shop;

namespace CustomerHelpAgent.Tests.Components;

/// <summary>
/// 顧客商店檢視的 bUnit 測試基底：in-memory SQLite 上的真服務（沒有服務介面可替換），
/// 加上一位擁有「顧客商店」權限的登入顧客，讓檢視跑完真的 <c>AuthenticationStateHelper.Check()</c>。
/// 種子：一雙跑鞋（黑／白 × 40／41／42），顧客的一張訂單內有「黑 40」與「白 41」兩個商品。
/// </summary>
public abstract class ShopViewTestBase : ComponentTestBase
{
    private readonly SqliteConnection connection;
    private readonly BackendDBContext longLivedContext;
    private readonly string fileRoot = Path.Combine(Path.GetTempPath(), "ShopViewTests-" + Guid.NewGuid().ToString("N"));

    protected ShopViewTestBase()
    {
        connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        Factory = new TestDbContextFactory(connection);
        longLivedContext = Factory.CreateDbContext();
        longLivedContext.Database.EnsureCreated();
        Seed(longLivedContext);

        var currentUser = new CurrentUserService();
        var audit = new RecordingAuditLogService();
        var time = TimeProvider.System;
        var settings = new SystemSettings();
        settings.ExternalFileSystem.AfterSalesFilePath = fileRoot;
        var shopOrderService = new ShopOrderService(Factory, TestDemoControl.Off(Factory), NullLogger<ShopOrderService>.Instance);

        // AntDesign 的 Select、TextArea 渲染後會向 JS 量自己的尺寸；Loose 模式回傳 null 會讓它們 NullReferenceException。
        JSInterop.Setup<AntDesign.JsInterop.DomRect>(_ => true).SetResult(new AntDesign.JsInterop.DomRect());
        JSInterop.Setup<AntDesign.Internal.TextAreaInfo>(_ => true).SetResult(new AntDesign.Internal.TextAreaInfo());

        Services.AddLogging();
        Services.AddSingleton(time);
        Services.AddSingleton(currentUser);
        Services.AddSingleton<AuthenticationStateProvider>(new FixedAuthenticationStateProvider(CustomerId, CustomerStamp));
        Services.AddSingleton(CreateAuthenticationStateHelper(currentUser));
        Services.AddSingleton(shopOrderService);
        Services.AddSingleton(new ProcessMetricService(Factory, time, NullLogger<ProcessMetricService>.Instance));
        Services.AddSingleton(new AfterSalesRequestService(
            Factory,
            new AfterSalesPolicyService(Factory, audit, currentUser, TestDemoControl.Off(Factory), time, NullLogger<AfterSalesPolicyService>.Instance),
            shopOrderService,
            new AfterSalesFileStore(Options.Create(settings), NullLogger<AfterSalesFileStore>.Instance),
            audit,
            currentUser,
            time,
            NullLogger<AfterSalesRequestService>.Instance));
    }

    protected TestDbContextFactory Factory { get; }

    protected int CustomerId { get; private set; }

    protected int Black40ItemId { get; private set; }

    protected int White41ItemId { get; private set; }

    private string CustomerStamp { get; set; } = string.Empty;

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            longLivedContext.Dispose();
            connection.Dispose();
            if (Directory.Exists(fileRoot))
            {
                Directory.Delete(fileRoot, recursive: true);
            }
        }
    }

    private void Seed(BackendDBContext context)
    {
        var role = new RoleView { Name = "顧客", TabViewJson = $"""["{MagicObjectHelper.角色_顧客商店}"]""" };
        var permission = new Permission { Key = MagicObjectHelper.角色_顧客商店, DisplayName = MagicObjectHelper.角色_顧客商店 };
        context.AddRange(role, permission);
        context.SaveChanges();
        context.RolePermissionMap.Add(new RolePermissionMap { RoleViewId = role.Id, PermissionId = permission.Id });

        var salt = Guid.NewGuid().ToString();
        var customer = new MyUser
        {
            Account = "customer1",
            Name = "顧客一",
            Salt = salt,
            Password = PasswordHelper.GetPasswordSHA(salt, "secure-password"),
            Status = true,
            RoleViewId = role.Id,
        };
        context.MyUser.Add(customer);

        var shoes = new Product { Code = "SH-RUN01", Name = "示範跑鞋", Category = ProductCategories.Shoes, Price = 2980m };
        foreach (var color in new[] { "黑", "白" })
        {
            foreach (var size in new[] { "40", "41", "42" })
            {
                shoes.Variants.Add(new ProductVariant { Color = color, Size = size, Stock = 5 });
            }
        }

        context.Product.Add(shoes);
        context.SaveChanges();

        var now = DateTime.Now;
        var order = new SalesOrder
        {
            OrderNo = "SO-TEST-0001",
            CustomerUserId = customer.Id,
            OrderedAt = now.AddDays(-6),
            DeliveredAt = now.AddDays(-3),
            RecipientName = "測試收件人",
            ShippingAddress = "測試地址",
            RecipientPhone = "0900000000",
            Items =
            [
                new SalesOrderItem { ProductVariantId = Variant(shoes, "黑", "40").Id, Quantity = 1, UnitPrice = 2980m },
                new SalesOrderItem { ProductVariantId = Variant(shoes, "白", "41").Id, Quantity = 1, UnitPrice = 2980m },
            ],
        };
        context.SalesOrder.Add(order);
        context.SaveChanges();
        context.ChangeTracker.Clear();

        CustomerId = customer.Id;
        CustomerStamp = customer.SecurityStamp;
        Black40ItemId = order.Items[0].Id;
        White41ItemId = order.Items[1].Id;
    }

    private static ProductVariant Variant(Product product, string color, string size)
        => product.Variants.Single(x => x.Color == color && x.Size == size);

    private AuthenticationStateHelper CreateAuthenticationStateHelper(CurrentUserService currentUser)
    {
        var loggerFactory = NullLoggerFactory.Instance;
        var mapper = new MapperConfiguration(configuration => configuration.AddProfile<AutoMapping>(), loggerFactory).CreateMapper();
        var auditLogService = new AuditLogService(Factory, loggerFactory.CreateLogger<AuditLogService>());

        return new AuthenticationStateHelper(
            loggerFactory.CreateLogger<AuthenticationStateHelper>(),
            mapper,
            new MyUserService(Factory, mapper, loggerFactory.CreateLogger<MyUserService>(), new RbacWriteService(longLivedContext, NullLogger<RbacWriteService>.Instance), auditLogService, currentUser, Options.Create(new BootstrapSettings()), PasswordTestDefaults.Policy()),
            currentUser,
            new RolePermissionService(),
            new EffectiveTeamResolver(longLivedContext, new ContextTeamTreeCache(longLivedContext), NullLogger<EffectiveTeamResolver>.Instance),
            new PermissionChecker(longLivedContext, NullLogger<PermissionChecker>.Instance),
            auditLogService,
            PasswordTestDefaults.Policy(),
            new SecurityStampService(Factory, TimeProvider.System, NullLogger<SecurityStampService>.Instance),
            TwoFactorTestDefaults.Service(Factory, currentUser: currentUser));
    }

    private sealed class FixedAuthenticationStateProvider(int userId, string securityStamp) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
            => Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.Sid, userId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    new Claim(MagicObjectHelper.SecurityStampClaimType, securityStamp),
                ],
                "Test"))));
    }
}

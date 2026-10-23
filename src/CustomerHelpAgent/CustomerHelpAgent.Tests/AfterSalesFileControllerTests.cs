using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Share.Shop;
using CustomerHelpAgent.Web.Auth;

namespace CustomerHelpAgent.Tests;

/// <summary>售後照片下載端點（Cookie 驗證）：本人與審核者拿得到檔案，其他顧客一律 404，匿名被挑戰。</summary>
[Collection(nameof(IntegrationHostCollection))]
public sealed class AfterSalesFileControllerTests : IClassFixture<ApiTestApplicationFactory>
{
    private static readonly byte[] PhotoBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    private readonly ApiTestApplicationFactory factory;

    public AfterSalesFileControllerTests(ApiTestApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task Download_AsOwner_ShouldReturnFile()
    {
        var owner = await AddUserAsync();
        var attachmentId = await AddAttachmentAsync(owner.Id);

        var response = await GetAsync(Url(attachmentId), Cookie(owner));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("inline", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal(PhotoBytes, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Download_AsOtherCustomer_ShouldReturn404()
    {
        var owner = await AddUserAsync();
        var other = await AddUserAsync();
        var attachmentId = await AddAttachmentAsync(owner.Id);

        var response = await GetAsync(Url(attachmentId), Cookie(other));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Download_AsReviewer_ShouldReturnFile()
    {
        var owner = await AddUserAsync();
        var reviewer = await AddUserAsync(MagicObjectHelper.角色_售後服務, MagicObjectHelper.角色_售後審核);
        var attachmentId = await AddAttachmentAsync(owner.Id);

        var response = await GetAsync(Url(attachmentId), Cookie(reviewer));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(PhotoBytes, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Download_Anonymous_ShouldRedirectOrUnauthorized()
    {
        var owner = await AddUserAsync();
        var attachmentId = await AddAttachmentAsync(owner.Id);

        var response = await GetAsync(Url(attachmentId), cookie: null);

        // 與 ProjectFileDownloadEndpoint_ShouldBeRegistered 相同：Cookie scheme 對 /api 路徑回 401。
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static string Url(int attachmentId) => $"/api/after-sales-files/{attachmentId}/download";

    private async Task<HttpResponseMessage> GetAsync(string url, string? cookie)
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }

        return await client.SendAsync(request);
    }

    private string Cookie(MyUser user)
    {
        var options = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(MagicObjectHelper.CookieScheme);
        var ticket = options.TicketDataFormat.Protect(new AuthenticationTicket(CookieClaims.Create(user), MagicObjectHelper.CookieScheme));
        return $"{options.Cookie.Name}={ticket}";
    }

    /// <summary>建立一般使用者；給了權限鍵就另建一個角色並設為主要角色。</summary>
    private async Task<MyUser> AddUserAsync(params string[] permissionKeys)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        var account = $"as-file-{Guid.NewGuid():N}";
        var user = new MyUser { Account = account, Name = account, Password = "x", Status = true };

        if (permissionKeys.Length > 0)
        {
            var role = new RoleView { Name = account, TabViewJson = Newtonsoft.Json.JsonConvert.SerializeObject(permissionKeys) };
            db.RoleView.Add(role);
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<IRbacWriteService>().SyncRolePermissionsAsync(db, role.Id, permissionKeys);
            user.RoleViewId = role.Id;
        }

        db.MyUser.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    /// <summary>建立一筆屬於 <paramref name="customerUserId"/> 的售後申請與一張照片（含實體檔），回傳附件 Id。</summary>
    private async Task<int> AddAttachmentAsync(int customerUserId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        var root = scope.ServiceProvider.GetRequiredService<IOptions<SystemSettings>>().Value.ExternalFileSystem.AfterSalesFilePath;
        var key = Guid.NewGuid().ToString("N");
        var relativePath = $"2026/10/{key}.png";
        var fullPath = Path.Combine(root, "2026", "10", $"{key}.png");
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllBytesAsync(fullPath, PhotoBytes);

        var now = new DateTime(2026, 10, 23, 10, 0, 0);
        var variant = new ProductVariant
        {
            Product = new Product { Code = $"T-{key}", Name = "測試鞋", Category = ProductCategories.Shoes, Price = 1000m },
            Color = "黑",
            Size = "41",
            Stock = 1,
        };
        var item = new SalesOrderItem { ProductVariant = variant, Quantity = 1, UnitPrice = 1000m };
        db.SalesOrder.Add(new SalesOrder
        {
            OrderNo = $"T{key}",
            CustomerUserId = customerUserId,
            OrderedAt = now.AddDays(-5),
            DeliveredAt = now.AddDays(-3),
            RecipientName = "測試",
            ShippingAddress = "測試地址",
            RecipientPhone = "0900000000",
            Items = [item],
        });
        var attachment = new AfterSalesAttachment
        {
            OriginalFileName = "sole.png",
            StoredFileName = $"{key}.png",
            RelativePath = relativePath,
            ContentType = "image/png",
            FileSize = PhotoBytes.Length,
            CreatedAt = now,
        };
        db.AfterSalesRequest.Add(new AfterSalesRequest
        {
            RequestNo = key,
            CaseKey = Guid.NewGuid(),
            CustomerUserId = customerUserId,
            SalesOrderItem = item,
            Type = AfterSalesType.QualityIssue,
            Status = AfterSalesStatus.Submitted,
            Channel = AfterSalesChannel.Traditional,
            Description = "鞋底開膠",
            CreatedAt = now,
            UpdatedAt = now,
            Attachments = [attachment],
        });
        await db.SaveChangesAsync();
        return attachment.Id;
    }
}

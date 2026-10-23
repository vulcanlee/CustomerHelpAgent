using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Business.Startup;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Web.Auth;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 展示身分切換（<c>/Auths/DemoSwitch</c>，真正的主機）：管理員以一次性票券切換成示範帳號、再切回；
/// 非管理員、非示範帳號、票券重複使用／過期／別人的、沒有展示者 claim、展示者版本換過一律拒絕。
/// 結果以回應的 Set-Cookie 解出的身分驗證（與登入 Cookie 相同的 TicketDataFormat）。
/// </summary>
[Collection(nameof(IntegrationHostCollection))]
public sealed class DemoSwitchTests : IClassFixture<ApiTestApplicationFactory>
{
    private readonly ApiTestApplicationFactory factory;

    public DemoSwitchTests(ApiTestApplicationFactory factory)
    {
        this.factory = factory;
    }

    private DemoSwitchTicketService Tickets => factory.Services.GetRequiredService<DemoSwitchTicketService>();

    [Fact]
    public async Task Switch_AdminToCustomer1_SignsInAsCustomer()
    {
        var admin = await AddUserAsync(isAdmin: true);
        var customer1 = await GetUserAsync(DemoSeedData.Customer1);

        var response = await GetAsync(SwitchUrl(Tickets.Issue(admin.Id, DemoSeedData.Customer1)), Cookie(admin));

        Assert.Equal("/shop", RedirectPath(response));
        var ticket = ReadIssuedCookie(response);
        Assert.NotNull(ticket);
        var principal = ticket!.Principal;
        Assert.Equal(customer1.Id.ToString(), principal.FindFirstValue(ClaimTypes.Sid));
        Assert.Equal(DemoSeedData.Customer1, principal.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.Equal(customer1.SecurityStamp, principal.FindFirstValue(MagicObjectHelper.SecurityStampClaimType));
        Assert.Equal(admin.Id.ToString(), principal.FindFirstValue(DemoClaims.PresenterId));
        Assert.Equal(admin.SecurityStamp, principal.FindFirstValue(DemoClaims.PresenterStamp));
        Assert.False(ticket.Properties.IsPersistent);

        var audit = await FindAuditAsync(AuditActions.Demo.SwitchIdentity, admin.Id);
        Assert.True(audit.Success);
        Assert.Equal(admin.Account, audit.ActorAccount);
        Assert.Equal(customer1.Id.ToString(), audit.TargetId);
    }

    [Theory]
    [InlineData(DemoSeedData.Agent1)]
    [InlineData(DemoSeedData.Merchant1)]
    public async Task Switch_AdminToStaffAccount_RedirectsToBackOffice(string account)
    {
        var admin = await AddUserAsync(isAdmin: true);

        var response = await GetAsync(SwitchUrl(Tickets.Issue(admin.Id, account)), Cookie(admin));

        Assert.Equal("/App", RedirectPath(response));
        Assert.Equal(account, ReadIssuedCookie(response)?.Principal.FindFirstValue(ClaimTypes.NameIdentifier));
    }

    [Fact]
    public async Task Switch_NonAdmin_IsRejected()
    {
        var user = await AddUserAsync(isAdmin: false);

        var response = await GetAsync(SwitchUrl(Tickets.Issue(user.Id, DemoSeedData.Customer1)), Cookie(user));

        AssertRejected(response);
        var audit = await FindAuditAsync(AuditActions.Demo.SwitchIdentity, user.Id);
        Assert.False(audit.Success);
        Assert.Equal("reason=NotAdmin", audit.Detail);
    }

    [Fact]
    public async Task Switch_TargetNotDemoAccount_IsRejected()
    {
        var admin = await AddUserAsync(isAdmin: true);
        var other = await AddUserAsync(isAdmin: false);

        var response = await GetAsync(SwitchUrl(Tickets.Issue(admin.Id, other.Account)), Cookie(admin));

        AssertRejected(response);
        var audit = await FindAuditAsync(AuditActions.Demo.SwitchIdentity, admin.Id);
        Assert.False(audit.Success);
        Assert.Equal("reason=NotDemoAccount", audit.Detail);
    }

    [Fact]
    public async Task Ticket_ReusedOrExpired_IsRejected()
    {
        var admin = await AddUserAsync(isAdmin: true);
        var ticket = Tickets.Issue(admin.Id, DemoSeedData.Customer1);

        Assert.Equal("/shop", RedirectPath(await GetAsync(SwitchUrl(ticket), Cookie(admin))));
        var reused = await GetAsync(SwitchUrl(ticket), Cookie(admin));
        AssertRejected(reused);

        // 別人的票券、亂填的票券、沒有票券：一律拒絕。
        var otherAdmin = await AddUserAsync(isAdmin: true);
        AssertRejected(await GetAsync(SwitchUrl(Tickets.Issue(admin.Id, DemoSeedData.Customer1)), Cookie(otherAdmin)));
        AssertRejected(await GetAsync(SwitchUrl("not-a-ticket"), Cookie(admin)));
        AssertRejected(await GetAsync("/Auths/DemoSwitch", Cookie(admin)));

        // 過期：60 秒後失效（單元層級，用可推進的時鐘）。
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 24, 2, 0, 0, TimeSpan.Zero));
        var service = new DemoSwitchTicketService(time, NullLogger<DemoSwitchTicketService>.Instance);
        var fresh = service.Issue(admin.Id, DemoSeedData.Customer1);
        var stale = service.Issue(admin.Id, DemoSeedData.Customer1);
        time.Advance(TimeSpan.FromSeconds(59));
        Assert.True(service.TryRedeem(fresh, out var redeemed));
        Assert.Equal(new DemoSwitchTicket(admin.Id, DemoSwitchPurpose.SwitchIdentity, DemoSeedData.Customer1), redeemed);
        Assert.False(service.TryRedeem(fresh, out _));
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.False(service.TryRedeem(stale, out _));
    }

    [Fact]
    public async Task SwitchBack_WithoutPresenterClaim_IsRejected()
    {
        var customer1 = await GetUserAsync(DemoSeedData.Customer1);

        var response = await GetAsync(SwitchUrl(Tickets.IssueSwitchBack(customer1.Id)), Cookie(customer1));

        AssertRejected(response, CustomerFailedPath);
        var audit = await FindAuditAsync(AuditActions.Demo.SwitchBack, customer1.Id);
        Assert.False(audit.Success);
        Assert.Equal("reason=NoPresenter", audit.Detail);
    }

    [Fact]
    public async Task SwitchBack_RestoresPresenter()
    {
        var admin = await AddUserAsync(isAdmin: true);
        var customer1 = await GetUserAsync(DemoSeedData.Customer1);
        var switchedCookie = await SwitchAsync(admin, DemoSeedData.Customer1);

        // 已切換的身分不能再往下切（示範帳號不是管理員）。
        AssertRejected(await GetAsync(SwitchUrl(Tickets.Issue(customer1.Id, DemoSeedData.Customer2)), switchedCookie), CustomerFailedPath);

        var response = await GetAsync(SwitchUrl(Tickets.IssueSwitchBack(customer1.Id)), switchedCookie);

        Assert.Equal("/demo-console", RedirectPath(response));
        var principal = ReadIssuedCookie(response)!.Principal;
        Assert.Equal(admin.Id.ToString(), principal.FindFirstValue(ClaimTypes.Sid));
        Assert.Equal(admin.SecurityStamp, principal.FindFirstValue(MagicObjectHelper.SecurityStampClaimType));
        Assert.Null(principal.FindFirst(DemoClaims.PresenterId));
        Assert.Null(principal.FindFirst(DemoClaims.PresenterStamp));

        var audit = await FindAuditAsync(AuditActions.Demo.SwitchBack, admin.Id);
        Assert.True(audit.Success);
        Assert.Equal(admin.Id.ToString(), audit.TargetId);
    }

    [Fact]
    public async Task SwitchBack_PresenterSessionRevoked_SignsOutToLogin()
    {
        var admin = await AddUserAsync(isAdmin: true);
        var customer1 = await GetUserAsync(DemoSeedData.Customer1);
        var switchedCookie = await SwitchAsync(admin, DemoSeedData.Customer1);
        await factory.Services.GetRequiredService<ISecurityStampService>().RotateAsync(admin.Id);

        var response = await GetAsync(SwitchUrl(Tickets.IssueSwitchBack(customer1.Id)), switchedCookie);

        Assert.Equal("/Auths/Login", RedirectPath(response));
        Assert.Null(ReadIssuedCookie(response));
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), x => x.StartsWith(CookieName + "=;", StringComparison.Ordinal));
        var audit = await FindAuditAsync(AuditActions.Demo.SwitchBack, customer1.Id);
        Assert.False(audit.Success);
        Assert.Equal("reason=PresenterSessionRevoked", audit.Detail);
    }

    [Fact]
    public async Task Switch_WithReturnUrl_RedirectsThere()
    {
        var admin = await AddUserAsync(isAdmin: true);

        var response = await GetAsync(SwitchUrl(Tickets.Issue(admin.Id, DemoSeedData.Customer1, "/shop/assistant")), Cookie(admin));

        Assert.Equal("/shop/assistant", RedirectPath(response));
        Assert.Equal(DemoSeedData.Customer1, ReadIssuedCookie(response)?.Principal.FindFirstValue(ClaimTypes.NameIdentifier));
    }

    [Theory]
    [InlineData("https://evil.example/shop")]
    [InlineData("//evil.example/shop")]
    [InlineData(@"/\evil.example/shop")]
    public async Task Switch_WithExternalReturnUrl_FallsBackToDefault(string returnUrl)
    {
        var admin = await AddUserAsync(isAdmin: true);

        var response = await GetAsync(SwitchUrl(Tickets.Issue(admin.Id, DemoSeedData.Customer1, returnUrl)), Cookie(admin));

        Assert.Equal("/shop", RedirectPath(response));
        Assert.Equal(DemoSeedData.Customer1, ReadIssuedCookie(response)?.Principal.FindFirstValue(ClaimTypes.NameIdentifier));
    }

    /// <summary>顧客（沒有後台首頁、有顧客商店）失敗時導回商店，ShopLayout 的提示列才看得到錯誤；後台使用者仍回 /App。</summary>
    [Fact]
    public async Task Switch_FailureAsCustomer_RedirectsToShop()
    {
        var customer1 = await GetUserAsync(DemoSeedData.Customer1);
        var staff = await AddUserAsync(isAdmin: false);

        AssertRejected(await GetAsync(SwitchUrl(Tickets.Issue(customer1.Id, DemoSeedData.Customer2)), Cookie(customer1)), CustomerFailedPath);
        AssertRejected(await GetAsync(SwitchUrl("not-a-ticket"), Cookie(customer1)), CustomerFailedPath);
        AssertRejected(await GetAsync(SwitchUrl(Tickets.Issue(staff.Id, DemoSeedData.Customer2)), Cookie(staff)));
    }

    /// <summary>
    /// 切回前也以資料庫比對目前（示範帳號）Cookie 的版本：Cookie 驗證器的快取還沒看到新版本（例如別的行程換的），
    /// 這一頁也不能讓失效的工作階段換回展示者的身分。
    /// </summary>
    [Fact]
    public async Task SwitchBack_DemoAccountStampRotated_IsRejected()
    {
        var admin = await AddUserAsync(isAdmin: true);
        var customer2 = await GetUserAsync(DemoSeedData.Customer2);
        var switchedCookie = await SwitchAsync(admin, DemoSeedData.Customer2);
        var stamps = factory.Services.GetRequiredService<ISecurityStampService>();
        Assert.True(factory.Services.GetRequiredService<IOptionsMonitor<CookieSettings>>().CurrentValue.ValidationIntervalMinutes > 0);

        try
        {
            // 驗證器快取目前的版本，再直接在資料庫換掉（不經 RotateAsync，快取不會失效）。
            await stamps.GetStateAsync(customer2.Id, TimeSpan.FromMinutes(1));
            using (var scope = factory.Services.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<BackendDBContext>().MyUser
                    .Where(x => x.Id == customer2.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.SecurityStamp, SecurityStamps.New()));
            }

            var response = await GetAsync(SwitchUrl(Tickets.IssueSwitchBack(customer2.Id)), switchedCookie);

            Assert.Equal("/Auths/Login", RedirectPath(response));
            Assert.Null(ReadIssuedCookie(response));
            var audit = await FindAuditAsync(AuditActions.Demo.SwitchBack, customer2.Id);
            Assert.False(audit.Success);
            Assert.Equal("reason=SessionRevoked", audit.Detail);
        }
        finally
        {
            stamps.Invalidate(customer2.Id);
        }
    }

    private async Task<string> SwitchAsync(MyUser admin, string account)
    {
        var response = await GetAsync(SwitchUrl(Tickets.Issue(admin.Id, account)), Cookie(admin));
        var value = response.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith(CookieName + "=", StringComparison.Ordinal));
        return value.Split(';')[0];
    }

    private static string SwitchUrl(string ticket) => $"/Auths/DemoSwitch?ticket={Uri.EscapeDataString(ticket)}";

    private static string RedirectPath(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found, $"預期導向，實際 {(int)response.StatusCode}");
        return new Uri(new Uri("http://localhost"), response.Headers.Location!).AbsolutePath;
    }

    private const string CustomerFailedPath = "/shop";

    /// <summary>拒絕：導向 /App（顧客為 /shop）並帶錯誤提示，而且沒有發出新的登入 Cookie。</summary>
    private void AssertRejected(HttpResponseMessage response, string path = "/App")
    {
        Assert.Equal(path, RedirectPath(response));
        Assert.Equal("?demoSwitch=failed", new Uri(new Uri("http://localhost"), response.Headers.Location!).Query);
        Assert.Null(ReadIssuedCookie(response));
    }

    private string CookieName => CookieOptions().Cookie.Name!;

    private CookieAuthenticationOptions CookieOptions()
        => factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(MagicObjectHelper.CookieScheme);

    /// <summary>回應發出的登入 Cookie（刪除用的空值不算）解回票證；沒有就回 null。</summary>
    private AuthenticationTicket? ReadIssuedCookie(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            return null;
        }

        var value = values
            .Select(x => x.Split(';')[0])
            .Where(x => x.StartsWith(CookieName + "=", StringComparison.Ordinal))
            .Select(x => x[(CookieName.Length + 1)..])
            .SingleOrDefault(x => x.Length > 0);
        return value is null ? null : CookieOptions().TicketDataFormat.Unprotect(value);
    }

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
        var ticket = CookieOptions().TicketDataFormat.Protect(new AuthenticationTicket(CookieClaims.Create(user), MagicObjectHelper.CookieScheme));
        return $"{CookieName}={ticket}";
    }

    private async Task<MyUser> AddUserAsync(bool isAdmin)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        var account = $"demo-switch-{Guid.NewGuid():N}";
        var user = new MyUser { Account = account, Name = account, Password = "x", Status = true, IsAdmin = isAdmin };
        db.MyUser.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private async Task<MyUser> GetUserAsync(string account)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<BackendDBContext>().MyUser.AsNoTracking().SingleAsync(x => x.Account == account);
    }

    /// <summary>這位使用者最新的一筆指定稽核。</summary>
    private async Task<AuditLog> FindAuditAsync(string action, int actorUserId)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<BackendDBContext>().AuditLog.AsNoTracking()
            .Where(x => x.Action == action && x.ActorUserId == actorUserId)
            .OrderByDescending(x => x.Id)
            .FirstAsync();
    }
}

/// <summary>示範模式關閉的主機：切換頁一律拒絕（示範功能只在 <c>DemoSettings:Enabled</c> 時存在）。</summary>
[Collection(nameof(IntegrationHostCollection))]
public sealed class DemoSwitchDisabledTests : IClassFixture<DemoDisabledApplicationFactory>
{
    private readonly DemoDisabledApplicationFactory factory;

    public DemoSwitchDisabledTests(DemoDisabledApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task Switch_DemoDisabled_IsRejected()
    {
        int adminId;
        MyUser admin;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
            var account = $"demo-off-{Guid.NewGuid():N}";
            admin = new MyUser { Account = account, Name = account, Password = "x", Status = true, IsAdmin = true };
            db.MyUser.Add(admin);
            await db.SaveChangesAsync();
            adminId = admin.Id;
        }

        var options = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(MagicObjectHelper.CookieScheme);
        var cookie = $"{options.Cookie.Name}={options.TicketDataFormat.Protect(new AuthenticationTicket(CookieClaims.Create(admin), MagicObjectHelper.CookieScheme))}";
        var ticket = factory.Services.GetRequiredService<DemoSwitchTicketService>().Issue(adminId, DemoSeedData.Customer1);

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/Auths/DemoSwitch?ticket={Uri.EscapeDataString(ticket)}");
        request.Headers.Add("Cookie", cookie);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/App?demoSwitch=failed", new Uri(new Uri("http://localhost"), response.Headers.Location!).PathAndQuery);
        Assert.False(response.Headers.TryGetValues("Set-Cookie", out var values)
            && values.Any(x => x.StartsWith(options.Cookie.Name + "=", StringComparison.Ordinal) && !x.StartsWith(options.Cookie.Name + "=;", StringComparison.Ordinal)));

        using var verify = factory.Services.CreateScope();
        var audit = await verify.ServiceProvider.GetRequiredService<BackendDBContext>().AuditLog.AsNoTracking()
            .SingleAsync(x => x.Action == AuditActions.Demo.SwitchIdentity && x.ActorUserId == adminId);
        Assert.False(audit.Success);
        Assert.Equal("reason=DemoDisabled", audit.Detail);
    }
}

/// <summary>與 <see cref="ApiTestApplicationFactory"/> 相同，但關閉示範模式。</summary>
public sealed class DemoDisabledApplicationFactory : ApiTestApplicationFactory
{
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DemoSettings:Enabled"] = "false",
            });
        });
    }
}

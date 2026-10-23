using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AutoMapper;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Business.Startup;
using CustomerHelpAgent.Dtos.Auths;
using CustomerHelpAgent.Dtos.Commons;
using CustomerHelpAgent.Models.AdapterModel;
using CustomerHelpAgent.Models.Others;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Web.Auth;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 工作階段失效（0.9.103 起）：哪些操作會換工作階段版本、版本快取、ticket 的一次性與時效。
/// </summary>
public sealed class SessionTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly TestDbContextFactory factory;
    private readonly ManualTimeProvider time = new(new DateTimeOffset(2026, 10, 4, 1, 0, 0, TimeSpan.Zero));
    private readonly IMapper mapper;
    private readonly RecordingAuditLogService audit = new();
    private readonly CurrentUserService currentUser = new() { CurrentUser = new CurrentUser { Id = 999_999, Account = "admin" } };
    private readonly List<IDisposable> disposables = [];

    public SessionTests()
    {
        connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        connection.Open();
        factory = new TestDbContextFactory(connection);
        using var context = factory.CreateDbContext();
        context.Database.EnsureCreated();
        mapper = new MapperConfiguration(c => c.AddProfile<AutoMapping>(), LoggerFactory.Create(_ => { })).CreateMapper();
    }

    // ---------- 哪些操作會換版本 ----------

    [Fact]
    public async Task ApplyingAPassword_ShouldRotate_ButTheLoginRehashShouldNot()
    {
        var legacy = AddUser("legacy", legacyPassword: "Legacy-pass1");
        var before = await StampOfAsync(legacy);

        Assert.NotNull((await Login().LoginAsync("legacy", "Legacy-pass1")).User);
        Assert.Equal(before, await StampOfAsync(legacy));

        await using var context = factory.CreateDbContext();
        var user = await context.MyUser.SingleAsync(x => x.Id == legacy);
        await PasswordTestDefaults.Policy().ApplyAsync(context, user, "Passw0rd-9", false);
        await context.SaveChangesAsync();
        Assert.NotEqual(before, await StampOfAsync(legacy));
    }

    [Fact]
    public async Task AdminEdit_ShouldRotateOnlyWhenStatusAdminFlagOrRolesChange()
    {
        var roleA = AddRole("A");
        var roleB = AddRole("B");
        var service = UserService();
        Assert.True((await service.AddAsync(new MyUserAdapterModel { Account = "bob", Name = "bob", Password = "Passw0rd-1", Status = true, RoleViewId = roleA })).Success);
        var id = await IdOfAsync("bob");

        async Task<bool> RotatesAsync(Action<MyUserAdapterModel> change)
        {
            var before = await StampOfAsync(id);
            var model = (await service.GetAsync(id)).Clone();
            var (additional, teams) = await service.GetUserAssignmentsAsync(id);
            model.AdditionalRoleIds = additional;
            model.TeamNames = teams;
            change(model);
            Assert.True((await service.UpdateAsync(model)).Success);
            return before != await StampOfAsync(id);
        }

        Assert.False(await RotatesAsync(m => m.Name = "Bob Lee"));
        Assert.True(await RotatesAsync(m => m.IsAdmin = true));
        Assert.True(await RotatesAsync(m => m.AdditionalRoleIds = [roleB]));
        Assert.False(await RotatesAsync(m => m.Email = "bob@example.com"));
        Assert.True(await RotatesAsync(m => m.RoleViewId = roleB));
        Assert.True(await RotatesAsync(m => m.Password = "Passw0rd-2"));
        Assert.True(await RotatesAsync(m => m.Status = false));
    }

    [Fact]
    public async Task DeleteAndForceLogout_ShouldRotate_ForceLogoutKeepsTheConcurrencyStamp()
    {
        var service = UserService();
        var deleted = AddUser("gone");
        var deletedBefore = await StampOfAsync(deleted);
        Assert.True((await service.DeleteAsync(deleted)).Success);
        Assert.NotEqual(deletedBefore, await StampOfAsync(deleted));

        var id = AddUser("alice");
        var before = await LoadAsync(id);
        Assert.True((await service.ForceLogoutAsync(id)).Success);
        var after = await LoadAsync(id);
        Assert.NotEqual(before.SecurityStamp, after.SecurityStamp);
        Assert.Equal(before.ConcurrencyStamp, after.ConcurrencyStamp);
        Assert.Contains(audit.Entries, e => e.Action == AuditActions.User.ForceLogout);
        Assert.False((await service.ForceLogoutAsync(424242)).Success);
    }

    [Fact]
    public async Task SupportSeeder_ShouldRotateOnlyWhenTheConfiguredPasswordChanges()
    {
        await using (var context = factory.CreateDbContext())
        {
            context.RoleView.Add(new RoleView { Name = MagicObjectHelper.預設角色, TabViewJson = "[]" });
            await context.SaveChangesAsync();
        }

        await SeedSupportAsync("first-password");
        var id = await IdOfAsync("support");
        var first = await StampOfAsync(id);

        await SeedSupportAsync("first-password");
        Assert.Equal(first, await StampOfAsync(id));

        await SeedSupportAsync("second-password");
        Assert.NotEqual(first, await StampOfAsync(id));
    }

    [Fact]
    public async Task Login_ShouldFillAnEmptyStamp()
    {
        var id = AddUser("old", password: "Passw0rd-1");
        await using (var context = factory.CreateDbContext())
        {
            await context.MyUser.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.SecurityStamp, string.Empty));
        }

        var (_, user) = await Login().LoginAsync("old", "Passw0rd-1");

        Assert.NotNull(user);
        Assert.False(string.IsNullOrEmpty(await StampOfAsync(id)));
        Assert.Equal(await StampOfAsync(id), user.SecurityStamp);
    }

    // ---------- 版本快取 ----------

    [Fact]
    public async Task StampService_ShouldCacheWithinMaxAge_AndRotateShouldInvalidateImmediately()
    {
        var id = AddUser("alice");
        var service = StampService();
        var first = await service.GetStateAsync(id, TimeSpan.FromMinutes(5));
        Assert.True(first.IsActive);

        await using (var context = factory.CreateDbContext())
        {
            await context.MyUser.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.SecurityStamp, "changed-elsewhere"));
        }

        Assert.Equal(first.SecurityStamp, (await service.GetStateAsync(id, TimeSpan.FromMinutes(5))).SecurityStamp);
        Assert.Equal("changed-elsewhere", (await service.GetStateAsync(id, TimeSpan.Zero)).SecurityStamp);

        // 快取過了存活時間就重查（別的行程換掉的版本最晚在這時看到）。
        await using (var context = factory.CreateDbContext())
        {
            await context.MyUser.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.SecurityStamp, "changed-again"));
        }

        Assert.Equal("changed-elsewhere", (await service.GetStateAsync(id, TimeSpan.FromMinutes(5))).SecurityStamp);
        time.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal("changed-again", (await service.GetStateAsync(id, TimeSpan.FromMinutes(5))).SecurityStamp);

        await service.RotateAsync(id);
        var rotated = await service.GetStateAsync(id, TimeSpan.FromMinutes(5));
        Assert.NotEqual("changed-again", rotated.SecurityStamp);
        Assert.False((await service.GetStateAsync(424242, TimeSpan.Zero)).IsActive);
    }

    /// <summary>
    /// ⭐ 實機發現的錯誤：版本在別處換過（停用又啟用、改密碼）之後立刻重新登入，快取還是舊版本，
    /// 新登入的 Cookie 被誤判失效、被踢回登入頁長達 5 分鐘。快取不符時必須再查一次資料庫。
    /// </summary>
    [Fact]
    public async Task IsValid_ShouldRecheckTheDatabaseBeforeRejectingAStaleCache()
    {
        var id = AddUser("alice");
        var service = StampService();
        var old = (await service.GetStateAsync(id, TimeSpan.FromMinutes(5))).SecurityStamp;
        await using (var context = factory.CreateDbContext())
        {
            await context.MyUser.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.SecurityStamp, "fresh-login-stamp"));
        }

        Assert.True(await service.IsValidAsync(id, "fresh-login-stamp", TimeSpan.FromMinutes(5)));
        Assert.False(await service.IsValidAsync(id, old, TimeSpan.FromMinutes(5)));
        Assert.False(await service.IsValidAsync(id, string.Empty, TimeSpan.FromMinutes(5)));
        Assert.False(await service.IsValidAsync(424242, "x", TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task StampService_EnsureShouldOnlyFillEmptyStamps()
    {
        var id = AddUser("alice");
        var service = StampService();
        var current = await StampOfAsync(id);
        Assert.Equal(current, await service.EnsureAsync(id));

        await using (var context = factory.CreateDbContext())
        {
            await context.MyUser.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.SecurityStamp, string.Empty));
        }

        var filled = await service.EnsureAsync(id);
        Assert.False(string.IsNullOrEmpty(filled));
        Assert.Equal(filled, await StampOfAsync(id));
    }

    [Theory]
    [InlineData("a", "a", true)]
    [InlineData("a", "b", false)]
    [InlineData("", "", false)]
    [InlineData(null, "a", false)]
    [InlineData("a", "", false)]
    public void Matches_ShouldTreatEmptyAsNotSet(string? claim, string current, bool expected)
        => Assert.Equal(expected, SecurityStamps.Matches(claim, current));

    // ---------- ticket ----------

    [Fact]
    public void Ticket_ShouldWorkOnce_AndRejectTamperingAndExpiry()
    {
        var tickets = Tickets();
        var issued = tickets.Issue(7, "old", "new");

        var consumed = tickets.Consume(issued);
        Assert.NotNull(consumed);
        Assert.Equal((7, "old", "new"), (consumed.UserId, consumed.OldStamp, consumed.NewStamp));
        Assert.Null(tickets.Consume(issued));
        Assert.Null(tickets.Consume(issued[..^2] + "AA"));
        Assert.Null(tickets.Consume(null));

        var past = new ManualTimeProvider(DateTimeOffset.UtcNow.AddMinutes(-10));
        var expired = Tickets(past).Issue(7, "old", "new");
        Assert.Null(Tickets().Consume(expired));
    }

    // ---------- 共用 ----------

    private SessionRefreshTicketService Tickets(TimeProvider? timeProvider = null)
        => new(DataProtectionProvider.Create("SessionTests"), timeProvider ?? TimeProvider.System, NullLogger<SessionRefreshTicketService>.Instance);

    private SecurityStampService StampService() => new(factory, time, NullLogger<SecurityStampService>.Instance);

    private MyUserService UserService()
    {
        var rbacContext = factory.CreateDbContext();
        disposables.Add(rbacContext);
        return new MyUserService(factory, mapper, NullLogger<MyUserService>.Instance,
            new RbacWriteService(rbacContext, NullLogger<RbacWriteService>.Instance), audit, currentUser,
            Options.Create(new BootstrapSettings()), PasswordTestDefaults.Policy());
    }

    private MyUserServiceLogin Login()
    {
        var context = factory.CreateDbContext();
        disposables.Add(context);
        return new MyUserServiceLogin(context, mapper, new ConfigurationBuilder().Build(), NullLogger<MyUserServiceLogin>.Instance,
            new RolePermissionService(), audit, PasswordTestDefaults.Lockout(), new RecordingNotificationSender(), TimeProvider.System,
            TwoFactorTestDefaults.Service(factory, audit));
    }

    private async Task SeedSupportAsync(string password)
    {
        await using var context = factory.CreateDbContext();
        await new SupportUserSeeder(context, Options.Create(new BootstrapSettings { SupportPassword = password }), NullLogger<SupportUserSeeder>.Instance)
            .SeedAsync(CancellationToken.None);
    }

    private int AddUser(string account, string password = "Passw0rd-1", string? legacyPassword = null)
    {
        using var context = factory.CreateDbContext();
        var user = new MyUser { Account = account, Name = account, Status = true, Salt = "salt" };
        user.Password = legacyPassword is null ? SecurePasswordHasher.HashPassword(password) : PasswordHelper.GetPasswordSHA("salt", legacyPassword);
        context.MyUser.Add(user);
        context.SaveChanges();
        return user.Id;
    }

    private int AddRole(string name)
    {
        using var context = factory.CreateDbContext();
        var role = new RoleView { Name = name, TabViewJson = "[]" };
        context.RoleView.Add(role);
        context.SaveChanges();
        return role.Id;
    }

    private async Task<string> StampOfAsync(int id)
    {
        await using var context = factory.CreateDbContext();
        return await context.MyUser.IgnoreQueryFilters().Where(x => x.Id == id).Select(x => x.SecurityStamp).SingleAsync();
    }

    private async Task<MyUser> LoadAsync(int id)
    {
        await using var context = factory.CreateDbContext();
        return await context.MyUser.AsNoTracking().SingleAsync(x => x.Id == id);
    }

    private async Task<int> IdOfAsync(string account)
    {
        await using var context = factory.CreateDbContext();
        return await context.MyUser.Where(x => x.Account == account).Select(x => x.Id).SingleAsync();
    }

    public void Dispose()
    {
        foreach (var item in disposables)
        {
            item.Dispose();
        }

        connection.Dispose();
    }
}

/// <summary>工作階段失效的端到端行為（真正的主機）：Cookie 驗證器、refresh、只收 access token、換發 Cookie 頁。</summary>
[Collection(nameof(IntegrationHostCollection))]
public sealed class SessionIntegrationTests : IClassFixture<ApiTestApplicationFactory>
{
    private readonly ApiTestApplicationFactory factory;

    public SessionIntegrationTests(ApiTestApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task Cookie_ShouldBeRejectedAfterRotation_AndWithoutAStamp()
    {
        var user = await AddUserAsync(isAdmin: true);
        var cookie = Cookie(user);
        var url = "/api/backups/backup-20261004-000000.zip/download";
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(url, cookie)).StatusCode);

        await factory.Services.GetRequiredService<ISecurityStampService>().RotateAsync(user.Id);
        AssertChallenged(await GetAsync(url, cookie));

        var withoutStamp = await AddUserAsync(isAdmin: true);
        withoutStamp.SecurityStamp = string.Empty;
        AssertChallenged(await GetAsync(url, Cookie(withoutStamp)));

        // 版本沒換、但帳號被直接停用：Cookie 一樣被拒。
        var disabled = await AddUserAsync(isAdmin: true);
        var disabledCookie = Cookie(disabled);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(url, disabledCookie)).StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<BackendDBContext>().MyUser.Where(x => x.Id == disabled.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, false));
        }

        factory.Services.GetRequiredService<ISecurityStampService>().Invalidate(disabled.Id);
        AssertChallenged(await GetAsync(url, disabledCookie));
    }

    [Fact]
    public async Task Refresh_ShouldFailAfterRotation_AndRefreshTokensShouldNotWorkAsBearer()
    {
        var user = await AddUserAsync(isAdmin: false, password: "Passw0rd-1");
        using var client = factory.CreateClient();
        var login = await (await client.PostAsJsonAsync("/api/Auth/login", new LoginRequestDto { Account = user.Account, Password = "Passw0rd-1" }))
            .Content.ReadFromJsonAsync<ApiResult<TokenResponseDto>>();
        var tokens = login!.Data!;

        using (var bearer = new HttpRequestMessage(HttpMethod.Get, "/api/v1/Auth/me"))
        {
            bearer.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.RefreshToken);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(bearer)).StatusCode);
        }

        using (var bearer = new HttpRequestMessage(HttpMethod.Get, "/api/v1/Auth/me"))
        {
            bearer.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(bearer)).StatusCode);
        }

        var refreshed = await client.PostAsJsonAsync("/api/Auth/refresh", new RefreshTokenRequestDto { RefreshToken = tokens.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);

        await factory.Services.GetRequiredService<ISecurityStampService>().RotateAsync(user.Id);
        var rejected = await client.PostAsJsonAsync("/api/Auth/refresh", new RefreshTokenRequestDto { RefreshToken = tokens.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        Assert.True(await db.AuditLog.AnyAsync(x => x.Action == AuditActions.Token.RefreshFailed && x.TargetId == user.Id.ToString() && x.Detail == "reason=SessionRevoked"));
    }

    [Fact]
    public async Task RefreshSession_ShouldReissueOnlyForTheOriginalCookieAndANewStamp()
    {
        var user = await AddUserAsync(isAdmin: false);
        var oldCookie = Cookie(user);
        var oldStamp = user.SecurityStamp;
        await SetStampAsync(user.Id, "rotated-stamp");
        var tickets = factory.Services.GetRequiredService<SessionRefreshTicketService>();

        // 沒有原本的 Cookie：不換發。
        var noCookie = await GetAsync(RefreshUrl(tickets.Issue(user.Id, oldStamp, "rotated-stamp")), cookie: null);
        Assert.Contains("/Auths/Logout", noCookie.Headers.Location?.ToString(), StringComparison.Ordinal);
        Assert.False(noCookie.Headers.Contains("Set-Cookie") && noCookie.Headers.GetValues("Set-Cookie").Any(x => x.StartsWith(".CustomerHelpAgent.Auth=", StringComparison.Ordinal) && !x.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase)));

        // 別人的 Cookie：不換發（防登入 CSRF）。
        var other = await AddUserAsync(isAdmin: false);
        var foreign = await GetAsync(RefreshUrl(tickets.Issue(user.Id, oldStamp, "rotated-stamp")), Cookie(other));
        Assert.Contains("/Auths/Logout", foreign.Headers.Location?.ToString(), StringComparison.Ordinal);

        // ticket 的新版本不是資料庫目前的版本：不換發。
        var stale = await GetAsync(RefreshUrl(tickets.Issue(user.Id, oldStamp, "something-else")), oldCookie);
        Assert.Contains("/Auths/Logout", stale.Headers.Location?.ToString(), StringComparison.Ordinal);

        // 正確：換發新 Cookie 並導回原頁；同一張 ticket 第二次失效。
        var ticket = tickets.Issue(user.Id, oldStamp, "rotated-stamp");
        var ok = await GetAsync(RefreshUrl(ticket), oldCookie);
        Assert.Equal("/App", new Uri(new Uri("http://localhost"), ok.Headers.Location!).AbsolutePath);
        Assert.Contains(ok.Headers.GetValues("Set-Cookie"), x => x.StartsWith(".CustomerHelpAgent.Auth=", StringComparison.Ordinal));
        var replay = await GetAsync(RefreshUrl(ticket), oldCookie);
        Assert.Contains("/Auths/Logout", replay.Headers.Location?.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Cookie 被驗證器拒絕 → 當成未登入 → 挑戰（導向登入頁或 401）。刻意不只看「不是 404」：
    /// 備份端點自己也會擋停用的帳號（403），那樣分不出是不是驗證器擋下的。
    /// </summary>
    private static void AssertChallenged(HttpResponseMessage response)
        => Assert.True(
            response.StatusCode == HttpStatusCode.Unauthorized
            || (response.StatusCode == HttpStatusCode.Redirect && response.Headers.Location?.ToString().Contains("/Auths/Login", StringComparison.Ordinal) == true),
            $"預期被當成未登入，實際 {(int)response.StatusCode} {response.Headers.Location}");

    private static string RefreshUrl(string ticket) => $"/Auths/RefreshSession?ticket={Uri.EscapeDataString(ticket)}&returnUrl=%2FApp";

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

    private async Task<MyUser> AddUserAsync(bool isAdmin, string password = "Passw0rd-1")
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        var account = $"session-{Guid.NewGuid():N}";
        var user = new MyUser { Account = account, Name = account, Password = SecurePasswordHasher.HashPassword(password), Status = true, IsAdmin = isAdmin };
        db.MyUser.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private async Task SetStampAsync(int userId, string stamp)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        await db.MyUser.Where(x => x.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(x => x.SecurityStamp, stamp));
        factory.Services.GetRequiredService<ISecurityStampService>().Invalidate(userId);
    }
}

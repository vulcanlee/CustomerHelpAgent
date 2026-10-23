using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using AutoMapper;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
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
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Business.Startup;
using CustomerHelpAgent.Dtos.Auths;
using CustomerHelpAgent.Dtos.Commons;
using CustomerHelpAgent.Models.Others;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Web.Auth;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 兩步驟驗證（0.9.104 起）：啟用、防重放、備用碼、登入兩段（不提前歸零、不能靠重輸密碼繞過鎖定）、強制與豁免、停用、管理員重設、加密、登入用 Cookie。
/// </summary>
public sealed class TwoFactorTests : IDisposable
{
    private const string Password = "Passw0rd-1";
    private const int BackupCodeCount = 10;

    private readonly SqliteConnection connection;
    private readonly TestDbContextFactory factory;
    private readonly ManualTimeProvider time = new(new DateTimeOffset(2026, 10, 4, 1, 0, 0, TimeSpan.Zero));
    private readonly IMapper mapper;
    private readonly RecordingAuditLogService audit = new();
    private readonly TwoFactorSettings settings = new();
    private readonly TotpService totp = new();
    private readonly List<IDisposable> disposables = [];

    public TwoFactorTests()
    {
        connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        connection.Open();
        factory = new TestDbContextFactory(connection);
        using var context = factory.CreateDbContext();
        context.Database.EnsureCreated();
        mapper = new MapperConfiguration(c => c.AddProfile<AutoMapping>(), LoggerFactory.Create(_ => { })).CreateMapper();
    }

    // ---------- 啟用 ----------

    [Fact]
    public async Task Enable_WithAWrongCode_ShouldStoreNothing()
    {
        var id = AddUser("alice");
        var service = Service();
        var enrollment = service.BeginEnrollment("alice");

        var result = await service.EnableAsync(id, enrollment.Secret, "000000" == Code(enrollment.Secret) ? "111111" : "000000");

        Assert.False(result.Success);
        var user = await LoadAsync(id);
        Assert.False(user.TwoFactorEnabled);
        Assert.Null(user.TwoFactorSecret);
        Assert.Equal(0, await service.CountUnusedBackupCodesAsync(id));
    }

    [Fact]
    public async Task Enable_ShouldEncryptTheSecret_RotateTheStamp_AndIssueTenBackupCodes()
    {
        var id = AddUser("alice");
        var stampBefore = (await LoadAsync(id)).SecurityStamp;
        var provider = new EphemeralDataProtectionProvider();
        var service = Service(provider);
        var enrollment = service.BeginEnrollment("alice");
        Assert.Contains("secret=" + enrollment.Secret, enrollment.ProvisioningUri, StringComparison.Ordinal);

        var result = await service.EnableAsync(id, enrollment.Secret, Code(enrollment.Secret));

        Assert.True(result.Success);
        Assert.Equal(BackupCodeCount, result.BackupCodes.Count);
        Assert.Equal(result.BackupCodes.Count, result.BackupCodes.Distinct().Count());
        var user = await LoadAsync(id);
        Assert.True(user.TwoFactorEnabled);
        Assert.NotEqual(enrollment.Secret, user.TwoFactorSecret);
        Assert.DoesNotContain(enrollment.Secret, user.TwoFactorSecret!, StringComparison.Ordinal);
        Assert.Equal(enrollment.Secret, TwoFactorTestDefaults.Protector(provider).Unprotect(user.TwoFactorSecret));
        Assert.NotEqual(stampBefore, user.SecurityStamp);
        Assert.Contains(audit.Entries, x => x.Action == AuditActions.User.TwoFactorEnable && x.ActorUserId == id);

        // 資料庫只存雜湊。
        await using var context = factory.CreateDbContext();
        var hashes = await context.TwoFactorBackupCode.Where(x => x.MyUserId == id).Select(x => x.CodeHash).ToListAsync();
        Assert.DoesNotContain(hashes, h => result.BackupCodes.Contains(h));
    }

    [Fact]
    public void Secret_EncryptedWithAnotherKeyRing_ShouldNotDecrypt()
    {
        var protectedSecret = TwoFactorTestDefaults.Protector(new EphemeralDataProtectionProvider()).Protect("JBSWY3DPEHPK3PXP");

        Assert.Null(TwoFactorTestDefaults.Protector(new EphemeralDataProtectionProvider()).Unprotect(protectedSecret));
        Assert.Null(TwoFactorTestDefaults.Protector().Unprotect(null));
    }

    // ---------- 驗證：防重放 ----------

    [Fact]
    public async Task Verify_TheSameCodeTwice_ShouldOnlySucceedOnce()
    {
        var (id, secret, _) = await EnrolAsync("alice");
        var service = Service();

        // 啟用時用過的那一組，在同一個時間步內不能再拿來登入。
        Assert.Equal(SecondFactorMethod.None, await service.VerifyAsync(id, Code(secret)));

        time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(SecondFactorMethod.Totp, await service.VerifyAsync(id, Code(secret)));
        Assert.Equal(SecondFactorMethod.None, await service.VerifyAsync(id, Code(secret)));

        time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(SecondFactorMethod.Totp, await service.VerifyAsync(id, " " + Code(secret) + " "));
    }

    [Fact]
    public async Task Verify_WhenAnotherRequestAlreadyUsedTheStep_ShouldFail()
    {
        var (id, secret, _) = await EnrolAsync("alice");
        time.Advance(TimeSpan.FromSeconds(30));
        var step = time.GetUtcNow().ToUnixTimeSeconds() / 30;

        // 模擬並行：另一個請求剛用同一組碼把時間步寫進去。
        await using (var context = factory.CreateDbContext())
        {
            await context.MyUser.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.TwoFactorLastStep, step));
        }

        Assert.Equal(SecondFactorMethod.None, await Service().VerifyAsync(id, Code(secret)));
    }

    [Fact]
    public async Task Verify_ACodeFromAnotherSecretOrEmpty_ShouldFail()
    {
        var (id, _, _) = await EnrolAsync("alice");
        time.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(SecondFactorMethod.None, await Service().VerifyAsync(id, Code(totp.GenerateSecret())));
        Assert.Equal(SecondFactorMethod.None, await Service().VerifyAsync(id, ""));
        Assert.Equal(SecondFactorMethod.None, await Service().VerifyAsync(id, null));
    }

    // ---------- 備用碼 ----------

    [Fact]
    public async Task BackupCode_ShouldWorkOnce_IgnoringCaseDashesAndSpaces()
    {
        var (id, _, codes) = await EnrolAsync("alice");
        var service = Service();
        var messy = " " + codes[0].Replace("-", "", StringComparison.Ordinal).ToLowerInvariant().Insert(3, " ") + " ";

        Assert.Equal(SecondFactorMethod.BackupCode, await service.VerifyAsync(id, messy));
        Assert.Equal(SecondFactorMethod.None, await service.VerifyAsync(id, codes[0]));
        Assert.Equal(BackupCodeCount - 1, await service.CountUnusedBackupCodesAsync(id));
    }

    [Fact]
    public async Task BackupCode_OfAnotherUser_ShouldNotWork()
    {
        var (_, _, aliceCodes) = await EnrolAsync("alice");
        var (bob, _, _) = await EnrolAsync("bob");

        Assert.Equal(SecondFactorMethod.None, await Service().VerifyAsync(bob, aliceCodes[0]));
    }

    [Fact]
    public async Task Regenerate_ShouldRequireACode_AndInvalidateTheOldCodes()
    {
        var (id, _, codes) = await EnrolAsync("alice");
        var service = Service();

        Assert.False((await service.RegenerateBackupCodesAsync(id, "bad")).Success);
        var result = await service.RegenerateBackupCodesAsync(id, codes[0]);

        Assert.True(result.Success);
        Assert.Equal(BackupCodeCount, await service.CountUnusedBackupCodesAsync(id));
        Assert.Equal(SecondFactorMethod.None, await service.VerifyAsync(id, codes[1]));
        Assert.Equal(SecondFactorMethod.BackupCode, await service.VerifyAsync(id, result.BackupCodes[0]));
        Assert.Contains(audit.Entries, x => x.Action == AuditActions.User.TwoFactorBackupCodesRegenerate);
    }

    // ---------- 登入兩段 ----------

    [Fact]
    public async Task PasswordStep_ShouldNotSignIn_ResetTheCounter_OrWriteSuccess()
    {
        var (id, _, _) = await EnrolAsync("alice");
        await SetFailedCountAsync(id, 2);

        var attempt = await Login().LoginAsync("alice", Password);

        Assert.True(attempt.RequiresTwoFactor);
        Assert.Equal(id, attempt.User?.Id);
        var (_, user) = attempt;
        Assert.Null(user);
        Assert.Equal(2, (await LoadAsync(id)).AccessFailedCount);
        Assert.DoesNotContain(audit.Entries, x => x.Action == AuditActions.Login.Success);
    }

    [Fact]
    public async Task UserWithoutTwoFactor_ShouldSignInWithThePasswordAlone()
    {
        var id = AddUser("plain");

        var attempt = await Login().LoginAsync("plain", Password);

        Assert.False(attempt.RequiresTwoFactor);
        Assert.Equal(id, attempt.User?.Id);
        Assert.Null((await Login().CompleteSecondFactorAsync(id, "123456")).User);
    }

    [Fact]
    public async Task SecondStep_Success_ShouldResetTheCounter_AndAuditTheMethod()
    {
        var (id, secret, codes) = await EnrolAsync("alice");
        await SetFailedCountAsync(id, 2);
        time.Advance(TimeSpan.FromSeconds(30));

        var result = await Login().CompleteSecondFactorAsync(id, Code(secret));

        Assert.Equal(id, result.User?.Id);
        Assert.Equal(0, (await LoadAsync(id)).AccessFailedCount);
        Assert.Contains(audit.Entries, x => x.Action == AuditActions.Login.Success && x.Detail == "mfa=totp");

        Assert.NotNull((await Login().CompleteSecondFactorAsync(id, codes[0])).User);
        Assert.Contains(audit.Entries, x => x.Action == AuditActions.Login.Success && x.Detail == "mfa=backup");

        Assert.NotNull((await Login().CompleteSecondFactorAsync(id, null, rememberedDevice: true, provider: "Google")).User);
        Assert.Contains(audit.Entries, x => x.Action == AuditActions.Login.SsoSuccess && x.Detail == "provider=Google; mfa=device");
    }

    /// <summary>⭐ 知道密碼的人不能靠「每猜幾次驗證碼就重輸一次密碼」把失敗次數洗掉。</summary>
    [Fact]
    public async Task WrongCodes_ShouldCountTowardsTheLockout_EvenWhenThePasswordIsReEntered()
    {
        var (id, secret, _) = await EnrolAsync("alice");
        time.Advance(TimeSpan.FromSeconds(30));

        for (var round = 0; round < 2; round++)
        {
            Assert.True((await Login(maxFailedAttempts: 3).LoginAsync("alice", Password)).RequiresTwoFactor);
            var wrong = await Login(maxFailedAttempts: 3).CompleteSecondFactorAsync(id, "000000" == Code(secret) ? "111111" : "000000");
            Assert.Null(wrong.User);
            Assert.Equal(MyUserServiceLogin.InvalidSecondFactorMessage, wrong.Message);
        }

        Assert.Equal(2, (await LoadAsync(id)).AccessFailedCount);
        Assert.Contains(audit.Entries, x => x.Action == AuditActions.Login.TwoFactorFailed && x.ActorUserId == id);

        Assert.True((await Login(maxFailedAttempts: 3).LoginAsync("alice", Password)).RequiresTwoFactor);
        Assert.Null((await Login(maxFailedAttempts: 3).CompleteSecondFactorAsync(id, "bad-code")).User);
        Assert.NotNull((await LoadAsync(id)).LockoutEndUtc);
        Assert.Contains(audit.Entries, x => x.Action == AuditActions.Login.LockedOut);

        // 鎖定中：正確的驗證碼與密碼都不放行。
        Assert.Null((await Login(maxFailedAttempts: 3).CompleteSecondFactorAsync(id, Code(secret))).User);
        var relogin = await Login(maxFailedAttempts: 3).LoginAsync("alice", Password);
        Assert.False(relogin.RequiresTwoFactor);
        Assert.Null(relogin.User);
    }

    [Fact]
    public async Task SecondStep_ForADisabledUser_ShouldBeRefused()
    {
        var (id, secret, _) = await EnrolAsync("alice");
        time.Advance(TimeSpan.FromSeconds(30));
        await using (var context = factory.CreateDbContext())
        {
            await context.MyUser.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, false));
        }

        Assert.Null((await Login().CompleteSecondFactorAsync(id, Code(secret))).User);
    }

    // ---------- 強制與豁免 ----------

    [Fact]
    public async Task IsRequired_ShouldFollowRolesAndTheAdminSetting_WithExemptions()
    {
        var service = Service();
        var plain = AddUser("plain");
        Assert.False(await service.IsRequiredAsync(plain));

        var strict = AddRole("strict", requireTwoFactor: true);
        var primary = AddUser("primary", roleId: strict);
        Assert.True(await service.IsRequiredAsync(primary));

        var extra = AddUser("extra");
        AddUserRole(extra, strict);
        Assert.True(await service.IsRequiredAsync(extra));

        var admin = AddUser("boss", isAdmin: true);
        Assert.False(await service.IsRequiredAsync(admin));
        settings.RequireForAdmins = true;
        Assert.True(await service.IsRequiredAsync(admin));

        // support 與沒有本機密碼（Google 建立）的帳號豁免。
        var support = AddUser("support", isAdmin: true, roleId: strict);
        Assert.False(await service.IsRequiredAsync(support));
        var google = AddUser("google", roleId: strict, password: null);
        Assert.False(await service.IsRequiredAsync(google));
    }

    [Fact]
    public async Task IsRequired_ShouldIgnoreDeletedRoles()
    {
        var strict = AddRole("strict", requireTwoFactor: true);
        var id = AddUser("primary", roleId: strict);
        await using (var context = factory.CreateDbContext())
        {
            await context.RoleView.Where(x => x.Id == strict).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsDeleted, true));
        }

        Assert.False(await Service().IsRequiredAsync(id));
    }

    // ---------- 停用與重設 ----------

    [Fact]
    public async Task Disable_ShouldRequireACode_BeRefusedWhenRequired_AndClearEverything()
    {
        var (id, _, codes) = await EnrolAsync("alice");
        var service = Service();
        var stampBefore = (await LoadAsync(id)).SecurityStamp;

        Assert.False((await service.DisableAsync(id, "bad")).Success);

        var strict = AddRole("strict", requireTwoFactor: true);
        AddUserRole(id, strict);
        var refused = await service.DisableAsync(id, codes[0]);
        Assert.False(refused.Success);
        Assert.True((await LoadAsync(id)).TwoFactorEnabled);
        await using (var context = factory.CreateDbContext())
        {
            await context.UserRole.Where(x => x.MyUserId == id).ExecuteDeleteAsync();
        }

        Assert.True((await service.DisableAsync(id, codes[1])).Success);
        var user = await LoadAsync(id);
        Assert.False(user.TwoFactorEnabled);
        Assert.Null(user.TwoFactorSecret);
        Assert.Null(user.TwoFactorLastStep);
        Assert.NotEqual(stampBefore, user.SecurityStamp);
        Assert.Equal(0, await service.CountUnusedBackupCodesAsync(id));
        Assert.Contains(audit.Entries, x => x.Action == AuditActions.User.TwoFactorDisable);
        Assert.False((await Login().LoginAsync("alice", Password)).RequiresTwoFactor);
    }

    [Fact]
    public async Task AdminReset_ShouldClearEverything_RotateTheStamp_AndAudit()
    {
        var (id, _, _) = await EnrolAsync("alice");
        var stampBefore = (await LoadAsync(id)).SecurityStamp;
        var admin = new CurrentUserService { CurrentUser = new CurrentUser { Id = 777, Account = "admin" } };

        Assert.True((await Service(currentUser: admin).ResetAsync(id)).Success);

        var user = await LoadAsync(id);
        Assert.False(user.TwoFactorEnabled);
        Assert.Null(user.TwoFactorSecret);
        Assert.NotEqual(stampBefore, user.SecurityStamp);
        Assert.Equal(0, await Service().CountUnusedBackupCodesAsync(id));
        Assert.Contains(audit.Entries, x => x.Action == AuditActions.User.TwoFactorReset && x.ActorUserId == 777 && x.Detail == "account=alice");
        Assert.False((await Service().ResetAsync(999_999)).Success);
    }

    // ---------- 登入用 Cookie ----------

    [Fact]
    public void PendingCookie_ShouldRoundTrip_AndExpireAfterFiveMinutes()
    {
        var provider = new EphemeralDataProtectionProvider();
        var pending = new PendingTwoFactorLogin(7, "stamp-1", true, "/projects", "Google");

        var written = new DefaultHttpContext();
        Cookies(provider, TimeProvider.System).SetPending(written, pending);
        Assert.Equal(pending, Cookies(provider, TimeProvider.System).ReadPending(Replay(written)));

        // 另一組金鑰（或被竄改）讀不到。
        Assert.Null(Cookies(new EphemeralDataProtectionProvider(), TimeProvider.System).ReadPending(Replay(written)));

        var old = new DefaultHttpContext();
        Cookies(provider, new ManualTimeProvider(DateTimeOffset.UtcNow.AddMinutes(-6))).SetPending(old, pending);
        Assert.Null(Cookies(provider, TimeProvider.System).ReadPending(Replay(old)));
    }

    [Fact]
    public void RememberedDevice_ShouldMatchTheUserAndTheCurrentStamp()
    {
        var provider = new EphemeralDataProtectionProvider();
        var written = new DefaultHttpContext();
        Cookies(provider, TimeProvider.System).RememberDevice(written, 7, "stamp-1");
        var request = Replay(written);

        Assert.True(Cookies(provider, TimeProvider.System).IsDeviceRemembered(request, 7, "stamp-1"));
        Assert.False(Cookies(provider, TimeProvider.System).IsDeviceRemembered(request, 7, "stamp-2"));
        Assert.False(Cookies(provider, TimeProvider.System).IsDeviceRemembered(request, 8, "stamp-1"));

        settings.RememberDeviceDays = 0;
        Assert.False(Cookies(provider, TimeProvider.System).IsDeviceRemembered(request, 7, "stamp-1"));
        var none = new DefaultHttpContext();
        Cookies(provider, TimeProvider.System).RememberDevice(none, 7, "stamp-1");
        Assert.False(none.Response.Headers.SetCookie.Any());
    }

    // ---------- helpers ----------

    private TwoFactorService Service(IDataProtectionProvider? provider = null, CurrentUserService? currentUser = null)
        => TwoFactorTestDefaults.Service(factory, audit, time, settings, currentUser: currentUser, dataProtectionProvider: provider ?? sharedProvider);

    private readonly EphemeralDataProtectionProvider sharedProvider = new();

    private MyUserServiceLogin Login(int maxFailedAttempts = 5)
    {
        var context = factory.CreateDbContext();
        disposables.Add(context);
        return new MyUserServiceLogin(context, mapper, new ConfigurationBuilder().Build(), NullLogger<MyUserServiceLogin>.Instance,
            new RolePermissionService(), audit, PasswordTestDefaults.Lockout(maxFailedAttempts), new RecordingNotificationSender(), time, Service());
    }

    private TwoFactorLoginCookies Cookies(IDataProtectionProvider provider, TimeProvider timeProvider)
        => new(provider, new StaticOptionsMonitor<TwoFactorSettings>(settings), timeProvider, NullLogger<TwoFactorLoginCookies>.Instance);

    /// <summary>把回應寫出的 Set-Cookie 當成下一個請求帶回來的 Cookie。</summary>
    private static DefaultHttpContext Replay(HttpContext written)
    {
        var next = new DefaultHttpContext();
        next.Request.Headers.Cookie = string.Join("; ", written.Response.Headers.SetCookie.Select(x => x!.Split(';')[0]));
        return next;
    }

    private string Code(string secret) => totp.ComputeCode(secret, time.GetUtcNow().ToUnixTimeSeconds());

    private async Task<(int Id, string Secret, IReadOnlyList<string> Codes)> EnrolAsync(string account)
    {
        var id = AddUser(account);
        var service = Service();
        var enrollment = service.BeginEnrollment(account);
        var result = await service.EnableAsync(id, enrollment.Secret, Code(enrollment.Secret));
        Assert.True(result.Success);
        return (id, enrollment.Secret, result.BackupCodes);
    }

    private int AddUser(string account, bool isAdmin = false, int? roleId = null, string? password = Password)
    {
        using var context = factory.CreateDbContext();
        var user = new MyUser
        {
            Account = account,
            Name = account,
            Status = true,
            IsAdmin = isAdmin,
            RoleViewId = roleId,
            Salt = "salt",
            Password = password is null ? string.Empty : SecurePasswordHasher.HashPassword(password),
        };
        context.MyUser.Add(user);
        context.SaveChanges();
        return user.Id;
    }

    private int AddRole(string name, bool requireTwoFactor)
    {
        using var context = factory.CreateDbContext();
        var role = new RoleView { Name = name, TabViewJson = "[]", RequireTwoFactor = requireTwoFactor };
        context.RoleView.Add(role);
        context.SaveChanges();
        return role.Id;
    }

    private void AddUserRole(int userId, int roleId)
    {
        using var context = factory.CreateDbContext();
        context.UserRole.Add(new UserRole { MyUserId = userId, RoleViewId = roleId });
        context.SaveChanges();
    }

    private async Task SetFailedCountAsync(int id, int count)
    {
        await using var context = factory.CreateDbContext();
        await context.MyUser.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.AccessFailedCount, count));
    }

    private async Task<MyUser> LoadAsync(int id)
    {
        await using var context = factory.CreateDbContext();
        return await context.MyUser.AsNoTracking().SingleAsync(x => x.Id == id);
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

/// <summary>Google 設定為已啟用的測試主機：外部登入的暫存 Cookie 方案才會註冊（Program.cs 啟動時就讀這段設定，所以用環境變數）。</summary>
public sealed class ApiTestApplicationFactoryWithGoogle : ApiTestApplicationFactory
{
    private static readonly Dictionary<string, string> GoogleSettings = new()
    {
        ["GoogleOAuthSettings__Enabled"] = "true",
        ["GoogleOAuthSettings__ClientId"] = "integration-client-id",
        ["GoogleOAuthSettings__ClientSecret"] = "integration-client-secret",
    };

    public ApiTestApplicationFactoryWithGoogle()
    {
        foreach (var item in GoogleSettings)
        {
            Environment.SetEnvironmentVariable(item.Key, item.Value);
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        foreach (var item in GoogleSettings)
        {
            Environment.SetEnvironmentVariable(item.Key, null);
        }
    }
}

/// <summary>兩步驟驗證的端到端行為（真正的主機）：Web API 登入、Google 登入。</summary>
[Collection(nameof(IntegrationHostCollection))]
public sealed class TwoFactorIntegrationTests : IClassFixture<ApiTestApplicationFactoryWithGoogle>
{
    private const string Password = "Passw0rd-1";
    private readonly ApiTestApplicationFactoryWithGoogle factory;

    public TwoFactorIntegrationTests(ApiTestApplicationFactoryWithGoogle factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task ApiLogin_ShouldRequireTheCode_CountWrongCodes_AndAcceptTheRightOne()
    {
        var (user, secret) = await AddEnrolledUserAsync();
        using var client = factory.CreateClient();

        var missing = await client.PostAsJsonAsync("/api/Auth/login", new LoginRequestDto { Account = user.Account, Password = Password });
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal("需要兩步驟驗證碼。", (await missing.Content.ReadFromJsonAsync<ApiResult<TokenResponseDto>>())!.ErrorMessage);
        Assert.Equal(0, await FailedCountAsync(user.Id));

        var wrong = await client.PostAsJsonAsync("/api/Auth/login", new LoginRequestDto { Account = user.Account, Password = Password, TwoFactorCode = "bad-code" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(1, await FailedCountAsync(user.Id));

        // 啟用時用過現在這一步，登入用下一步（允許前後各一步的誤差）。
        var code = new TotpService().ComputeCode(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 30);
        var ok = await client.PostAsJsonAsync("/api/Auth/login", new LoginRequestDto { Account = user.Account, Password = Password, TwoFactorCode = code });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.False(string.IsNullOrEmpty((await ok.Content.ReadFromJsonAsync<ApiResult<TokenResponseDto>>())!.Data!.AccessToken));
        Assert.Equal(0, await FailedCountAsync(user.Id));
    }

    [Fact]
    public async Task ApiLogin_WhenTwoFactorIsRequiredButNotSetUp_ShouldBeRefused()
    {
        int roleId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
            var role = new RoleView { Name = $"strict-{Guid.NewGuid():N}", TabViewJson = "[]", RequireTwoFactor = true };
            db.RoleView.Add(role);
            await db.SaveChangesAsync();
            roleId = role.Id;
        }

        var user = await AddUserAsync(roleId);
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/Auth/login", new LoginRequestDto { Account = user.Account, Password = Password });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("請先在網頁完成兩步驟驗證設定。", (await response.Content.ReadFromJsonAsync<ApiResult<TokenResponseDto>>())!.ErrorMessage);
    }

    /// <summary>⭐ Google 會連結到同 Email 的既有帳號；已啟用兩步驟驗證的帳號改用 Google 登入也要第二步，不能繞過。</summary>
    [Fact]
    public async Task GoogleLogin_ForAnAccountWithTwoFactor_ShouldGoToTheSecondStep()
    {
        var (enrolled, _) = await AddEnrolledUserAsync();
        var response = await GoogleCallbackAsync(enrolled.Email!);

        Assert.Equal("/Auths/TwoFactor", response.Headers.Location?.ToString());
        var cookies = response.Headers.GetValues("Set-Cookie").ToList();
        Assert.Contains(cookies, x => x.StartsWith(TwoFactorLoginCookies.PendingCookieName + "=", StringComparison.Ordinal));
        Assert.DoesNotContain(cookies, x => x.StartsWith(".CustomerHelpAgent.Auth=", StringComparison.Ordinal));

        // 沒有啟用的帳號照舊直接登入。
        var plain = await AddUserAsync(roleId: null);
        var direct = await GoogleCallbackAsync(plain.Email!);
        Assert.Equal("/App", direct.Headers.Location?.ToString());
        Assert.Contains(direct.Headers.GetValues("Set-Cookie"), x => x.StartsWith(".CustomerHelpAgent.Auth=", StringComparison.Ordinal));
    }

    private async Task<HttpResponseMessage> GoogleCallbackAsync(string email)
    {
        var options = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(MagicObjectHelper.ExternalCookieScheme);
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, $"google-{Guid.NewGuid():N}"), new Claim(ClaimTypes.Email, email), new Claim(ClaimTypes.Name, email)],
            "Google");
        var ticket = options.TicketDataFormat.Protect(new AuthenticationTicket(new ClaimsPrincipal(identity), MagicObjectHelper.ExternalCookieScheme));

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/Auths/Google/Callback");
        request.Headers.Add("Cookie", $"{options.Cookie.Name}={ticket}");
        return await client.SendAsync(request);
    }

    private async Task<(MyUser User, string Secret)> AddEnrolledUserAsync()
    {
        var user = await AddUserAsync(roleId: null);
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ITwoFactorService>();
        var enrollment = service.BeginEnrollment(user.Account);
        var code = new TotpService().ComputeCode(enrollment.Secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        Assert.True((await service.EnableAsync(user.Id, enrollment.Secret, code)).Success);
        return (user, enrollment.Secret);
    }

    private async Task<MyUser> AddUserAsync(int? roleId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        var account = $"tfa-{Guid.NewGuid():N}";
        var user = new MyUser
        {
            Account = account,
            Name = account,
            Email = $"{account}@example.com",
            Password = SecurePasswordHasher.HashPassword(Password),
            Status = true,
            RoleViewId = roleId,
        };
        db.MyUser.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private async Task<int> FailedCountAsync(int userId)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<BackendDBContext>().MyUser.Where(x => x.Id == userId).Select(x => x.AccessFailedCount).SingleAsync();
    }
}

using AutoMapper;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Helpers;
using System.Security.Claims;
using Microsoft.Extensions.Logging.Abstractions;

namespace CustomerHelpAgent.Tests;

public sealed class AuthenticationStateHelperTests
{
    /// <summary>
    /// ⚠️ 「未認證」必須導向**登入頁**，不可導向登出頁（0.9.39 起）。
    ///
    /// /Auths/Logout 會無條件 SignOutAsync 刪掉 Cookie，而這個分支同時涵蓋
    /// 「Cookie 一時讀不出來」（伺服器重啟、Data Protection 金鑰換掉、換連接埠）——
    /// 導到登出頁會把使用者原本還有效的「記住我」**永久毀掉**，而且症狀會自我延續。
    ///
    /// **看到這條測試紅了，請先確認不是把那個缺陷改回去了。**
    /// 其餘五個分支（Sid 無效／查無使用者／帳號停用／缺 RoleView／例外）
    /// 仍應導向登出頁，各自有對照測試釘住。
    /// </summary>
    [Fact]
    public async Task Check_WithUnauthenticatedPrincipal_ShouldNavigateLoginWithoutClearingCookie()
    {
        await using var fixture = await AuthenticationStateHelperFixture.CreateAsync();
        var authProvider = new TestAuthenticationStateProvider(new ClaimsPrincipal(new ClaimsIdentity()));
        var navigationManager = new TestNavigationManager("http://localhost/App");

        var result = await fixture.CreateHelper().Check(authProvider, navigationManager);

        Assert.Equal(AuthenticationCheckResult.Unauthenticated, result);
        Assert.Equal("/Auths/Login", navigationManager.NavigatedTo);
        Assert.NotEqual("/Auths/Logout", navigationManager.NavigatedTo);
    }

    [Fact]
    public async Task Check_WithInvalidSidClaim_ShouldReturnInvalidUserAndNavigateLogout()
    {
        await using var fixture = await AuthenticationStateHelperFixture.CreateAsync();
        var authProvider = new TestAuthenticationStateProvider(CreatePrincipal("not-a-number"));
        var navigationManager = new TestNavigationManager("http://localhost/App");

        var result = await fixture.CreateHelper().Check(authProvider, navigationManager);

        Assert.Equal(AuthenticationCheckResult.InvalidUser, result);
        Assert.Equal("/Auths/Logout", navigationManager.NavigatedTo);
    }

    [Fact]
    public async Task Check_WithMissingUser_ShouldReturnInvalidUserAndNavigateLogout()
    {
        await using var fixture = await AuthenticationStateHelperFixture.CreateAsync();
        var authProvider = new TestAuthenticationStateProvider(CreatePrincipal("999"));
        var navigationManager = new TestNavigationManager("http://localhost/App");

        var result = await fixture.CreateHelper().Check(authProvider, navigationManager);

        Assert.Equal(AuthenticationCheckResult.InvalidUser, result);
        Assert.Equal("/Auths/Logout", navigationManager.NavigatedTo);
    }

    [Fact]
    public async Task Check_WithDisabledUser_ShouldReturnInvalidUserAndNavigateLogout()
    {
        await using var fixture = await AuthenticationStateHelperFixture.CreateAsync();
        var user = await fixture.AddUserAsync(status: false);
        var authProvider = new TestAuthenticationStateProvider(CreatePrincipal(user));
        var navigationManager = new TestNavigationManager("http://localhost/App");

        var result = await fixture.CreateHelper().Check(authProvider, navigationManager);

        Assert.Equal(AuthenticationCheckResult.InvalidUser, result);
        Assert.Equal("/Auths/Logout", navigationManager.NavigatedTo);
    }

    [Fact]
    public async Task Check_WithMissingRoleView_ShouldReturnInvalidUserAndNavigateLogout()
    {
        await using var fixture = await AuthenticationStateHelperFixture.CreateAsync();
        var user = await fixture.AddUserWithoutRoleAsync();
        var authProvider = new TestAuthenticationStateProvider(CreatePrincipal(user));
        var navigationManager = new TestNavigationManager("http://localhost/App");

        var result = await fixture.CreateHelper().Check(authProvider, navigationManager);

        Assert.Equal(AuthenticationCheckResult.InvalidUser, result);
        Assert.Equal("/Auths/Logout", navigationManager.NavigatedTo);
    }

    [Fact]
    public async Task Check_WithInvalidRoleJson_ShouldReturnInvalidUserAndNavigateLogout()
    {
        await using var fixture = await AuthenticationStateHelperFixture.CreateAsync();
        var user = await fixture.AddUserAsync(roleJson: "not-json");
        var authProvider = new TestAuthenticationStateProvider(CreatePrincipal(user));
        var navigationManager = new TestNavigationManager("http://localhost/App");

        var result = await fixture.CreateHelper().Check(authProvider, navigationManager);

        Assert.Equal(AuthenticationCheckResult.InvalidUser, result);
        Assert.Equal("/Auths/Logout", navigationManager.NavigatedTo);
    }

    [Fact]
    public async Task Check_WhenPasswordChangeRequiredOutsideChangePasswordPage_ShouldReturnRequiresPasswordChange()
    {
        await using var fixture = await AuthenticationStateHelperFixture.CreateAsync();
        var user = await fixture.AddUserAsync(mustChangePassword: true);
        var authProvider = new TestAuthenticationStateProvider(CreatePrincipal(user));
        var navigationManager = new TestNavigationManager("http://localhost/App");

        var result = await fixture.CreateHelper().Check(authProvider, navigationManager);

        Assert.Equal(AuthenticationCheckResult.RequiresPasswordChange, result);
        Assert.Equal("/ChangePassword", navigationManager.NavigatedTo);
    }

    [Fact]
    public async Task Check_WhenPasswordChangeRequiredOnChangePasswordPageWithQuery_ShouldSucceed()
    {
        await using var fixture = await AuthenticationStateHelperFixture.CreateAsync();
        var user = await fixture.AddUserAsync(mustChangePassword: true);
        var authProvider = new TestAuthenticationStateProvider(CreatePrincipal(user));
        var navigationManager = new TestNavigationManager("http://localhost/ChangePassword?returnUrl=/App");

        var result = await fixture.CreateHelper().Check(authProvider, navigationManager);

        Assert.Equal(AuthenticationCheckResult.Succeeded, result);
        Assert.Null(navigationManager.NavigatedTo);
    }

    [Fact]
    public async Task Check_WithCachedUserStillRechecksPasswordChangeRequirement()
    {
        await using var fixture = await AuthenticationStateHelperFixture.CreateAsync();
        var user = await fixture.AddUserAsync(mustChangePassword: true);
        fixture.CurrentUserService.CurrentUser.IsAuthenticated = true;
        fixture.CurrentUserService.CurrentUser.Id = user.Id;
        var authProvider = new TestAuthenticationStateProvider(CreatePrincipal(user));
        var navigationManager = new TestNavigationManager("http://localhost/App");

        var result = await fixture.CreateHelper().Check(authProvider, navigationManager);

        Assert.Equal(AuthenticationCheckResult.RequiresPasswordChange, result);
        Assert.Equal("/ChangePassword", navigationManager.NavigatedTo);
    }

    /// <summary>0.9.104 起：必須使用兩步驟驗證卻還沒設定 → 每次換頁都帶去設定頁。</summary>
    [Fact]
    public async Task Check_WhenTwoFactorRequiredButNotSetUp_ShouldRedirectToTheSetupPage()
    {
        await using var fixture = await AuthenticationStateHelperFixture.CreateAsync();
        var user = await fixture.AddUserAsync();
        await fixture.RequireTwoFactorForRoleOfAsync(user);
        var navigationManager = new TestNavigationManager("http://localhost/App");

        var result = await fixture.CreateHelper().Check(new TestAuthenticationStateProvider(CreatePrincipal(user)), navigationManager);

        Assert.Equal(AuthenticationCheckResult.RequiresTwoFactorSetup, result);
        Assert.Equal("/TwoFactorSetup", navigationManager.NavigatedTo);
    }

    [Fact]
    public async Task Check_OnTheSetupPage_OrWhenAlreadyEnabled_ShouldSucceed()
    {
        await using var fixture = await AuthenticationStateHelperFixture.CreateAsync();
        var user = await fixture.AddUserAsync();
        await fixture.RequireTwoFactorForRoleOfAsync(user);

        var onSetup = new TestNavigationManager("http://localhost/TwoFactorSetup");
        Assert.Equal(AuthenticationCheckResult.Succeeded, await fixture.CreateHelper().Check(new TestAuthenticationStateProvider(CreatePrincipal(user)), onSetup));
        Assert.Null(onSetup.NavigatedTo);

        await fixture.Context.MyUser.Where(x => x.Id == user.Id).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.TwoFactorEnabled, true).SetProperty(x => x.TwoFactorSecret, "protected"));
        var elsewhere = new TestNavigationManager("http://localhost/App");
        Assert.Equal(AuthenticationCheckResult.Succeeded, await fixture.CreateHelper().Check(new TestAuthenticationStateProvider(CreatePrincipal(user)), elsewhere));
        Assert.Null(elsewhere.NavigatedTo);
    }

    /// <summary>同時要改密碼與設定兩步驟驗證：先改密碼，而且改密碼頁不會被帶去設定頁（兩頁互不導向，否則來回跳）。</summary>
    [Fact]
    public async Task Check_WhenPasswordChangeIsAlsoRequired_ShouldGoToChangePasswordFirst_WithoutALoop()
    {
        await using var fixture = await AuthenticationStateHelperFixture.CreateAsync();
        var user = await fixture.AddUserAsync(mustChangePassword: true);
        await fixture.RequireTwoFactorForRoleOfAsync(user);

        var onApp = new TestNavigationManager("http://localhost/App");
        Assert.Equal(AuthenticationCheckResult.RequiresPasswordChange, await fixture.CreateHelper().Check(new TestAuthenticationStateProvider(CreatePrincipal(user)), onApp));
        Assert.Equal("/ChangePassword", onApp.NavigatedTo);

        var onChangePassword = new TestNavigationManager("http://localhost/ChangePassword");
        Assert.Equal(AuthenticationCheckResult.Succeeded, await fixture.CreateHelper().Check(new TestAuthenticationStateProvider(CreatePrincipal(user)), onChangePassword));
        Assert.Null(onChangePassword.NavigatedTo);
    }

    /// <summary>0.9.103 起：工作階段版本與資料庫不符（改密碼、停用、強制登出之後的舊登入）→ 系統登出並記 Login.SessionExpired。</summary>
    [Fact]
    public async Task Check_WithAStaleSecurityStamp_ShouldRevokeTheSession()
    {
        await using var fixture = await AuthenticationStateHelperFixture.CreateAsync();
        var user = await fixture.AddUserAsync();
        var stale = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Sid, user.Id.ToString()), new Claim(MagicObjectHelper.SecurityStampClaimType, "old-stamp")], "Test"));
        var withoutStamp = CreatePrincipal(user.Id.ToString());

        foreach (var principal in new[] { stale, withoutStamp })
        {
            var navigationManager = new TestNavigationManager("http://localhost/App");
            var result = await fixture.CreateHelper().Check(new TestAuthenticationStateProvider(principal), navigationManager);

            Assert.Equal(AuthenticationCheckResult.SessionRevoked, result);
            Assert.Equal("/Auths/Logout?reason=session", navigationManager.NavigatedTo);
        }

        Assert.Equal(2, await fixture.Context.AuditLog.CountAsync(x => x.Action == AuditActions.Login.SessionExpired && x.ActorUserId == user.Id));
    }

    /// <summary>0.9.101 起只看旗標與到期：密碼是 123456 本身不再觸發（登入時才補旗標，見 MyUserServiceLoginTests）。</summary>
    [Fact]
    public async Task Check_WithLegacyDefaultPasswordButNoFlag_ShouldSucceed()
    {
        await using var fixture = await AuthenticationStateHelperFixture.CreateAsync();
        var user = await fixture.AddUserAsync(password: MagicObjectHelper.NeedChangePassword);
        var navigationManager = new TestNavigationManager("http://localhost/App");

        var result = await fixture.CreateHelper().Check(new TestAuthenticationStateProvider(CreatePrincipal(user)), navigationManager);

        Assert.Equal(AuthenticationCheckResult.Succeeded, result);
    }

    [Fact]
    public async Task Check_WhenPasswordExpired_ShouldReturnRequiresPasswordChange()
    {
        await using var fixture = await AuthenticationStateHelperFixture.CreateAsync();
        var user = await fixture.AddUserAsync(passwordChangedAtUtc: DateTime.UtcNow.AddDays(-31));
        var navigationManager = new TestNavigationManager("http://localhost/App");
        var policy = PasswordTestDefaults.Policy(new PasswordPolicySettings { ExpiryDays = 30 });

        var result = await fixture.CreateHelper(policy).Check(new TestAuthenticationStateProvider(CreatePrincipal(user)), navigationManager);

        Assert.Equal(AuthenticationCheckResult.RequiresPasswordChange, result);
        Assert.Equal("/ChangePassword", navigationManager.NavigatedTo);
    }

    /// <summary>0.9.102 起：每次換頁的登入檢查載入最新資料後通知右上角（管理員改了姓名，對方換頁就看到）。</summary>
    [Fact]
    public async Task Check_WithValidUser_ShouldRaiseChangedAfterLoadingTheLatestData()
    {
        await using var fixture = await AuthenticationStateHelperFixture.CreateAsync();
        var user = await fixture.AddUserAsync();
        string? nameSeen = null;
        fixture.CurrentUserService.Changed += () => nameSeen = fixture.CurrentUserService.CurrentUser.Name;

        var result = await fixture.CreateHelper().Check(new TestAuthenticationStateProvider(CreatePrincipal(user)), new TestNavigationManager("http://localhost/App"));

        Assert.Equal(AuthenticationCheckResult.Succeeded, result);
        Assert.Equal("Test User", nameSeen);
    }

    [Fact]
    public async Task Check_WithValidUser_ShouldInitializeCurrentUser()
    {
        await using var fixture = await AuthenticationStateHelperFixture.CreateAsync();
        var user = await fixture.AddUserAsync(permissionName: "PermissionA");
        var authProvider = new TestAuthenticationStateProvider(CreatePrincipal(user));
        var navigationManager = new TestNavigationManager("http://localhost/App");

        var result = await fixture.CreateHelper().Check(authProvider, navigationManager);

        Assert.Equal(AuthenticationCheckResult.Succeeded, result);
        Assert.Equal(user.Id, fixture.CurrentUserService.CurrentUser.Id);
        Assert.True(fixture.CurrentUserService.CurrentUser.IsAuthenticated);
        Assert.Contains("PermissionA", fixture.CurrentUserService.CurrentUser.RoleList);
        Assert.Null(navigationManager.NavigatedTo);
    }

    [Fact]
    public async Task Check_WithMultipleRoles_ShouldInitializeRoleListAsUnion()
    {
        await using var fixture = await AuthenticationStateHelperFixture.CreateAsync();
        var user = await fixture.AddMultiRoleUserAsync(primaryKey: "PermissionA", additionalKey: "PermissionB");
        var authProvider = new TestAuthenticationStateProvider(CreatePrincipal(user));
        var navigationManager = new TestNavigationManager("http://localhost/App");

        var result = await fixture.CreateHelper().Check(authProvider, navigationManager);

        Assert.Equal(AuthenticationCheckResult.Succeeded, result);
        var roleList = fixture.CurrentUserService.CurrentUser.RoleList;
        Assert.Contains("PermissionA", roleList);
        Assert.Contains("PermissionB", roleList);
    }

    [Fact]
    public async Task Check_WithSoftDeletedUser_ShouldReturnInvalidUserAndNavigateLogout()
    {
        // 0.9.95 起使用者是軟刪除：已登入的人被刪除後，下一次換頁就要被登出（全域過濾器讓他「找不到」）。
        await using var fixture = await AuthenticationStateHelperFixture.CreateAsync();
        var user = await fixture.AddUserAsync();
        await fixture.Context.MyUser.Where(x => x.Id == user.Id).ExecuteUpdateAsync(x => x.SetProperty(u => u.IsDeleted, true));
        var authProvider = new TestAuthenticationStateProvider(CreatePrincipal(user));
        var navigationManager = new TestNavigationManager("http://localhost/App");

        var result = await fixture.CreateHelper().Check(authProvider, navigationManager);

        Assert.Equal(AuthenticationCheckResult.InvalidUser, result);
        Assert.Equal("/Auths/Logout", navigationManager.NavigatedTo);
    }

    [Fact]
    public async Task Check_WithSoftDeletedAdditionalRole_ShouldDropItsPermissionsFromRoleList()
    {
        // 額外角色的關聯（UserRole）刻意保留，權限判斷必須經 RoleView 過濾，否則已刪除的角色會繼續給畫面權限。
        await using var fixture = await AuthenticationStateHelperFixture.CreateAsync();
        var user = await fixture.AddMultiRoleUserAsync(primaryKey: "PermissionA", additionalKey: "PermissionB");
        var additionalRoleId = await fixture.Context.UserRole
            .Where(x => x.MyUserId == user.Id && x.RoleViewId != user.RoleViewId)
            .Select(x => x.RoleViewId)
            .SingleAsync();
        await fixture.Context.RoleView.Where(x => x.Id == additionalRoleId).ExecuteUpdateAsync(x => x.SetProperty(r => r.IsDeleted, true));
        var authProvider = new TestAuthenticationStateProvider(CreatePrincipal(user));
        var navigationManager = new TestNavigationManager("http://localhost/App");

        var result = await fixture.CreateHelper().Check(authProvider, navigationManager);

        Assert.Equal(AuthenticationCheckResult.Succeeded, result);
        var roleList = fixture.CurrentUserService.CurrentUser.RoleList;
        Assert.Contains("PermissionA", roleList);
        Assert.DoesNotContain("PermissionB", roleList);
    }

    /// <summary>
    /// 角色矩陣只勾「檢視」時產生的是「頁面:view」而不含裸鍵。若 CheckAccessPage 只認裸鍵，
    /// 唯讀角色會連頁面都打不開、選單也不顯示 —— 「可看不可改」就只剩 API 端生效。
    /// </summary>
    [Fact]
    public async Task CheckAccessPage_WithOnlyViewActionKey_ShouldAllow()
    {
        await using var fixture = await AuthenticationStateHelperFixture.CreateAsync();
        fixture.CurrentUserService.CurrentUser.IsAdmin = false;
        fixture.CurrentUserService.CurrentUser.RoleList =
            [PermissionKey.For(MagicObjectHelper.角色_分類清單, PermissionActions.View)];

        Assert.True(fixture.CreateHelper().CheckAccessPage(MagicObjectHelper.角色_分類清單));
    }

    /// <summary>
    /// 放寬的邊界只到 view：只有 create 的角色仍然不得進入頁面，
    /// 否則「頁面鍵」會退化成「任何動作鍵都能進」。
    /// </summary>
    [Fact]
    public async Task CheckAccessPage_WithOnlyNonViewActionKey_ShouldDeny()
    {
        await using var fixture = await AuthenticationStateHelperFixture.CreateAsync();
        fixture.CurrentUserService.CurrentUser.IsAdmin = false;
        fixture.CurrentUserService.CurrentUser.RoleList =
            [PermissionKey.For(MagicObjectHelper.角色_分類清單, PermissionActions.Create)];

        Assert.False(fixture.CreateHelper().CheckAccessPage(MagicObjectHelper.角色_分類清單));
    }

    /// <summary>與真正登入相同：帶著使用者目前的工作階段版本（0.9.103 起）。</summary>
    private static ClaimsPrincipal CreatePrincipal(MyUser user)
        => new(new ClaimsIdentity(
            [new Claim(ClaimTypes.Sid, user.Id.ToString()), new Claim(MagicObjectHelper.SecurityStampClaimType, user.SecurityStamp)],
            "Test"));

    private static ClaimsPrincipal CreatePrincipal(string sid)
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Sid, sid)],
            "Test");

        return new ClaimsPrincipal(identity);
    }

    private sealed class AuthenticationStateHelperFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly IMapper mapper;
        private readonly ILoggerFactory loggerFactory;

        private AuthenticationStateHelperFixture(SqliteConnection connection, BackendDBContext context)
        {
            this.connection = connection;
            Context = context;
            CurrentUserService = new CurrentUserService();

            loggerFactory = LoggerFactory.Create(_ => { });
            var mapperConfiguration = new MapperConfiguration(
                configuration => configuration.AddProfile<AutoMapping>(),
                loggerFactory);
            mapper = mapperConfiguration.CreateMapper();
        }

        public BackendDBContext Context { get; }

        public CurrentUserService CurrentUserService { get; }

        /// <summary>兩步驟驗證設定（0.9.104 起）；測試可在建立 helper 前修改。</summary>
        public TwoFactorSettings TwoFactorSettings { get; } = new();

        public static async Task<AuthenticationStateHelperFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<BackendDBContext>()
                .UseSqlite(connection)
                .Options;

            var context = new BackendDBContext(options);
            await context.Database.EnsureCreatedAsync();

            return new AuthenticationStateHelperFixture(connection, context);
        }

        public AuthenticationStateHelper CreateHelper(IPasswordPolicy? passwordPolicy = null)
        {
            var rolePermissionService = new RolePermissionService();

            return new AuthenticationStateHelper(
                loggerFactory.CreateLogger<AuthenticationStateHelper>(),
                mapper,
                new MyUserService(new TestDbContextFactory(connection), mapper, loggerFactory.CreateLogger<MyUserService>(), new RbacWriteService(Context, NullLogger<RbacWriteService>.Instance), new AuditLogService(new TestDbContextFactory(connection), loggerFactory.CreateLogger<AuditLogService>()), CurrentUserService, Options.Create(new BootstrapSettings()), PasswordTestDefaults.Policy()),
                CurrentUserService,
                rolePermissionService,
                new EffectiveTeamResolver(Context, new ContextTeamTreeCache(Context), NullLogger<EffectiveTeamResolver>.Instance),
                new PermissionChecker(Context, NullLogger<PermissionChecker>.Instance),
                new AuditLogService(new TestDbContextFactory(connection), loggerFactory.CreateLogger<AuditLogService>()),
                passwordPolicy ?? PasswordTestDefaults.Policy(),
                new SecurityStampService(new TestDbContextFactory(connection), TimeProvider.System, NullLogger<SecurityStampService>.Instance),
                TwoFactorTestDefaults.Service(new TestDbContextFactory(connection), currentUser: CurrentUserService, settings: TwoFactorSettings));
        }

        public async Task<MyUser> AddUserAsync(
            bool status = true,
            string password = "secure-password",
            string roleJson = """["PermissionA"]""",
            string permissionName = "PermissionA",
            bool mustChangePassword = false,
            DateTime? passwordChangedAtUtc = null)
        {
            var roleView = new RoleView
            {
                Name = $"role-{Guid.NewGuid():N}",
                TabViewJson = roleJson == """["PermissionA"]"""
                    ? $"""["{permissionName}"]"""
                    : roleJson
            };

            Context.RoleView.Add(roleView);
            await Context.SaveChangesAsync();

            // UI 權限來源已改讀 RBAC 表（IPermissionChecker），故種子資料需雙寫 RolePermissionMap，
            // 使登入後 CurrentUser.RoleList 反映此角色的權限鍵。
            await new RbacWriteService(Context, NullLogger<RbacWriteService>.Instance).SyncRolePermissionsAsync(roleView.Id, new[] { permissionName });

            var user = new MyUser
            {
                Account = $"user-{Guid.NewGuid():N}",
                Name = "Test User",
                Salt = Guid.NewGuid().ToString(),
                Status = status,
                RoleViewId = roleView.Id,
                MustChangePassword = mustChangePassword,
                PasswordChangedAtUtc = passwordChangedAtUtc,
            };
            user.Password = PasswordHelper.GetPasswordSHA(user.Salt, password);

            Context.MyUser.Add(user);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();

            return user;
        }

        public async Task RequireTwoFactorForRoleOfAsync(MyUser user)
            => await Context.RoleView.Where(x => x.Id == user.RoleViewId).ExecuteUpdateAsync(s => s.SetProperty(x => x.RequireTwoFactor, true));

        public async Task<MyUser> AddMultiRoleUserAsync(string primaryKey, string additionalKey)
        {
            var writer = new RbacWriteService(Context, NullLogger<RbacWriteService>.Instance);

            var primaryRole = new RoleView { Name = $"role-{Guid.NewGuid():N}", TabViewJson = $"""["{primaryKey}"]""" };
            var additionalRole = new RoleView { Name = $"role-{Guid.NewGuid():N}", TabViewJson = $"""["{additionalKey}"]""" };
            Context.RoleView.AddRange(primaryRole, additionalRole);
            await Context.SaveChangesAsync();
            await writer.SyncRolePermissionsAsync(primaryRole.Id, new[] { primaryKey });
            await writer.SyncRolePermissionsAsync(additionalRole.Id, new[] { additionalKey });

            var user = new MyUser
            {
                Account = $"user-{Guid.NewGuid():N}",
                Name = "Test User",
                Salt = Guid.NewGuid().ToString(),
                Status = true,
                RoleViewId = primaryRole.Id
            };
            user.Password = PasswordHelper.GetPasswordSHA(user.Salt, "secure-password");
            Context.MyUser.Add(user);
            await Context.SaveChangesAsync();
            await writer.SyncUserRolesAsync(user.Id, new[] { primaryRole.Id, additionalRole.Id });
            Context.ChangeTracker.Clear();

            return user;
        }

        public async Task<MyUser> AddUserWithoutRoleAsync()
        {
            var user = new MyUser
            {
                Account = $"user-{Guid.NewGuid():N}",
                Name = "Test User",
                Salt = Guid.NewGuid().ToString(),
                Status = true
            };
            user.Password = PasswordHelper.GetPasswordSHA(user.Salt, "secure-password");

            Context.MyUser.Add(user);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();

            return user;
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
            loggerFactory.Dispose();
        }
    }

    private sealed class TestAuthenticationStateProvider : AuthenticationStateProvider
    {
        private readonly ClaimsPrincipal principal;

        public TestAuthenticationStateProvider(ClaimsPrincipal principal)
        {
            this.principal = principal;
        }

        public override Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            return Task.FromResult(new AuthenticationState(principal));
        }
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager(string initialUri)
        {
            Initialize("http://localhost/", initialUri);
        }

        public string? NavigatedTo { get; private set; }

        protected override void NavigateToCore(string uri, NavigationOptions options)
        {
            NavigatedTo = uri;
            Uri = ToAbsoluteUri(uri).ToString();
        }
    }
}

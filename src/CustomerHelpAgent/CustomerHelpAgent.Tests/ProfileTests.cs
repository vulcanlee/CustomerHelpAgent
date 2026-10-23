using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Others;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Web.Components.Layout;
using CustomerHelpAgent.Web.Components.Views.Profiles;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 個人資料（0.9.102 起）：登入紀錄只看自己的（精確 Id、同帳號不同人排除）、只含登入與登出、新到舊、上限 20 筆；
/// 改姓名只動姓名、版本號衝突、空白被擋、通知右上角；角色排除已刪除、團隊用有效團隊；縮寫規則；頁名退回操作說明。
/// </summary>
public sealed class ProfileTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly TestDbContextFactory factory;
    private readonly RecordingAuditLogService audit = new();
    private readonly CurrentUserService currentUser = new();
    private readonly List<IDisposable> disposables = [];

    public ProfileTests()
    {
        connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        connection.Open();
        factory = new TestDbContextFactory(connection);
        using var context = factory.CreateDbContext();
        context.Database.EnsureCreated();
    }

    // ---------- 登入紀錄 ----------

    [Fact]
    public async Task RecentLogins_ShouldOnlyIncludeTheUsersOwnLoginEvents_NewestFirst_AndCappedAtTwenty()
    {
        var me = AddUser("alice");
        var other = AddUser("bob");
        var start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        await using (var context = factory.CreateDbContext())
        {
            for (var i = 0; i < 25; i++)
            {
                // 0.9.123 起帶出來源 IP；較舊的幾筆模擬 0.9.117 之前沒有 IP 的紀錄。
                context.AuditLog.Add(new AuditLog { OccurredAt = start.AddMinutes(i), ActorUserId = me, ActorAccount = "alice", Action = i % 5 == 0 ? AuditActions.Logout : AuditActions.Login.Success, ClientIp = i >= 20 ? $"203.0.113.{i}" : null });
            }

            // 同帳號字串但不是同一個人（例如帳號被刪除後重新使用）、匿名失敗、非登入動作：都不算。
            context.AuditLog.Add(new AuditLog { OccurredAt = start.AddDays(1), ActorUserId = other, ActorAccount = "alice", Action = AuditActions.Login.Success });
            context.AuditLog.Add(new AuditLog { OccurredAt = start.AddDays(1), ActorUserId = null, ActorAccount = "alice", Action = AuditActions.Login.Failed, Success = false });
            context.AuditLog.Add(new AuditLog { OccurredAt = start.AddDays(1), ActorUserId = me, ActorAccount = "alice", Action = AuditActions.User.Update });
            await context.SaveChangesAsync();
        }

        var logins = await Service().GetRecentLoginsAsync(me);

        Assert.Equal(ProfileService.LoginHistorySize, logins.Count);
        Assert.Equal(start.AddMinutes(24), logins[0].OccurredAtUtc);
        Assert.Equal(start.AddMinutes(5), logins[^1].OccurredAtUtc);
        Assert.Equal("203.0.113.24", logins[0].ClientIp);
        Assert.Null(logins[^1].ClientIp);
        Assert.All(logins, x => Assert.True(x.Action == AuditActions.Logout || x.Action.StartsWith("Login.", StringComparison.Ordinal)));
    }

    [Fact]
    public void DescribeAction_ShouldTranslateLoginEvents()
    {
        Assert.Equal("登入", ProfileView.DescribeAction(AuditActions.Login.Success));
        Assert.Equal("連續輸錯，帳號被鎖定", ProfileView.DescribeAction(AuditActions.Login.LockedOut));
        Assert.Equal("Google 登入", ProfileView.DescribeAction(AuditActions.Login.SsoSuccess));
        Assert.Equal("登出", ProfileView.DescribeAction(AuditActions.Logout));
        Assert.Equal("Something.Else", ProfileView.DescribeAction("Something.Else"));
    }

    // ---------- 改姓名 ----------

    [Fact]
    public async Task UpdateName_ShouldOnlyChangeTheName_AndNotifyTheLayout()
    {
        var id = AddUser("alice", email: "alice@example.com", isAdmin: true, status: true, mustChange: true, lockoutEnd: DateTime.UtcNow.AddMinutes(5));
        currentUser.CurrentUser = new CurrentUser { Id = id, Account = "alice", Name = "alice" };
        var notified = 0;
        currentUser.Changed += () => notified++;
        var before = await LoadAsync(id);

        var result = await Service().UpdateNameAsync(id, "  王小明  ", before.ConcurrencyStamp);

        Assert.True(result.Success);
        var after = await LoadAsync(id);
        Assert.Equal("王小明", after.Name);
        Assert.Equal(before.Email, after.Email);
        Assert.Equal(before.IsAdmin, after.IsAdmin);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.MustChangePassword, after.MustChangePassword);
        Assert.Equal(before.LockoutEndUtc, after.LockoutEndUtc);
        Assert.Equal(before.Password, after.Password);
        Assert.NotEqual(before.ConcurrencyStamp, after.ConcurrencyStamp);
        Assert.Equal("王小明", currentUser.CurrentUser.Name);
        Assert.Equal(1, notified);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditActions.User.ProfileUpdate, entry.Action);
        Assert.DoesNotContain("王小明", entry.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateName_WithAStaleStamp_ShouldConflict_AndBlankShouldBeRejected()
    {
        var id = AddUser("alice");
        var stale = (await LoadAsync(id)).ConcurrencyStamp;
        Assert.True((await Service().UpdateNameAsync(id, "第一次", stale)).Success);

        var conflict = await Service().UpdateNameAsync(id, "第二次", stale);
        Assert.False(conflict.Success);
        Assert.Equal(ConcurrencyStampHelper.ConflictMessage, conflict.Message);
        Assert.Equal("第一次", (await LoadAsync(id)).Name);

        var blank = await Service().UpdateNameAsync(id, "   ", (await LoadAsync(id)).ConcurrencyStamp);
        Assert.False(blank.Success);
        Assert.Equal("姓名不可空白。", blank.Message);
    }

    [Fact]
    public async Task UpdateName_ForSomeoneElse_ShouldNotTouchTheCurrentUser()
    {
        var me = AddUser("alice");
        var other = AddUser("bob");
        currentUser.CurrentUser = new CurrentUser { Id = me, Account = "alice", Name = "alice" };
        var notified = 0;
        currentUser.Changed += () => notified++;

        Assert.True((await Service().UpdateNameAsync(other, "Bob Lee", (await LoadAsync(other)).ConcurrencyStamp)).Success);

        Assert.Equal("alice", currentUser.CurrentUser.Name);
        Assert.Equal(0, notified);
    }

    // ---------- 讀取 ----------

    [Fact]
    public async Task Get_ShouldListLiveRoles_EffectiveTeams_AndPasswordExpiry()
    {
        int primary, extra, deleted;
        await using (var context = factory.CreateDbContext())
        {
            var p = new RoleView { Name = "業務", TabViewJson = "[]", DefaultTeamsJson = JsonSerializer.Serialize(new[] { "北區" }) };
            var e = new RoleView { Name = "主管", TabViewJson = "[]" };
            var d = new RoleView { Name = "已刪除", TabViewJson = "[]", IsDeleted = true };
            context.RoleView.AddRange(p, e, d);
            context.Team.AddRange(new Team { Name = "北區" }, new Team { Name = "南區" });
            await context.SaveChangesAsync();
            (primary, extra, deleted) = (p.Id, e.Id, d.Id);
        }

        var changedAt = DateTime.UtcNow.AddDays(-10);
        var id = AddUser("alice", roleId: primary, changedAt: changedAt);
        await using (var context = factory.CreateDbContext())
        {
            context.UserRole.AddRange(new UserRole { MyUserId = id, RoleViewId = extra }, new UserRole { MyUserId = id, RoleViewId = deleted });
            context.UserTeam.Add(new UserTeam { MyUserId = id, TeamId = await context.Team.Where(x => x.Name == "南區").Select(x => x.Id).SingleAsync() });
            await context.SaveChangesAsync();
        }

        var profile = await Service(new PasswordPolicySettings { ExpiryDays = 30 }).GetAsync(id);

        Assert.NotNull(profile);
        Assert.Equal(new[] { "主管", "業務" }, profile.Roles);
        Assert.Equal(new[] { "北區", "南區" }, profile.Teams);
        Assert.True(profile.HasLocalPassword);
        Assert.Equal(changedAt.AddDays(30), profile.PasswordExpiresAtUtc!.Value, TimeSpan.FromSeconds(1));
        Assert.Null(await Service().GetAsync(9999));
    }

    // ---------- 縮寫與頁名 ----------

    [Theory]
    [InlineData("王小明", "alice", "王")]
    [InlineData("John Smith", "js", "JS")]
    [InlineData("mary ann lee", "m", "ML")]
    [InlineData("alice", "alice", "A")]
    [InlineData("", "bob", "B")]
    [InlineData("  ", "", "?")]
    [InlineData("𠀋一", "x", "𠀋")]
    public void Initials_ShouldUseTheFirstCharacter_OrFirstAndLastEnglishWords(string name, string account, string expected)
        => Assert.Equal(expected, UserInitials.From(name, account));

    [Fact]
    public void PageTitle_ShouldFallBackToTheHelpTopicForPagesOutsideTheMenu()
    {
        var menu = new List<SidebarMenuItemModel> { new() { Name = "專案項目", Url = "/projects" } };
        var topics = new List<PageHelpTopicModel> { new() { Route = "/Profile", Title = "個人資料" }, new() { Route = "/ChangePassword", Title = "變更密碼" } };

        Assert.Equal("專案項目", MainLayout.ResolvePageTitle(menu, topics, "/projects"));
        Assert.Equal("個人資料", MainLayout.ResolvePageTitle(menu, topics, "/Profile"));
        Assert.Equal("變更密碼", MainLayout.ResolvePageTitle(menu, topics, "/changepassword"));
        Assert.Equal("系統首頁", MainLayout.ResolvePageTitle(menu, topics, "/somewhere"));
    }

    // ---------- 共用 ----------

    private ProfileService Service(PasswordPolicySettings? policy = null)
    {
        var context = factory.CreateDbContext();
        disposables.Add(context);
        return new ProfileService(factory, new EffectiveTeamResolver(context, new ContextTeamTreeCache(context), NullLogger<EffectiveTeamResolver>.Instance),
            PasswordTestDefaults.Policy(policy), audit, currentUser, NullLogger<ProfileService>.Instance);
    }

    private int AddUser(string account, string? email = null, bool isAdmin = false, bool status = true, bool mustChange = false,
        DateTime? lockoutEnd = null, int? roleId = null, DateTime? changedAt = null)
    {
        using var context = factory.CreateDbContext();
        var user = new MyUser
        {
            Account = account,
            Name = account,
            Password = SecurePasswordHasher.HashPassword("Passw0rd-1"),
            Email = email,
            IsAdmin = isAdmin,
            Status = status,
            MustChangePassword = mustChange,
            LockoutEndUtc = lockoutEnd,
            RoleViewId = roleId,
            PasswordChangedAtUtc = changedAt,
        };
        context.MyUser.Add(user);
        context.SaveChanges();
        return user.Id;
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

using AutoMapper;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.AdapterModel;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Web.Scheduling;
using CustomerHelpAgent.Web.Scheduling.Jobs;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 密碼原則與登入鎖定（0.9.101 起）：規則邊界、歷史（含目前密碼、第 N 與第 N+1 次、0 關閉、裁切）、必須變更（旗標、到期邊界、豁免）、
/// 每條設定密碼的路徑都套用原則、鎖定（門檻、到期先歸零、稽核標籤、同一則訊息、通知一次、解鎖不換版本號）、
/// Google 登入看鎖定、123456 登入補旗標、到期提醒作業，以及「只有政策服務能雜湊」的守門。
/// </summary>
public sealed class PasswordPolicyTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 1, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection connection;
    private readonly TestDbContextFactory factory;
    private readonly ManualTimeProvider time = new(Now);
    private readonly IMapper mapper;
    private readonly RecordingAuditLogService audit = new();
    private readonly RecordingNotificationSender notifications = new();
    private readonly List<IDisposable> disposables = [];

    public PasswordPolicyTests()
    {
        connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        connection.Open();
        factory = new TestDbContextFactory(connection);
        using var context = factory.CreateDbContext();
        context.Database.EnsureCreated();
        mapper = new MapperConfiguration(c => c.AddProfile<AutoMapping>(), LoggerFactory.Create(_ => { })).CreateMapper();
    }

    private DateTime NowUtc => Now.UtcDateTime;

    // ---------- 規則 ----------

    [Theory]
    [InlineData("abcdefg1", null)]
    [InlineData("abcdef1", "至少 8 個字元")]
    [InlineData("abcdefgh", "要有數字")]
    [InlineData("12345678", "要有英文字母")]
    [InlineData("密碼密碼密碼密碼12", "要有英文字母")]
    public void Check_DefaultRules(string password, string? expectedFailure)
    {
        var result = PasswordTestDefaults.Policy().Check(password);

        if (expectedFailure is null)
        {
            Assert.Null(result);
        }
        else
        {
            Assert.Contains(expectedFailure, result, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Check_ShouldListEveryFailedRule_AndHonourOptionalRules()
    {
        var strict = PasswordTestDefaults.Policy(new PasswordPolicySettings { MinimumLength = 10, RequireUppercase = true, RequireSymbol = true });

        Assert.Null(strict.Check("Abcdefgh1!"));
        var message = strict.Check("abc");
        Assert.Contains("至少 10 個字元", message, StringComparison.Ordinal);
        Assert.Contains("要有數字", message, StringComparison.Ordinal);
        Assert.Contains("要有大寫英文字母", message, StringComparison.Ordinal);
        Assert.Contains("要有符號", message, StringComparison.Ordinal);
        Assert.Contains("要有符號", strict.Check("Abcdefgh12"), StringComparison.Ordinal);
        Assert.Contains("要有大寫英文字母", strict.Check("abcdefgh1!"), StringComparison.Ordinal);

        var relaxed = PasswordTestDefaults.Policy(new PasswordPolicySettings { MinimumLength = 6, RequireLetter = false, RequireDigit = false });
        Assert.Null(relaxed.Check("zzzzzz"));
        Assert.NotNull(relaxed.Check(null));
    }

    [Fact]
    public void Describe_ShouldReflectTheSettings()
    {
        Assert.Equal("至少 8 個字元，需包含英文字母、數字，不可與最近 3 次用過的密碼相同。", PasswordTestDefaults.Policy().Describe());
        Assert.Equal("至少 6 個字元。", PasswordTestDefaults.Policy(new PasswordPolicySettings { MinimumLength = 6, RequireLetter = false, RequireDigit = false, HistoryCount = 0 }).Describe());
    }

    /// <summary>「複製使用者」的「產生」鈕（0.9.127 起）：產生的密碼必定通過目前的規則。</summary>
    [Theory]
    [InlineData(8, true, true, false, false, 12)]
    [InlineData(8, true, true, true, true, 12)]
    [InlineData(20, false, false, false, false, 20)]
    public void Generate_ShouldAlwaysPassTheCurrentRules(int minimumLength, bool letter, bool digit, bool upper, bool symbol, int expectedLength)
    {
        var policy = PasswordTestDefaults.Policy(new PasswordPolicySettings
        {
            MinimumLength = minimumLength,
            RequireLetter = letter,
            RequireDigit = digit,
            RequireUppercase = upper,
            RequireSymbol = symbol,
        });

        var passwords = Enumerable.Range(0, 50).Select(_ => policy.Generate()).ToList();

        Assert.All(passwords, p =>
        {
            Assert.Null(policy.Check(p));
            Assert.Equal(expectedLength, p.Length);
            Assert.DoesNotContain(p, c => "0O1lI".Contains(c));
        });
        Assert.Equal(passwords.Count, passwords.Distinct().Count());
    }

    // ---------- 歷史 ----------

    [Fact]
    public async Task History_ShouldCoverTheCurrentAndLastNPasswords_AndBeTrimmed()
    {
        var policy = PasswordTestDefaults.Policy(timeProvider: time);
        var userId = await CreateUserWithPasswordsAsync(policy, "Passw0rd-1", "Passw0rd-2", "Passw0rd-3", "Passw0rd-4");

        await using var context = factory.CreateDbContext();
        var user = await context.MyUser.SingleAsync(x => x.Id == userId);
        Assert.True(await policy.IsReusedAsync(context, user, "Passw0rd-4"));   // 目前
        Assert.True(await policy.IsReusedAsync(context, user, "Passw0rd-2"));   // 第 3 次（N）
        Assert.False(await policy.IsReusedAsync(context, user, "Passw0rd-1"));  // 第 4 次（N+1）
        Assert.Equal(3, await context.PasswordHistory.CountAsync(x => x.MyUserId == userId));
        Assert.DoesNotContain(await context.PasswordHistory.Select(x => x.PasswordHash).ToListAsync(), x => x.Contains("Passw0rd", StringComparison.Ordinal));
    }

    /// <summary>調小 HistoryCount 之後，表裡還留著較多筆：只能比對最近 N 筆（下一次設定密碼時才裁切）。</summary>
    [Fact]
    public async Task History_AfterLoweringTheCount_ShouldOnlyCompareTheLatestN()
    {
        var userId = await CreateUserWithPasswordsAsync(PasswordTestDefaults.Policy(new PasswordPolicySettings { HistoryCount = 5 }, time),
            "Passw0rd-1", "Passw0rd-2", "Passw0rd-3", "Passw0rd-4", "Passw0rd-5");
        var lowered = PasswordTestDefaults.Policy(new PasswordPolicySettings { HistoryCount = 3 }, time);

        await using var context = factory.CreateDbContext();
        var user = await context.MyUser.SingleAsync(x => x.Id == userId);
        Assert.Equal(5, await context.PasswordHistory.CountAsync(x => x.MyUserId == userId));
        Assert.True(await lowered.IsReusedAsync(context, user, "Passw0rd-3"));
        Assert.False(await lowered.IsReusedAsync(context, user, "Passw0rd-2"));
    }

    [Fact]
    public async Task History_Zero_ShouldNotCheck_AndShouldDeleteExistingRows()
    {
        var userId = await CreateUserWithPasswordsAsync(PasswordTestDefaults.Policy(timeProvider: time), "Passw0rd-1", "Passw0rd-2");
        var off = PasswordTestDefaults.Policy(new PasswordPolicySettings { HistoryCount = 0 }, time);

        await using (var context = factory.CreateDbContext())
        {
            var user = await context.MyUser.SingleAsync(x => x.Id == userId);
            Assert.False(await off.IsReusedAsync(context, user, "Passw0rd-2"));
            await off.ApplyAsync(context, user, "Passw0rd-3", false);
            await context.SaveChangesAsync();
        }

        await using var check = factory.CreateDbContext();
        Assert.Equal(0, await check.PasswordHistory.CountAsync());
    }

    [Fact]
    public async Task History_ShouldCountALegacyCurrentPassword()
    {
        await using var context = factory.CreateDbContext();
        var user = new MyUser { Account = "legacy", Name = "legacy", Salt = "salt" };
        user.Password = PasswordHelper.GetPasswordSHA("salt", "Legacy-pass1");
        context.MyUser.Add(user);
        await context.SaveChangesAsync();

        Assert.True(await PasswordTestDefaults.Policy().IsReusedAsync(context, user, "Legacy-pass1"));
    }

    [Fact]
    public async Task Apply_ShouldHash_StampTheTime_SetTheFlag_AndUnlock()
    {
        await using var context = factory.CreateDbContext();
        var user = new MyUser { Account = "a", Name = "a", AccessFailedCount = 4, LockoutEndUtc = NowUtc.AddMinutes(5) };
        context.MyUser.Add(user);

        await PasswordTestDefaults.Policy(timeProvider: time).ApplyAsync(context, user, "Passw0rd-x", mustChangeAtNextLogin: true);
        await context.SaveChangesAsync();

        Assert.Equal(PasswordVerificationOutcome.Success, SecurePasswordHasher.VerifyPassword("Passw0rd-x", user.Password, user.Salt));
        Assert.Equal(NowUtc, user.PasswordChangedAtUtc);
        Assert.True(user.MustChangePassword);
        Assert.Equal(0, user.AccessFailedCount);
        Assert.Null(user.LockoutEndUtc);
        Assert.Equal(1, await context.PasswordHistory.CountAsync(x => x.MyUserId == user.Id));
    }

    // ---------- 必須變更 ----------

    [Fact]
    public void RequiresChange_FlagAndExpiryBoundary_WithExemptions()
    {
        var policy = PasswordTestDefaults.Policy(new PasswordPolicySettings { ExpiryDays = 30 }, time);
        var atExpiry = NowUtc.AddDays(-30);

        Assert.True(policy.RequiresChange("alice", true, false, atExpiry));
        Assert.False(policy.RequiresChange("alice", true, false, atExpiry.AddTicks(1)));
        Assert.Equal(NowUtc.AddTicks(1), policy.GetExpiresAtUtc("alice", true, atExpiry.AddTicks(1)));
        Assert.True(policy.RequiresChange("alice", true, true, NowUtc));
        Assert.False(policy.RequiresChange("alice", true, false, null));
        Assert.Null(policy.GetExpiresAtUtc("alice", true, null));

        // support 與沒有本機密碼的帳號：旗標與到期都不算（否則會永遠卡在變更密碼頁）。
        Assert.False(policy.RequiresChange("SUPPORT", true, true, atExpiry));
        Assert.False(policy.RequiresChange("google-user", false, true, atExpiry));
        Assert.Null(policy.GetExpiresAtUtc("support", true, atExpiry));

        var noExpiry = PasswordTestDefaults.Policy(timeProvider: time);
        Assert.False(noExpiry.RequiresChange("alice", true, false, NowUtc.AddYears(-10)));
        Assert.Null(noExpiry.GetExpiresAtUtc("alice", true, NowUtc));
    }

    // ---------- 每條設定密碼的路徑 ----------

    [Fact]
    public async Task AddUser_ShouldApplyThePolicy_AndRecordTheFlagTimeAndHistory()
    {
        var service = UserService();

        var weak = await service.AddAsync(NewUserModel("weak", "short"));
        Assert.False(weak.Success);
        Assert.Contains("至少 8 個字元", weak.Message, StringComparison.Ordinal);

        Assert.True((await service.AddAsync(NewUserModel("bob", "Passw0rd-1", mustChange: true))).Success);
        await using var context = factory.CreateDbContext();
        var bob = await context.MyUser.SingleAsync(x => x.Account == "bob");
        Assert.True(bob.MustChangePassword);
        Assert.Equal(NowUtc, bob.PasswordChangedAtUtc);
        Assert.Equal(1, await context.PasswordHistory.CountAsync(x => x.MyUserId == bob.Id));
        Assert.False(await context.MyUser.AnyAsync(x => x.Account == "weak"));
    }

    [Fact]
    public async Task UpdateUser_ShouldRejectWeakAndReusedPasswords_AndSaveTheFlagWithoutAPasswordChange()
    {
        var service = UserService();
        Assert.True((await service.AddAsync(NewUserModel("bob", "Passw0rd-1"))).Success);
        var id = await IdOfAsync("bob");

        var reused = await service.UpdateAsync(await EditModelAsync(service, id, password: "Passw0rd-1"));
        Assert.False(reused.Success);
        Assert.Equal(PasswordPolicy.ReusedMessage, reused.Message);

        var weak = await service.UpdateAsync(await EditModelAsync(service, id, password: "abcdefgh"));
        Assert.False(weak.Success);
        Assert.Contains("要有數字", weak.Message, StringComparison.Ordinal);

        Assert.True((await service.UpdateAsync(await EditModelAsync(service, id, mustChange: true))).Success);
        Assert.True((await LoadAsync(id)).MustChangePassword);

        await LockAsync(id);
        Assert.True((await service.UpdateAsync(await EditModelAsync(service, id, password: "Passw0rd-2", mustChange: false))).Success);
        var saved = await LoadAsync(id);
        Assert.False(saved.MustChangePassword);
        Assert.Null(saved.LockoutEndUtc);
        Assert.Equal(PasswordVerificationOutcome.Success, SecurePasswordHasher.VerifyPassword("Passw0rd-2", saved.Password, saved.Salt));
    }

    [Fact]
    public async Task ChangeOwnPassword_ShouldRejectReuse_AndClearTheFlag()
    {
        var service = UserService();
        Assert.True((await service.AddAsync(NewUserModel("bob", "Passw0rd-1", mustChange: true))).Success);
        var id = await IdOfAsync("bob");

        var reused = await service.ChangeOwnPasswordAsync(id, "Passw0rd-1", "Passw0rd-1", "Passw0rd-1");
        Assert.False(reused.Success);
        Assert.Equal(PasswordPolicy.ReusedMessage, reused.Message);
        Assert.False((await service.ChangeOwnPasswordAsync(id, "Passw0rd-1", "nodigits", "nodigits")).Success);

        Assert.True((await service.ChangeOwnPasswordAsync(id, "Passw0rd-1", "Passw0rd-2", "Passw0rd-2")).Success);
        var saved = await LoadAsync(id);
        Assert.False(saved.MustChangePassword);
        Assert.Equal(2, await CountHistoryAsync(id));
    }

    /// <summary>⭐ 守門：雜湊只准出現在政策服務、support 種子與登入時的舊雜湊升級 —— 新的設定密碼路徑不可繞過原則。</summary>
    [Fact]
    public void HashPassword_ShouldOnlyBeCalledFromThePolicyTheSupportSeederAndTheLoginRehash()
    {
        var root = FindSourceRoot();
        var callers = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !p.Contains("CustomerHelpAgent.Tests", StringComparison.Ordinal))
            .Where(p => File.ReadAllText(p).Contains("SecurePasswordHasher.HashPassword(", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "MyUserServiceLogin.cs", "PasswordPolicy.cs", "SupportUserSeeder.cs" }, callers);
    }

    // ---------- 登入鎖定 ----------

    [Fact]
    public async Task Lockout_ShouldLockAtTheThreshold_LabelOnlyThatAttempt_AndNotifyOnce()
    {
        var id = AddUser("alice", "Passw0rd-1");
        var login = Login(maxFailedAttempts: 3, lockoutMinutes: 10);

        await login.LoginAsync("alice", "wrong-1");
        await login.LoginAsync("alice", "wrong-2");
        Assert.Null((await LoadAsync(id)).LockoutEndUtc);
        Assert.Empty(notifications.Requests);

        var (message, user) = await Login(3, 10).LoginAsync("alice", "wrong-3");
        Assert.Null(user);
        Assert.Equal(MyUserServiceLogin.InvalidCredentialsMessage, message);
        Assert.Equal(NowUtc.AddMinutes(10), (await LoadAsync(id)).LockoutEndUtc);

        // 鎖定中：正確密碼也不行、同一則訊息、不再通知、稽核記 Failed。
        var (lockedMessage, lockedUser) = await Login(3, 10).LoginAsync("alice", "Passw0rd-1");
        Assert.Null(lockedUser);
        Assert.Equal(MyUserServiceLogin.InvalidCredentialsMessage, lockedMessage);

        Assert.Equal(
            new[] { AuditActions.Login.Failed, AuditActions.Login.Failed, AuditActions.Login.LockedOut, AuditActions.Login.Failed },
            audit.Entries.Select(x => x.Action));
        Assert.Equal("reason=Locked", audit.Entries[^1].Detail);
        var notice = Assert.Single(notifications.Requests);
        Assert.Equal(NotificationCategories.AccountLocked, notice.Category);
        Assert.True(notice.AlsoEmail);
        Assert.True(notice.Target.Admins);
        Assert.Contains("alice", notice.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lockout_UnknownWrongAndLocked_ShouldAllGetTheSameMessage()
    {
        var id = AddUser("alice", "Passw0rd-1");
        await LockAsync(id);

        var unknown = (await Login().LoginAsync("nobody", "x")).Message;
        var locked = (await Login().LoginAsync("alice", "Passw0rd-1")).Message;
        AddUser("bob", "Passw0rd-1");
        var wrong = (await Login().LoginAsync("bob", "wrong")).Message;

        Assert.All(new[] { unknown, locked, wrong }, m => Assert.Equal(MyUserServiceLogin.InvalidCredentialsMessage, m));
    }

    /// <summary>⭐ 0.9.100 之前：鎖定到期後次數留在門檻上，再錯一次就立刻又鎖。</summary>
    [Fact]
    public async Task Lockout_AfterItExpires_ShouldStartCountingFromZero()
    {
        var id = AddUser("alice", "Passw0rd-1");
        await using (var context = factory.CreateDbContext())
        {
            await context.MyUser.Where(x => x.Id == id).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.AccessFailedCount, 3).SetProperty(x => x.LockoutEndUtc, NowUtc.AddMinutes(-1)));
        }

        await Login(maxFailedAttempts: 3).LoginAsync("alice", "wrong");

        var saved = await LoadAsync(id);
        Assert.Null(saved.LockoutEndUtc);
        Assert.Equal(1, saved.AccessFailedCount);
        Assert.Empty(notifications.Requests);
    }

    [Fact]
    public async Task Lockout_AfterItExpires_CorrectPasswordShouldClearTheState()
    {
        var id = AddUser("alice", "Passw0rd-1");
        await using (var context = factory.CreateDbContext())
        {
            await context.MyUser.Where(x => x.Id == id).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.AccessFailedCount, 5).SetProperty(x => x.LockoutEndUtc, NowUtc.AddMinutes(-1)));
        }

        Assert.NotNull((await Login().LoginAsync("alice", "Passw0rd-1")).User);
        var saved = await LoadAsync(id);
        Assert.Null(saved.LockoutEndUtc);
        Assert.Equal(0, saved.AccessFailedCount);
    }

    [Fact]
    public async Task Login_WithTheLegacyDefaultPassword_ShouldSetTheFlag()
    {
        var legacy = AddUser("old", MagicObjectHelper.NeedChangePassword);
        var normal = AddUser("new", "Passw0rd-1");

        Assert.NotNull((await Login().LoginAsync("old", MagicObjectHelper.NeedChangePassword)).User);
        Assert.NotNull((await Login().LoginAsync("new", "Passw0rd-1")).User);

        Assert.True((await LoadAsync(legacy)).MustChangePassword);
        Assert.False((await LoadAsync(normal)).MustChangePassword);
    }

    [Fact]
    public async Task Unlock_ShouldClearTheLockout_KeepTheStamp_AndAudit()
    {
        var service = UserService();
        Assert.True((await service.AddAsync(NewUserModel("bob", "Passw0rd-1"))).Success);
        var id = await IdOfAsync("bob");
        await LockAsync(id);
        var stamp = (await LoadAsync(id)).ConcurrencyStamp;

        Assert.True((await service.UnlockAsync(id)).Success);
        var saved = await LoadAsync(id);
        Assert.Null(saved.LockoutEndUtc);
        Assert.Equal(0, saved.AccessFailedCount);
        Assert.Equal(stamp, saved.ConcurrencyStamp);
        Assert.Contains(audit.Entries, e => e.Action == AuditActions.User.Unlock);

        Assert.False((await service.UnlockAsync(id)).Success);
    }

    [Fact]
    public void GoogleLogin_ShouldRefuseALockedAccount()
    {
        var now = NowUtc;
        ExternalLoginOutcome Evaluate(MyUser user, bool deleted = false) => new ExternalLoginResult(user, deleted).Evaluate(now);

        Assert.Equal(ExternalLoginOutcome.SignIn, Evaluate(new MyUser { Status = true }));
        Assert.Equal(ExternalLoginOutcome.Locked, Evaluate(new MyUser { Status = true, LockoutEndUtc = now.AddSeconds(1) }));
        Assert.Equal(ExternalLoginOutcome.SignIn, Evaluate(new MyUser { Status = true, LockoutEndUtc = now }));
        Assert.Equal(ExternalLoginOutcome.Pending, Evaluate(new MyUser { Status = false, LockoutEndUtc = now.AddMinutes(5) }));
        Assert.Equal(ExternalLoginOutcome.Deleted, Evaluate(new MyUser { Status = true, LockoutEndUtc = now.AddMinutes(5) }, deleted: true));
    }

    // ---------- 到期提醒 ----------

    [Fact]
    public async Task ExpiryReminder_ShouldOnlyRemindPeopleWhosePasswordExpiresWithinSevenDays()
    {
        var soon = AddUser("soon", "Passw0rd-1", changedAtUtc: NowUtc.AddDays(-25));
        var edge = AddUser("edge", "Passw0rd-1", changedAtUtc: NowUtc.AddDays(-23));
        AddUser("later", "Passw0rd-1", changedAtUtc: NowUtc.AddDays(-23).AddTicks(1));
        AddUser("expired", "Passw0rd-1", changedAtUtc: NowUtc.AddDays(-31));
        AddUser("support", "Passw0rd-1", changedAtUtc: NowUtc.AddDays(-25));
        AddUser("google", string.Empty, changedAtUtc: NowUtc.AddDays(-25));
        AddUser("disabled", "Passw0rd-1", changedAtUtc: NowUtc.AddDays(-25), status: false);

        var result = await ReminderJob(expiryDays: 30).ExecuteAsync(JobContext(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(new[] { soon, edge }, notifications.Requests.SelectMany(x => x.Target.UserIds).Order());
        Assert.All(notifications.Requests, x =>
        {
            Assert.Equal(NotificationCategories.PasswordExpiring, x.Category);
            Assert.False(x.AlsoEmail);
            Assert.Equal("/ChangePassword", x.Link);
        });

        // 同一個到期日的去重鍵相同（實際去重由 INotificationSender 依 SourceKey 處理）。
        var firstKeys = notifications.Requests.Select(x => x.SourceKey).ToList();
        notifications.Requests.Clear();
        await ReminderJob(expiryDays: 30).ExecuteAsync(JobContext(), CancellationToken.None);
        Assert.Equal(firstKeys, notifications.Requests.Select(x => x.SourceKey));
        Assert.All(firstKeys, k => Assert.StartsWith("PasswordExpiring:", k, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExpiryReminder_ShouldDoNothingWhenPasswordsNeverExpire()
    {
        AddUser("soon", "Passw0rd-1", changedAtUtc: NowUtc.AddDays(-25));

        var result = await ReminderJob(expiryDays: 0).ExecuteAsync(JobContext(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Empty(notifications.Requests);
    }

    // ---------- 共用 ----------

    private MyUserService UserService()
    {
        var rbacContext = factory.CreateDbContext();
        disposables.Add(rbacContext);
        return new MyUserService(factory, mapper, NullLogger<MyUserService>.Instance,
            new RbacWriteService(rbacContext, NullLogger<RbacWriteService>.Instance), audit, new CurrentUserService(),
            Options.Create(new BootstrapSettings()), PasswordTestDefaults.Policy(timeProvider: time));
    }

    private MyUserServiceLogin Login(int maxFailedAttempts = 5, int lockoutMinutes = 15)
    {
        var context = factory.CreateDbContext();
        disposables.Add(context);
        return new MyUserServiceLogin(context, mapper, new ConfigurationBuilder().Build(), NullLogger<MyUserServiceLogin>.Instance,
            new RolePermissionService(), audit, PasswordTestDefaults.Lockout(maxFailedAttempts, lockoutMinutes), notifications, time,
            TwoFactorTestDefaults.Service(factory, audit, time));
    }

    private PasswordExpiryReminderJob ReminderJob(int expiryDays)
    {
        var settings = new PasswordPolicySettings { ExpiryDays = expiryDays };
        return new PasswordExpiryReminderJob(factory, PasswordTestDefaults.Policy(settings, time), notifications,
            new StaticOptionsMonitor<PasswordPolicySettings>(settings), time, NullLogger<PasswordExpiryReminderJob>.Instance);
    }

    private static ScheduledJobContext JobContext() => new(1, JobRunTriggers.Schedule, DateTime.UtcNow, null);

    private MyUserAdapterModel NewUserModel(string account, string password, bool mustChange = false)
    {
        int roleId;
        using (var context = factory.CreateDbContext())
        {
            roleId = context.RoleView.Select(x => x.Id).FirstOrDefault();
            if (roleId == 0)
            {
                var role = new RoleView { Name = "role", TabViewJson = "[]" };
                context.RoleView.Add(role);
                context.SaveChanges();
                roleId = role.Id;
            }
        }

        return new MyUserAdapterModel { Account = account, Name = account, Password = password, Status = true, RoleViewId = roleId, MustChangePassword = mustChange };
    }

    private static async Task<MyUserAdapterModel> EditModelAsync(MyUserService service, int id, string password = "", bool? mustChange = null)
    {
        var model = (await service.GetAsync(id)).Clone();
        model.Password = password;
        if (mustChange is { } flag)
        {
            model.MustChangePassword = flag;
        }

        return model;
    }

    private int AddUser(string account, string password, DateTime? changedAtUtc = null, bool status = true)
    {
        using var context = factory.CreateDbContext();
        var user = new MyUser
        {
            Account = account,
            Name = account,
            Password = password.Length == 0 ? string.Empty : SecurePasswordHasher.HashPassword(password),
            Status = status,
            PasswordChangedAtUtc = changedAtUtc,
        };
        context.MyUser.Add(user);
        context.SaveChanges();
        return user.Id;
    }

    private async Task<int> CreateUserWithPasswordsAsync(IPasswordPolicy policy, params string[] passwords)
    {
        int id;
        await using (var context = factory.CreateDbContext())
        {
            var user = new MyUser { Account = "h", Name = "h" };
            context.MyUser.Add(user);
            await policy.ApplyAsync(context, user, passwords[0], false);
            await context.SaveChangesAsync();
            id = user.Id;
        }

        foreach (var password in passwords.Skip(1))
        {
            time.Advance(TimeSpan.FromMinutes(1));
            await using var context = factory.CreateDbContext();
            var user = await context.MyUser.SingleAsync(x => x.Id == id);
            await policy.ApplyAsync(context, user, password, false);
            await context.SaveChangesAsync();
        }

        return id;
    }

    private async Task LockAsync(int id)
    {
        await using var context = factory.CreateDbContext();
        await context.MyUser.Where(x => x.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.AccessFailedCount, 5).SetProperty(x => x.LockoutEndUtc, NowUtc.AddMinutes(10)));
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

    private async Task<int> CountHistoryAsync(int id)
    {
        await using var context = factory.CreateDbContext();
        return await context.PasswordHistory.CountAsync(x => x.MyUserId == id);
    }

    private static string FindSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "CustomerHelpAgent");
            if (Directory.Exists(Path.Combine(candidate, "CustomerHelpAgent.Web")))
            {
                return candidate;
            }

            if (Directory.Exists(Path.Combine(dir.FullName, "CustomerHelpAgent.Web")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("找不到 CustomerHelpAgent.Web 所在的原始碼目錄。");
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

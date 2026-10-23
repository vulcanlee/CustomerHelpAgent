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

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 樂觀並行（0.9.93 起）。每個編輯路徑都要證明「偵測得到衝突」，而不只是「欄位存在」——
/// 「先載入再複製欄位」的寫法若忘了設定比對基準，欄位存在、測試看起來都正常，衝突卻永遠偵測不到。
///
/// 情境固定為：A、B 兩人同時開啟同一筆（拿到同一個版本號），A 先存成功，B 再存必須失敗，資料庫保留 A 的內容。
/// </summary>
public sealed class OptimisticConcurrencyTests
{
    [Fact]
    public async Task Category_SecondEditorWithStaleStamp_ShouldBeRejected()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CategoryService();
        Assert.True((await service.AddAsync(new CategoryAdapterModel { Name = "原名", IsEnabled = true })).Success);
        var opened = await fixture.Context.Category.AsNoTracking().SingleAsync();

        var first = await service.UpdateAsync(new CategoryAdapterModel { Id = opened.Id, Name = "A 改的", IsEnabled = true, ConcurrencyStamp = opened.ConcurrencyStamp });
        var second = await service.UpdateAsync(new CategoryAdapterModel { Id = opened.Id, Name = "B 改的", IsEnabled = true, ConcurrencyStamp = opened.ConcurrencyStamp });

        Assert.True(first.Success);
        AssertConflict(second);
        var saved = await fixture.Context.Category.AsNoTracking().SingleAsync();
        Assert.Equal("A 改的", saved.Name);
        Assert.NotEqual(opened.ConcurrencyStamp, saved.ConcurrencyStamp);
    }

    [Fact]
    public async Task Team_SecondEditorWithStaleStamp_ShouldBeRejected()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.TeamService();
        Assert.True((await service.AddAsync(new TeamAdapterModel { Name = "研發部", Code = "RD" })).Success);
        var opened = await fixture.Context.Team.AsNoTracking().SingleAsync();

        var first = await service.UpdateAsync(new TeamAdapterModel { Id = opened.Id, Name = "研發一部", Code = "RD", ConcurrencyStamp = opened.ConcurrencyStamp });
        var second = await service.UpdateAsync(new TeamAdapterModel { Id = opened.Id, Name = "研發二部", Code = "RD", ConcurrencyStamp = opened.ConcurrencyStamp });

        Assert.True(first.Success);
        AssertConflict(second);
        Assert.Equal("研發一部", (await fixture.Context.Team.AsNoTracking().SingleAsync()).Name);
    }

    [Fact]
    public async Task Project_SecondEditorWithStaleStamp_ShouldBeRejected()
    {
        // ProjectService 是「先載入（tracked）再逐欄複製」：最容易因為沒設比對基準而偵測不到衝突的寫法。
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.ProjectService();
        Assert.True((await service.AddAsync(NewProject(0, "原標題", ""))).Success);
        var opened = await fixture.Context.Project.AsNoTracking().SingleAsync();

        var first = await service.UpdateAsync(NewProject(opened.Id, "A 的標題", opened.ConcurrencyStamp));
        var second = await service.UpdateAsync(NewProject(opened.Id, "B 的標題", opened.ConcurrencyStamp));

        Assert.True(first.Success);
        AssertConflict(second);
        Assert.Equal("A 的標題", (await fixture.Context.Project.AsNoTracking().SingleAsync()).Title);
    }

    [Fact]
    public async Task RoleView_SecondEditorWithStaleStamp_ShouldBeRejected()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.RoleService();
        Assert.True((await service.AddAsync(new RoleViewAdapterModel { Name = "檢視員" })).Success);
        var opened = await fixture.Context.RoleView.AsNoTracking().SingleAsync();

        var first = await service.UpdateAsync(new RoleViewAdapterModel { Id = opened.Id, Name = "檢視員 A", ConcurrencyStamp = opened.ConcurrencyStamp });
        var second = await service.UpdateAsync(new RoleViewAdapterModel { Id = opened.Id, Name = "檢視員 B", ConcurrencyStamp = opened.ConcurrencyStamp });

        Assert.True(first.Success);
        AssertConflict(second);
        Assert.Equal("檢視員 A", (await fixture.Context.RoleView.AsNoTracking().SingleAsync()).Name);
    }

    [Fact]
    public async Task MyUser_SecondEditorWithStaleStamp_ShouldBeRejected()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.UserService();
        var opened = await fixture.AddUserAsync(service, "alice");

        var first = await service.UpdateAsync(EditUser(opened, "Alice A"));
        var second = await service.UpdateAsync(EditUser(opened, "Alice B"));

        Assert.True(first.Success);
        AssertConflict(second);
        Assert.Equal("Alice A", (await fixture.Context.MyUser.AsNoTracking().SingleAsync()).Name);
    }

    [Fact]
    public async Task MyUser_AdminEdit_ShouldKeepLockoutAndTwoFactorState()
    {
        // 0.9.92 之前整筆覆蓋：畫面模型沒有這些欄位，管理員只改姓名，被鎖定的帳號就解鎖了。
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.UserService();
        var opened = await fixture.AddUserAsync(service, "bob");
        var lockoutEnd = DateTime.UtcNow.AddMinutes(15);
        await fixture.Context.MyUser.Where(x => x.Id == opened.Id).ExecuteUpdateAsync(x => x
            .SetProperty(u => u.AccessFailedCount, 5)
            .SetProperty(u => u.LockoutEndUtc, lockoutEnd)
            .SetProperty(u => u.TwoFactorEnabled, true)
            .SetProperty(u => u.TwoFactorSecret, "SECRET"));

        Assert.True((await service.UpdateAsync(EditUser(opened, "Bob Renamed"))).Success);

        var saved = await fixture.Context.MyUser.AsNoTracking().SingleAsync();
        Assert.Equal("Bob Renamed", saved.Name);
        Assert.Equal(5, saved.AccessFailedCount);
        Assert.NotNull(saved.LockoutEndUtc);
        Assert.True(saved.TwoFactorEnabled);
        Assert.Equal("SECRET", saved.TwoFactorSecret);
    }

    [Fact]
    public async Task MyUser_AdminSettingNewPassword_ShouldUnlockAccount()
    {
        // 「被鎖住時請管理員改密碼」是操作說明教的解鎖方式：設定新密碼要解除鎖定，只改姓名則不動（見上一個測試）。
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.UserService();
        var opened = await fixture.AddUserAsync(service, "dave");
        await fixture.Context.MyUser.Where(x => x.Id == opened.Id).ExecuteUpdateAsync(x => x
            .SetProperty(u => u.AccessFailedCount, 5)
            .SetProperty(u => u.LockoutEndUtc, DateTime.UtcNow.AddMinutes(15)));

        var edit = EditUser(opened, "dave");
        edit.Password = "new-password-2";
        Assert.True((await service.UpdateAsync(edit)).Success);

        var saved = await fixture.Context.MyUser.AsNoTracking().SingleAsync();
        Assert.Equal(0, saved.AccessFailedCount);
        Assert.Null(saved.LockoutEndUtc);
    }

    [Fact]
    public async Task FailedLogin_ShouldNotChangeStamp_SoAdminEditIsNotAFalseConflict()
    {
        // 版本號只在編輯存檔時更換：使用者登入失敗（計數 +1）不該讓正在編輯他的管理員收到「已被其他人修改」。
        await using var fixture = await Fixture.CreateAsync();
        var userService = fixture.UserService();
        var opened = await fixture.AddUserAsync(userService, "carol");

        var (message, _) = await fixture.LoginService().LoginAsync("carol", "wrong-password");
        Assert.False(string.IsNullOrEmpty(message));

        var afterLogin = await fixture.Context.MyUser.AsNoTracking().SingleAsync();
        Assert.Equal(1, afterLogin.AccessFailedCount);
        Assert.Equal(opened.ConcurrencyStamp, afterLogin.ConcurrencyStamp);

        Assert.True((await userService.UpdateAsync(EditUser(opened, "Carol Renamed"))).Success);
        Assert.Equal(1, (await fixture.Context.MyUser.AsNoTracking().SingleAsync()).AccessFailedCount);
    }

    [Fact]
    public void EditableEntities_ShouldUseConcurrencyStampAsConcurrencyToken()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var context = new BackendDBContext(new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connection).Options);

        var stamped = context.Model.GetEntityTypes()
            .Where(x => typeof(IConcurrencyStamped).IsAssignableFrom(x.ClrType))
            .ToList();

        Assert.Equal(
            new[] { nameof(Category), nameof(MyUser), nameof(Project), nameof(RoleView), nameof(Team) },
            stamped.Select(x => x.ClrType.Name).Order().ToArray());
        Assert.All(stamped, entityType =>
            Assert.True(entityType.FindProperty(nameof(IConcurrencyStamped.ConcurrencyStamp))!.IsConcurrencyToken,
                $"{entityType.ClrType.Name}.ConcurrencyStamp 必須是 concurrency token，否則存檔時不會比對版本號。"));
    }

    private static void AssertConflict(VerifyRecordResult result)
    {
        Assert.False(result.Success, "拿舊版本號存檔應該被拒絕，實際卻成功了 —— 衝突沒有被偵測到。");
        Assert.Equal(ConcurrencyStampHelper.ConflictMessage, result.Message);
    }

    private static ProjectAdapterModel NewProject(int id, string title, string stamp) => new()
    {
        Id = id,
        Title = title,
        Owner = "owner",
        Status = "未開始",
        Priority = "中",
        StartDate = DateTime.Today,
        EndDate = DateTime.Today.AddDays(7),
        ConcurrencyStamp = stamp,
    };

    private static MyUserAdapterModel EditUser(MyUser opened, string name) => new()
    {
        Id = opened.Id,
        Account = opened.Account,
        Name = name,
        Status = true,
        ConcurrencyStamp = opened.ConcurrencyStamp,
    };

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly IMapper mapper;
        private readonly ILoggerFactory loggerFactory = LoggerFactory.Create(_ => { });
        private readonly CurrentUserService currentUser = new();

        private Fixture(SqliteConnection connection, BackendDBContext context)
        {
            this.connection = connection;
            Context = context;
            mapper = new MapperConfiguration(c => c.AddProfile<AutoMapping>(), loggerFactory).CreateMapper();
        }

        public BackendDBContext Context { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var context = new BackendDBContext(new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync();
            return new Fixture(connection, context);
        }

        public CategoryService CategoryService()
            => new(Factory(), mapper, NullLogger<CategoryService>.Instance, new FakeRecordAccessScopeProvider(true, []), new RecordingAuditLogService(), currentUser);

        public TeamService TeamService()
            => new(Factory(), mapper, NullLogger<TeamService>.Instance, new RecordingAuditLogService(), currentUser, new ContextTeamTreeCache(Context));

        public ProjectService ProjectService()
            => new(Factory(), mapper, NullLogger<ProjectService>.Instance, Options.Create(new SystemSettings()),
                new FakeRecordAccessScopeProvider(true, []), new RecordingAuditLogService(), currentUser,
                new ProjectFileStore(Options.Create(new SystemSettings()), NullLogger<ProjectFileStore>.Instance));

        public RoleViewService RoleService()
            => new(Factory(), mapper, NullLogger<RoleViewService>.Instance, new RolePermissionService(),
                new RbacWriteService(Context, NullLogger<RbacWriteService>.Instance), new RecordingAuditLogService(), currentUser);

        public MyUserService UserService()
            => new(Factory(), mapper, NullLogger<MyUserService>.Instance,
                new RbacWriteService(Context, NullLogger<RbacWriteService>.Instance), new RecordingAuditLogService(), currentUser, Options.Create(new BootstrapSettings()), PasswordTestDefaults.Policy());

        public MyUserServiceLogin LoginService()
            => new(Context, mapper, new ConfigurationBuilder().Build(), NullLogger<MyUserServiceLogin>.Instance,
                new RolePermissionService(), new RecordingAuditLogService(), PasswordTestDefaults.Lockout(), new RecordingNotificationSender(), TimeProvider.System,
                TwoFactorTestDefaults.Service(Factory()));

        public async Task<MyUser> AddUserAsync(MyUserService service, string account)
        {
            Assert.True((await service.AddAsync(new MyUserAdapterModel { Account = account, Name = account, Password = "correct-password-1", Status = true })).Success);
            return await Context.MyUser.AsNoTracking().SingleAsync(x => x.Account == account);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
            loggerFactory.Dispose();
        }

        private TestDbContextFactory Factory() => new(connection);
    }
}

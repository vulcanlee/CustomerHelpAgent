using AutoMapper;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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
/// 清單上點狀態標籤切換啟用／停用（0.9.120 起）：只改狀態、換版本號、寫對應的稽核；
/// 使用者另外要換工作階段版本、不能清掉角色與團隊、不能停用自己與 support。
/// </summary>
public sealed class StatusToggleServiceTests
{
    [Fact]
    public async Task Team_SetEnabled_ShouldOnlyChangeStatus_BumpStamp_AndAudit()
    {
        await using var fixture = await StatusToggleFixture.CreateAsync();
        var team = await fixture.AddTeamAsync("研發部");
        var service = fixture.CreateTeamService();

        Assert.True((await service.SetEnabledAsync(team.Id, false)).Success);
        var disabled = await fixture.Context.Team.AsNoTracking().SingleAsync(x => x.Id == team.Id);
        Assert.False(disabled.IsEnabled);
        Assert.Equal("研發部", disabled.Name);
        Assert.NotEqual(team.ConcurrencyStamp, disabled.ConcurrencyStamp);

        Assert.True((await service.SetEnabledAsync(team.Id, true)).Success);
        Assert.True((await fixture.Context.Team.AsNoTracking().SingleAsync(x => x.Id == team.Id)).IsEnabled);

        Assert.Equal([AuditActions.Team.Disable, AuditActions.Team.Enable], fixture.Audit.Entries.Select(x => x.Action));
        Assert.Equal("name=研發部", fixture.Audit.Entries[0].Detail);
    }

    [Fact]
    public async Task Team_SetEnabled_WhenAlreadyInTargetState_ShouldSucceedWithoutAudit()
    {
        await using var fixture = await StatusToggleFixture.CreateAsync();
        var team = await fixture.AddTeamAsync("研發部");
        var service = fixture.CreateTeamService();

        // 參數是目標值：重複點（例如兩個分頁各點一次）不會反轉回去。
        Assert.True((await service.SetEnabledAsync(team.Id, true)).Success);

        Assert.Equal(team.ConcurrencyStamp, (await fixture.Context.Team.AsNoTracking().SingleAsync(x => x.Id == team.Id)).ConcurrencyStamp);
        Assert.Empty(fixture.Audit.Entries);
    }

    [Fact]
    public async Task Team_SetEnabled_ThenSavingAStaleEditForm_ShouldConflict()
    {
        await using var fixture = await StatusToggleFixture.CreateAsync();
        var team = await fixture.AddTeamAsync("研發部");
        var service = fixture.CreateTeamService();

        // 別人先開了修改窗（拿到舊版本號），這時清單上停用；他存檔時不能把狀態蓋回「啟用」。
        var staleForm = new TeamAdapterModel { Id = team.Id, Name = "研發部", IsEnabled = true, ConcurrencyStamp = team.ConcurrencyStamp };
        Assert.True((await service.SetEnabledAsync(team.Id, false)).Success);

        var result = await service.UpdateAsync(staleForm);

        Assert.False(result.Success);
        Assert.Equal(ConcurrencyStampHelper.ConflictMessage, result.Message);
        Assert.False((await fixture.Context.Team.AsNoTracking().SingleAsync(x => x.Id == team.Id)).IsEnabled);
    }

    [Fact]
    public async Task Team_SetEnabled_WhenDeletedOrMissing_ShouldFail()
    {
        await using var fixture = await StatusToggleFixture.CreateAsync();
        var team = await fixture.AddTeamAsync("研發部");
        var service = fixture.CreateTeamService();
        Assert.True((await service.DeleteAsync(team.Id)).Success);
        fixture.Audit.Entries.Clear();

        Assert.False((await service.SetEnabledAsync(team.Id, false)).Success);
        Assert.False((await service.SetEnabledAsync(9999, false)).Success);
        Assert.Empty(fixture.Audit.Entries);
    }

    [Fact]
    public async Task Category_SetEnabled_ShouldChangeStatusAndAudit()
    {
        await using var fixture = await StatusToggleFixture.CreateAsync();
        var category = await fixture.AddCategoryAsync("技術文件", teams: null);
        var service = fixture.CreateCategoryService(isAdmin: true);

        Assert.True((await service.SetEnabledAsync(category.Id, false)).Success);

        var saved = await fixture.Context.Category.AsNoTracking().SingleAsync(x => x.Id == category.Id);
        Assert.False(saved.IsEnabled);
        Assert.NotEqual(category.ConcurrencyStamp, saved.ConcurrencyStamp);
        Assert.Equal([AuditActions.Category.Disable], fixture.Audit.Entries.Select(x => x.Action));
    }

    [Fact]
    public async Task Category_SetEnabled_OutsideTeamScope_ShouldBeDenied()
    {
        await using var fixture = await StatusToggleFixture.CreateAsync();
        var category = await fixture.AddCategoryAsync("團隊B分類", teams: ["團隊B"]);
        var service = fixture.CreateCategoryService(isAdmin: false, "團隊A");

        var result = await service.SetEnabledAsync(category.Id, false);

        Assert.False(result.Success);
        Assert.True((await fixture.Context.Category.AsNoTracking().SingleAsync(x => x.Id == category.Id)).IsEnabled);
        Assert.Empty(fixture.Audit.Entries);
    }

    [Fact]
    public async Task User_SetStatus_ShouldKeepRolesAndTeams_RotateStamps_AndAudit()
    {
        await using var fixture = await StatusToggleFixture.CreateAsync();
        var user = await fixture.AddUserWithAssignmentsAsync("bob");
        var service = fixture.CreateUserService(currentUserId: 0);

        Assert.True((await service.SetStatusAsync(user.Id, false)).Success);

        var saved = await fixture.Context.MyUser.AsNoTracking().SingleAsync(x => x.Id == user.Id);
        Assert.False(saved.Status);
        Assert.NotEqual(user.SecurityStamp, saved.SecurityStamp);
        Assert.NotEqual(user.ConcurrencyStamp, saved.ConcurrencyStamp);

        // ⚠️ 用清單列呼叫 UpdateAsync 會把額外角色與團隊清掉；只改狀態的路徑不可以。
        var (additionalRoleIds, teamNames) = await service.GetUserAssignmentsAsync(user.Id);
        Assert.Single(additionalRoleIds);
        Assert.Equal(["團隊A"], teamNames);

        Assert.True((await service.SetStatusAsync(user.Id, true)).Success);
        Assert.True((await fixture.Context.MyUser.AsNoTracking().SingleAsync(x => x.Id == user.Id)).Status);
        Assert.Equal([AuditActions.User.Disable, AuditActions.User.Enable], fixture.Audit.Entries.Select(x => x.Action));
        Assert.Equal("account=bob", fixture.Audit.Entries[0].Detail);
    }

    [Fact]
    public async Task User_SetStatus_ShouldNotDisableSupportOrSelf()
    {
        await using var fixture = await StatusToggleFixture.CreateAsync();
        var support = await fixture.AddUserWithAssignmentsAsync("SUPPORT");
        var self = await fixture.AddUserWithAssignmentsAsync("alice");
        var service = fixture.CreateUserService(currentUserId: self.Id);

        Assert.False((await service.SetStatusAsync(support.Id, false)).Success);
        Assert.False((await service.SetStatusAsync(self.Id, false)).Success);
        Assert.True(await fixture.Context.MyUser.AsNoTracking().AllAsync(x => x.Status));
        Assert.Empty(fixture.Audit.Entries);

        Assert.False(service.CanToggleStatus(new MyUserAdapterModel { Id = support.Id, Account = "SUPPORT", Status = true }));
        Assert.False(service.CanToggleStatus(new MyUserAdapterModel { Id = self.Id, Account = "alice", Status = true }));
        // 已停用的 support 仍可以點一下重新啟用（救援）。
        Assert.True(service.CanToggleStatus(new MyUserAdapterModel { Id = support.Id, Account = "SUPPORT", Status = false }));
    }

    [Fact]
    public async Task User_UpdateAsync_ShouldNotDisableSupportOrSelf()
    {
        await using var fixture = await StatusToggleFixture.CreateAsync();
        var support = await fixture.AddUserWithAssignmentsAsync("support");
        var self = await fixture.AddUserWithAssignmentsAsync("alice");
        var service = fixture.CreateUserService(currentUserId: self.Id);

        foreach (var target in new[] { support, self })
        {
            var form = (await service.GetAsync(target.Id)).Clone();
            (form.AdditionalRoleIds, form.TeamNames) = await service.GetUserAssignmentsAsync(target.Id);
            form.Status = false;

            var result = await service.UpdateAsync(form);

            Assert.False(result.Success);
            Assert.True((await fixture.Context.MyUser.AsNoTracking().SingleAsync(x => x.Id == target.Id)).Status);
        }
    }

    private sealed class StatusToggleFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly IMapper mapper;
        private readonly ILoggerFactory loggerFactory;

        private StatusToggleFixture(SqliteConnection connection, BackendDBContext context)
        {
            this.connection = connection;
            Context = context;
            loggerFactory = LoggerFactory.Create(_ => { });
            mapper = new MapperConfiguration(configuration => configuration.AddProfile<AutoMapping>(), loggerFactory).CreateMapper();
        }

        public BackendDBContext Context { get; }

        public RecordingAuditLogService Audit { get; } = new();

        public static async Task<StatusToggleFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var context = new BackendDBContext(new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync();
            return new StatusToggleFixture(connection, context);
        }

        public TeamService CreateTeamService()
            => new(new TestDbContextFactory(connection), mapper, loggerFactory.CreateLogger<TeamService>(), Audit, new CurrentUserService(), new ContextTeamTreeCache(Context));

        public CategoryService CreateCategoryService(bool isAdmin, params string[] teams)
            => new(new TestDbContextFactory(connection), mapper, loggerFactory.CreateLogger<CategoryService>(),
                new FakeRecordAccessScopeProvider(isAdmin, teams), Audit, new CurrentUserService());

        public MyUserService CreateUserService(int currentUserId)
        {
            var currentUser = new CurrentUserService();
            if (currentUserId > 0)
            {
                currentUser.CurrentUser = new CustomerHelpAgent.Models.Others.CurrentUser { Id = currentUserId, Account = "alice" };
            }

            return new(new TestDbContextFactory(connection), mapper, loggerFactory.CreateLogger<MyUserService>(),
                new RbacWriteService(Context, NullLogger<RbacWriteService>.Instance), Audit, currentUser,
                Options.Create(new BootstrapSettings { SupportAccount = "support" }), PasswordTestDefaults.Policy());
        }

        public async Task<Team> AddTeamAsync(string name)
        {
            var team = new Team { Name = name, IsEnabled = true };
            Context.Team.Add(team);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
            return team;
        }

        public async Task<Category> AddCategoryAsync(string name, IEnumerable<string>? teams)
        {
            var category = new Category { Name = name, IsEnabled = true, Teams = TagStringHelper.ToStored(teams) };
            Context.Category.Add(category);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
            return category;
        }

        /// <summary>建立一位有主要角色、一個額外角色與一個團隊的使用者（經 AddAsync，關聯表與正式路徑一致）。</summary>
        public async Task<MyUser> AddUserWithAssignmentsAsync(string account)
        {
            var main = new RoleView { Name = $"{account}-主要", TabViewJson = "[]" };
            var extra = new RoleView { Name = $"{account}-額外", TabViewJson = "[]" };
            Context.RoleView.AddRange(main, extra);
            if (!await Context.Team.AnyAsync(x => x.Name == "團隊A"))
            {
                Context.Team.Add(new Team { Name = "團隊A", IsEnabled = true });
            }

            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();

            var result = await CreateUserService(currentUserId: 0).AddAsync(new MyUserAdapterModel
            {
                Account = account,
                Name = account,
                Password = "Passw0rd",
                Status = true,
                RoleViewId = main.Id,
                AdditionalRoleIds = [extra.Id],
                TeamNames = ["團隊A"],
            });
            Assert.True(result.Success, result.Message);
            Audit.Entries.Clear();
            return await Context.MyUser.AsNoTracking().SingleAsync(x => x.Account == account);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}

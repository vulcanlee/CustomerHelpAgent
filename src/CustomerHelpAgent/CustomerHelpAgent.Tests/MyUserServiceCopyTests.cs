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
/// 使用者管理「複製」（0.9.127 起）：以現有使用者為範本建立新帳號。
/// 帶入角色、額外角色、團隊、狀態與管理員身分；帳號、名稱、密碼、Email 一律留空；support 不可當範本。
/// </summary>
public sealed class MyUserServiceCopyTests
{
    [Fact]
    public async Task BuildCopyDraftAsync_ShouldCarryRolesTeamsStatusAndAdmin_AndBlankPersonalFields()
    {
        await using var fixture = await CopyFixture.CreateAsync();
        var roleA = await fixture.AddRoleAsync("角色A");
        var roleB = await fixture.AddRoleAsync("角色B");
        await fixture.AddTeamAsync("團隊A");
        var service = fixture.CreateService();
        var sourceId = await fixture.AddUserAsync(service, "alice", roleA.Id, [roleB.Id], ["團隊A"], status: false, isAdmin: true);

        var copy = await service.BuildCopyDraftAsync(sourceId);

        Assert.NotNull(copy);
        var draft = copy.Value.Draft;
        Assert.Equal(0, draft.Id);
        Assert.Equal(string.Empty, draft.Account);
        Assert.Equal(string.Empty, draft.Name);
        Assert.Equal(string.Empty, draft.Password);
        Assert.True(string.IsNullOrEmpty(draft.Email));
        Assert.Equal(roleA.Id, draft.RoleViewId);
        Assert.Equal([roleB.Id], draft.AdditionalRoleIds);
        Assert.Equal(["團隊A"], draft.TeamNames);
        Assert.False(draft.Status);
        Assert.True(draft.IsAdmin);
        Assert.True(draft.MustChangePassword);
        Assert.Empty(copy.Value.SkippedTeamNames);
    }

    [Fact]
    public async Task BuildCopyDraftAsync_ShouldSkipDisabledTeams()
    {
        await using var fixture = await CopyFixture.CreateAsync();
        var role = await fixture.AddRoleAsync("角色A");
        await fixture.AddTeamAsync("團隊A");
        var teamB = await fixture.AddTeamAsync("團隊B");
        var service = fixture.CreateService();
        var sourceId = await fixture.AddUserAsync(service, "alice", role.Id, [], ["團隊A", "團隊B"]);
        teamB.IsEnabled = false;
        fixture.Context.Team.Update(teamB);
        await fixture.Context.SaveChangesAsync();
        fixture.Context.ChangeTracker.Clear();

        var copy = await service.BuildCopyDraftAsync(sourceId);

        Assert.NotNull(copy);
        Assert.Equal(["團隊A"], copy.Value.Draft.TeamNames);
        Assert.Equal(["團隊B"], copy.Value.SkippedTeamNames);
    }

    [Fact]
    public async Task BuildCopyDraftAsync_ShouldRejectSupportAndMissingSource()
    {
        await using var fixture = await CopyFixture.CreateAsync();
        var role = await fixture.AddRoleAsync("角色A");
        var service = fixture.CreateService();
        var supportId = await fixture.AddUserAsync(service, "Support", role.Id, [], []);

        Assert.Null(await service.BuildCopyDraftAsync(supportId));
        Assert.Null(await service.BuildCopyDraftAsync(9999));
    }

    [Fact]
    public void CanCopy_ShouldExcludeSupportOnly()
    {
        var service = new MyUserService(null!, null!, NullLogger<MyUserService>.Instance, null!, null!, new CurrentUserService(),
            Options.Create(new BootstrapSettings()), PasswordTestDefaults.Policy());

        Assert.False(service.CanCopy(new MyUserAdapterModel { Account = "SUPPORT" }));
        Assert.True(service.CanCopy(new MyUserAdapterModel { Account = "alice" }));
    }

    [Fact]
    public async Task GetAccountsUsingEmailAsync_ShouldIgnoreCaseAndSpaces_AndSkipDeleted()
    {
        await using var fixture = await CopyFixture.CreateAsync();
        var role = await fixture.AddRoleAsync("角色A");
        var service = fixture.CreateService();
        await fixture.AddUserAsync(service, "alice", role.Id, [], [], email: "Alice@Example.com");
        var deletedId = await fixture.AddUserAsync(service, "old", role.Id, [], [], email: "alice@example.com");
        var deleted = await fixture.Context.MyUser.SingleAsync(x => x.Id == deletedId);
        deleted.IsDeleted = true;
        await fixture.Context.SaveChangesAsync();
        fixture.Context.ChangeTracker.Clear();

        Assert.Equal(["alice"], await service.GetAccountsUsingEmailAsync("  alice@EXAMPLE.com "));
        Assert.Empty(await service.GetAccountsUsingEmailAsync("bob@example.com"));
        Assert.Empty(await service.GetAccountsUsingEmailAsync("   "));
    }

    [Fact]
    public async Task AddAsync_FromCopyDraft_ShouldPersistSameAssignments_AndAuditTheSource()
    {
        await using var fixture = await CopyFixture.CreateAsync();
        var roleA = await fixture.AddRoleAsync("角色A");
        var roleB = await fixture.AddRoleAsync("角色B");
        var team = await fixture.AddTeamAsync("團隊A");
        var service = fixture.CreateService();
        var sourceId = await fixture.AddUserAsync(service, "alice", roleA.Id, [roleB.Id], ["團隊A"]);

        var draft = (await service.BuildCopyDraftAsync(sourceId))!.Value.Draft;
        draft.Account = "bob";
        draft.Name = "Bob";
        draft.Password = "Passw0rd";
        var result = await service.AddAsync(draft, copiedFromAccount: "alice");

        Assert.True(result.Success);
        var bob = await fixture.Context.MyUser.AsNoTracking().SingleAsync(x => x.Account == "bob");
        Assert.True(bob.MustChangePassword);
        var roleIds = await fixture.Context.UserRole.AsNoTracking().Where(x => x.MyUserId == bob.Id).Select(x => x.RoleViewId).ToListAsync();
        Assert.Equal(new[] { roleA.Id, roleB.Id }.Order(), roleIds.Order());
        var teamIds = await fixture.Context.UserTeam.AsNoTracking().Where(x => x.MyUserId == bob.Id).Select(x => x.TeamId).ToListAsync();
        Assert.Equal([team.Id], teamIds);

        var audit = await fixture.Context.AuditLog.AsNoTracking()
            .Where(x => x.Action == AuditActions.User.Create && x.TargetId == bob.Id.ToString())
            .SingleAsync();
        Assert.Contains("copiedFrom=alice", audit.Detail, StringComparison.Ordinal);
    }

    private sealed class CopyFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly IMapper mapper;
        private readonly ILoggerFactory loggerFactory;

        private CopyFixture(SqliteConnection connection, BackendDBContext context)
        {
            this.connection = connection;
            Context = context;
            loggerFactory = LoggerFactory.Create(_ => { });
            mapper = new MapperConfiguration(c => c.AddProfile<AutoMapping>(), loggerFactory).CreateMapper();
        }

        public BackendDBContext Context { get; }

        public static async Task<CopyFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connection).Options;
            var context = new BackendDBContext(options);
            await context.Database.EnsureCreatedAsync();
            return new CopyFixture(connection, context);
        }

        public MyUserService CreateService()
            => new(new TestDbContextFactory(connection), mapper, loggerFactory.CreateLogger<MyUserService>(), new RbacWriteService(Context, NullLogger<RbacWriteService>.Instance),
                new AuditLogService(new TestDbContextFactory(connection), loggerFactory.CreateLogger<AuditLogService>()), new CurrentUserService(), Options.Create(new BootstrapSettings()), PasswordTestDefaults.Policy());

        public async Task<RoleView> AddRoleAsync(string name)
        {
            var role = new RoleView { Name = name, TabViewJson = "[]" };
            Context.RoleView.Add(role);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
            return role;
        }

        public async Task<Team> AddTeamAsync(string name)
        {
            var team = new Team { Name = name, IsEnabled = true };
            Context.Team.Add(team);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
            return team;
        }

        public async Task<int> AddUserAsync(MyUserService service, string account, int roleId, List<int> additionalRoleIds, List<string> teamNames,
            bool status = true, bool isAdmin = false, string? email = null)
        {
            var result = await service.AddAsync(new MyUserAdapterModel
            {
                Account = account,
                Name = account,
                Password = "Passw0rd",
                Email = email,
                Status = status,
                IsAdmin = isAdmin,
                RoleViewId = roleId,
                AdditionalRoleIds = additionalRoleIds,
                TeamNames = teamNames,
            });
            Assert.True(result.Success, result.Message);
            Context.ChangeTracker.Clear();
            return await Context.MyUser.AsNoTracking().Where(x => x.Account == account).Select(x => x.Id).SingleAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
            loggerFactory.Dispose();
        }
    }
}

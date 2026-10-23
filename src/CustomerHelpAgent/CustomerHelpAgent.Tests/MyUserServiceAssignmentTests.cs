using AutoMapper;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.AdapterModel;
using CustomerHelpAgent.Models.Systems;
using Microsoft.Extensions.Logging.Abstractions;

namespace CustomerHelpAgent.Tests;

public sealed class MyUserServiceAssignmentTests
{
    [Fact]
    public async Task AddAsync_WithMultipleRolesAndTeams_ShouldPersistUserRoleAndUserTeam()
    {
        await using var fixture = await MyUserServiceAssignmentFixture.CreateAsync();
        var roleA = await fixture.AddRoleAsync("角色A");
        var roleB = await fixture.AddRoleAsync("角色B");
        var teamA = await fixture.AddTeamAsync("團隊A");
        var service = fixture.CreateService();

        var result = await service.AddAsync(new MyUserAdapterModel
        {
            Account = "multi",
            Name = "multi",
            Password = "Passw0rd",
            Status = true,
            RoleViewId = roleA.Id,
            AdditionalRoleIds = new List<int> { roleB.Id },
            TeamNames = new List<string> { "團隊A" },
        });

        Assert.True(result.Success);
        var user = await fixture.Context.MyUser.AsNoTracking().SingleAsync(x => x.Account == "multi");

        var roleIds = await fixture.Context.UserRole.AsNoTracking()
            .Where(x => x.MyUserId == user.Id).Select(x => x.RoleViewId).ToListAsync();
        Assert.Contains(roleA.Id, roleIds);
        Assert.Contains(roleB.Id, roleIds);

        var teamIds = await fixture.Context.UserTeam.AsNoTracking()
            .Where(x => x.MyUserId == user.Id).Select(x => x.TeamId).ToListAsync();
        Assert.Contains(teamA.Id, teamIds);

        var (additionalRoleIds, teamNames) = await service.GetUserAssignmentsAsync(user.Id);
        Assert.Contains(roleB.Id, additionalRoleIds);
        Assert.DoesNotContain(roleA.Id, additionalRoleIds); // 主要角色不列入額外角色
        Assert.Contains("團隊A", teamNames);
    }

    /// <summary>
    /// 修改使用者的差異（0.9.131 起）：密碼只記「已變更」、絕不寫值；額外角色與團隊列出新增與移除。
    /// </summary>
    [Fact]
    public async Task UpdateAsync_ShouldAuditADiff_WithPasswordMasked_AndAssignmentChanges()
    {
        await using var fixture = await MyUserServiceAssignmentFixture.CreateAsync();
        var roleA = await fixture.AddRoleAsync("角色A");
        var roleB = await fixture.AddRoleAsync("角色B");
        await fixture.AddTeamAsync("團隊A");
        await fixture.AddTeamAsync("團隊B");
        var audit = new RecordingAuditLogService();
        var service = fixture.CreateService(audit);
        Assert.True((await service.AddAsync(new MyUserAdapterModel
        {
            Account = "diff",
            Name = "舊姓名",
            Password = "Passw0rd",
            Status = true,
            RoleViewId = roleA.Id,
            AdditionalRoleIds = [roleB.Id],
            TeamNames = ["團隊A"],
        })).Success);
        var userId = (await fixture.Context.MyUser.AsNoTracking().SingleAsync(x => x.Account == "diff")).Id;
        var edit = (await service.GetAsync(userId)).Clone();
        edit.Name = "新姓名";
        edit.Password = "N3wPassw0rd";
        edit.AdditionalRoleIds = [];
        edit.TeamNames = ["團隊B"];

        Assert.True((await service.UpdateAsync(edit)).Success);

        var content = audit.Contents.Single(x => x.Action == "User.Update").Content;
        Assert.Contains("Name: 舊姓名 → 新姓名", content, StringComparison.Ordinal);
        Assert.Contains("Password: 已變更（內容不記錄）", content, StringComparison.Ordinal);
        Assert.Contains($"AdditionalRoleIds: 移除 [{roleB.Id}]", content, StringComparison.Ordinal);
        Assert.Contains("Teams: 新增 [團隊B]；移除 [團隊A]", content, StringComparison.Ordinal);
        Assert.DoesNotContain("N3wPassw0rd", content, StringComparison.Ordinal);
        Assert.DoesNotContain("SecurityStamp", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Salt", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddAsync_WhenAssignmentSyncFails_ShouldNotLeaveAUserWithoutRolesOrTeams()
    {
        // 0.9.136 起主表與角色、團隊對應在同一個交易：同步失敗時整筆回復，不會留下只存了一半的帳號。
        await using var fixture = await MyUserServiceAssignmentFixture.CreateAsync();
        var role = await fixture.AddRoleAsync("角色A");
        await fixture.AddTeamAsync("團隊A");
        var service = fixture.CreateService(failTeamSync: true);

        var result = await service.AddAsync(new MyUserAdapterModel
        {
            Account = "half",
            Name = "half",
            Password = "Passw0rd",
            Status = true,
            RoleViewId = role.Id,
            TeamNames = new List<string> { "團隊A" },
        });

        Assert.False(result.Success);
        Assert.False(await fixture.Context.MyUser.AsNoTracking().AnyAsync(x => x.Account == "half"));
        Assert.False(await fixture.Context.UserRole.AsNoTracking().AnyAsync());
    }

    [Fact]
    public async Task UpdateAsync_WhenAssignmentSyncFails_ShouldKeepTheUserAndRolesUnchanged()
    {
        await using var fixture = await MyUserServiceAssignmentFixture.CreateAsync();
        var roleA = await fixture.AddRoleAsync("角色A");
        var roleB = await fixture.AddRoleAsync("角色B");
        Assert.True((await fixture.CreateService().AddAsync(new MyUserAdapterModel
        {
            Account = "carol",
            Name = "原本的名字",
            Password = "Passw0rd",
            Status = true,
            RoleViewId = roleA.Id,
        })).Success);
        var user = await fixture.Context.MyUser.AsNoTracking().SingleAsync(x => x.Account == "carol");
        var service = fixture.CreateService(failTeamSync: true);
        var model = await service.GetAsync(user.Id);
        model.Name = "改過的名字";
        model.AdditionalRoleIds = new List<int> { roleB.Id };

        var result = await service.UpdateAsync(model);

        Assert.False(result.Success);
        var reloaded = await fixture.Context.MyUser.AsNoTracking().SingleAsync(x => x.Id == user.Id);
        Assert.Equal("原本的名字", reloaded.Name);
        Assert.Equal(user.ConcurrencyStamp, reloaded.ConcurrencyStamp);
        var roleIds = await fixture.Context.UserRole.AsNoTracking().Where(x => x.MyUserId == user.Id).Select(x => x.RoleViewId).ToListAsync();
        Assert.DoesNotContain(roleB.Id, roleIds);
    }

    /// <summary>角色照常同步，到了團隊同步時丟例外，模擬雙寫做到一半失敗。</summary>
    private sealed class FailingTeamSyncRbacWriteService(IRbacWriteService inner) : IRbacWriteService
    {
        public Task SyncRolePermissionsAsync(int roleViewId, IEnumerable<string> permissionKeys) => inner.SyncRolePermissionsAsync(roleViewId, permissionKeys);
        public Task SyncRolePermissionsAsync(BackendDBContext context, int roleViewId, IEnumerable<string> permissionKeys) => inner.SyncRolePermissionsAsync(context, roleViewId, permissionKeys);
        public Task SyncUserRolesAsync(int userId, IEnumerable<int> roleViewIds) => inner.SyncUserRolesAsync(userId, roleViewIds);
        public Task SyncUserRolesAsync(BackendDBContext context, int userId, IEnumerable<int> roleViewIds) => inner.SyncUserRolesAsync(context, userId, roleViewIds);
        public Task SyncUserTeamsAsync(int userId, IEnumerable<int> teamIds) => throw new InvalidOperationException("模擬團隊同步失敗");
        public Task SyncUserTeamsAsync(BackendDBContext context, int userId, IEnumerable<int> teamIds) => throw new InvalidOperationException("模擬團隊同步失敗");
    }

    private sealed class MyUserServiceAssignmentFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly IMapper mapper;
        private readonly ILoggerFactory loggerFactory;

        private MyUserServiceAssignmentFixture(SqliteConnection connection, BackendDBContext context)
        {
            this.connection = connection;
            Context = context;
            loggerFactory = LoggerFactory.Create(_ => { });
            var mapperConfiguration = new MapperConfiguration(
                configuration => configuration.AddProfile<AutoMapping>(),
                loggerFactory);
            mapper = mapperConfiguration.CreateMapper();
        }

        public BackendDBContext Context { get; }

        public static async Task<MyUserServiceAssignmentFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connection).Options;
            var context = new BackendDBContext(options);
            await context.Database.EnsureCreatedAsync();
            return new MyUserServiceAssignmentFixture(connection, context);
        }

        public MyUserService CreateService(IAuditLogService? auditLogService = null, bool failTeamSync = false)
        {
            IRbacWriteService rbac = new RbacWriteService(Context, NullLogger<RbacWriteService>.Instance);
            return new(new TestDbContextFactory(connection), mapper, loggerFactory.CreateLogger<MyUserService>(), failTeamSync ? new FailingTeamSyncRbacWriteService(rbac) : rbac,
                auditLogService ?? new AuditLogService(new TestDbContextFactory(connection), loggerFactory.CreateLogger<AuditLogService>()), new CurrentUserService(), Options.Create(new BootstrapSettings()), PasswordTestDefaults.Policy());
        }

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

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
            loggerFactory.Dispose();
        }
    }
}

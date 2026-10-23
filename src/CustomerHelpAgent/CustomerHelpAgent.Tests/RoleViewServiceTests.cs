using AutoMapper;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.AdapterModel;
using CustomerHelpAgent.Models.Others;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Helpers;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 角色存檔（0.9.136 起）：主表與權限對應在同一個交易；「預設角色」不可改名。
/// </summary>
public sealed class RoleViewServiceTests
{
    [Fact]
    public async Task AddAsync_WhenPermissionSyncFails_ShouldNotCreateTheRole()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.Service(failPermissionSync: true);

        var result = await service.AddAsync(fixture.NewModel("業務角色"));

        Assert.False(result.Success);
        Assert.False(await fixture.Context.RoleView.AsNoTracking().AnyAsync(x => x.Name == "業務角色"));
    }

    [Fact]
    public async Task UpdateAsync_WhenPermissionSyncFails_ShouldKeepTheRoleUnchanged()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.True((await fixture.Service().AddAsync(fixture.NewModel("業務角色"))).Success);
        var role = await fixture.Context.RoleView.AsNoTracking().SingleAsync(x => x.Name == "業務角色");
        var service = fixture.Service(failPermissionSync: true);
        var model = await service.GetAsync(role.Id);
        model.Name = "改過的名稱";

        var result = await service.UpdateAsync(model);

        Assert.False(result.Success);
        var reloaded = await fixture.Context.RoleView.AsNoTracking().SingleAsync(x => x.Id == role.Id);
        Assert.Equal("業務角色", reloaded.Name);
        Assert.Equal(role.ConcurrencyStamp, reloaded.ConcurrencyStamp);
    }

    [Fact]
    public async Task DefaultRole_Rename_ShouldBeRejected_AndKeepTheVersionStamp()
    {
        // 系統以名稱找「預設角色」，改名後啟動時會重建一個帶全部權限的新「預設角色」。
        await using var fixture = await Fixture.CreateAsync();
        var roleId = await fixture.AddRoleDirectlyAsync(MagicObjectHelper.預設角色);
        var service = fixture.Service();
        var model = await service.GetAsync(roleId);
        var stamp = model.ConcurrencyStamp;
        model.Name = "一般同事";

        var check = await service.BeforeUpdateCheckAsync(model);
        var result = await service.UpdateAsync(model);

        Assert.False(check.Success);
        Assert.Contains("不可改名", check.Message);
        Assert.False(result.Success);
        var reloaded = await fixture.Context.RoleView.AsNoTracking().SingleAsync(x => x.Id == roleId);
        Assert.Equal(MagicObjectHelper.預設角色, reloaded.Name);
        Assert.Equal(stamp, reloaded.ConcurrencyStamp);
    }

    [Fact]
    public async Task DefaultRole_ChangingOtherFields_ShouldStillWork()
    {
        await using var fixture = await Fixture.CreateAsync();
        var roleId = await fixture.AddRoleDirectlyAsync(MagicObjectHelper.預設角色);
        var service = fixture.Service();
        var model = await service.GetAsync(roleId);
        model.RequireTwoFactor = true;

        Assert.True((await service.BeforeUpdateCheckAsync(model)).Success);
        Assert.True((await service.UpdateAsync(model)).Success);
        Assert.True((await fixture.Context.RoleView.AsNoTracking().SingleAsync(x => x.Id == roleId)).RequireTwoFactor);
    }

    /// <summary>權限對應寫到一半丟例外，模擬雙寫失敗。</summary>
    private sealed class FailingPermissionSyncRbacWriteService(IRbacWriteService inner) : IRbacWriteService
    {
        public Task SyncRolePermissionsAsync(int roleViewId, IEnumerable<string> permissionKeys) => throw new InvalidOperationException("模擬權限同步失敗");
        public Task SyncRolePermissionsAsync(BackendDBContext context, int roleViewId, IEnumerable<string> permissionKeys) => throw new InvalidOperationException("模擬權限同步失敗");
        public Task SyncUserRolesAsync(int userId, IEnumerable<int> roleViewIds) => inner.SyncUserRolesAsync(userId, roleViewIds);
        public Task SyncUserRolesAsync(BackendDBContext context, int userId, IEnumerable<int> roleViewIds) => inner.SyncUserRolesAsync(context, userId, roleViewIds);
        public Task SyncUserTeamsAsync(int userId, IEnumerable<int> teamIds) => inner.SyncUserTeamsAsync(userId, teamIds);
        public Task SyncUserTeamsAsync(BackendDBContext context, int userId, IEnumerable<int> teamIds) => inner.SyncUserTeamsAsync(context, userId, teamIds);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly IMapper mapper;
        private readonly ILoggerFactory loggerFactory = LoggerFactory.Create(_ => { });
        private readonly CurrentUserService currentUser = new();
        private readonly RolePermissionService rolePermissionService = new();

        private Fixture(SqliteConnection connection, BackendDBContext context)
        {
            this.connection = connection;
            Context = context;
            mapper = new MapperConfiguration(c => c.AddProfile<AutoMapping>(), loggerFactory).CreateMapper();
            currentUser.CurrentUser = new CurrentUser { Id = 7, Account = "alice" };
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

        public RoleViewService Service(bool failPermissionSync = false)
        {
            IRbacWriteService rbac = new RbacWriteService(Context, NullLogger<RbacWriteService>.Instance);
            return new(new TestDbContextFactory(connection), mapper, NullLogger<RoleViewService>.Instance, rolePermissionService,
                failPermissionSync ? new FailingPermissionSyncRbacWriteService(rbac) : rbac, new RecordingAuditLogService(), currentUser);
        }

        public RoleViewAdapterModel NewModel(string name)
            => new() { Name = name, RolePermission = rolePermissionService.InitializePermissionSetting() };

        public async Task<int> AddRoleDirectlyAsync(string name)
        {
            var role = new RoleView { Name = name, TabViewJson = "[]" };
            Context.RoleView.Add(role);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
            return role.Id;
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
            loggerFactory.Dispose();
        }
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using AutoMapper;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Repositories;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Dtos.Auths;
using CustomerHelpAgent.Dtos.Commons;
using CustomerHelpAgent.Dtos.Models;
using CustomerHelpAgent.Models.AdapterModel;
using CustomerHelpAgent.Models.Others;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Web.Auth;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 部門樹（0.9.105 起）：樹的走訪、快取、寫入規則（循環、刪除、還原、永久刪除）、改名同步、有效團隊展開與反查、
/// 指派規則、Web API 與畫面一致的團隊過濾。migration 不動既有資料見 <c>TeamTreeMigrationTests</c>。
/// </summary>
public sealed class TeamTreeTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly TestDbContextFactory factory;
    private readonly IMapper mapper;
    private readonly RecordingAuditLogService audit = new();
    private readonly CurrentUserService currentUser = new() { CurrentUser = new CurrentUser { Id = 1, Account = "admin" } };
    private readonly List<IDisposable> disposables = [];

    public TeamTreeTests()
    {
        connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        connection.Open();
        factory = new TestDbContextFactory(connection);
        using var context = factory.CreateDbContext();
        context.Database.EnsureCreated();
        mapper = new MapperConfiguration(c => c.AddProfile<AutoMapping>(), LoggerFactory.Create(_ => { })).CreateMapper();
    }

    // ---------- 樹的走訪 ----------

    [Fact]
    public void Tree_ShouldExpandDescendants_AndListAncestors_IgnoringCaseAndUnknownNames()
    {
        var tree = new TeamTree([new(1, "總部", null), new(2, "研發處", 1), new(3, "研發一部", 2), new(4, "業務處", 1), new(5, "Lab", 3)]);

        Assert.Equal(["研發處", "研發一部", "Lab"], tree.ExpandWithDescendants(["研發處"]));
        Assert.Equal(["業務處", "外部團隊"], tree.ExpandWithDescendants(["業務處", "外部團隊", " 業務處 "]));
        Assert.Equal(["lab", "研發一部", "研發處", "總部"], tree.AncestorsAndSelf("lab").Select((x, i) => i == 0 ? x.ToLowerInvariant() : x));
        Assert.Equal(["不存在"], tree.AncestorsAndSelf("不存在"));
        Assert.Equal(new HashSet<int> { 2, 3, 4, 5 }, tree.DescendantIds(1));
    }

    [Fact]
    public void Tree_WithACycleInTheData_ShouldNotLoopForever()
    {
        var tree = new TeamTree([new(1, "A", 2), new(2, "B", 1)]);

        Assert.Equal(["A", "B"], tree.ExpandWithDescendants(["A"]));
        Assert.Equal(["A", "B"], tree.AncestorsAndSelf("A"));
    }

    [Fact]
    public async Task Cache_ShouldServeTheSnapshot_UntilInvalidatedOrExpired()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var cache = new TeamTreeCache(factory, time, NullLogger<TeamTreeCache>.Instance);
        AddTeam("總部");
        Assert.Single((await cache.GetAsync()).Nodes);

        AddTeam("研發處");
        Assert.Single((await cache.GetAsync()).Nodes);

        cache.Invalidate();
        Assert.Equal(2, (await cache.GetAsync()).Nodes.Count);

        AddTeam("業務處");
        time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(3, (await cache.GetAsync()).Nodes.Count);
    }

    // ---------- 寫入規則 ----------

    [Fact]
    public async Task Parent_CannotBeSelf_ADescendant_OrADeletedTeam()
    {
        var root = AddTeam("總部");
        var child = AddTeam("研發處", root);
        var grandchild = AddTeam("研發一部", child);
        var deleted = AddTeam("舊部門", deletedAt: DateTime.Now);
        var service = NewTeamService(out _);

        Assert.Equal(TeamHierarchy.ParentIsSelfMessage, (await service.UpdateAsync(await AdapterAsync(root, parentId: root))).Message);
        Assert.Equal(TeamHierarchy.ParentIsDescendantMessage, (await service.UpdateAsync(await AdapterAsync(root, parentId: grandchild))).Message);
        Assert.Equal(TeamHierarchy.ParentNotFoundMessage, (await service.UpdateAsync(await AdapterAsync(child, parentId: deleted))).Message);
        Assert.Equal(TeamHierarchy.ParentNotFoundMessage, (await service.AddAsync(new TeamAdapterModel { Name = "新部門", ParentId = deleted })).Message);
        Assert.Null((await LoadTeamAsync(root)).ParentId);

        Assert.True((await service.UpdateAsync(await AdapterAsync(grandchild, parentId: root))).Success);
        Assert.Equal(root, (await LoadTeamAsync(grandchild)).ParentId);
    }

    /// <summary>⭐ 兩個請求同時「互設對方為上層」：檢查與寫入在同一個交易內，SQLite 讓寫入排隊，不會兩個都成功。</summary>
    [Fact]
    public async Task ConcurrentUpdates_ShouldNotCreateACycle()
    {
        var path = Path.Combine(Path.GetTempPath(), $"team-cycle-{Guid.NewGuid():N}.db");
        try
        {
            var fileFactory = new FileDbContextFactory($"Data Source={path};Foreign Keys=True;Pooling=False");
            await using (var context = fileFactory.CreateDbContext())
            {
                await context.Database.EnsureCreatedAsync();
                await context.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
            }

            int a, b;
            await using (var context = fileFactory.CreateDbContext())
            {
                var teamA = new Team { Name = "A" };
                var teamB = new Team { Name = "B" };
                context.Team.AddRange(teamA, teamB);
                await context.SaveChangesAsync();
                (a, b) = (teamA.Id, teamB.Id);
            }

            async Task<bool> SetParentAsync(int id, int parent)
            {
                await using var context = fileFactory.CreateDbContext();
                var team = await context.Team.AsNoTracking().SingleAsync(x => x.Id == id);
                var service = new TeamService(fileFactory, mapper, NullLogger<TeamService>.Instance, audit, currentUser, new TeamTreeCache(fileFactory, TimeProvider.System, NullLogger<TeamTreeCache>.Instance));
                return (await service.UpdateAsync(new TeamAdapterModel { Id = id, Name = team.Name, ParentId = parent, ConcurrencyStamp = team.ConcurrencyStamp })).Success;
            }

            var results = await Task.WhenAll(Task.Run(() => SetParentAsync(a, b)), Task.Run(() => SetParentAsync(b, a)));

            Assert.Single(results, x => x);
            await using var check = fileFactory.CreateDbContext();
            var parents = await check.Team.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.ParentId);
            Assert.False(parents[a] == b && parents[b] == a);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var file in new[] { path, path + "-wal", path + "-shm" })
            {
                File.Delete(file);
            }
        }
    }

    [Fact]
    public async Task Delete_WithActiveChildren_ShouldBeBlocked_AndRestore_UnderADeletedParentToo()
    {
        var root = AddTeam("總部");
        var child = AddTeam("研發處", root);
        var service = NewTeamService(out var tree);

        var blocked = await service.DeleteAsync(root);
        Assert.False(blocked.Success);
        Assert.Equal(TeamHierarchy.HasChildrenMessage(1), blocked.Message);

        Assert.True((await service.DeleteAsync(child)).Success);
        Assert.True((await service.DeleteAsync(root)).Success);
        Assert.Equal(2, tree.InvalidateCount);

        var restoreChild = await service.RestoreAsync(child);
        Assert.False(restoreChild.Success);
        Assert.Contains("總部", restoreChild.Message, StringComparison.Ordinal);

        Assert.True((await service.RestoreAsync(root)).Success);
        Assert.True((await service.RestoreAsync(child)).Success);
        Assert.Equal(4, tree.InvalidateCount);
    }

    [Fact]
    public async Task Purge_WhileStillTheParentOfADeletedTeam_ShouldBeBlocked()
    {
        var root = AddTeam("總部", deletedAt: DateTime.Now);
        AddTeam("研發處", root, deletedAt: DateTime.Now);
        var service = NewTeamService(out _);

        var result = await service.PurgeAsync(root);

        // 外鍵本身也會擋下，但只會得到「永久刪除團隊失敗。」；要的是說明原因的訊息。
        Assert.False(result.Success);
        Assert.Contains("以它為上層部門", result.Message, StringComparison.Ordinal);
        Assert.NotNull(await LoadTeamAsync(root, ignoreFilters: true));
    }

    [Fact]
    public async Task ScheduledPurge_ShouldRemoveTheDeepestFirst_AndKeepParentsOfUnexpiredChildren()
    {
        var old = DateTime.Now.AddDays(-200);
        var root = AddTeam("總部", deletedAt: old);
        var child = AddTeam("研發處", root, deletedAt: old);
        AddTeam("研發一部", child, deletedAt: old);
        var keptRoot = AddTeam("業務處", deletedAt: old);
        AddTeam("業務一部", keptRoot, deletedAt: DateTime.Now);

        var settings = new SystemSettings();
        var purge = new SoftDeletePurgeService(factory, new ProjectFileStore(Options.Create(settings), NullLogger<ProjectFileStore>.Instance),
            Options.Create(new BootstrapSettings()), NullLogger<SoftDeletePurgeService>.Instance);
        var result = await purge.PurgeExpiredAsync(DateTime.Now.AddDays(-90), default);

        var teams = result.Types.Single(x => x.EntityType == nameof(Team));
        Assert.Equal(["研發一部", "研發處", "總部"], teams.Removed.Select(x => x.Name));
        Assert.Equal(1, teams.InUse);
        Assert.False(teams.Failed);
    }

    // ---------- 改名同步 ----------

    [Fact]
    public async Task Rename_ShouldUpdateEveryReference_IncludingDeletedRows_AndChangeTheirStamps()
    {
        var team = AddTeam("研發部");
        AddTeam("研發部二");
        await using (var context = factory.CreateDbContext())
        {
            context.Project.AddRange(
                new Project { Title = "P1", Teams = TagStringHelper.ToStored(["研發部", "業務部"]) },
                new Project { Title = "P2", Teams = TagStringHelper.ToStored(["研發部二"]) },
                new Project { Title = "P3", Teams = TagStringHelper.ToStored(["研發部"]), IsDeleted = true, DeletedAt = DateTime.Now });
            context.Category.Add(new Category { Name = "C1", Teams = TagStringHelper.ToStored([" 研發部 "]) });
            context.RoleView.AddRange(
                new RoleView { Name = "R1", TabViewJson = "[]", DefaultTeamsJson = TeamJsonHelper.Serialize(["研發部"]) },
                new RoleView { Name = "R2", TabViewJson = "[]", DefaultTeamsJson = TeamJsonHelper.Serialize(["研發部"]), IsDeleted = true, DeletedAt = DateTime.Now });
            await context.SaveChangesAsync();
        }

        var stamps = await ProjectStampsAsync();
        var result = await NewTeamService(out _).UpdateAsync(await AdapterAsync(team, name: "研究發展部"));

        Assert.True(result.Success, result.Message);
        await using var check = factory.CreateDbContext();
        var projects = await check.Project.IgnoreQueryFilters([ISoftDeletable.FilterName]).AsNoTracking().OrderBy(x => x.Title).ToListAsync();
        Assert.Equal(["研究發展部", "業務部"], TagStringHelper.ToList(projects[0].Teams));
        Assert.Equal(["研發部二"], TagStringHelper.ToList(projects[1].Teams));
        Assert.Equal(["研究發展部"], TagStringHelper.ToList(projects[2].Teams));
        Assert.NotEqual(stamps["P1"], projects[0].ConcurrencyStamp);
        Assert.Equal(stamps["P2"], projects[1].ConcurrencyStamp);
        Assert.Equal(["研究發展部"], TagStringHelper.ToList((await check.Category.AsNoTracking().SingleAsync()).Teams));
        Assert.All(await check.RoleView.IgnoreQueryFilters([ISoftDeletable.FilterName]).AsNoTracking().ToListAsync(),
            role => Assert.Equal(["研究發展部"], TeamJsonHelper.Deserialize(role.DefaultTeamsJson)));
        Assert.Contains(audit.Entries, x => x.Action == AuditActions.Team.Update && x.Detail!.Contains("from=研發部; renamedProjects=2; renamedCategories=1; renamedRoles=2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rename_ToANameADeletedTeamUsed_ShouldBeBlocked_OnBothPaths()
    {
        var team = AddTeam("研發部");
        AddTeam("舊研發部", deletedAt: DateTime.Now);

        var result = await NewTeamService(out _).UpdateAsync(await AdapterAsync(team, name: "舊研發部"));
        Assert.False(result.Success);
        Assert.Equal(TeamService.DeletedNameMessage("舊研發部"), result.Message);

        var current = await LoadTeamAsync(team);
        var api = await TeamRepository().UpdateAsync(new Team { Id = team, Name = "舊研發部", ConcurrencyStamp = current.ConcurrencyStamp });
        Assert.Equal(TeamWriteStatus.Invalid, api.Status);
        Assert.Equal("研發部", (await LoadTeamAsync(team)).Name);
    }

    [Fact]
    public async Task ApiRepository_ShouldApplyTheSameRules()
    {
        var root = AddTeam("總部");
        var child = AddTeam("研發處", root);
        await using (var context = factory.CreateDbContext())
        {
            context.Project.Add(new Project { Title = "P", Teams = TagStringHelper.ToStored(["研發處"]) });
            await context.SaveChangesAsync();
        }

        Assert.Equal(TeamWriteStatus.Invalid, (await TeamRepository().AddAsync(new Team { Name = "新", ParentId = 999 })).Status);
        Assert.Equal(TeamWriteStatus.Invalid, (await TeamRepository().DeleteAsync(root, "api")).Status);
        var current = await LoadTeamAsync(root);
        Assert.Equal(TeamWriteStatus.Invalid, (await TeamRepository().UpdateAsync(new Team { Id = root, Name = "總部", ParentId = child, ConcurrencyStamp = current.ConcurrencyStamp })).Status);

        var childRow = await LoadTeamAsync(child);
        var renamed = await TeamRepository().UpdateAsync(new Team { Id = child, Name = "研發中心", ParentId = root, ConcurrencyStamp = childRow.ConcurrencyStamp });
        Assert.Equal(TeamWriteStatus.Ok, renamed.Status);
        Assert.Equal("renamedProjects=1; renamedCategories=0; renamedRoles=0", renamed.Message);
        await using var check = factory.CreateDbContext();
        Assert.Equal(["研發中心"], TagStringHelper.ToList((await check.Project.AsNoTracking().SingleAsync()).Teams));
    }

    // ---------- 有效團隊與反查 ----------

    [Fact]
    public async Task EffectiveTeams_ShouldIncludeDescendants_ButAssignedTeamsShouldNot()
    {
        var root = AddTeam("總部");
        var rd = AddTeam("研發處", root);
        AddTeam("研發一部", rd);
        AddTeam("業務處", root);
        var role = AddRole("主管", ["研發處"]);
        var user = AddUser("boss", role);

        var resolver = Resolver();
        Assert.Equal(["研發處"], await resolver.GetAssignedTeamNamesAsync(user));
        Assert.Equal(["研發處", "研發一部"], await resolver.GetEffectiveTeamNamesAsync(user));
    }

    /// <summary>⭐ 反查與正向互相對照：每位使用者×每個部門，u 在 GetUserIdsInTeamAsync(t) ⇔ t 在 u 的有效團隊（含上層與下屬的各種組合）。</summary>
    [Fact]
    public async Task ReverseLookup_ShouldMirrorEffectiveTeams_AcrossTheTree()
    {
        var root = AddTeam("總部");
        var rd = AddTeam("研發處", root);
        var rd1 = AddTeam("研發一部", rd);
        var sales = AddTeam("業務處", root);
        var users = new[]
        {
            AddUser("u-root", directTeams: [root]),
            AddUser("u-rd", directTeams: [rd]),
            AddUser("u-rd1", directTeams: [rd1]),
            AddUser("u-sales-role", AddRole("業務", ["業務處"])),
            AddUser("u-none"),
        };

        var resolver = Resolver();
        foreach (var teamName in new[] { "總部", "研發處", "研發一部", "業務處" })
        {
            var members = await resolver.GetUserIdsInTeamAsync(teamName);
            foreach (var userId in users)
            {
                var effective = await resolver.GetEffectiveTeamNamesAsync(userId);
                Assert.Equal(effective.Contains(teamName), members.Contains(userId));
            }
        }

        Assert.Equal([users[0], users[1], users[2]], await resolver.GetUserIdsInTeamAsync("研發一部"));
        Assert.Equal([users[0]], await resolver.GetUserIdsInTeamAsync("總部"));
        _ = sales;
    }

    [Fact]
    public async Task Profile_ShouldShowAssignedAndSubordinateTeamsSeparately()
    {
        var rd = AddTeam("研發處");
        AddTeam("研發一部", rd);
        var user = AddUser("boss", directTeams: [rd]);
        await using var context = factory.CreateDbContext();
        var profile = new ProfileService(factory, new EffectiveTeamResolver(context, new ContextTeamTreeCache(context), NullLogger<EffectiveTeamResolver>.Instance),
            PasswordTestDefaults.Policy(), audit, currentUser, NullLogger<ProfileService>.Instance);

        var info = await profile.GetAsync(user);

        Assert.Equal(["研發處"], info!.Teams);
        Assert.Equal(["研發一部"], info.SubordinateTeams);
    }

    // ---------- 指派與可見性規則 ----------

    [Fact]
    public void Assignment_AddedTeamsMustBeInScope_ExistingOnesMayStay_AndCannotBecomePublic()
    {
        var scope = new RecordAccessScope(false, ["研發處", "研發一部"]);
        string Stored(params string[] teams) => TagStringHelper.ToStored(teams)!;

        Assert.Null(RecordTeamScope.CheckAssignment(null, Stored("研發一部"), scope));
        Assert.Null(RecordTeamScope.CheckAssignment(null, null, scope));
        Assert.Contains("業務處", RecordTeamScope.CheckAssignment(null, Stored("研發處", "業務處"), scope), StringComparison.Ordinal);
        Assert.Null(RecordTeamScope.CheckAssignment(Stored("研發處", "業務處"), Stored("研發處", "業務處"), scope));
        Assert.Null(RecordTeamScope.CheckAssignment(Stored("研發處", "業務處"), Stored("研發處"), scope));
        Assert.Equal(RecordTeamScope.ClearToPublicMessage, RecordTeamScope.CheckAssignment(Stored("研發處"), null, scope));
        Assert.Null(RecordTeamScope.CheckAssignment(Stored("研發處"), null, new RecordAccessScope(true, [])));
        Assert.Equal(RecordTeamScope.DeniedAssignmentMessage, RecordTeamScope.CheckAssignment(null, null, RecordAccessScope.None));
    }

    [Fact]
    public void DeniedScope_ShouldSeeNothing_EvenPublicRecordsAndCategories()
    {
        Assert.False(RecordTeamScope.CanAccess(null, RecordAccessScope.None));
        Assert.False(RecordTeamScope.CanAccessCategory(null, RecordAccessScope.None));
        Assert.True(RecordTeamScope.CanAccessCategory(TagStringHelper.ToStored(["業務處"]), new RecordAccessScope(false, [])));
    }

    [Fact]
    public async Task ProjectService_Update_ShouldCheckTheExistingRow_AndTheAssignment()
    {
        int sales, rd;
        await using (var context = factory.CreateDbContext())
        {
            var a = new Project { Title = "業務案", Teams = TagStringHelper.ToStored(["業務處"]) };
            var b = new Project { Title = "研發案", Teams = TagStringHelper.ToStored(["研發處"]) };
            context.Project.AddRange(a, b);
            await context.SaveChangesAsync();
            (sales, rd) = (a.Id, b.Id);
        }

        var service = ProjectService(new FakeRecordAccessScopeProvider(false, ["研發處"]));
        var hidden = await ProjectAdapterAsync(sales);
        hidden.Title = "改掉";
        Assert.Equal("這筆專案不在你的團隊範圍內，無法修改。", (await service.UpdateAsync(hidden)).Message);

        var mine = await ProjectAdapterAsync(rd);
        mine.Teams = ["研發處", "業務處"];
        Assert.Contains("業務處", (await service.UpdateAsync(mine)).Message, StringComparison.Ordinal);
        Assert.False((await service.AddAsync(new ProjectAdapterModel { Title = "新案", Teams = ["業務處"] })).Success);

        mine.Teams = ["研發處"];
        mine.Title = "研發案二";
        Assert.True((await service.UpdateAsync(mine)).Success);
    }

    [Fact]
    public async Task ScopeProvider_ShouldResolveJwtUsers_AndDenyUnresolvableOnes()
    {
        var rd = AddTeam("研發處");
        AddTeam("研發一部", rd);
        var user = AddUser("jwt-user", directTeams: [rd]);
        await using var context = factory.CreateDbContext();

        RecordAccessScopeProvider Provider(params Claim[] claims)
        {
            var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer")) };
            return new RecordAccessScopeProvider(new CurrentUserService(), new HttpContextAccessor { HttpContext = http }, context,
                new EffectiveTeamResolver(context, new ContextTeamTreeCache(context), NullLogger<EffectiveTeamResolver>.Instance), NullLogger<RecordAccessScopeProvider>.Instance);
        }

        var jwt = await Provider(new Claim(ClaimTypes.NameIdentifier, user.ToString()), new Claim(ClaimTypes.Name, "jwt-user")).GetAsync();
        Assert.Equal(["研發處", "研發一部"], jwt.Teams);
        Assert.False(jwt.Denied);

        Assert.True((await Provider(new Claim(ClaimTypes.Name, "nobody")).GetAsync()).Denied);
        Assert.True((await Provider(new Claim(ClaimTypes.NameIdentifier, "999999")).GetAsync()).Denied);
    }

    // migration 不動既有資料的測試在 TeamTreeMigrationTests（衍生專案複刻時會刪除該檔）。

    // ---------- helpers ----------

    private TeamService NewTeamService(out ContextTeamTreeCache tree)
    {
        var context = factory.CreateDbContext();
        disposables.Add(context);
        tree = new ContextTeamTreeCache(context);
        return new TeamService(factory, mapper, NullLogger<TeamService>.Instance, audit, currentUser, tree);
    }

    private TeamRepository TeamRepository()
    {
        var context = factory.CreateDbContext();
        disposables.Add(context);
        return new TeamRepository(context, new ContextTeamTreeCache(context), NullLogger<TeamRepository>.Instance);
    }

    private EffectiveTeamResolver Resolver()
    {
        var context = factory.CreateDbContext();
        disposables.Add(context);
        return new EffectiveTeamResolver(context, new ContextTeamTreeCache(context), NullLogger<EffectiveTeamResolver>.Instance);
    }

    private ProjectService ProjectService(FakeRecordAccessScopeProvider scope)
    {
        var settings = new SystemSettings();
        return new ProjectService(factory, mapper, NullLogger<ProjectService>.Instance, Options.Create(settings), scope, audit, currentUser,
            new ProjectFileStore(Options.Create(settings), NullLogger<ProjectFileStore>.Instance));
    }

    private async Task<ProjectAdapterModel> ProjectAdapterAsync(int id)
    {
        await using var context = factory.CreateDbContext();
        return mapper.Map<ProjectAdapterModel>(await context.Project.AsNoTracking().SingleAsync(x => x.Id == id));
    }

    private int AddTeam(string name, int? parentId = null, DateTime? deletedAt = null)
    {
        using var context = factory.CreateDbContext();
        var team = new Team { Name = name, ParentId = parentId, IsDeleted = deletedAt is not null, DeletedAt = deletedAt };
        context.Team.Add(team);
        context.SaveChanges();
        return team.Id;
    }

    private int AddRole(string name, string[] defaultTeams)
    {
        using var context = factory.CreateDbContext();
        var role = new RoleView { Name = name, TabViewJson = "[]", DefaultTeamsJson = TeamJsonHelper.Serialize(defaultTeams) };
        context.RoleView.Add(role);
        context.SaveChanges();
        return role.Id;
    }

    private int AddUser(string account, int? roleId = null, int[]? directTeams = null)
    {
        using var context = factory.CreateDbContext();
        var user = new MyUser { Account = account, Name = account, Status = true, RoleViewId = roleId, Password = string.Empty };
        context.MyUser.Add(user);
        context.SaveChanges();
        foreach (var teamId in directTeams ?? [])
        {
            context.UserTeam.Add(new UserTeam { MyUserId = user.Id, TeamId = teamId });
        }

        context.SaveChanges();
        return user.Id;
    }

    private async Task<Team> LoadTeamAsync(int id, bool ignoreFilters = false)
    {
        await using var context = factory.CreateDbContext();
        var query = ignoreFilters ? context.Team.IgnoreQueryFilters([ISoftDeletable.FilterName]) : context.Team;
        return (await query.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id))!;
    }

    private async Task<TeamAdapterModel> AdapterAsync(int id, int? parentId = null, string? name = null)
    {
        var model = mapper.Map<TeamAdapterModel>(await LoadTeamAsync(id));
        model.ParentId = parentId ?? model.ParentId;
        model.Name = name ?? model.Name;
        return model;
    }

    private async Task<Dictionary<string, string>> ProjectStampsAsync()
    {
        await using var context = factory.CreateDbContext();
        return await context.Project.IgnoreQueryFilters([ISoftDeletable.FilterName]).AsNoTracking().ToDictionaryAsync(x => x.Title, x => x.ConcurrencyStamp);
    }

    public void Dispose()
    {
        foreach (var item in disposables)
        {
            item.Dispose();
        }

        connection.Dispose();
    }

    private sealed class FileDbContextFactory(string connectionString) : IDbContextFactory<BackendDBContext>
    {
        public BackendDBContext CreateDbContext()
            => new(new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connectionString).Options);
    }
}

/// <summary>⭐ Web API 與畫面套用同一套團隊範圍（真正的主機、JWT）：上層看得到下屬、同層看不到、範圍外 404、不能指派範圍外團隊。</summary>
[Collection(nameof(IntegrationHostCollection))]
public sealed class TeamTreeApiTests : IClassFixture<ApiTestApplicationFactory>
{
    private readonly ApiTestApplicationFactory factory;

    public TeamTreeApiTests(ApiTestApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task ManagerOfAParentTeam_ShouldSeeSubordinateProjects_ButNotSiblings()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        string root = $"總部-{suffix}", rd = $"研發處-{suffix}", rd1 = $"研發一部-{suffix}", sales = $"業務處-{suffix}";
        int salesProject, rd1Project;
        var account = $"mgr-{suffix}";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
            var rootTeam = new Team { Name = root };
            db.Team.Add(rootTeam);
            await db.SaveChangesAsync();
            var rdTeam = new Team { Name = rd, ParentId = rootTeam.Id };
            var salesTeam = new Team { Name = sales, ParentId = rootTeam.Id };
            db.Team.AddRange(rdTeam, salesTeam);
            await db.SaveChangesAsync();
            db.Team.Add(new Team { Name = rd1, ParentId = rdTeam.Id });

            var keys = new[] { "專案項目" };
            var role = new RoleView { Name = $"主管-{suffix}", TabViewJson = JsonSerializer.Serialize(keys) };
            db.RoleView.Add(role);
            var p1 = new Project { Title = $"研發一部案-{suffix}", Teams = TagStringHelper.ToStored([rd1]) };
            var p2 = new Project { Title = $"業務案-{suffix}", Teams = TagStringHelper.ToStored([sales]) };
            var p3 = new Project { Title = $"研發案-{suffix}", Teams = TagStringHelper.ToStored([rd]) };
            db.Project.AddRange(p1, p2, p3);
            await db.SaveChangesAsync();
            (rd1Project, salesProject) = (p1.Id, p2.Id);

            var writer = scope.ServiceProvider.GetRequiredService<IRbacWriteService>();
            await writer.SyncRolePermissionsAsync(role.Id, keys);
            var user = new MyUser { Account = account, Name = account, Status = true, RoleViewId = role.Id, Password = SecurePasswordHasher.HashPassword("Passw0rd-1") };
            db.MyUser.Add(user);
            await db.SaveChangesAsync();
            await writer.SyncUserRolesAsync(user.Id, [role.Id]);
            db.UserTeam.Add(new UserTeam { MyUserId = user.Id, TeamId = rdTeam.Id });
            await db.SaveChangesAsync();
        }

        factory.Services.GetRequiredService<ITeamTreeCache>().Invalidate();
        using var client = factory.CreateClient();
        var login = await (await client.PostAsJsonAsync("/api/Auth/login", new LoginRequestDto { Account = account, Password = "Passw0rd-1" }))
            .Content.ReadFromJsonAsync<ApiResult<TokenResponseDto>>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.Data!.AccessToken);

        var search = await (await client.PostAsJsonAsync("/api/Project/search", new ProjectSearchRequestDto { Keyword = suffix, PageIndex = 1, PageSize = 50 }))
            .Content.ReadFromJsonAsync<ApiResult<PagedResult<ProjectDto>>>();
        Assert.Equal([$"研發一部案-{suffix}", $"研發案-{suffix}"], search!.Data!.Items.Select(x => x.Title).Order(StringComparer.Ordinal));

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/Project/{rd1Project}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/Project/{salesProject}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/Project/{salesProject}")).StatusCode);

        var outside = await client.PostAsJsonAsync("/api/Project", new ProjectCreateUpdateDto
        {
            Id = 0,
            Title = $"越權-{suffix}",
            Owner = "mgr",
            Status = "進行中",
            Priority = "中",
            Teams = TagStringHelper.ToStored([sales]),
            StartDate = DateTime.Today,
            EndDate = DateTime.Today,
        });
        Assert.Equal(HttpStatusCode.BadRequest, outside.StatusCode);
        Assert.Contains(sales, await outside.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}

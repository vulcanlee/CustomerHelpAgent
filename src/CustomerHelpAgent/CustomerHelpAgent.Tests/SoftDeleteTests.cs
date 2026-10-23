using AutoMapper;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Repositories;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.AdapterModel;
using CustomerHelpAgent.Models.Others;
using CustomerHelpAgent.Models.Systems;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 軟刪除（0.9.94 起：專案、分類、團隊）。
///
/// 除了「刪除 → 還原 → 永久刪除」的基本流程，特別釘住審查時抓到的三個漏洞：
/// 已刪除專案的附件會被任何人下載、編輯使用者會刪掉他和已刪除團隊的關聯、整筆覆蓋的存檔會把已刪除的資料救活。
/// </summary>
public sealed class SoftDeleteTests
{
    // ------------------------------------------------------------------ 基本流程（分類）

    [Fact]
    public async Task Category_Delete_ShouldHideFromListsAndLookups_AndAppearInDeletedList()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CategoryService();
        var id = await fixture.AddCategoryAsync(service, "技術文件");

        Assert.True((await service.DeleteAsync(id)).Success);

        Assert.Equal(0, (await service.GetAsync(AllRows())).Count);
        Assert.DoesNotContain("技術文件", await service.GetAllEnabledNamesAsync());
        var deleted = (await service.GetDeletedAsync(AllRows())).Result.Single();
        Assert.Equal("技術文件", deleted.Name);
        Assert.Equal("alice", deleted.DeletedBy);
        Assert.NotNull(deleted.DeletedAt);
    }

    [Fact]
    public async Task Category_Restore_ShouldBringItBack()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CategoryService();
        var id = await fixture.AddCategoryAsync(service, "技術文件");
        await service.DeleteAsync(id);

        Assert.True((await service.RestoreAsync(id)).Success);

        Assert.Equal("技術文件", (await service.GetAsync(AllRows())).Result.Single().Name);
        Assert.Empty((await service.GetDeletedAsync(AllRows())).Result);
    }

    [Fact]
    public async Task Category_Purge_ShouldOnlyWorkOnDeletedRows_AndRemoveTheRow()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CategoryService();
        var id = await fixture.AddCategoryAsync(service, "技術文件");

        Assert.False((await service.PurgeAsync(id)).Success, "未刪除的資料不可直接永久刪除。");

        await service.DeleteAsync(id);
        Assert.True((await service.PurgeAsync(id)).Success);
        Assert.Equal(0, await fixture.Context.Category.IgnoreQueryFilters([ISoftDeletable.FilterName]).CountAsync());
    }

    [Fact]
    public async Task Category_SameNameCanBeRecreatedAfterDelete_ButRestoreThenConflicts()
    {
        // 部分唯一索引只約束未刪除的資料；還原時由服務層擋下同名衝突，而不是讓資料庫丟例外。
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CategoryService();
        var oldId = await fixture.AddCategoryAsync(service, "技術文件");
        await service.DeleteAsync(oldId);

        await fixture.AddCategoryAsync(service, "技術文件");
        var restore = await service.RestoreAsync(oldId);

        Assert.False(restore.Success);
        Assert.Contains("同名", restore.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Category_DeleteOutsideTeamScope_ShouldBeRejected()
    {
        // 0.9.93 之前刪除完全不檢查團隊範圍。
        await using var fixture = await Fixture.CreateAsync();
        var admin = fixture.CategoryService();
        var id = await fixture.AddCategoryAsync(admin, "研發專用", teams: ["研發部"]);

        var outsider = fixture.CategoryService(new FakeRecordAccessScopeProvider(false, ["業務部"]));
        var result = await outsider.DeleteAsync(id);

        Assert.False(result.Success);
        Assert.False(await fixture.Context.Category.IgnoreQueryFilters([ISoftDeletable.FilterName]).Select(x => x.IsDeleted).SingleAsync());
    }

    [Fact]
    public async Task Category_EditorWhoOpenedBeforeDelete_ShouldNotSaveIntoDeletedRow()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CategoryService();
        var id = await fixture.AddCategoryAsync(service, "技術文件");
        var opened = await fixture.Context.Category.AsNoTracking().SingleAsync();

        await service.DeleteAsync(id);
        var save = await service.UpdateAsync(new CategoryAdapterModel { Id = id, Name = "改名", IsEnabled = true, ConcurrencyStamp = opened.ConcurrencyStamp });

        Assert.False(save.Success);
        var row = await fixture.Context.Category.IgnoreQueryFilters([ISoftDeletable.FilterName]).AsNoTracking().SingleAsync();
        Assert.True(row.IsDeleted);
        Assert.Equal("技術文件", row.Name);
    }

    [Fact]
    public async Task ProtectFlags_ShouldStopAWholeRowOverwriteFromUndeleting()
    {
        // 整筆覆蓋（attach + Modified、API 的 SetValues）會把 IsDeleted 寫回畫面模型的預設值 false。
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CategoryService();
        var id = await fixture.AddCategoryAsync(service, "技術文件");
        await service.DeleteAsync(id);
        var stamp = await fixture.Context.Category.IgnoreQueryFilters([ISoftDeletable.FilterName]).Select(x => x.ConcurrencyStamp).SingleAsync();

        await using (var context = fixture.NewContext())
        {
            var entry = context.Entry(new Category { Id = id, Name = "整筆覆蓋", IsDeleted = false, ConcurrencyStamp = stamp });
            entry.State = EntityState.Modified;
            ConcurrencyStampHelper.Apply(entry, stamp);
            SoftDeleteHelper.ProtectFlags(entry);
            await context.SaveChangesAsync();
        }

        var row = await fixture.Context.Category.IgnoreQueryFilters([ISoftDeletable.FilterName]).AsNoTracking().SingleAsync();
        Assert.True(row.IsDeleted, "整筆覆蓋把已刪除的資料救活了 —— ProtectFlags 沒有發揮作用。");
        Assert.Equal("alice", row.DeletedBy);
    }

    // ------------------------------------------------------------------ 團隊

    [Fact]
    public async Task Team_RestoreWithCodeTakenByActiveTeam_ShouldBeRejected()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.TeamService();
        Assert.True((await service.AddAsync(new TeamAdapterModel { Name = "研發部", Code = "RD" })).Success);
        var oldId = await fixture.Context.Team.Select(x => x.Id).SingleAsync();
        await service.DeleteAsync(oldId);
        Assert.True((await service.AddAsync(new TeamAdapterModel { Name = "研發新部", Code = "rd" })).Success);

        var restore = await service.RestoreAsync(oldId);

        Assert.False(restore.Success);
        Assert.Contains("代號", restore.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EditingUser_ShouldKeepLinkToDeletedTeam_SoRestoringTheTeamRestoresMembership()
    {
        // 編輯畫面看不到已刪除的團隊；若存檔時照樣刪掉 UserTeam，團隊還原後成員關係就回不來了。
        await using var fixture = await Fixture.CreateAsync();
        var teams = fixture.TeamService();
        Assert.True((await teams.AddAsync(new TeamAdapterModel { Name = "研發部" })).Success);
        var teamId = await fixture.Context.Team.Select(x => x.Id).SingleAsync();
        var users = fixture.UserService();
        Assert.True((await users.AddAsync(new MyUserAdapterModel { Account = "bob", Name = "bob", Password = "Passw0rd", Status = true, TeamNames = ["研發部"] })).Success);
        var user = await fixture.Context.MyUser.AsNoTracking().SingleAsync();

        await teams.DeleteAsync(teamId);
        var (_, visibleTeams) = await users.GetUserAssignmentsAsync(user.Id);
        Assert.Empty(visibleTeams);

        Assert.True((await users.UpdateAsync(new MyUserAdapterModel
        {
            Id = user.Id,
            Account = "bob",
            Name = "Bob",
            Status = true,
            TeamNames = visibleTeams,
            ConcurrencyStamp = user.ConcurrencyStamp,
        })).Success);
        Assert.Equal(1, await fixture.Context.UserTeam.CountAsync(x => x.MyUserId == user.Id && x.TeamId == teamId));

        await teams.RestoreAsync(teamId);
        Assert.Contains("研發部", (await users.GetUserAssignmentsAsync(user.Id)).TeamNames);
    }

    [Fact]
    public async Task EffectiveTeams_ShouldExcludeDeletedTeam()
    {
        await using var fixture = await Fixture.CreateAsync();
        var teams = fixture.TeamService();
        Assert.True((await teams.AddAsync(new TeamAdapterModel { Name = "研發部" })).Success);
        var teamId = await fixture.Context.Team.Select(x => x.Id).SingleAsync();
        var users = fixture.UserService();
        Assert.True((await users.AddAsync(new MyUserAdapterModel { Account = "carol", Name = "carol", Password = "Passw0rd", Status = true, TeamNames = ["研發部"] })).Success);
        var userId = await fixture.Context.MyUser.Select(x => x.Id).SingleAsync();
        var resolver = new EffectiveTeamResolver(fixture.Context, new ContextTeamTreeCache(fixture.Context), NullLogger<EffectiveTeamResolver>.Instance);
        Assert.Contains("研發部", await resolver.GetEffectiveTeamNamesAsync(userId));

        await teams.DeleteAsync(teamId);

        Assert.DoesNotContain("研發部", await fixture.NewResolver().GetEffectiveTeamNamesAsync(userId));
    }

    // ------------------------------------------------------------------ 專案與附件

    [Fact]
    public async Task DeletedProjectAttachment_ShouldNotBeDownloadable()
    {
        // 父專案被過濾掉後 IsTeamAccessible(null) 回「公開」—— 原本限團隊的附件會對任何人公開。
        await using var fixture = await Fixture.CreateAsync();
        var (projectId, fileId, _) = await fixture.AddProjectWithFileAsync(teams: "研發部");
        var outsider = fixture.ProjectService(new FakeRecordAccessScopeProvider(false, ["業務部"]));
        Assert.Null(await outsider.GetFileDownloadAsync(fileId));

        await fixture.ProjectService().DeleteAsync(projectId);

        var download = await outsider.GetFileDownloadAsync(fileId);
        download?.Content.Dispose();
        Assert.Null(download);
        Assert.Null(await fixture.ProjectService().GetFileDownloadAsync(fileId));
    }

    [Fact]
    public async Task ProjectPurge_ShouldDeleteAttachmentFilesOnlyAtPurge()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (projectId, _, fullPath) = await fixture.AddProjectWithFileAsync(teams: null);
        var service = fixture.ProjectService();

        await service.DeleteAsync(projectId);
        Assert.True(File.Exists(fullPath), "軟刪除不可以刪掉附件實體檔，否則還原後附件就壞了。");

        Assert.True((await service.PurgeAsync(projectId)).Success);
        Assert.False(File.Exists(fullPath));
        Assert.Equal(0, await fixture.Context.ProjectFile.CountAsync());
    }

    [Theory]
    [InlineData("未開始")]
    [InlineData("中")]
    public async Task DeletedProjectSearch_ShouldMatchStatusAndPriority_LikeTheNormalList(string search)
    {
        // 0.9.136 起「顯示已刪除」的關鍵字與一般清單比對相同欄位（之前漏了狀態與優先級）。
        await using var fixture = await Fixture.CreateAsync();
        var (projectId, _, _) = await fixture.AddProjectWithFileAsync(teams: null);
        var service = fixture.ProjectService();
        await service.DeleteAsync(projectId);

        var request = AllRows();
        request.Search = search;

        Assert.Single((await service.GetDeletedAsync(request)).Result);
    }

    // ------------------------------------------------------------------ Web API 的 Repository

    [Fact]
    public async Task Repository_FindAsyncOnDeletedRow_ShouldReturnNotFound()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CategoryService();
        var id = await fixture.AddCategoryAsync(service, "技術文件");
        await service.DeleteAsync(id);

        await using var context = fixture.NewContext();
        var repository = new CategoryRepository(context, new FakeRecordAccessScopeProvider(true, []), NullLogger<CategoryRepository>.Instance);

        Assert.Null(await repository.GetByIdAsync(id));
        Assert.False(await repository.DeleteAsync(id, "api-user"));
    }

    private static DataRequest AllRows() => new() { CurrentPage = 1, PageSize = 100, Take = 0 };

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly IMapper mapper;
        private readonly ILoggerFactory loggerFactory = LoggerFactory.Create(_ => { });
        private readonly CurrentUserService currentUser = new();
        private readonly string fileRoot = Path.Combine(Path.GetTempPath(), "CustomerHelpAgentSoftDelete", Guid.NewGuid().ToString("N"));

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

        public BackendDBContext NewContext() => new(new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connection).Options);

        public EffectiveTeamResolver NewResolver()
        {
            var context = NewContext();
            return new(context, new ContextTeamTreeCache(context), NullLogger<EffectiveTeamResolver>.Instance);
        }

        public CategoryService CategoryService(FakeRecordAccessScopeProvider? scope = null)
            => new(Factory(), mapper, NullLogger<CategoryService>.Instance, scope ?? new FakeRecordAccessScopeProvider(true, []), new RecordingAuditLogService(), currentUser);

        public TeamService TeamService()
            => new(Factory(), mapper, NullLogger<TeamService>.Instance, new RecordingAuditLogService(), currentUser, new ContextTeamTreeCache(Context));

        public ProjectService ProjectService(FakeRecordAccessScopeProvider? scope = null)
        {
            var settings = new SystemSettings();
            settings.ExternalFileSystem.ProjectFilePath = fileRoot;
            return new(Factory(), mapper, NullLogger<ProjectService>.Instance, Options.Create(settings),
                scope ?? new FakeRecordAccessScopeProvider(true, []), new RecordingAuditLogService(), currentUser,
                new ProjectFileStore(Options.Create(settings), NullLogger<ProjectFileStore>.Instance));
        }

        public MyUserService UserService()
            => new(Factory(), mapper, NullLogger<MyUserService>.Instance,
                new RbacWriteService(NewContext(), NullLogger<RbacWriteService>.Instance), new RecordingAuditLogService(), currentUser, Options.Create(new BootstrapSettings()), PasswordTestDefaults.Policy());

        public async Task<int> AddCategoryAsync(CategoryService service, string name, List<string>? teams = null)
        {
            Assert.True((await service.AddAsync(new CategoryAdapterModel { Name = name, IsEnabled = true, Teams = teams ?? [] })).Success);
            return await Context.Category.Where(x => x.Name == name).OrderByDescending(x => x.Id).Select(x => x.Id).FirstAsync();
        }

        /// <summary>直接寫入一筆專案與一個實體附件檔（略過上傳流程）。</summary>
        public async Task<(int ProjectId, int FileId, string FullPath)> AddProjectWithFileAsync(string? teams)
        {
            var relative = Path.Combine("2026", "10", $"{Guid.NewGuid():N}.txt");
            var fullPath = Path.Combine(fileRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(fullPath, "attachment");

            var project = new Project { Title = "專案", Owner = "owner", Status = "未開始", Priority = "中", Teams = teams };
            project.Files.Add(new ProjectFile { OriginalFileName = "a.txt", StoredFileName = Path.GetFileName(relative), RelativePath = relative, ContentType = "text/plain", FileSize = 10 });
            Context.Project.Add(project);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
            return (project.Id, project.Files.Single().Id, fullPath);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
            loggerFactory.Dispose();
            try
            {
                if (Directory.Exists(fileRoot))
                {
                    Directory.Delete(fileRoot, recursive: true);
                }
            }
            catch (IOException)
            {
                // 暫存檔清不掉不影響測試結果。
            }
        }

        private TestDbContextFactory Factory() => new(connection);
    }
}

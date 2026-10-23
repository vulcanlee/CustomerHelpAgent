using AutoMapper;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.AdapterModel;
using CustomerHelpAgent.Models.Systems;

namespace CustomerHelpAgent.Tests;

public sealed class ProjectServiceTeamAccessTests
{
    [Fact]
    public async Task GetAsync_Admin_ShouldSeeAllRecords()
    {
        await using var fixture = await ProjectServiceFixture.CreateAsync();
        await fixture.SeedDefaultProjectsAsync();
        var service = fixture.CreateService(isAdmin: true);

        var result = await service.GetAsync(NewRequest());

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public async Task GetAsync_NonAdmin_ShouldSeeOnlyPublicOrIntersectingTeamRecords()
    {
        await using var fixture = await ProjectServiceFixture.CreateAsync();
        await fixture.SeedDefaultProjectsAsync();
        var service = fixture.CreateService(isAdmin: false, "團隊A");

        var result = await service.GetAsync(NewRequest());
        var titles = result.Result.Select(x => x.Title).OrderBy(x => x).ToList();

        // 公開（無團隊）與 團隊A 可見；團隊B 不可見
        Assert.Equal(["公開專案", "團隊A專案"], titles);
    }

    [Fact]
    public async Task GetAsync_NonAdminWithoutTeams_ShouldSeeOnlyPublicRecords()
    {
        await using var fixture = await ProjectServiceFixture.CreateAsync();
        await fixture.SeedDefaultProjectsAsync();
        var service = fixture.CreateService(isAdmin: false);

        var result = await service.GetAsync(NewRequest());

        Assert.Equal(["公開專案"], result.Result.Select(x => x.Title).ToList());
    }

    [Fact]
    public async Task GetAsync_WithTeamFilter_ShouldFilterByTeam()
    {
        await using var fixture = await ProjectServiceFixture.CreateAsync();
        await fixture.SeedDefaultProjectsAsync();
        var service = fixture.CreateService(isAdmin: true);

        var request = NewRequest();
        request.TeamFilters = ["團隊B"];
        var result = await service.GetAsync(request);

        Assert.Equal(["團隊B專案"], result.Result.Select(x => x.Title).ToList());
    }

    [Fact]
    public async Task GetById_NonAdmin_ShouldDenyRecordOutsideTeamScope()
    {
        await using var fixture = await ProjectServiceFixture.CreateAsync();
        var ids = await fixture.SeedDefaultProjectsAsync();
        var service = fixture.CreateService(isAdmin: false, "團隊A");

        var denied = await service.GetAsync(ids["團隊B專案"]);
        var allowed = await service.GetAsync(ids["團隊A專案"]);

        Assert.Equal(0, denied.Id); // 守門回空模型
        Assert.Equal("團隊A專案", allowed.Title);
    }

    [Fact]
    public async Task GetFileDownloadAsync_Admin_ShouldReturnStreamWithOriginalName()
    {
        await using var fixture = await ProjectServiceFixture.CreateAsync();
        var ids = await fixture.SeedDefaultProjectsAsync();
        var fileId = await fixture.SeedFileAsync(ids["團隊B專案"], "季報表.pdf", "application/pdf", "PDF-CONTENT");
        var service = fixture.CreateService(isAdmin: true);

        var result = await service.GetFileDownloadAsync(fileId);

        Assert.NotNull(result);
        await using var content = result.Content;
        Assert.Equal("季報表.pdf", result.DownloadFileName);
        Assert.Equal("application/pdf", result.ContentType);
        Assert.Equal("PDF-CONTENT", await new StreamReader(content).ReadToEndAsync());
    }

    [Fact]
    public async Task GetFileDownloadAsync_UnknownId_ShouldReturnNull()
    {
        await using var fixture = await ProjectServiceFixture.CreateAsync();
        await fixture.SeedDefaultProjectsAsync();
        var service = fixture.CreateService(isAdmin: true);

        Assert.Null(await service.GetFileDownloadAsync(9999));
    }

    [Fact]
    public async Task GetFileDownloadAsync_NonAdminOutsideTeam_ShouldReturnNull()
    {
        await using var fixture = await ProjectServiceFixture.CreateAsync();
        var ids = await fixture.SeedDefaultProjectsAsync();
        var denied = await fixture.SeedFileAsync(ids["團隊B專案"], "b.txt", "text/plain", "B");
        var allowed = await fixture.SeedFileAsync(ids["團隊A專案"], "a.txt", "text/plain", "A");
        var service = fixture.CreateService(isAdmin: false, "團隊A");

        Assert.Null(await service.GetFileDownloadAsync(denied));

        var result = await service.GetFileDownloadAsync(allowed);
        Assert.NotNull(result);
        await result.Content.DisposeAsync();
    }

    [Fact]
    public async Task GetFileDownloadAsync_WhenPhysicalFileMissing_ShouldReturnNull()
    {
        // metadata 還在、實體檔案被刪掉 —— 備份還原或人工清檔之後的常見狀態。
        await using var fixture = await ProjectServiceFixture.CreateAsync();
        var ids = await fixture.SeedDefaultProjectsAsync();
        var fileId = await fixture.SeedFileAsync(ids["公開專案"], "gone.txt", "text/plain", content: null);
        var service = fixture.CreateService(isAdmin: true);

        Assert.Null(await service.GetFileDownloadAsync(fileId));
    }

    [Fact]
    public async Task GetFileDownloadAsync_WhenRelativePathEscapesRoot_ShouldReturnNull()
    {
        // 只有直接改資料庫才寫得出這種值；這個端點會把伺服器本機的檔案送出去，
        // 因此不把「RelativePath 一定安全」當成前提。
        await using var fixture = await ProjectServiceFixture.CreateAsync();
        var ids = await fixture.SeedDefaultProjectsAsync();
        // 實體檔案必須真的存在於根目錄之外，這條測試才分得出「被守門擋下」與「檔案剛好不存在」。
        await File.WriteAllTextAsync(Path.Combine(fixture.Sandbox, "outside.txt"), "SECRET");

        var fileId = await fixture.SeedFileAsync(
            ids["公開專案"], "outside.txt", "text/plain", content: null, relativePath: "../outside.txt");
        var service = fixture.CreateService(isAdmin: true);

        Assert.Null(await service.GetFileDownloadAsync(fileId));
    }

    // ---- 移除附件（0.9.97 起經 ProjectFileStore）----

    [Fact]
    public async Task UpdateAsync_RemovingAnAttachmentWhosePathEscapesTheRoot_ShouldNotDeleteTheOutsideFile()
    {
        // 0.9.96 之前移除附件沒有根目錄檢查，會照資料庫給的路徑刪掉附件目錄以外的檔案。
        await using var fixture = await ProjectServiceFixture.CreateAsync();
        var ids = await fixture.SeedDefaultProjectsAsync();
        var outside = Path.Combine(fixture.Sandbox, "outside.txt");
        await File.WriteAllTextAsync(outside, "KEEP");
        var fileId = await fixture.SeedFileAsync(ids["公開專案"], "outside.txt", "text/plain", content: null, relativePath: "../outside.txt");
        var service = fixture.CreateService(isAdmin: true);
        var project = await service.GetAsync(ids["公開專案"]);

        var result = await service.UpdateAsync(project, removedFileIds: [fileId]);

        Assert.True(result.Success, result.Message);
        Assert.True(File.Exists(outside), "移除附件時刪掉了附件根目錄以外的檔案。");
        Assert.Equal(0, await fixture.Context.ProjectFile.CountAsync());
    }

    [Fact]
    public async Task UpdateAsync_WhenRemovingAnAttachmentFailsToSave_ShouldKeepThePhysicalFile()
    {
        // 0.9.96 之前是先刪實體檔再存資料庫：存檔失敗時資料列還在、檔案卻已經不見了。
        await using var fixture = await ProjectServiceFixture.CreateAsync();
        var ids = await fixture.SeedDefaultProjectsAsync();
        var fileId = await fixture.SeedFileAsync(ids["公開專案"], "a.txt", "text/plain", content: "attachment", relativePath: "2026/08/keep.txt");
        var physical = Path.Combine(fixture.FileRoot, "2026", "08", "keep.txt");
        var service = fixture.CreateService(isAdmin: true, interceptor: new FailWhenRemovingFiles());
        var project = await service.GetAsync(ids["公開專案"]);

        var result = await service.UpdateAsync(project, removedFileIds: [fileId]);

        Assert.False(result.Success);
        Assert.True(File.Exists(physical), "存檔失敗，附件實體檔卻已經被刪了。");
        Assert.Equal(1, await fixture.Context.ProjectFile.CountAsync());
    }

    private sealed class FailWhenRemovingFiles : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
            => eventData.Context!.ChangeTracker.Entries<ProjectFile>().Any(e => e.State == EntityState.Deleted)
                ? throw new InvalidOperationException("simulated database failure")
                : base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private sealed class InterceptingFactory(SqliteConnection connection, IInterceptor interceptor) : IDbContextFactory<BackendDBContext>
    {
        public BackendDBContext CreateDbContext()
            => new(new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connection).AddInterceptors(interceptor).Options);
    }


    // ---- 分頁排序退路（0.4.46）----
    // ProjectService 原本連預設排序都沒有；這兩個測例補上「欄位不認得」與「方向未指定」兩種落空輸入。

    [Fact]
    public async Task GetAsync_WithUnknownSortField_ShouldFallBackToDefaultOrder()
    {
        await using var fixture = await ProjectServiceFixture.CreateAsync();
        await fixture.SeedOrderedProjectsAsync();
        var service = fixture.CreateService(isAdmin: true);

        var request = NewRequest();
        request.SortField = "NotAColumn";
        request.SortDescending = true;

        var result = await service.GetAsync(request);

        Assert.Equal(["最新", "中間", "最舊"], result.Result.Select(x => x.Title).ToList());
    }

    [Fact]
    public async Task GetAsync_WithNullSortDescending_ShouldFallBackToDefaultOrder()
    {
        await using var fixture = await ProjectServiceFixture.CreateAsync();
        await fixture.SeedOrderedProjectsAsync();
        var service = fixture.CreateService(isAdmin: true);

        var request = NewRequest();
        request.SortField = nameof(ProjectAdapterModel.Title);
        request.SortDescending = null;

        var result = await service.GetAsync(request);

        // 依 Title 排會是「中間／最新／最舊」，因此這組斷言足以區分「有沒有退回預設排序」。
        Assert.Equal(["最新", "中間", "最舊"], result.Result.Select(x => x.Title).ToList());
    }

    private static DataRequest NewRequest() => new()
    {
        Search = string.Empty,
        SortField = string.Empty,
        CurrentPage = 1,
        PageSize = 50,
        Take = 0,
    };

    private sealed class FakeScopeProvider(bool isAdmin, IReadOnlyList<string> teams) : IRecordAccessScopeProvider
    {
        public Task<RecordAccessScope> GetAsync() => Task.FromResult(new RecordAccessScope(isAdmin, teams));
    }

    private sealed class ProjectServiceFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly IMapper mapper;
        private readonly ILoggerFactory loggerFactory;

        private ProjectServiceFixture(SqliteConnection connection, BackendDBContext context, string sandbox)
        {
            this.connection = connection;
            Context = context;
            Sandbox = sandbox;
            FileRoot = Path.Combine(sandbox, "root");

            loggerFactory = LoggerFactory.Create(_ => { });
            var mapperConfiguration = new MapperConfiguration(
                configuration => configuration.AddProfile<AutoMapping>(),
                loggerFactory);
            mapper = mapperConfiguration.CreateMapper();
        }

        public BackendDBContext Context { get; }

        /// <summary>附件根目錄。附件下載會比對實體路徑，因此不能沿用空字串設定。</summary>
        public string FileRoot { get; }

        /// <summary>FileRoot 的上層；逸出測試把「根目錄外」的檔案放在這裡。</summary>
        public string Sandbox { get; }

        public static async Task<ProjectServiceFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<BackendDBContext>()
                .UseSqlite(connection)
                .Options;

            var context = new BackendDBContext(options);
            await context.Database.EnsureCreatedAsync();

            // 附件根目錄放在一個「自己的」上層目錄底下，逸出測試才有地方擺根目錄外的檔案，
            // 而且不會與平行執行的其他 fixture 撞名。
            var sandbox = Path.Combine(Path.GetTempPath(), $"CustomerHelpAgent-ProjectFile-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.Combine(sandbox, "root"));

            return new ProjectServiceFixture(connection, context, sandbox);
        }

        public ProjectService CreateService(bool isAdmin, params string[] teams) => CreateService(isAdmin, null, teams);

        public ProjectService CreateService(bool isAdmin, IInterceptor? interceptor, params string[] teams)
        {
            var settings = new SystemSettings();
            settings.ExternalFileSystem.ProjectFilePath = FileRoot;

            return new ProjectService(
                interceptor is null ? new TestDbContextFactory(connection) : new InterceptingFactory(connection, interceptor),
                mapper,
                loggerFactory.CreateLogger<ProjectService>(),
                Options.Create(settings),
                new FakeScopeProvider(isAdmin, teams),
                new RecordingAuditLogService(),
                new CustomerHelpAgent.Business.Services.Other.CurrentUserService(),
                new CustomerHelpAgent.Business.Services.Other.ProjectFileStore(Options.Create(settings), loggerFactory.CreateLogger<CustomerHelpAgent.Business.Services.Other.ProjectFileStore>()));
        }

        /// <summary>
        /// 建立一筆附件 metadata；<paramref name="content"/> 為 null 代表只建 metadata、不寫實體檔案。
        /// </summary>
        public async Task<int> SeedFileAsync(
            int projectId,
            string originalFileName,
            string contentType,
            string? content,
            string? relativePath = null)
        {
            relativePath ??= $"2026/08/{Guid.NewGuid():N}{Path.GetExtension(originalFileName)}";

            if (content is not null)
            {
                var fullPath = Path.Combine(FileRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                await File.WriteAllTextAsync(fullPath, content);
            }

            var file = new ProjectFile
            {
                ProjectId = projectId,
                OriginalFileName = originalFileName,
                StoredFileName = Path.GetFileName(relativePath),
                RelativePath = relativePath,
                ContentType = contentType,
                FileSize = content?.Length ?? 0,
            };

            Context.ProjectFile.Add(file);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();

            return file.Id;
        }

        public async Task<Dictionary<string, int>> SeedDefaultProjectsAsync()
        {
            var pub = NewProject("公開專案", null);
            var teamA = NewProject("團隊A專案", ["團隊A"]);
            var teamB = NewProject("團隊B專案", ["團隊B"]);

            Context.Project.AddRange(pub, teamA, teamB);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();

            return new Dictionary<string, int>
            {
                ["公開專案"] = pub.Id,
                ["團隊A專案"] = teamA.Id,
                ["團隊B專案"] = teamB.Id,
            };
        }

        /// <summary>建立三筆 UpdatedAt 明確不同的資料，供預設排序斷言使用。</summary>
        public async Task SeedOrderedProjectsAsync()
        {
            var baseTime = new DateTime(2026, 8, 27, 10, 0, 0);
            var oldest = NewProject("最舊", null);
            oldest.UpdatedAt = baseTime;
            var middle = NewProject("中間", null);
            middle.UpdatedAt = baseTime.AddMinutes(10);
            var newest = NewProject("最新", null);
            newest.UpdatedAt = baseTime.AddMinutes(20);

            Context.Project.AddRange(oldest, middle, newest);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
        }

        private static Project NewProject(string title, IEnumerable<string>? teams) => new()
        {
            Title = title,
            Status = "未開始",
            Priority = "中",
            Owner = "tester",
            Teams = TagStringHelper.ToStored(teams),
        };

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
            loggerFactory.Dispose();

            try
            {
                Directory.Delete(Sandbox, recursive: true);
            }
            catch (IOException)
            {
                // 清理是盡力而為，不該讓斷言結果被作業系統的檔案時序影響。
            }
        }
    }
}

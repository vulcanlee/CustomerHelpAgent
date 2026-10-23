using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Repositories;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// Repository（Web API 路徑）的唯一性判定。
///
/// 這一層先前完全沒有測試，因此「API 不 Trim、區分大小寫」這個與 Blazor 路徑不一致的
/// 破口從未被發現：同一份資料 UI 擋得下、API 卻塞得進去。
/// 兩條路徑的判定語意必須一致。
/// </summary>
public sealed class CategoryTeamRepositoryUniquenessTests
{
    [Fact]
    public async Task CategoryExistsByName_WithUntrimmedInput_ShouldMatch()
    {
        await using var fixture = await RepositoryFixture.CreateAsync();
        fixture.Context.Category.Add(new Category { Name = "技術文件" });
        await fixture.Context.SaveChangesAsync();

        Assert.True(await fixture.CreateCategoryRepository().ExistsByNameAsync("  技術文件  "));
    }

    [Fact]
    public async Task CategoryExistsByName_WithDifferentCase_ShouldMatch()
    {
        await using var fixture = await RepositoryFixture.CreateAsync();
        fixture.Context.Category.Add(new Category { Name = "Report" });
        await fixture.Context.SaveChangesAsync();

        Assert.True(await fixture.CreateCategoryRepository().ExistsByNameAsync("report"));
    }

    [Fact]
    public async Task CategoryExistsByName_WhenExcludingSelf_ShouldNotMatch()
    {
        await using var fixture = await RepositoryFixture.CreateAsync();
        var category = new Category { Name = "技術文件" };
        fixture.Context.Category.Add(category);
        await fixture.Context.SaveChangesAsync();

        Assert.False(await fixture.CreateCategoryRepository().ExistsByNameAsync("技術文件", category.Id));
    }

    [Fact]
    public async Task TeamExistsByName_WithUntrimmedInputAndDifferentCase_ShouldMatch()
    {
        await using var fixture = await RepositoryFixture.CreateAsync();
        fixture.Context.Team.Add(new Team { Name = "RD Team" });
        await fixture.Context.SaveChangesAsync();

        Assert.True(await fixture.CreateTeamRepository().ExistsByNameAsync(" rd team "));
    }

    [Fact]
    public async Task TeamExistsByCode_WithUntrimmedInputAndDifferentCase_ShouldMatch()
    {
        await using var fixture = await RepositoryFixture.CreateAsync();
        fixture.Context.Team.Add(new Team { Name = "研發部", Code = "RD" });
        await fixture.Context.SaveChangesAsync();

        Assert.True(await fixture.CreateTeamRepository().ExistsByCodeAsync(" rd "));
    }

    [Fact]
    public async Task TeamExistsByCode_WithBlankInput_ShouldNotMatch()
    {
        // 「未填代號」不算重複，否則第二個沒填代號的團隊會被誤擋。
        await using var fixture = await RepositoryFixture.CreateAsync();
        fixture.Context.Team.Add(new Team { Name = "研發部", Code = null });
        await fixture.Context.SaveChangesAsync();

        var repository = fixture.CreateTeamRepository();
        Assert.False(await repository.ExistsByCodeAsync("   "));
        Assert.False(await repository.ExistsByCodeAsync(string.Empty));
    }

    /// <summary>API 路徑的修改也帶回差異（0.9.131 起），由 Controller 寫進稽核的詳細內容檔。</summary>
    [Fact]
    public async Task CategoryUpdate_ShouldReturnTheDiff()
    {
        await using var fixture = await RepositoryFixture.CreateAsync();
        var category = new Category { Name = "舊名稱", Description = "說明" };
        fixture.Context.Category.Add(category);
        await fixture.Context.SaveChangesAsync();
        fixture.Context.ChangeTracker.Clear();

        var outcome = await fixture.CreateCategoryRepository().UpdateAsync(
            new Category { Id = category.Id, Name = "新名稱", Description = "說明", ConcurrencyStamp = category.ConcurrencyStamp });

        Assert.True(outcome.Updated);
        Assert.Equal($"分類 #{category.Id}（舊名稱）修改\nName: 舊名稱 → 新名稱", outcome.Diff);
    }

    private sealed class RepositoryFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly ILoggerFactory loggerFactory;

        private RepositoryFixture(SqliteConnection connection, BackendDBContext context)
        {
            this.connection = connection;
            Context = context;
            loggerFactory = LoggerFactory.Create(_ => { });
        }

        /// <summary>Repository 走 scoped DbContext（API 路徑的慣例），這裡直接共用同一個。</summary>
        public BackendDBContext Context { get; }

        public static async Task<RepositoryFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<BackendDBContext>()
                .UseSqlite(connection)
                .Options;

            var context = new BackendDBContext(options);
            await context.Database.EnsureCreatedAsync();

            return new RepositoryFixture(connection, context);
        }

        public CategoryRepository CreateCategoryRepository()
            => new(Context, new FakeRecordAccessScopeProvider(true, []), loggerFactory.CreateLogger<CategoryRepository>());

        public TeamRepository CreateTeamRepository()
            => new(Context, new ContextTeamTreeCache(Context), loggerFactory.CreateLogger<TeamRepository>());

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
            loggerFactory.Dispose();
        }
    }
}

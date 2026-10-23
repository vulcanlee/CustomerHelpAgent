using AutoMapper;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Models.AdapterModel;
using CustomerHelpAgent.Models.Systems;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 分類的團隊可見性。
///
/// 注意：這裡的規則刻意與紀錄（見 <see cref="ProjectServiceTeamAccessTests"/>）不同 ——
/// 紀錄是「使用者沒有團隊 → 只看得到公開紀錄」，分類是「使用者沒有團隊 → 看得到全部」。
/// 分類可見性只是下拉清單的便利性過濾，不是安全邊界。
/// </summary>
public sealed class CategoryServiceTeamVisibilityTests
{
    [Fact]
    public async Task GetAsync_Admin_ShouldSeeAllCategories()
    {
        await using var fixture = await CategoryVisibilityFixture.CreateAsync();
        await fixture.SeedDefaultCategoriesAsync();
        var service = fixture.CreateService(isAdmin: true);

        var result = await service.GetAsync(NewRequest());

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public async Task GetAsync_NonAdminWithoutTeams_ShouldSeeAllCategories()
    {
        // 與紀錄相反：沒綁團隊的使用者視為不受限，看得到全部分類。
        await using var fixture = await CategoryVisibilityFixture.CreateAsync();
        await fixture.SeedDefaultCategoriesAsync();
        var service = fixture.CreateService(isAdmin: false);

        var result = await service.GetAsync(NewRequest());

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public async Task GetAsync_NonAdminWithTeams_ShouldSeeOnlyPublicOrIntersectingCategories()
    {
        await using var fixture = await CategoryVisibilityFixture.CreateAsync();
        await fixture.SeedDefaultCategoriesAsync();
        var service = fixture.CreateService(isAdmin: false, "團隊A");

        var result = await service.GetAsync(NewRequest());
        var names = result.Result.Select(x => x.Name).OrderBy(x => x).ToList();

        Assert.Equal(["公用分類", "團隊A分類"], names);
    }

    [Fact]
    public async Task GetAllEnabledNamesAsync_NonAdminWithTeams_ShouldFilterByTeamAndSkipDisabled()
    {
        await using var fixture = await CategoryVisibilityFixture.CreateAsync();
        await fixture.SeedDefaultCategoriesAsync();
        await fixture.AddCategoryAsync("已停用的團隊A分類", ["團隊A"], isEnabled: false);
        var service = fixture.CreateService(isAdmin: false, "團隊A");

        var names = await service.GetAllEnabledNamesAsync();

        Assert.Equal(["公用分類", "團隊A分類"], names);
    }

    [Fact]
    public async Task GetAllEnabledNamesAsync_NonAdminWithoutTeams_ShouldReturnAllEnabled()
    {
        await using var fixture = await CategoryVisibilityFixture.CreateAsync();
        await fixture.SeedDefaultCategoriesAsync();
        var service = fixture.CreateService(isAdmin: false);

        var names = await service.GetAllEnabledNamesAsync();

        Assert.Equal(["公用分類", "團隊A分類", "團隊B分類"], names);
    }

    [Fact]
    public async Task GetById_NonAdmin_ShouldDenyCategoryOutsideTeamScope()
    {
        await using var fixture = await CategoryVisibilityFixture.CreateAsync();
        var ids = await fixture.SeedDefaultCategoriesAsync();
        var service = fixture.CreateService(isAdmin: false, "團隊A");

        var denied = await service.GetAsync(ids["團隊B分類"]);
        var allowed = await service.GetAsync(ids["團隊A分類"]);

        Assert.Equal(0, denied.Id); // 守門回空模型
        Assert.Equal("團隊A分類", allowed.Name);
    }

    [Fact]
    public async Task BeforeAddCheckAsync_WithNameOfInvisibleCategory_ShouldStillFail()
    {
        // 名稱唯一性是全域的：看不到不代表可以再建一筆同名分類。
        await using var fixture = await CategoryVisibilityFixture.CreateAsync();
        await fixture.SeedDefaultCategoriesAsync();
        var service = fixture.CreateService(isAdmin: false, "團隊A");

        var result = await service.BeforeAddCheckAsync(new CategoryAdapterModel { Name = "團隊B分類" });

        Assert.False(result.Success);
    }

    [Fact]
    public async Task AddAsync_ShouldRoundTripTeamsBetweenListAndStoredString()
    {
        await using var fixture = await CategoryVisibilityFixture.CreateAsync();
        var service = fixture.CreateService(isAdmin: true);

        await service.AddAsync(new CategoryAdapterModel { Name = "技術文件", Teams = ["團隊A", "團隊B"] });

        var saved = await fixture.Context.Category.AsNoTracking().SingleAsync(x => x.Name == "技術文件");
        Assert.Equal(TagStringHelper.ToStored(["團隊A", "團隊B"]), saved.Teams);

        var reloaded = await service.GetAsync(saved.Id);
        Assert.Equal(["團隊A", "團隊B"], reloaded.Teams);
        Assert.Equal("團隊A、團隊B", reloaded.TeamsText);
    }

    [Fact]
    public async Task UpdateAsync_WithEmptyTeams_ShouldStoreNullAsPublicCategory()
    {
        await using var fixture = await CategoryVisibilityFixture.CreateAsync();
        var ids = await fixture.SeedDefaultCategoriesAsync();
        var service = fixture.CreateService(isAdmin: true);

        var target = await service.GetAsync(ids["團隊A分類"]);
        target.Teams = [];
        await service.UpdateAsync(target);

        var saved = await fixture.Context.Category.AsNoTracking().SingleAsync(x => x.Id == ids["團隊A分類"]);
        Assert.Null(saved.Teams);
    }

    private static DataRequest NewRequest() => new()
    {
        Search = string.Empty,
        SortField = string.Empty,
        CurrentPage = 1,
        PageSize = 50,
        Take = 0,
    };

    private sealed class CategoryVisibilityFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly IMapper mapper;
        private readonly ILoggerFactory loggerFactory;

        private CategoryVisibilityFixture(SqliteConnection connection, BackendDBContext context)
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

        public static async Task<CategoryVisibilityFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<BackendDBContext>()
                .UseSqlite(connection)
                .Options;

            var context = new BackendDBContext(options);
            await context.Database.EnsureCreatedAsync();

            return new CategoryVisibilityFixture(connection, context);
        }

        public CategoryService CreateService(bool isAdmin, params string[] teams)
        {
            return new CategoryService(
                new TestDbContextFactory(connection),
                mapper,
                loggerFactory.CreateLogger<CategoryService>(),
                new FakeRecordAccessScopeProvider(isAdmin, teams),
                new RecordingAuditLogService(),
                new CustomerHelpAgent.Business.Services.Other.CurrentUserService());
        }

        public async Task<Category> AddCategoryAsync(string name, IEnumerable<string>? teams, bool isEnabled = true)
        {
            var category = new Category
            {
                Name = name,
                IsEnabled = isEnabled,
                Teams = TagStringHelper.ToStored(teams),
            };

            Context.Category.Add(category);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
            return category;
        }

        public async Task<Dictionary<string, int>> SeedDefaultCategoriesAsync()
        {
            var pub = await AddCategoryAsync("公用分類", null);
            var teamA = await AddCategoryAsync("團隊A分類", ["團隊A"]);
            var teamB = await AddCategoryAsync("團隊B分類", ["團隊B"]);

            return new Dictionary<string, int>
            {
                ["公用分類"] = pub.Id,
                ["團隊A分類"] = teamA.Id,
                ["團隊B分類"] = teamB.Id,
            };
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
            loggerFactory.Dispose();
        }
    }
}

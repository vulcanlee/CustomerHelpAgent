using AutoMapper;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Models.AdapterModel;
using CustomerHelpAgent.Models.Systems;

namespace CustomerHelpAgent.Tests;

public sealed class TeamServiceTests
{
    [Fact]
    public async Task AddUpdateDelete_ShouldWriteAuditWithCurrentUser()
    {
        // LOG-14：0.9.78 之前分類、團隊、專案的增刪改完全沒有稽核紀錄。
        await using var fixture = await TeamServiceFixture.CreateAsync();
        fixture.CurrentUser.CurrentUser = new CustomerHelpAgent.Models.Others.CurrentUser { Id = 7, Account = "alice" };
        var service = fixture.CreateService();

        Assert.True((await service.AddAsync(new TeamAdapterModel { Name = "研發部", Code = "RD" })).Success);
        var created = await fixture.Context.Team.AsNoTracking().SingleAsync();
        // 0.9.93 起編輯必須帶開窗時讀到的版本號（樂觀並行），否則視為衝突。
        Assert.True((await service.UpdateAsync(new TeamAdapterModel { Id = created.Id, Name = "研發一部", Code = "RD", ConcurrencyStamp = created.ConcurrencyStamp })).Success);
        Assert.True((await service.DeleteAsync(created.Id)).Success);

        Assert.Equal(
            [CustomerHelpAgent.Business.Helpers.AuditActions.Team.Create, CustomerHelpAgent.Business.Helpers.AuditActions.Team.Update, CustomerHelpAgent.Business.Helpers.AuditActions.Team.Delete],
            fixture.Audit.Entries.Select(x => x.Action));
        Assert.All(fixture.Audit.Entries, entry =>
        {
            Assert.True(entry.Success);
            Assert.Equal(7, entry.ActorUserId);
            Assert.Equal("alice", entry.ActorAccount);
        });
        // 0.9.105 起帶上層部門；改名時再帶舊名稱與同步更新的筆數。
        Assert.Equal("name=研發一部; parentId=; from=研發部; renamedProjects=0; renamedCategories=0; renamedRoles=0", fixture.Audit.Entries[1].Detail);
    }

    [Fact]
    public async Task AddAsync_WhenRejected_ShouldNotWriteAudit()
    {
        await using var fixture = await TeamServiceFixture.CreateAsync();
        await fixture.AddTeamAsync("研發部", "RD");
        var service = fixture.CreateService();

        // 繞過前置檢查直接撞唯一索引：失敗的寫入不得留下稽核。
        var result = await service.AddAsync(new TeamAdapterModel { Name = "研發部", Code = "RD9" });

        Assert.False(result.Success);
        Assert.Empty(fixture.Audit.Entries);
    }

    [Fact]
    public async Task BeforeAddCheckAsync_WithUniqueNameAndCode_ShouldSucceed()
    {
        await using var fixture = await TeamServiceFixture.CreateAsync();
        var service = fixture.CreateService();

        var result = await service.BeforeAddCheckAsync(new TeamAdapterModel { Name = "研發部", Code = "RD" });

        Assert.True(result.Success);
    }

    [Fact]
    public async Task BeforeAddCheckAsync_WithDuplicateName_ShouldFail()
    {
        await using var fixture = await TeamServiceFixture.CreateAsync();
        await fixture.AddTeamAsync("研發部", "RD");
        var service = fixture.CreateService();

        var result = await service.BeforeAddCheckAsync(new TeamAdapterModel { Name = "研發部", Code = "RD2" });

        Assert.False(result.Success);
    }

    [Fact]
    public async Task BeforeAddCheckAsync_WithDuplicateCode_ShouldFail()
    {
        await using var fixture = await TeamServiceFixture.CreateAsync();
        await fixture.AddTeamAsync("研發部", "RD");
        var service = fixture.CreateService();

        var result = await service.BeforeAddCheckAsync(new TeamAdapterModel { Name = "業務部", Code = "RD" });

        Assert.False(result.Success);
    }

    [Fact]
    public async Task BeforeAddCheckAsync_WithEmptyCode_ShouldSucceedEvenIfAnotherEmptyCodeExists()
    {
        await using var fixture = await TeamServiceFixture.CreateAsync();
        await fixture.AddTeamAsync("研發部", null);
        var service = fixture.CreateService();

        var result = await service.BeforeAddCheckAsync(new TeamAdapterModel { Name = "業務部", Code = null });

        Assert.True(result.Success);
    }

    [Fact]
    public async Task BeforeUpdateCheckAsync_WithSameRecord_ShouldSucceed()
    {
        await using var fixture = await TeamServiceFixture.CreateAsync();
        var existing = await fixture.AddTeamAsync("研發部", "RD");
        var service = fixture.CreateService();

        var result = await service.BeforeUpdateCheckAsync(new TeamAdapterModel { Id = existing.Id, Name = "研發部", Code = "RD" });

        Assert.True(result.Success);
    }

    [Fact]
    public async Task BeforeUpdateCheckAsync_WithCodeUsedByOtherRecord_ShouldFail()
    {
        await using var fixture = await TeamServiceFixture.CreateAsync();
        await fixture.AddTeamAsync("研發部", "RD");
        var other = await fixture.AddTeamAsync("業務部", "SALES");
        var service = fixture.CreateService();

        var result = await service.BeforeUpdateCheckAsync(new TeamAdapterModel { Id = other.Id, Name = "業務部", Code = "RD" });

        Assert.False(result.Success);
    }

    [Fact]
    public async Task AddAsync_ShouldPersistTeam()
    {
        await using var fixture = await TeamServiceFixture.CreateAsync();
        var service = fixture.CreateService();

        var result = await service.AddAsync(new TeamAdapterModel { Name = "研發部", Code = "RD", IsEnabled = true });

        Assert.True(result.Success);
        var saved = await fixture.Context.Team.AsNoTracking().SingleAsync(x => x.Name == "研發部");
        Assert.Equal("RD", saved.Code);
        Assert.True(saved.IsEnabled);
    }

    [Fact]
    public async Task AddAsync_WithUntrimmedNameAndCode_ShouldPersistTrimmedValues()
    {
        await using var fixture = await TeamServiceFixture.CreateAsync();
        var service = fixture.CreateService();

        await service.AddAsync(new TeamAdapterModel { Name = " 研發部 ", Code = " RD " });

        var saved = await fixture.Context.Team.AsNoTracking().SingleAsync();
        Assert.Equal("研發部", saved.Name);
        Assert.Equal("RD", saved.Code);
    }

    [Fact]
    public async Task AddAsync_WithBlankCode_ShouldPersistNull()
    {
        // 「未填代號」在資料庫中必須只有 NULL 一種表示法：
        // SQLite 的唯一索引視 NULL 互不相等，但空字串彼此相同。
        await using var fixture = await TeamServiceFixture.CreateAsync();
        var service = fixture.CreateService();

        await service.AddAsync(new TeamAdapterModel { Name = "研發部", Code = "   " });

        var saved = await fixture.Context.Team.AsNoTracking().SingleAsync();
        Assert.Null(saved.Code);
    }

    [Fact]
    public async Task AddAsync_TwoTeamsWithoutCode_ShouldBothSucceed()
    {
        // Code 上有唯一索引，但沒填代號的團隊必須能有很多個。
        await using var fixture = await TeamServiceFixture.CreateAsync();
        var service = fixture.CreateService();

        var first = await service.AddAsync(new TeamAdapterModel { Name = "研發部", Code = null });
        var second = await service.AddAsync(new TeamAdapterModel { Name = "業務部", Code = "" });

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Equal(2, await fixture.Context.Team.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task BeforeAddCheckAsync_AfterAddingUntrimmedName_ShouldRejectTrimmedName()
    {
        await using var fixture = await TeamServiceFixture.CreateAsync();
        var service = fixture.CreateService();
        await service.AddAsync(new TeamAdapterModel { Name = "研發部 " });

        var result = await service.BeforeAddCheckAsync(new TeamAdapterModel { Name = "研發部" });

        Assert.False(result.Success);
    }

    [Fact]
    public async Task BeforeAddCheckAsync_WithDuplicateCodeDifferentCase_ShouldFail()
    {
        await using var fixture = await TeamServiceFixture.CreateAsync();
        await fixture.AddTeamAsync("研發部", "RD");
        var service = fixture.CreateService();

        var result = await service.BeforeAddCheckAsync(new TeamAdapterModel { Name = "業務部", Code = "rd" });

        Assert.False(result.Success);
    }

    [Fact]
    public async Task AddAsync_WithDuplicateName_ShouldReturnFriendlyMessage()
    {
        // 略過前置檢查直接寫入，驗證唯一索引兜底與訊息轉譯。
        await using var fixture = await TeamServiceFixture.CreateAsync();
        await fixture.AddTeamAsync("研發部", null);
        var service = fixture.CreateService();

        var result = await service.AddAsync(new TeamAdapterModel { Name = "研發部" });

        Assert.False(result.Success);
        Assert.Equal("團隊名稱已存在，無法儲存。", result.Message);
    }

    [Fact]
    public async Task AddAsync_WithDuplicateCode_ShouldReturnFriendlyMessage()
    {
        await using var fixture = await TeamServiceFixture.CreateAsync();
        await fixture.AddTeamAsync("研發部", "RD");
        var service = fixture.CreateService();

        var result = await service.AddAsync(new TeamAdapterModel { Name = "業務部", Code = "RD" });

        Assert.False(result.Success);
        Assert.Equal("團隊代號已存在，無法儲存。", result.Message);
    }


    // ---- 分頁排序退路（0.4.46）----
    // 三個測例對應三種「排序落空」的輸入；落空時若沒有退回預設排序，
    // Skip/Take 就會少了 OrderBy，分頁順序不保證（TestDbContextFactory 會直接擲例外）。

    [Fact]
    public async Task GetAsync_WithoutSortField_ShouldOrderByUpdatedAtDescending()
    {
        await using var fixture = await TeamServiceFixture.CreateAsync();
        await fixture.SeedOrderedTeamsAsync();
        var service = fixture.CreateService();

        var result = await service.GetAsync(NewRequest());

        Assert.Equal(["最新", "中間", "最舊"], result.Result.Select(x => x.Name).ToList());
    }

    [Fact]
    public async Task GetAsync_WithUnknownSortField_ShouldFallBackToDefaultOrder()
    {
        await using var fixture = await TeamServiceFixture.CreateAsync();
        await fixture.SeedOrderedTeamsAsync();
        var service = fixture.CreateService();

        var result = await service.GetAsync(NewRequest("NotAColumn", sortDescending: true));

        Assert.Equal(["最新", "中間", "最舊"], result.Result.Select(x => x.Name).ToList());
    }

    [Fact]
    public async Task GetAsync_WithNullSortDescending_ShouldFallBackToDefaultOrder()
    {
        await using var fixture = await TeamServiceFixture.CreateAsync();
        await fixture.SeedOrderedTeamsAsync();
        var service = fixture.CreateService();

        var result = await service.GetAsync(NewRequest(nameof(TeamAdapterModel.Name), sortDescending: null));

        // 依 Name 排會是「中間／最新／最舊」，因此這組斷言足以區分「有沒有退回預設排序」。
        Assert.Equal(["最新", "中間", "最舊"], result.Result.Select(x => x.Name).ToList());
    }

    private static DataRequest NewRequest(string sortField = "", bool? sortDescending = null) => new()
    {
        Search = string.Empty,
        SortField = sortField,
        SortDescending = sortDescending,
        CurrentPage = 1,
        PageSize = 50,
        Take = 0,
    };

    private sealed class TeamServiceFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly IMapper mapper;
        private readonly ILoggerFactory loggerFactory;

        private TeamServiceFixture(SqliteConnection connection, BackendDBContext context)
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

        /// <summary>部門樹（0.9.105 起）；每次重讀，也記下失效次數。</summary>
        public ContextTeamTreeCache Tree => tree ??= new ContextTeamTreeCache(Context);

        private ContextTeamTreeCache? tree;

        public static async Task<TeamServiceFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<BackendDBContext>()
                .UseSqlite(connection)
                .Options;

            var context = new BackendDBContext(options);
            await context.Database.EnsureCreatedAsync();

            return new TeamServiceFixture(connection, context);
        }

        /// <summary>CreateService 建出來的服務寫進這裡的稽核（LOG-14）。</summary>
        public RecordingAuditLogService Audit { get; } = new();

        public CustomerHelpAgent.Business.Services.Other.CurrentUserService CurrentUser { get; } = new();

        public TeamService CreateService()
        {
            return new TeamService(
                new TestDbContextFactory(connection),
                mapper,
                loggerFactory.CreateLogger<TeamService>(),
                Audit,
                CurrentUser,
                Tree);
        }

        public async Task<Team> AddTeamAsync(string name, string? code)
        {
            var team = new Team { Name = name, Code = code };
            Context.Team.Add(team);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
            return team;
        }

        /// <summary>建立三筆 UpdatedAt 明確不同的資料，供預設排序斷言使用。</summary>
        public async Task SeedOrderedTeamsAsync()
        {
            var baseTime = new DateTime(2026, 8, 27, 10, 0, 0);
            Context.Team.AddRange(
                new Team { Name = "最舊", UpdatedAt = baseTime },
                new Team { Name = "中間", UpdatedAt = baseTime.AddMinutes(10) },
                new Team { Name = "最新", UpdatedAt = baseTime.AddMinutes(20) });
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
            loggerFactory.Dispose();
        }
    }
}

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Systems;

namespace CustomerHelpAgent.Tests;

public sealed class AuditLogServiceTests
{
    [Fact]
    public async Task WriteAsync_ShouldPersistAuditRow()
    {
        await using var fixture = await AuditFixture.CreateAsync();
        var service = fixture.CreateService();

        await service.WriteAsync(
            action: "Login.Success",
            success: true,
            actorUserId: 7,
            actorAccount: "alice",
            targetType: "MyUser",
            targetId: "7",
            detail: "unit test");

        var saved = await fixture.Context.AuditLog.AsNoTracking().SingleAsync();
        Assert.Equal("Login.Success", saved.Action);
        Assert.True(saved.Success);
        Assert.Equal(7, saved.ActorUserId);
        Assert.Equal("alice", saved.ActorAccount);
        Assert.Equal("MyUser", saved.TargetType);
        Assert.Equal("7", saved.TargetId);
        Assert.NotEqual(default, saved.OccurredAt);
        Assert.Null(saved.ClientIp); // 沒有 IClientIpProvider（例如排程作業的情境）就不記 IP
    }

    [Fact]
    public async Task WriteAsync_ShouldPersistClientIpFromProvider()
    {
        await using var fixture = await AuditFixture.CreateAsync();
        var service = fixture.CreateService(new FixedClientIpProvider("203.0.113.5"));

        await service.WriteAsync("Login.Success", actorUserId: 7, actorAccount: "alice");

        var saved = await fixture.Context.AuditLog.AsNoTracking().SingleAsync();
        Assert.Equal("203.0.113.5", saved.ClientIp);
    }

    [Fact]
    public async Task WriteAsync_WhenPersistFails_ShouldNotThrow()
    {
        var fixture = await AuditFixture.CreateAsync();
        var service = fixture.CreateService();
        await fixture.DisposeAsync(); // context disposed → write will fail internally

        var exception = await Record.ExceptionAsync(() => service.WriteAsync("Login.Failed", success: false));

        Assert.Null(exception);
    }

    [Fact]
    public async Task WriteAsync_WithContent_ShouldWriteAFile_AndKeepOnlyThePathInTheRow()
    {
        await using var fixture = await AuditFixture.CreateAsync();
        var store = fixture.CreateContentStore();
        var service = fixture.CreateService(contentFileStore: store);

        await service.WriteAsync("Category.Update", detail: "name=分類B", content: "Name: 分類A → 分類B");

        var saved = await fixture.Context.AuditLog.AsNoTracking().SingleAsync();
        Assert.Equal("name=分類B", saved.Detail);
        Assert.NotNull(saved.ContentFile);
        Assert.Equal("Name: 分類A → 分類B", await store.ReadAsync(saved.ContentFile));
    }

    [Fact]
    public async Task WriteAsync_WithoutContent_ShouldNotWriteAFile()
    {
        await using var fixture = await AuditFixture.CreateAsync();
        var service = fixture.CreateService(contentFileStore: fixture.CreateContentStore());

        await service.WriteAsync("Page.View", detail: "from=/");

        Assert.Null((await fixture.Context.AuditLog.AsNoTracking().SingleAsync()).ContentFile);
        Assert.False(Directory.Exists(fixture.ContentRoot) && Directory.EnumerateFiles(fixture.ContentRoot, "*", SearchOption.AllDirectories).Any());
    }

    /// <summary>每筆稽核同步記一行 Debug（0.9.131 起），只有資料庫的短摘要，內容檔（搜尋原文、差異）不進日誌。</summary>
    [Fact]
    public async Task WriteAsync_ShouldLogADebugLine_WithoutTheContent()
    {
        await using var fixture = await AuditFixture.CreateAsync();
        var logger = new CapturingLogger<AuditLogService>();
        var service = fixture.CreateService(contentFileStore: fixture.CreateContentStore(), logger: logger);

        await service.WriteAsync("Category.Search", actorUserId: 7, actorAccount: "alice", detail: "hasKeyword=true", content: "keyword: 王小明");

        var (level, message, _) = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Debug, level);
        Assert.Contains("Audit recorded. Action=Category.Search", message, StringComparison.Ordinal);
        Assert.Contains("Detail=hasKeyword=true", message, StringComparison.Ordinal);
        Assert.DoesNotContain("王小明", message, StringComparison.Ordinal);
    }

    private sealed class AuditFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly ILoggerFactory loggerFactory;

        private AuditFixture(SqliteConnection connection, BackendDBContext context)
        {
            this.connection = connection;
            Context = context;
            loggerFactory = LoggerFactory.Create(_ => { });
        }

        public BackendDBContext Context { get; }

        public string ContentRoot { get; } = Path.Combine(Path.GetTempPath(), "audit-service-content-" + Guid.NewGuid().ToString("N"));

        public AuditContentFileStore CreateContentStore()
        {
            var settings = new SystemSettings();
            settings.ExternalFileSystem.AuditContentPath = ContentRoot;
            return new AuditContentFileStore(Options.Create(settings), NullLogger<AuditContentFileStore>.Instance);
        }

        public static async Task<AuditFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<BackendDBContext>()
                .UseSqlite(connection)
                .Options;
            var context = new BackendDBContext(options);
            await context.Database.EnsureCreatedAsync();
            return new AuditFixture(connection, context);
        }

        public AuditLogService CreateService(
            IClientIpProvider? clientIpProvider = null,
            AuditContentFileStore? contentFileStore = null,
            ILogger<AuditLogService>? logger = null)
        {
            return new AuditLogService(
                new TestDbContextFactory(connection),
                logger ?? loggerFactory.CreateLogger<AuditLogService>(),
                contentFileStore,
                clientIpProvider);
        }

        public async ValueTask DisposeAsync()
        {
            if (Directory.Exists(ContentRoot))
            {
                Directory.Delete(ContentRoot, recursive: true);
            }

            await Context.DisposeAsync();
            await connection.DisposeAsync();
            loggerFactory.Dispose();
        }
    }

    private sealed class FixedClientIpProvider(string? ip) : IClientIpProvider
    {
        public string? GetClientIp() => ip;
    }
}

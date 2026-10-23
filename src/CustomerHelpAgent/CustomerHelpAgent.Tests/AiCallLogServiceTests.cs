using System.Text.Json;
using AutoMapper;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Systems;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// AI 對話紀錄的服務層（0.9.72 起）。
///
/// 重點：
/// 1. 記錄時內文寫進內容檔，資料表只留可篩選的中繼資料；停用時什麼都不記。
/// 2. 刪除資料列一定要同時刪掉內容檔，不可留下孤兒檔；自動過期會清掉舊月份目錄。
/// 3. 明細以 CallId 帶出 Token 用量、以 ConversationId 帶出同一對話。
/// 4. <c>RecordAsync</c> <b>絕不拋出</b>。
/// </summary>
public sealed class AiCallLogServiceTests
{
    [Fact]
    public async Task RecordAsync_ShouldInsertRowAndWriteContentFile()
    {
        await using var fixture = await Fixture.CreateAsync();
        var entry = CreateEntry(requestBody: RequestWithMessages("系統提示", "使用者內容"), responseText: "AI 回應");

        await fixture.CreateService().RecordAsync(entry);

        var row = Assert.Single(await fixture.ListAsync());
        Assert.Equal(entry.CallId, row.CallId);
        Assert.Equal(entry.RequestBody.Length, row.RequestCharacters);
        Assert.Equal("AI 回應".Length, row.ResponseCharacters);
        Assert.NotNull(row.ContentFile);

        var json = await File.ReadAllTextAsync(fixture.FullPath(row.ContentFile!));
        var roundTrip = JsonSerializer.Deserialize<AiCallLogEntry>(json);
        Assert.NotNull(roundTrip);
        Assert.Equal(entry.RequestBody, roundTrip!.RequestBody);
        Assert.Equal("AI 回應", roundTrip.ResponseText);

        // 中文不可被跳脫成 \uXXXX：檔案要能直接閱讀。
        Assert.Contains("AI 回應", json);
    }

    [Fact]
    public async Task RecordAsync_ShouldDoNothing_WhenDisabled()
    {
        await using var fixture = await Fixture.CreateAsync();

        await fixture.CreateService(enabled: false).RecordAsync(CreateEntry());

        Assert.Empty(await fixture.ListAsync());
        Assert.Empty(Directory.GetFileSystemEntries(fixture.RootPath));
    }

    [Fact]
    public async Task RecordAsync_ShouldStillInsertRow_WhenContentFileCannotBeWritten()
    {
        await using var fixture = await Fixture.CreateAsync();

        await fixture.CreateService(rootPath: string.Empty).RecordAsync(CreateEntry());

        var row = Assert.Single(await fixture.ListAsync());
        Assert.Null(row.ContentFile);
    }

    [Fact]
    public async Task RecordAsync_ShouldTruncateRelatedInfo()
    {
        await using var fixture = await Fixture.CreateAsync();

        await fixture.CreateService().RecordAsync(CreateEntry(relatedInfo: new string('例', 800)));

        var row = Assert.Single(await fixture.ListAsync());
        Assert.Equal(AiCallLogService.MaxRelatedInfoLength, row.RelatedInfo!.Length);
    }

    [Fact]
    public async Task RecordAsync_ShouldNotThrow_WhenDatabaseIsUnavailable()
    {
        var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService();
        await fixture.DisposeConnectionAsync();

        await service.RecordAsync(CreateEntry());

        // 資料列建不起來，剛寫的內容檔也要一起清掉。
        Assert.Empty(Directory.EnumerateFiles(fixture.RootPath, "*.json", SearchOption.AllDirectories));
        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task GetAsync_ShouldApplyFilters()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService();
        await service.RecordAsync(CreateEntry(operation: TokenUsageOperations.AiLogAnalysis, account: "chen", relatedInfo: "例外紀錄 #12（NullReferenceException）", model: "gpt-a"));
        await service.RecordAsync(CreateEntry(operation: TokenUsageOperations.AiExceptionAnalysis, account: "wang", success: false, model: "gpt-b"));
        await service.RecordAsync(CreateEntry(operation: TokenUsageOperations.SystemHealthCheck, account: null, occurredAt: DateTime.Now.AddDays(-10)));

        Assert.Single((await service.GetAsync(new AiCallLogQuery { Operation = TokenUsageOperations.AiLogAnalysis })).Result);
        Assert.Single((await service.GetAsync(new AiCallLogQuery { Account = "che" })).Result);
        Assert.Single((await service.GetAsync(new AiCallLogQuery { Success = false })).Result);
        Assert.Single((await service.GetAsync(new AiCallLogQuery { Model = "gpt-b" })).Result);
        Assert.Single((await service.GetAsync(new AiCallLogQuery { Keyword = "#12" })).Result);
        Assert.Equal(2, (await service.GetAsync(new AiCallLogQuery { StartDate = DateTime.Today, EndDate = DateTime.Today })).Count);
    }

    [Fact]
    public async Task GetAsync_ShouldSortByOccurredAtDescending_AndPage()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService();
        var now = DateTime.Now;
        for (var index = 0; index < 5; index++)
        {
            await service.RecordAsync(CreateEntry(occurredAt: now.AddMinutes(-index), relatedInfo: $"#{index}"));
        }

        var page = await service.GetAsync(new AiCallLogQuery { CurrentPage = 2, PageSize = 2 });

        Assert.Equal(5, page.Count);
        Assert.Equal(["#2", "#3"], page.Result.Select(x => x.RelatedInfo).ToList());
    }

    [Fact]
    public async Task GetDetailAsync_ShouldParseMessages_AndLinkUsageAndConversation()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService();
        var conversationId = Guid.NewGuid();
        var first = CreateEntry(requestBody: RequestWithMessages("sys", "明細"), conversationId: conversationId, occurredAt: DateTime.Now.AddMinutes(-1));
        var second = CreateEntry(conversationId: conversationId);
        await service.RecordAsync(first);
        await service.RecordAsync(second);
        await service.RecordAsync(CreateEntry());
        await fixture.AddUsageAsync(first.CallId);

        var id = await service.FindIdByCallIdAsync(first.CallId);
        var detail = await service.GetDetailAsync(id!.Value);

        Assert.NotNull(detail);
        Assert.NotNull(detail!.Content);
        Assert.Equal(["system", "user"], detail.Messages!.Select(x => x.Role).ToList());
        Assert.Equal("明細", detail.Messages![1].Content);
        Assert.NotNull(detail.Usage);
        Assert.Equal(first.CallId, detail.Usage!.CallId);
        Assert.Equal([first.CallId, second.CallId], detail.Conversation.Select(x => x.CallId).ToList());
    }

    [Fact]
    public async Task GetDetailAsync_ShouldReturnNullContent_WhenFileIsMissing()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService();
        var entry = CreateEntry();
        await service.RecordAsync(entry);
        var row = Assert.Single(await fixture.ListAsync());
        File.Delete(fixture.FullPath(row.ContentFile!));

        var detail = await service.GetDetailAsync(row.Id);

        Assert.NotNull(detail);
        Assert.Null(detail!.Content);
        Assert.Null(detail.Messages);
        Assert.False(await service.ExistsAsync(Guid.NewGuid()));
        Assert.True(await service.ExistsAsync(entry.CallId));
    }

    [Fact]
    public async Task DeleteAsync_ShouldRemoveRowAndFile()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService();
        await service.RecordAsync(CreateEntry());
        var row = Assert.Single(await fixture.ListAsync());

        var result = await service.DeleteAsync(row.Id);

        Assert.True(result.Success);
        Assert.Empty(await fixture.ListAsync());
        Assert.False(File.Exists(fixture.FullPath(row.ContentFile!)));
    }

    [Fact]
    public async Task PurgeBeforeAsync_ShouldRemoveOnlyOlderRowsAndTheirFiles()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService();
        await service.RecordAsync(CreateEntry(occurredAt: DateTime.Today.AddDays(-5), relatedInfo: "old"));
        await service.RecordAsync(CreateEntry(occurredAt: DateTime.Today, relatedInfo: "new"));
        var oldFile = (await fixture.ListAsync()).Single(x => x.RelatedInfo == "old").ContentFile!;

        var result = await service.PurgeBeforeAsync(DateTime.Today.AddDays(-1));

        Assert.True(result.Success);
        Assert.Equal("new", Assert.Single(await fixture.ListAsync()).RelatedInfo);
        Assert.False(File.Exists(fixture.FullPath(oldFile)));
    }

    [Fact]
    public async Task ClearAllAsync_ShouldEmptyTableAndDirectory()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService();
        await service.RecordAsync(CreateEntry());
        await service.RecordAsync(CreateEntry());

        var result = await service.ClearAllAsync();

        Assert.True(result.Success);
        Assert.Empty(await fixture.ListAsync());
        Assert.Empty(Directory.GetFileSystemEntries(fixture.RootPath));
        Assert.True(Directory.Exists(fixture.RootPath));
    }

    [Fact]
    public async Task PurgeExpiredAsync_ShouldRespectRetentionDays_AndSweepOldMonthFolders()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService(retentionDays: 30);
        await service.RecordAsync(CreateEntry(occurredAt: DateTime.Today.AddDays(-40), relatedInfo: "expired"));
        await service.RecordAsync(CreateEntry(occurredAt: DateTime.Today.AddDays(-2), relatedInfo: "kept"));

        // 「檔已寫、資料列沒建起來」留下的孤兒檔，在很舊的月份目錄裡。
        var orphanFolder = Path.Combine(fixture.RootPath, DateTime.Today.AddMonths(-6).ToString("yyyyMM"));
        Directory.CreateDirectory(orphanFolder);
        await File.WriteAllTextAsync(Path.Combine(orphanFolder, "orphan.json"), "{}");

        var removed = await service.PurgeExpiredAsync();

        Assert.Equal(1, removed);
        Assert.Equal("kept", Assert.Single(await fixture.ListAsync()).RelatedInfo);
        Assert.False(Directory.Exists(orphanFolder));
    }

    [Fact]
    public async Task PurgeExpiredAsync_ShouldStillRun_WhenRecordingIsDisabled()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.CreateService().RecordAsync(CreateEntry(occurredAt: DateTime.Today.AddDays(-200)));

        var removed = await fixture.CreateService(enabled: false, retentionDays: 90).PurgeExpiredAsync();

        Assert.Equal(1, removed);
        Assert.Empty(await fixture.ListAsync());
    }

    private static string RequestWithMessages(string system, string user)
        => JsonSerializer.Serialize(new
        {
            model = "gpt-a",
            messages = new[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user },
            },
        });

    private static AiCallLogEntry CreateEntry(
        string operation = TokenUsageOperations.AiLogAnalysis,
        string? account = "support",
        bool success = true,
        string model = "gpt-a",
        string? relatedInfo = null,
        Guid? conversationId = null,
        DateTime? occurredAt = null,
        string? requestBody = null,
        string responseText = "ok")
        => new()
        {
            CallId = Guid.NewGuid(),
            OccurredAt = occurredAt ?? DateTime.Now,
            Operation = operation,
            Provider = "AzureOpenAI",
            RequestedModel = model,
            Model = model,
            Account = account,
            UserId = account is null ? null : 7,
            Endpoint = "https://example.invalid/openai/v1/chat/completions",
            RelatedInfo = relatedInfo,
            ConversationId = conversationId,
            RequestBody = requestBody ?? RequestWithMessages("s", "u"),
            HttpStatus = 200,
            ResponseBody = "{}",
            ResponseText = responseText,
            FinishReason = "stop",
            Success = success,
            FailureReason = success ? null : "UpstreamError",
            ElapsedMilliseconds = 1234,
        };

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly IMapper mapper;
        private readonly ILoggerFactory loggerFactory;

        private Fixture(SqliteConnection connection, string rootPath)
        {
            this.connection = connection;
            RootPath = rootPath;

            loggerFactory = LoggerFactory.Create(_ => { });
            mapper = new MapperConfiguration(configuration => configuration.AddProfile<AutoMapping>(), loggerFactory).CreateMapper();
        }

        public string RootPath { get; }

        public string FullPath(string relativePath)
            => Path.Combine(RootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connection).Options;
            await using var context = new BackendDBContext(options);
            await context.Database.EnsureCreatedAsync();

            var path = Path.Combine(Path.GetTempPath(), "CustomerHelpAgentTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);

            return new Fixture(connection, path);
        }

        public AiCallLogService CreateService(bool enabled = true, int retentionDays = 90, string? rootPath = null)
        {
            var settings = new SystemSettings();
            settings.ExternalFileSystem.AiCallLogPath = rootPath ?? RootPath;

            return new AiCallLogService(
                new TestDbContextFactory(connection),
                mapper,
                loggerFactory.CreateLogger<AiCallLogService>(),
                new AiCallLogFileStore(Options.Create(settings), loggerFactory.CreateLogger<AiCallLogFileStore>()),
                new StaticOptionsMonitor<AiCallLogSettings>(new AiCallLogSettings { Enabled = enabled, RetentionDays = retentionDays }));
        }

        public async Task<List<AiCallLog>> ListAsync()
        {
            await using var context = new TestDbContextFactory(connection).CreateDbContext();
            return await context.AiCallLog.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
        }

        public async Task AddUsageAsync(Guid callId)
        {
            await using var context = new TestDbContextFactory(connection).CreateDbContext();
            context.TokenUsageLog.Add(new TokenUsageLog
            {
                OccurredAt = DateTime.Now,
                Operation = TokenUsageOperations.AiLogAnalysis,
                CallKind = TokenUsageCallKinds.Chat,
                Provider = "AzureOpenAI",
                Model = "gpt-a",
                InputCount = 10,
                OutputCount = 5,
                TotalCount = 15,
                Success = true,
                CallId = callId,
            });
            await context.SaveChangesAsync();
        }

        /// <summary>刻意關閉連線，用來驗證「RecordAsync 不得拋出」。</summary>
        public async Task DisposeConnectionAsync() => await connection.DisposeAsync();

        public async ValueTask DisposeAsync()
        {
            await connection.DisposeAsync();
            loggerFactory.Dispose();

            try
            {
                if (Directory.Exists(RootPath))
                {
                    Directory.Delete(RootPath, recursive: true);
                }
            }
            catch (IOException)
            {
                // 測試殘留目錄不影響結果。
            }
        }
    }
}

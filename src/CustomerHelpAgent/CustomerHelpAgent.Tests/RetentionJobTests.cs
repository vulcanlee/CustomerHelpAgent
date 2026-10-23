using AutoMapper;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Web.Scheduling;
using CustomerHelpAgent.Web.Scheduling.Jobs;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 四個清理作業（0.9.96 起由排程執行；前兩個由 0.9.78 的 LogRetentionWorker 遷入）。
///
/// ⚠️ 時區的測試刻意把資料放在本地與 UTC 相差的 8 小時之內：例外紀錄存本地時間、稽核存 UTC，
/// 門檻用錯時鐘時只有這個範圍內的資料結果會不同（舊的 LogRetentionWorkerTests 資料離門檻好幾週，抓不到對調）。
/// </summary>
public sealed class RetentionJobTests : IAsyncDisposable
{
    // UTC 2026-10-01 01:00 ＝ 本地（UTC+8）2026-10-01 09:00。
    private readonly ManualTimeProvider clock = new(new DateTimeOffset(2026, 10, 1, 1, 0, 0, TimeSpan.Zero));
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly string fileRoot = Path.Combine(Path.GetTempPath(), "CustomerHelpAgentRetentionJobs", Guid.NewGuid().ToString("N"));
    private readonly RecordingAuditLogService audit = new();
    private ServiceProvider? services;

    private static readonly ScheduledJobContext ScheduledRun = new(1, JobRunTriggers.Schedule, null, null);

    // ------------------------------------------------------------------ 系統例外紀錄（本地時間）

    [Fact]
    public async Task ExceptionLogJob_ShouldUseTheLocalClock()
    {
        // 本地門檻 07-03 09:00；若誤用 UTC 門檻 07-03 01:00，這一筆（本地 05:00）會被留下。
        await InitializeAsync(new LogRetentionSettings { ExceptionLogDays = 90 });
        await SeedExceptionLogsAsync(new DateTime(2026, 7, 3, 5, 0, 0), new DateTime(2026, 9, 30));

        var result = await RunAsync<ExceptionLogRetentionJob>();

        Assert.True(result.Succeeded);
        await using var context = NewContext();
        Assert.Equal(new DateTime(2026, 9, 30), Assert.Single(await context.ExceptionLog.ToListAsync()).LastOccurredAt);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditActions.ExceptionLog.AutoPurge, entry.Action);
        Assert.Equal("rows=1; days=90; trigger=Schedule", entry.Detail);
        Assert.Null(entry.ActorUserId);
    }

    // ------------------------------------------------------------------ 稽核紀錄（UTC）

    [Fact]
    public async Task AuditLogJob_ShouldUseTheUtcClock()
    {
        // UTC 門檻 2025-10-01 01:00；若誤用本地門檻 2025-10-01 09:00，這一筆（UTC 05:00）會被刪掉。
        await InitializeAsync(new LogRetentionSettings { AuditLogDays = 365 });
        await SeedAuditLogsAsync(new DateTime(2025, 10, 1, 5, 0, 0), new DateTime(2025, 1, 1));

        var result = await RunAsync<AuditLogRetentionJob>();

        Assert.True(result.Succeeded);
        await using var context = NewContext();
        Assert.Equal(new DateTime(2025, 10, 1, 5, 0, 0), Assert.Single(await context.AuditLog.ToListAsync()).OccurredAt);
        Assert.Equal(AuditActions.Audit.AutoPurge, Assert.Single(audit.Entries).Action);
    }

    [Fact]
    public async Task Jobs_WithZeroDays_ShouldNotPurgeOrAudit()
    {
        await InitializeAsync(new LogRetentionSettings { ExceptionLogDays = 0, AuditLogDays = 0, TokenUsageLogDays = 0 });
        await SeedExceptionLogsAsync(new DateTime(2020, 1, 1));
        await SeedAuditLogsAsync(new DateTime(2020, 1, 1));
        await SeedTokenUsageAsync(new DateTime(2020, 1, 1));

        Assert.True((await RunAsync<ExceptionLogRetentionJob>()).Succeeded);
        Assert.True((await RunAsync<AuditLogRetentionJob>()).Succeeded);
        Assert.True((await RunAsync<TokenUsageLogRetentionJob>()).Succeeded);

        await using var context = NewContext();
        Assert.Equal(1, await context.ExceptionLog.CountAsync());
        Assert.Equal(1, await context.AuditLog.CountAsync());
        Assert.Equal(1, await context.TokenUsageLog.CountAsync());
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task Jobs_WithNothingExpired_ShouldNotWriteAudit()
    {
        await InitializeAsync(new LogRetentionSettings());
        await SeedExceptionLogsAsync(new DateTime(2026, 9, 30));
        await SeedAuditLogsAsync(new DateTime(2026, 9, 30));

        await RunAsync<ExceptionLogRetentionJob>();
        await RunAsync<AuditLogRetentionJob>();

        Assert.Empty(audit.Entries);
    }

    // ------------------------------------------------------------------ AI 對話紀錄

    [Fact]
    public async Task AiCallLogJob_ShouldUseTheInjectedClock()
    {
        // 改用 DateTime.Now 的話，「現在」是真實日期，2026-05-01 的紀錄（相對假時鐘超過 90 天）就不會被刪。
        await InitializeAsync(new LogRetentionSettings());
        await SeedAiCallLogsAsync(new DateTime(2026, 5, 1), new DateTime(2026, 9, 30));

        var result = await RunAsync<AiCallLogRetentionJob>();

        Assert.True(result.Succeeded, result.Message);
        await using var context = NewContext();
        Assert.Equal(new DateTime(2026, 9, 30), Assert.Single(await context.AiCallLog.ToListAsync()).OccurredAt);
        Assert.Equal(AuditActions.AiCallLog.AutoPurge, Assert.Single(audit.Entries).Action);
    }

    [Fact]
    public async Task AiCallLogJob_WhenTheServiceFails_ShouldReportFailure()
    {
        // 服務吞掉例外回 null：只回一段文字的話會被記成成功。
        await InitializeAsync(new LogRetentionSettings());
        await using (var context = NewContext())
        {
            await context.Database.ExecuteSqlRawAsync("DROP TABLE AiCallLog");
        }

        var result = await RunAsync<AiCallLogRetentionJob>();

        Assert.False(result.Succeeded);
        Assert.Empty(audit.Entries);
    }

    // ------------------------------------------------------------------ Token 用量

    [Fact]
    public async Task TokenUsageJob_ShouldDeleteExpiredRowsAndTheirFiles()
    {
        await InitializeAsync(new LogRetentionSettings { TokenUsageLogDays = 365 });
        var (oldFile, newFile) = (await SeedTokenUsageAsync(new DateTime(2025, 9, 30)), await SeedTokenUsageAsync(new DateTime(2025, 10, 1)));

        var result = await RunAsync<TokenUsageLogRetentionJob>();

        // 門檻 = 今天（本地 2026-10-01）往前 365 天 = 2025-10-01（含當日不刪）。
        Assert.True(result.Succeeded, result.Message);
        await using var context = NewContext();
        Assert.Equal(new DateTime(2025, 10, 1), Assert.Single(await context.TokenUsageLog.ToListAsync()).OccurredAt);
        Assert.False(File.Exists(oldFile));
        Assert.True(File.Exists(newFile));
        Assert.Equal(AuditActions.TokenUsage.AutoPurge, Assert.Single(audit.Entries).Action);
    }

    [Fact]
    public async Task TokenUsagePurge_ShouldDeleteEveryBatch()
    {
        await InitializeAsync(new LogRetentionSettings());
        for (var index = 0; index < 5; index++)
        {
            await SeedTokenUsageAsync(new DateTime(2024, 1, 1).AddDays(index));
        }

        var removed = await services!.CreateScope().ServiceProvider.GetRequiredService<TokenUsageLogService>()
            .PurgeExpiredAsync(365, new DateTime(2026, 10, 1), CancellationToken.None, batchSize: 2);

        Assert.Equal(5, removed);
        await using var context = NewContext();
        Assert.Equal(0, await context.TokenUsageLog.CountAsync());
    }

    [Fact]
    public async Task TokenUsagePurge_ShouldStopWhenCancelled()
    {
        await InitializeAsync(new LogRetentionSettings());
        await SeedTokenUsageAsync(new DateTime(2024, 1, 1));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var service = services!.CreateScope().ServiceProvider.GetRequiredService<TokenUsageLogService>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.PurgeExpiredAsync(365, new DateTime(2026, 10, 1), cancelled.Token));

        await using var context = NewContext();
        Assert.Equal(1, await context.TokenUsageLog.CountAsync());
    }

    [Fact]
    public async Task TokenUsageRawStore_DeleteMonthsBefore_ShouldKeepTheThresholdMonth()
    {
        await InitializeAsync(new LogRetentionSettings());
        var store = services!.CreateScope().ServiceProvider.GetRequiredService<TokenUsageRawStore>();
        var tokenRoot = Path.Combine(fileRoot, "token");
        foreach (var month in new[] { "202508", "202509", "202510" })
        {
            Directory.CreateDirectory(Path.Combine(tokenRoot, month));
        }

        store.DeleteMonthsBefore(new DateTime(2025, 9, 15));

        Assert.False(Directory.Exists(Path.Combine(tokenRoot, "202508")));
        Assert.True(Directory.Exists(Path.Combine(tokenRoot, "202509")));
        Assert.True(Directory.Exists(Path.Combine(tokenRoot, "202510")));
    }

    [Fact]
    public void ManualDays_ShouldFallBackToDefaults_WhenAutoPurgeIsDisabled()
    {
        var settings = new LogRetentionSettings { ExceptionLogDays = 0, AuditLogDays = 0 };

        Assert.Equal(LogRetentionSettings.DefaultExceptionLogDays, settings.ManualExceptionLogDays);
        Assert.Equal(LogRetentionSettings.DefaultAuditLogDays, settings.ManualAuditLogDays);
    }

    // ------------------------------------------------------------------ 資料保留與清理頁的統計（0.9.133 起）
    // 「下次會清掉幾筆」必須等於作業實際刪掉的筆數：資料放在本地與 UTC 相差的 8 小時內，時鐘用錯就會對不上。

    [Fact]
    public async Task ExceptionLogStats_ShouldMatchWhatTheJobRemoves()
    {
        await InitializeAsync(new LogRetentionSettings { ExceptionLogDays = 90 });
        await SeedExceptionLogsAsync(new DateTime(2026, 7, 3, 5, 0, 0), new DateTime(2026, 7, 3, 12, 0, 0), new DateTime(2026, 9, 30));

        var stats = await Service<ExceptionLogService>().GetRetentionStatsAsync(RetentionCutoffs.ExceptionLog(90, clock));
        await RunAsync<ExceptionLogRetentionJob>();

        await using var context = NewContext();
        Assert.Equal(3, stats.Total);
        Assert.Equal(new DateTime(2026, 7, 3, 5, 0, 0), stats.Oldest);
        Assert.Equal(stats.Total - await context.ExceptionLog.CountAsync(), stats.Expired);
        Assert.Equal(1, stats.Expired);
    }

    [Fact]
    public async Task AuditLogStats_ShouldMatchWhatTheJobRemoves()
    {
        await InitializeAsync(new LogRetentionSettings { AuditLogDays = 365 });
        await SeedAuditLogsAsync(new DateTime(2025, 9, 30, 23, 0, 0), new DateTime(2025, 10, 1, 5, 0, 0), new DateTime(2026, 9, 1));

        var stats = await Service<AuditLogQueryService>().GetRetentionStatsAsync(RetentionCutoffs.AuditLog(365, clock));
        await RunAsync<AuditLogRetentionJob>();

        await using var context = NewContext();
        Assert.Equal(stats.Total - await context.AuditLog.CountAsync(), stats.Expired);
        Assert.Equal(1, stats.Expired);
    }

    [Fact]
    public async Task TokenUsageStats_ShouldMatchWhatTheJobRemoves()
    {
        await InitializeAsync(new LogRetentionSettings { TokenUsageLogDays = 30 });
        await SeedTokenUsageAsync(new DateTime(2026, 8, 31, 23, 0, 0));
        await SeedTokenUsageAsync(new DateTime(2026, 9, 1, 1, 0, 0));
        await SeedTokenUsageAsync(new DateTime(2026, 9, 30));

        var stats = await Service<TokenUsageLogService>().GetRetentionStatsAsync(30, clock.GetLocalNow().DateTime);
        await RunAsync<TokenUsageLogRetentionJob>();

        await using var context = NewContext();
        Assert.Equal(stats.Total - await context.TokenUsageLog.CountAsync(), stats.Expired);
        Assert.Equal(1, stats.Expired);
        Assert.Null((await Service<TokenUsageLogService>().GetRetentionStatsAsync(0, clock.GetLocalNow().DateTime)).Expired);
    }

    [Fact]
    public async Task AiCallLogStats_ShouldMatchWhatTheJobRemoves()
    {
        await InitializeAsync(new LogRetentionSettings());
        await SeedAiCallLogsAsync(new DateTime(2026, 7, 2, 23, 0, 0), new DateTime(2026, 7, 3, 1, 0, 0), new DateTime(2026, 9, 30));

        var stats = await Service<AiCallLogService>().GetRetentionStatsAsync(clock.GetLocalNow().DateTime);
        await RunAsync<AiCallLogRetentionJob>();

        await using var context = NewContext();
        Assert.Equal(stats.Total - await context.AiCallLog.CountAsync(), stats.Expired);
        Assert.Equal(1, stats.Expired);
    }

    // ------------------------------------------------------------------ 測試基礎

    private TService Service<TService>()
        where TService : notnull
        => services!.CreateScope().ServiceProvider.GetRequiredService<TService>();

    private async Task<ScheduledJobResult> RunAsync<TJob>()
        where TJob : IScheduledJob
    {
        using var scope = services!.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<TJob>().ExecuteAsync(ScheduledRun, CancellationToken.None);
    }

    private BackendDBContext NewContext() => new TestDbContextFactory(connection).CreateDbContext();

    private async Task SeedExceptionLogsAsync(params DateTime[] lastOccurredLocal)
    {
        await using var context = NewContext();
        foreach (var occurredAt in lastOccurredLocal)
        {
            context.ExceptionLog.Add(new ExceptionLog
            {
                Signature = Guid.NewGuid().ToString("N"),
                ExceptionType = "System.Exception",
                Message = "m",
                Source = ExceptionSources.Unknown,
                OccurrenceCount = 1,
                FirstOccurredAt = occurredAt,
                LastOccurredAt = occurredAt,
            });
        }

        await context.SaveChangesAsync();
    }

    private async Task SeedAuditLogsAsync(params DateTime[] occurredUtc)
    {
        await using var context = NewContext();
        foreach (var occurredAt in occurredUtc)
        {
            context.AuditLog.Add(new AuditLog { Action = AuditActions.Email.Test, OccurredAt = occurredAt, Success = true });
        }

        await context.SaveChangesAsync();
    }

    private async Task SeedAiCallLogsAsync(params DateTime[] occurredAt)
    {
        await using var context = NewContext();
        foreach (var at in occurredAt)
        {
            context.AiCallLog.Add(new AiCallLog { CallId = Guid.NewGuid(), OccurredAt = at, Operation = "op", Provider = "p", Model = "m", Success = true });
        }

        await context.SaveChangesAsync();
    }

    /// <summary>寫入一筆 Token 用量與它的原始檔，回傳檔案的完整路徑。</summary>
    private async Task<string> SeedTokenUsageAsync(DateTime occurredAt)
    {
        var relative = $"{occurredAt:yyyyMM}/{Guid.NewGuid():N}.json";
        var fullPath = Path.Combine(fileRoot, "token", relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, "{}");

        await using var context = NewContext();
        context.TokenUsageLog.Add(new TokenUsageLog { OccurredAt = occurredAt, Operation = "op", CallKind = "chat", Provider = "p", Model = "m", RawUsageFile = relative });
        await context.SaveChangesAsync();
        return fullPath;
    }

    private async Task InitializeAsync(LogRetentionSettings retention)
    {
        await connection.OpenAsync();
        await using (var context = NewContext())
        {
            await context.Database.EnsureCreatedAsync();
        }

        var systemSettings = new SystemSettings();
        systemSettings.ExternalFileSystem.ExceptionPath = Path.Combine(fileRoot, "exception");
        systemSettings.ExternalFileSystem.TokenUsagePath = Path.Combine(fileRoot, "token");
        systemSettings.ExternalFileSystem.AiCallLogPath = Path.Combine(fileRoot, "ai");
        systemSettings.ExternalFileSystem.AuditContentPath = Path.Combine(fileRoot, "audit");
        Directory.CreateDirectory(fileRoot);

        var collection = new ServiceCollection();
        collection.AddSingleton<IDbContextFactory<BackendDBContext>>(new TestDbContextFactory(connection));
        collection.AddSingleton<IMapper>(new MapperConfiguration(c => c.AddProfile<AutoMapping>(), NullLoggerFactory.Instance).CreateMapper());
        collection.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        collection.AddSingleton(Options.Create(systemSettings));
        collection.AddSingleton<IOptionsMonitor<LogRetentionSettings>>(new StaticOptionsMonitor<LogRetentionSettings>(retention));
        collection.AddSingleton<IOptionsMonitor<AiCallLogSettings>>(new StaticOptionsMonitor<AiCallLogSettings>(new AiCallLogSettings { RetentionDays = 90 }));
        collection.AddSingleton<TimeProvider>(clock);
        collection.AddSingleton<IAuditLogService>(audit);
        collection.AddSingleton<IAiQuotaService>(new StubAiQuotaService());
        collection.AddSingleton<IAiUsageCostCalculator>(new AiUsageCostCalculator(new StaticOptionsMonitor<AiPricingSettings>(new AiPricingSettings())));
        collection.AddScoped<ExceptionStackFileStore>();
        collection.AddScoped<ExceptionLogService>();
        collection.AddScoped<AuditContentFileStore>();
        collection.AddScoped<AuditLogQueryService>();
        collection.AddScoped<AiCallLogFileStore>();
        collection.AddScoped<AiCallLogService>();
        collection.AddScoped<TokenUsageRawStore>();
        collection.AddScoped<TokenUsageLogService>();
        collection.AddScoped<ExceptionLogRetentionJob>();
        collection.AddScoped<AuditLogRetentionJob>();
        collection.AddScoped<AiCallLogRetentionJob>();
        collection.AddScoped<TokenUsageLogRetentionJob>();
        services = collection.BuildServiceProvider();
    }

    public async ValueTask DisposeAsync()
    {
        if (services is not null)
        {
            await services.DisposeAsync();
        }

        await connection.DisposeAsync();
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
}

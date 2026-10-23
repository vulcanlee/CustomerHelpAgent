using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Others;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Web.Ai;
using CustomerHelpAgent.Web.Configuration;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// AI 用量上限（0.9.109）。假時鐘的本地時區是 UTC+8：Token 用量的 OccurredAt 存本地時間，
/// 「今天」「本月」若誤用 UTC 計算，跨午夜與月初的案例會算錯。
/// </summary>
public sealed class AiQuotaTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly TestDbContextFactory factory;
    private readonly AiQuotaSettings settings = new();
    private readonly RecordingNotificationSender notifications = new();

    public AiQuotaTests()
    {
        connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        connection.Open();
        factory = new TestDbContextFactory(connection);
        using var context = factory.CreateDbContext();
        context.Database.EnsureCreated();
    }

    public void Dispose() => connection.Dispose();

    /// <summary>本地時間 2026-10-04 00:30（UTC 2026-10-03 16:30）。</summary>
    private static ManualTimeProvider ClockAtLocal(DateTime local) => new(new DateTimeOffset(local.AddHours(-8), TimeSpan.Zero));

    private AiQuotaService Service(ManualTimeProvider? clock = null, INotificationSender? sender = null)
        => new(factory, new StaticOptionsMonitor<AiQuotaSettings>(settings), sender ?? notifications,
            clock ?? ClockAtLocal(new DateTime(2026, 10, 4, 0, 30, 0)), NullLogger<AiQuotaService>.Instance);

    private void Usage(DateTime occurredLocal, double? costTwd, int? userId = 7)
    {
        using var context = factory.CreateDbContext();
        context.TokenUsageLog.Add(new TokenUsageLog
        {
            OccurredAt = occurredLocal,
            Operation = TokenUsageOperations.AiLogAnalysis,
            CallKind = TokenUsageCallKinds.Chat,
            Provider = "OpenAI",
            Model = "gpt-4o",
            UserId = userId,
            Success = true,
            CostTwd = costTwd,
        });
        context.SaveChanges();
    }

    [Fact]
    public async Task Today_ShouldStartAtLocalMidnight()
    {
        Usage(new DateTime(2026, 10, 3, 23, 59, 0), 50);
        Usage(new DateTime(2026, 10, 4, 0, 10, 0), 10);

        var status = await Service().GetStatusAsync(7);

        var globalDaily = Assert.Single(status, x => x is { Scope: AiQuotaScope.Global, Period: AiQuotaPeriod.Daily });
        Assert.Equal(10, globalDaily.UsedTwd);
        Assert.Equal(new DateTime(2026, 10, 5), globalDaily.ResetAt);
        Assert.Equal(60, Assert.Single(status, x => x is { Scope: AiQuotaScope.Global, Period: AiQuotaPeriod.Monthly }).UsedTwd);
        Assert.Equal(4, status.Count);
    }

    [Fact]
    public async Task ThisMonth_ShouldStartOnTheFirstAtLocalMidnight()
    {
        Usage(new DateTime(2026, 10, 31, 23, 0, 0), 40);
        Usage(new DateTime(2026, 11, 1, 0, 5, 0), 3);

        var status = await Service(ClockAtLocal(new DateTime(2026, 11, 1, 0, 20, 0))).GetStatusAsync(null);

        var monthly = Assert.Single(status, x => x.Period == AiQuotaPeriod.Monthly);
        Assert.Equal(3, monthly.UsedTwd);
        Assert.Equal(new DateTime(2026, 12, 1), monthly.ResetAt);
        Assert.Equal(2, status.Count);
    }

    [Theory]
    [InlineData(nameof(AiQuotaSettings.GlobalDailyTwd), AiQuotaScope.Global, AiQuotaPeriod.Daily)]
    [InlineData(nameof(AiQuotaSettings.GlobalMonthlyTwd), AiQuotaScope.Global, AiQuotaPeriod.Monthly)]
    [InlineData(nameof(AiQuotaSettings.PerUserDailyTwd), AiQuotaScope.User, AiQuotaPeriod.Daily)]
    [InlineData(nameof(AiQuotaSettings.PerUserMonthlyTwd), AiQuotaScope.User, AiQuotaPeriod.Monthly)]
    public async Task EachLimit_ShouldBlockOnceReached(string property, AiQuotaScope scope, AiQuotaPeriod period)
    {
        typeof(AiQuotaSettings).GetProperty(property)!.SetValue(settings, 20);
        var service = Service();
        Usage(new DateTime(2026, 10, 4, 0, 1, 0), 19.5);

        Assert.Null(await service.CheckAsync(7));

        Usage(new DateTime(2026, 10, 4, 0, 2, 0), 0.5);
        var reached = await service.CheckAsync(7);

        Assert.NotNull(reached);
        Assert.Equal((scope, period, 20, 20.0), (reached.Scope, reached.Period, reached.LimitTwd, reached.UsedTwd));
    }

    [Fact]
    public async Task ZeroLimits_ShouldNeverBlock_AndUnpricedCallsCountAsZero()
    {
        Usage(new DateTime(2026, 10, 4, 0, 1, 0), 100_000);
        Assert.Null(await Service().CheckAsync(7));

        settings.PerUserDailyTwd = 1;
        await using (var context = factory.CreateDbContext())
        {
            await context.TokenUsageLog.ExecuteDeleteAsync();
        }

        Usage(new DateTime(2026, 10, 4, 0, 1, 0), null);
        Usage(new DateTime(2026, 10, 4, 0, 2, 0), null);
        Assert.Null(await Service().CheckAsync(7));
    }

    [Fact]
    public async Task PerUserLimit_ShouldOnlyCountThatUser_ButGlobalCountsEveryone()
    {
        settings.PerUserDailyTwd = 10;
        settings.GlobalDailyTwd = 25;
        Usage(new DateTime(2026, 10, 4, 0, 1, 0), 9, userId: 7);
        Usage(new DateTime(2026, 10, 4, 0, 2, 0), 15, userId: 8);
        var service = Service();

        Assert.Null(await service.CheckAsync(7));
        Assert.Equal(AiQuotaScope.User, (await service.CheckAsync(8))!.Scope);

        Usage(new DateTime(2026, 10, 4, 0, 3, 0), 1, userId: null);
        var reached = await service.CheckAsync(7);
        Assert.Equal((AiQuotaScope.Global, 25.0), (reached!.Scope, reached.UsedTwd));
        Assert.Equal(AiQuotaScope.Global, (await service.CheckAsync(null))!.Scope);
    }

    [Fact]
    public async Task Check_ShouldReportTheLimitThatResetsLast()
    {
        settings.PerUserDailyTwd = 5;
        settings.GlobalMonthlyTwd = 5;
        Usage(new DateTime(2026, 10, 4, 0, 1, 0), 6);

        var reached = await Service().CheckAsync(7);

        Assert.Equal((AiQuotaScope.Global, AiQuotaPeriod.Monthly), (reached!.Scope, reached.Period));
        Assert.Contains("已達全系統每月 AI 用量上限（NT$ 5，本月已用 NT$ 6）", AiQuotaService.BlockedMessage(reached));
        Assert.Contains("2026-11-01 00:00 重置", AiQuotaService.BlockedMessage(reached));
    }

    [Fact]
    public async Task ChangedLimits_ShouldTakeEffectImmediately()
    {
        Usage(new DateTime(2026, 10, 4, 0, 1, 0), 8);
        settings.PerUserMonthlyTwd = 100;
        var service = Service();
        Assert.Null(await service.CheckAsync(7));

        settings.PerUserMonthlyTwd = 8;

        Assert.NotNull(await service.CheckAsync(7));
    }

    [Fact]
    public async Task Check_ShouldAllowTheCall_WhenTheDatabaseFails()
    {
        settings.GlobalDailyTwd = 1;
        var service = new AiQuotaService(new ThrowingFactory(), new StaticOptionsMonitor<AiQuotaSettings>(settings), notifications,
            ClockAtLocal(new DateTime(2026, 10, 4, 0, 30, 0)), NullLogger<AiQuotaService>.Instance);

        Assert.Null(await service.CheckAsync(7));
    }

    [Fact]
    public async Task Notify_ShouldSendAt80And100Percent_ToTheRightPeople()
    {
        settings.PerUserDailyTwd = 10;
        settings.GlobalMonthlyTwd = 100;
        var service = Service();

        Usage(new DateTime(2026, 10, 4, 0, 1, 0), 7.9);
        await service.NotifyIfReachedAsync(7);
        Assert.Empty(notifications.Requests);

        Usage(new DateTime(2026, 10, 4, 0, 2, 0), 0.1);
        await service.NotifyIfReachedAsync(7);
        var warning = Assert.Single(notifications.Requests);
        Assert.Equal(NotificationCategories.AiQuota, warning.Category);
        Assert.Equal("AiQuota:user:7:daily:2026-10-04:80", warning.SourceKey);
        Assert.Equal([7], warning.Target.UserIds);
        Assert.False(warning.AlsoEmail);
        Assert.Contains("今日已用 NT$ 8／上限 NT$ 10", warning.Body);

        Usage(new DateTime(2026, 10, 4, 0, 3, 0), 92);
        notifications.Requests.Clear();
        await service.NotifyIfReachedAsync(7);

        Assert.Equal(2, notifications.Requests.Count);
        var user = Assert.Single(notifications.Requests, x => x.SourceKey!.StartsWith("AiQuota:user:", StringComparison.Ordinal));
        Assert.Equal("AiQuota:user:7:daily:2026-10-04:100", user.SourceKey);
        var global = Assert.Single(notifications.Requests, x => x.SourceKey!.StartsWith("AiQuota:global:", StringComparison.Ordinal));
        Assert.Equal("AiQuota:global:monthly:2026-10:100", global.SourceKey);
        Assert.True(global.Target.Admins);
        Assert.True(global.AlsoEmail);
        Assert.Equal("/token-usage", global.Link);
    }

    /// <summary>⭐ 經真正的通知服務：同一個門檻在同一期間只通知一次，到了下一期間重新計算。</summary>
    [Fact]
    public async Task Notify_ShouldOnlyNotifyOncePerThresholdAndPeriod()
    {
        int userId;
        await using (var context = factory.CreateDbContext())
        {
            var user = new MyUser { Account = "erin", Name = "erin", Password = "x", Status = true };
            context.MyUser.Add(user);
            await context.SaveChangesAsync();
            userId = user.Id;
        }

        settings.PerUserDailyTwd = 10;
        var resolverContext = factory.CreateDbContext();
        var sender = new NotificationSender(
            factory,
            new EffectiveTeamResolver(resolverContext, new ContextTeamTreeCache(resolverContext), NullLogger<EffectiveTeamResolver>.Instance),
            new NotificationSignal(NullLogger<NotificationSignal>.Instance),
            new NullMailer(),
            TimeProvider.System,
            NullLogger<NotificationSender>.Instance);
        var today = Service(sender: sender);

        Usage(new DateTime(2026, 10, 4, 0, 1, 0), 9, userId);
        await today.NotifyIfReachedAsync(userId);
        await today.NotifyIfReachedAsync(userId);
        Usage(new DateTime(2026, 10, 4, 0, 2, 0), 0.5, userId);
        await today.NotifyIfReachedAsync(userId);

        Usage(new DateTime(2026, 10, 5, 9, 0, 0), 9, userId);
        await Service(ClockAtLocal(new DateTime(2026, 10, 5, 9, 30, 0)), sender).NotifyIfReachedAsync(userId);

        await using var check = factory.CreateDbContext();
        Assert.Equal(
            ["AiQuota:user:" + userId + ":daily:2026-10-04:80", "AiQuota:user:" + userId + ":daily:2026-10-05:80"],
            await check.Notification.OrderBy(x => x.Id).Select(x => x.SourceKey).ToListAsync());
        await resolverContext.DisposeAsync();
    }

    /// <summary>⭐ 已達上限時不送出、不記用量，回傳原因與訊息並寫稽核。</summary>
    [Fact]
    public async Task ChatClient_ShouldNotSend_WhenAQuotaIsReached()
    {
        var quota = new StubAiQuotaService
        {
            Block = new AiQuotaLimitStatus(AiQuotaScope.User, AiQuotaPeriod.Daily, 50, 51.25, new DateTime(2026, 10, 4), new DateTime(2026, 10, 5)),
        };
        var (client, handler, usage, audit) = Client(quota);

        var result = await client.CompleteAsync(Request());

        Assert.False(result.Success);
        Assert.Equal(AiAnalysisFailureReason.QuotaExceeded, result.Reason);
        Assert.Contains("已達每人每日 AI 用量上限（NT$ 50，今日已用 NT$ 51.25）", result.ErrorMessage);
        Assert.Equal(0, handler.CallCount);
        Assert.Empty(usage.Entries);
        Assert.Equal([7], quota.Checked);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal((AuditActions.Ai.QuotaBlocked, false, 7, "support"), (entry.Action, entry.Success, entry.ActorUserId, entry.ActorAccount));
        Assert.Equal($"operation={TokenUsageOperations.AiLogAnalysis}; limit=50; used=51.25", entry.Detail);
    }

    [Fact]
    public async Task ChatClient_ShouldSend_WhenNoQuotaIsReached()
    {
        var (client, handler, usage, audit) = Client(new StubAiQuotaService());

        var result = await client.CompleteAsync(Request());

        Assert.True(result.Success);
        Assert.Equal(1, handler.CallCount);
        Assert.Single(usage.Entries);
        Assert.Empty(audit.Entries);
    }

    /// <summary>記帳後才評估提醒；沒有費用（未定價、失敗）的呼叫不評估。健康檢測同樣計入（它也經這個記錄點）。</summary>
    [Fact]
    public async Task Recording_ShouldEvaluateNotifications_OnlyForPricedCalls()
    {
        var quota = new StubAiQuotaService();
        var service = new CustomerHelpAgent.Business.Services.DataAccess.TokenUsageLogService(
            factory,
            new AutoMapper.MapperConfiguration(x => x.AddProfile<CustomerHelpAgent.Models.Systems.AutoMapping>(), Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance).CreateMapper(),
            NullLogger<CustomerHelpAgent.Business.Services.DataAccess.TokenUsageLogService>.Instance,
            new CustomerHelpAgent.Business.Services.Other.TokenUsageRawStore(
                Microsoft.Extensions.Options.Options.Create(new SystemSettings { ExternalFileSystem = new ExternalFileSystem { TokenUsagePath = Path.Combine(Path.GetTempPath(), "quota-" + Guid.NewGuid().ToString("N")) } }),
                NullLogger<CustomerHelpAgent.Business.Services.Other.TokenUsageRawStore>.Instance),
            new FixedCostCalculator(),
            quota);

        await service.RecordAsync(Entry(TokenUsageOperations.SystemHealthCheck, priced: true));
        await service.RecordAsync(Entry(TokenUsageOperations.AiLogAnalysis, priced: false));

        Assert.Equal([7], quota.Notified);
        await using var context = factory.CreateDbContext();
        Assert.Equal(2, await context.TokenUsageLog.CountAsync());
    }

    private static TokenUsageEntry Entry(string operation, bool priced) => new()
    {
        Operation = operation,
        CallKind = TokenUsageCallKinds.Chat,
        Provider = "OpenAI",
        Model = priced ? "priced-model" : "unpriced-model",
        UserId = 7,
        Account = "support",
        InputCount = 10,
        OutputCount = 5,
        TotalCount = 15,
        Success = true,
    };

    private static AiChatCompletionRequest Request() => new()
    {
        Operation = TokenUsageOperations.AiLogAnalysis,
        Messages = [new AiChatMessage(AiChatRoles.System, "s"), new AiChatMessage(AiChatRoles.User, "u")],
        ContextLengthExceededMessage = "too long",
        TimeoutHint = "retry",
        SubmittedContentLabel = "content",
    };

    private static (AiChatCompletionClient Client, StubHttpMessageHandler Handler, RecordingTokenUsageRecorder Usage, RecordingAuditLogService Audit)
        Client(IAiQuotaService quota)
    {
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, """
            { "model": "gpt-4o", "choices": [ { "finish_reason": "stop", "message": { "content": "ok" } } ],
              "usage": { "prompt_tokens": 1, "completion_tokens": 1, "total_tokens": 2 } }
            """);
        var usage = new RecordingTokenUsageRecorder();
        var audit = new RecordingAuditLogService();
        var options = new StaticOptionsMonitor<AiSettings>(new AiSettings { Provider = nameof(AiProvider.OpenAI), ApiKey = "key", Model = "gpt-4o" });
        var client = new AiChatCompletionClient(
            NullLogger<AiChatCompletionClient>.Instance,
            new StubHttpClientFactory(handler),
            options,
            usage,
            new CurrentUserService { CurrentUser = new CurrentUser { Id = 7, Account = "support" } },
            new FakeAiCallLogRecorder(),
            quota,
            audit);
        return (client, handler, usage, audit);
    }

    private sealed class FixedCostCalculator : IAiUsageCostCalculator
    {
        public AiUsageCost? Calculate(TokenUsageEntry entry)
            => entry.Model == "priced-model" ? new AiUsageCost { CostUsd = 1, CostTwd = 31.5, ExchangeRate = 31.5, PriceKey = "priced-model" } : null;
    }

    private sealed class NullMailer : INotificationMailer
    {
        public int Send(IReadOnlyList<string> emails, string title, string? body, string? link) => 0;
    }

    private sealed class ThrowingFactory : IDbContextFactory<BackendDBContext>
    {
        public BackendDBContext CreateDbContext() => throw new InvalidOperationException("database is unavailable");
    }
}

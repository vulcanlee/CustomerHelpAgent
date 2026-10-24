using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Business.Startup;
using CustomerHelpAgent.Models.Agents;
using CustomerHelpAgent.Models.Others;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Agents;
using CustomerHelpAgent.Share.Demo;
using CustomerHelpAgent.Web.Agents;
using CustomerHelpAgent.Web.Agents.AfterSales;
using CustomerHelpAgent.Web.Agents.PreReview;
using CustomerHelpAgent.Web.Configuration;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 回放（1.0.5 起）：示範模式為回放且指定了錄製時，Runner 不建立模型 client（不需金鑰、不查額度、不記用量），
/// 改由 <see cref="ReplayChatClient"/> 依 <see cref="ReplayCursorStore"/> 的游標逐筆送出錄製的回應；工具照常真實執行。
/// 先以腳本化模型即時跑一次旗艦劇本 A 錄下來，再以真正的 <see cref="DemoResetService"/> 重置後回放。
/// </summary>
public sealed class AgentReplayTests : IAsyncLifetime
{
    /// <summary>本地時間（UTC+8）2026/10/23 10:00。</summary>
    private static readonly DateTimeOffset UtcNow = new(2026, 10, 23, 2, 0, 0, TimeSpan.Zero);

    /// <summary>回放在隔天：申請編號的日期段不同（錄製時 AS20261023…，回放時 AS20261024…）。</summary>
    private static readonly DateTimeOffset Later = UtcNow.AddDays(1);

    private static readonly Regex RequestNoPattern = new(@"^AS\d{12}$");

    private const string FirstLine = "剛買的鞋尺碼不合適，想換大一碼";
    private const string SecondLine = "黑色那雙，沒穿過";
    private const string DivergedExplanation = "回放中止：目前資料或故障開關和錄製時不同，請到展示控制台重置示範資料後再試。";
    private const string ActionMismatchExplanation = "回放中請按照錄製時的步驟操作。";
    private const string FinishedExplanation = "回放的錄製內容已播完。";

    private readonly string fileRoot = Path.Combine(Path.GetTempPath(), "AgentReplayTests-" + Guid.NewGuid().ToString("N"));
    private readonly AgentPhotoInbox inbox = new();
    private readonly ReplayCursorStore cursors = new();
    private readonly ScriptedAgentModel model = new();
    private SqliteConnection connection = null!;
    private TestDbContextFactory factory = null!;
    private BackendDBContext permissionContext = null!;
    private int customer1;
    private int black40;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(fileRoot);
        connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        factory = new TestDbContextFactory(connection);
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            var demoOptions = Options.Create(new DemoSettings { Enabled = true, DemoUserPassword = "demo1234" });
            var rolePermissionService = new RolePermissionService();
            IDatabaseSeeder[] seeders =
            [
                new DefaultRoleViewSeeder(db, rolePermissionService, NullLogger<DefaultRoleViewSeeder>.Instance),
                new SupportUserSeeder(db, Options.Create(new BootstrapSettings { SupportPassword = "support-test-password" }), NullLogger<SupportUserSeeder>.Instance),
                new RbacBackfillSeeder(new RbacBackfillService(db, rolePermissionService, NullLogger<RbacBackfillService>.Instance), NullLogger<RbacBackfillSeeder>.Instance),
                new DemoAccountSeeder(db, new RbacWriteService(db, NullLogger<RbacWriteService>.Instance), demoOptions, NullLogger<DemoAccountSeeder>.Instance),
                new DemoCatalogSeeder(db, demoOptions, new ManualTimeProvider(UtcNow), NullLogger<DemoCatalogSeeder>.Instance),
            ];
            foreach (var seeder in seeders.OrderBy(x => x.Order))
            {
                await seeder.SeedAsync(CancellationToken.None);
                db.ChangeTracker.Clear();
            }

            customer1 = (await db.MyUser.SingleAsync(x => x.Account == DemoSeedData.Customer1)).Id;
            black40 = await db.SalesOrderItem.Where(x => x.SalesOrder!.OrderNo == "SO-DEMO-0001").Select(x => x.Id).SingleAsync();
        }

        permissionContext = factory.CreateDbContext();
    }

    public async Task DisposeAsync()
    {
        await permissionContext.DisposeAsync();
        await connection.DisposeAsync();
        if (Directory.Exists(fileRoot))
        {
            Directory.Delete(fileRoot, recursive: true);
        }
    }

    /// <summary>一個 Runner 與它的計費／額度替身；<see cref="InnerClientCreations"/> 是模型 client 被建立的次數。</summary>
    private sealed class Harness
    {
        public required AfterSalesAgentRunner Runner { get; init; }

        public required StubAiQuotaService Quota { get; init; }

        public required RecordingTokenUsageRecorder Usage { get; init; }

        public required FakeAiCallLogRecorder CallLog { get; init; }

        public int InnerClientCreations { get; set; }
    }

    /// <param name="modelAvailable">false＝沒有金鑰，且建立模型 client 就丟例外（回放不可以走到這裡）。</param>
    private Harness CreateHarness(DemoControlService demo, TimeProvider time, bool modelAvailable)
    {
        var currentUser = new CurrentUserService { CurrentUser = new CurrentUser { Id = customer1, Account = DemoSeedData.Customer1 } };
        var audit = new RecordingAuditLogService();
        var systemSettings = new SystemSettings();
        systemSettings.ExternalFileSystem.AfterSalesFilePath = fileRoot;
        var policyService = new AfterSalesPolicyService(factory, audit, currentUser, TestDemoControl.Off(factory), time, NullLogger<AfterSalesPolicyService>.Instance);
        var orderService = new ShopOrderService(factory, TestDemoControl.Off(factory), NullLogger<ShopOrderService>.Instance);
        var requestService = new AfterSalesRequestService(
            factory,
            policyService,
            orderService,
            new AfterSalesFileStore(Options.Create(systemSettings), NullLogger<AfterSalesFileStore>.Instance),
            audit,
            currentUser,
            time,
            NullLogger<AfterSalesRequestService>.Instance);
        var quota = new StubAiQuotaService();
        var usage = new RecordingTokenUsageRecorder();
        var callLog = new FakeAiCallLogRecorder();
        Harness? harness = null;
        var chatFactory = new AgentChatClientFactory(
            new StaticOptionsMonitor<AiSettings>(new AiSettings
            {
                Provider = nameof(AiProvider.AzureOpenAI),
                Endpoint = "https://unit-test.openai.azure.com",
                ApiKey = modelAvailable ? "key" : string.Empty,
                Model = "my-deployment",
            }),
            new StaticOptionsMonitor<SlowOperationSettings>(new SlowOperationSettings()),
            usage,
            callLog,
            quota,
            audit,
            currentUser,
            NullLoggerFactory.Instance)
        {
            InnerClientFactory = (_, _) =>
            {
                harness!.InnerClientCreations++;
                return modelAvailable ? model : throw new InvalidOperationException("The model must not be created during replay.");
            },
        };

        var runner = new AfterSalesAgentRunner(
            chatFactory,
            new StubSystemPromptProvider(),
            new AgentSessionStore(factory, time, NullLogger<AgentSessionStore>.Instance),
            new WorkflowCheckpointService(factory, time, NullLogger<WorkflowCheckpointService>.Instance),
            new AgentTraceService(factory, time, NullLogger<AgentTraceService>.Instance),
            orderService,
            policyService,
            requestService,
            new CustomerMemoryService(factory, audit, time, NullLogger<CustomerMemoryService>.Instance),
            new PermissionChecker(permissionContext, NullLogger<PermissionChecker>.Instance),
            inbox,
            demo,
            new AgentRecordingService(factory, time, NullLogger<AgentRecordingService>.Instance),
            cursors,
            currentUser,
            new StaticOptionsMonitor<AfterSalesAgentSettings>(new AfterSalesAgentSettings()),
            time,
            NullLoggerFactory.Instance)
        {
            ReplayUpdateDelay = TimeSpan.Zero,
        };

        harness = new Harness { Runner = runner, Quota = quota, Usage = usage, CallLog = callLog };
        return harness;
    }

    private static Task<AgentTurnResult> SendAsync(AfterSalesAgentRunner runner, Guid key, string text)
        => runner.SendAsync(key, text, _ => Task.CompletedTask, _ => Task.CompletedTask, CancellationToken.None);

    private static Task<AgentTurnResult> ConfirmAsync(AfterSalesAgentRunner runner, Guid key, int confirmationId, bool approve)
        => runner.ConfirmAsync(key, confirmationId, approve, _ => Task.CompletedTask, _ => Task.CompletedTask, CancellationToken.None);

    private Dictionary<string, object?> SizeExchangeArgs() => new()
    {
        ["orderItemId"] = black40,
        ["issueType"] = "size_exchange",
        ["targetSize"] = "41",
        ["isWorn"] = false,
        ["description"] = "尺寸偏小，想換大一碼",
    };

    /// <summary>
    /// 以即時模式（錄製中）跑旗艦劇本 A，回傳錄製 Id 與三輪結果。最後一句由模型依建立結果說出申請編號，
    /// 回放時那個編號必須換成回放當下建立的編號。
    /// </summary>
    private async Task<(int RecordingId, AgentTurnResult[] Results)> RecordFlagshipAsync()
    {
        var harness = CreateHarness(TestDemoControl.Create(factory, enabled: true), new ManualTimeProvider(UtcNow), modelAvailable: true);
        var runner = harness.Runner;
        var key = await runner.StartOrResumeAsync();
        model.Call("query_my_orders", []);
        model.Reply("您有兩雙鞋：黑色 40 碼、白色 41 碼。請問是黑 40 還是白 41？有穿過嗎？");
        var first = await SendAsync(runner, key, FirstLine);

        model.Call("get_effective_policy", new() { ["orderItemId"] = black40, ["issueType"] = "size_exchange" });
        model.Call("check_variant_stock", new() { ["orderItemId"] = black40 });
        model.Call("create_after_sales_request", SizeExchangeArgs());
        var second = await SendAsync(runner, key, SecondLine);
        var card = Assert.IsType<ConfirmationCard>(second.Confirmation);

        model.Fallback = messages =>
        {
            var created = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Last();
            var no = Regex.Match(JsonSerializer.Serialize(created.Result), @"AS\d{12}").Value;
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, $"已為您送出換尺碼申請，申請編號 {no}。"));
        };
        var third = await ConfirmAsync(runner, key, card.ConfirmationId, approve: true);
        model.Fallback = null;
        Assert.Matches(RequestNoPattern, third.CreatedRequestNo!);
        Assert.Contains(third.CreatedRequestNo!, third.ReplyText);
        Assert.Equal(6, model.CallCount);

        await using var db = factory.CreateDbContext();
        var recordingId = await db.AgentRecording.Where(x => x.SessionKey == key).Select(x => x.Id).SingleAsync();
        return (recordingId, [first, second, third]);
    }

    private async Task ResetAsync(TimeProvider time)
    {
        var systemSettings = new SystemSettings();
        systemSettings.ExternalFileSystem.AfterSalesFilePath = fileRoot;
        var reset = new DemoResetService(
            factory,
            new AfterSalesFileStore(Options.Create(systemSettings), NullLogger<AfterSalesFileStore>.Instance),
            Options.Create(new DemoSettings { Enabled = true, DemoUserPassword = "demo1234" }),
            time,
            new RecordingAuditLogService(),
            new CurrentUserService { CurrentUser = new CurrentUser { Id = 1, Account = "support" } },
            [inbox, cursors],
            NullLogger<DemoResetService>.Instance);
        Assert.True((await reset.ResetAsync()).Success);
    }

    private async Task<DemoControlService> ReplayDemoAsync(int recordingId)
    {
        var demo = TestDemoControl.Create(factory, enabled: true);
        await demo.SetModeAsync(DemoMode.Replay, recordingId);
        return demo;
    }

    /// <summary>錄製旗艦劇本 A → 隔天重置 → 切到回放（沒有金鑰、模型 client 一建立就失敗）。</summary>
    private async Task<(Harness Replay, AgentTurnResult[] Recorded)> RecordResetAndReplayAsync(Func<Task>? afterReset = null)
    {
        var (recordingId, recorded) = await RecordFlagshipAsync();
        var later = new ManualTimeProvider(Later);
        await ResetAsync(later);
        if (afterReset is not null)
        {
            await afterReset();
        }

        return (CreateHarness(await ReplayDemoAsync(recordingId), later, modelAvailable: false), recorded);
    }

    private async Task<int> RecordingRowCountAsync()
    {
        await using var db = factory.CreateDbContext();
        return await db.AgentRecording.CountAsync() + await db.AgentRecordedTurn.CountAsync() + await db.AgentRecordedCall.CountAsync();
    }

    [Fact]
    public async Task Replay_FlagshipA_ReproducesRepliesAndCreatesRequestWithoutModel()
    {
        var (replay, recorded) = await RecordResetAndReplayAsync();
        var runner = replay.Runner;
        var key = await runner.StartOrResumeAsync();

        var first = await SendAsync(runner, key, FirstLine);
        Assert.Equal(recorded[0].ReplyText, first.ReplyText);
        Assert.Equal(AgentStopReason.AwaitingUserReply, first.StopReason);

        var second = await SendAsync(runner, key, SecondLine);
        var card = Assert.IsType<ConfirmationCard>(second.Confirmation);
        Assert.Equal(AgentStopReason.AwaitingConfirmation, second.StopReason);
        Assert.Equal(recorded[1].Confirmation!.ProductLine, card.ProductLine);
        Assert.Equal(recorded[1].Confirmation!.ToSpec, card.ToSpec);

        var third = await ConfirmAsync(runner, key, card.ConfirmationId, approve: true);
        Assert.Equal(AgentStopReason.Answered, third.StopReason);
        Assert.Matches(RequestNoPattern, third.CreatedRequestNo!);
        // 工具真的建立了一筆新申請；模型（錄製）文字裡的舊編號換成這次建立的編號。
        Assert.NotEqual(recorded[2].CreatedRequestNo, third.CreatedRequestNo);
        Assert.Equal($"已為您送出換尺碼申請，申請編號 {third.CreatedRequestNo}。", third.ReplyText);
        await using (var db = factory.CreateDbContext())
        {
            Assert.True(await db.AfterSalesRequest.AnyAsync(x => x.RequestNo == third.CreatedRequestNo && x.CustomerUserId == customer1));
        }

        // 沒有呼叫模型、也沒有建立模型 client（不需金鑰）。
        Assert.Equal(6, model.CallCount);
        Assert.Equal(0, replay.InnerClientCreations);
    }

    /// <summary>
    /// AI 初審（M4）：即時錄下劇本 A 與三個初審節點 → 隔天重置 → 回放劇本 A 建立新申請 → 這筆申請的初審也走同一份錄製：
    /// 各節點依自己的 stream 重播，結論與錄製時相同，完全沒有建立模型 client（沒有金鑰也可以）、沒有用量，並記下回放來源。
    /// </summary>
    [Fact]
    public async Task Replay_PreReview_UsesRecordedStreamsWithoutModel()
    {
        var (recordingId, _) = await RecordFlagshipAsync();
        var liveKit = PreReviewNodeKit.Create(
            factory, fileRoot, model, TestDemoControl.Create(factory, enabled: true), cursors, new ManualTimeProvider(UtcNow));
        var liveRequestId = await RequestIdOfRecordingAsync(recordingId);
        await PreReviewNodeKit.AddPhotoAsync(factory, fileRoot, liveRequestId, "shoe.jpg", UtcNow.LocalDateTime);
        PreReviewNodeKit.ScriptThreeNodes(model);
        var expected = await liveKit.RunThreeNodesAsync(await liveKit.CaseAsync(liveRequestId));
        var liveCalls = model.CallCount;

        var later = new ManualTimeProvider(Later);
        await ResetAsync(later);
        var demo = await ReplayDemoAsync(recordingId);
        var replay = CreateHarness(demo, later, modelAvailable: false);
        var key = await replay.Runner.StartOrResumeAsync();
        await SendAsync(replay.Runner, key, FirstLine);
        var card = Assert.IsType<ConfirmationCard>((await SendAsync(replay.Runner, key, SecondLine)).Confirmation);
        Assert.NotNull((await ConfirmAsync(replay.Runner, key, card.ConfirmationId, approve: true)).CreatedRequestNo);
        int requestId;
        await using (var db = factory.CreateDbContext())
        {
            requestId = await db.AfterSalesRequest.Where(x => x.CaseKey == key).Select(x => x.Id).SingleAsync();
        }

        await PreReviewNodeKit.AddPhotoAsync(factory, fileRoot, requestId, "shoe.jpg", later.GetLocalNow().DateTime);
        var replayKit = PreReviewNodeKit.Create(factory, fileRoot, model: null, demo, cursors, later);
        var reviewCase = await replayKit.CaseAsync(requestId);

        var actual = await replayKit.RunThreeNodesAsync(reviewCase);

        Assert.Equal(FindingStatus.Ok, actual.Photo.Status);
        Assert.Equal(expected.Photo, actual.Photo);
        Assert.Equal(FindingStatus.Ok, actual.History.Status);
        Assert.Equal(expected.History, actual.History);
        Assert.Equal(expected.Summary.Recommendation, actual.Summary.Recommendation);
        Assert.Equal(expected.Summary.Headline, actual.Summary.Headline);
        Assert.Equal(expected.Summary.Reasons, actual.Summary.Reasons);
        Assert.Equal(liveCalls, model.CallCount);
        Assert.Empty(replayKit.CreatedModels);
        Assert.Empty(replayKit.Usage.Entries);
        Assert.Equal(recordingId, (await replayKit.Runs.GetAsync(reviewCase.RunId))!.ReplayRecordingId);

        // 錄製只有一次初審：再跑一輪就沒有可重播的回應，節點拋出（executor 會改為 Failed 結論），仍不會呼叫模型。
        var again = await replayKit.CaseAsync(requestId);
        await Assert.ThrowsAsync<ReplayExhaustedException>(() => replayKit.Photo.ReviewAsync(again, CancellationToken.None));
        Assert.Empty(replayKit.CreatedModels);
    }

    /// <summary>
    /// 展示控制台是回放模式，但這個案件不是回放建立的（例如即時建立的案件在回放模式下按「重新初審」、重啟續跑、啟動補建）：
    /// 沒有可重播的錄製 → 節點拋例外（executor 改為 Failed 結論），絕不建立模型 client、不呼叫真實模型。
    /// </summary>
    [Fact]
    public async Task Replay_PreReviewCaseWithoutRecording_FailsWithoutModel()
    {
        var (recordingId, _) = await RecordFlagshipAsync();
        var liveRequestId = await RequestIdOfRecordingAsync(recordingId);
        await PreReviewNodeKit.AddPhotoAsync(factory, fileRoot, liveRequestId, "shoe.jpg", UtcNow.LocalDateTime);
        var demo = await ReplayDemoAsync(recordingId);
        // 模型可用（給了腳本化 client）：若程式退回即時呼叫，這裡就會被呼叫到。
        var kit = PreReviewNodeKit.Create(factory, fileRoot, model, demo, cursors, new ManualTimeProvider(UtcNow));
        PreReviewNodeKit.ScriptThreeNodes(model);
        var callsBefore = model.CallCount;
        var reviewCase = await kit.CaseAsync(liveRequestId);

        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Photo.ReviewAsync(reviewCase, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.History.ReviewAsync(reviewCase, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Summary.SummarizeAsync(reviewCase, [], CancellationToken.None));

        Assert.Empty(kit.CreatedModels);
        Assert.Equal(callsBefore, model.CallCount);
        Assert.Empty(kit.Usage.Entries);
        Assert.Null((await kit.Runs.GetAsync(reviewCase.RunId))!.ReplayRecordingId);
    }

    private async Task<int> RequestIdOfRecordingAsync(int recordingId)
    {
        await using var db = factory.CreateDbContext();
        var sessionKey = await db.AgentRecording.Where(x => x.Id == recordingId).Select(x => x.SessionKey).SingleAsync();
        return await db.AfterSalesRequest.Where(x => x.CaseKey == sessionKey).Select(x => x.Id).SingleAsync();
    }

    [Fact]
    public async Task Replay_MarksRunTraceAndModelContext_LiveDoesNot()
    {
        var (recordingId, _) = await RecordFlagshipAsync();
        await using (var db = factory.CreateDbContext())
        {
            Assert.All(await db.AgentRunTrace.ToListAsync(), run => Assert.Null(run.ReplayRecordingId));
            var liveContexts = await db.AgentRunStep.Where(x => x.Kind == AgentStepKind.ModelContext).Select(x => x.PlainText).ToListAsync();
            Assert.Contains(liveContexts, text => text.StartsWith("第 1 次呼叫模型", StringComparison.Ordinal));
            Assert.DoesNotContain(liveContexts, text => text.Contains("回放", StringComparison.Ordinal));
        }

        var later = new ManualTimeProvider(Later);
        await ResetAsync(later);
        var replay = CreateHarness(await ReplayDemoAsync(recordingId), later, modelAvailable: false);
        var key = await replay.Runner.StartOrResumeAsync();
        await SendAsync(replay.Runner, key, FirstLine);

        await using (var db = factory.CreateDbContext())
        {
            var run = await db.AgentRunTrace.SingleAsync(x => x.SessionKey == key);
            Assert.Equal(recordingId, run.ReplayRecordingId);
            var contexts = await db.AgentRunStep
                .Where(x => x.AgentRunTraceId == run.Id && x.Kind == AgentStepKind.ModelContext)
                .Select(x => x.PlainText)
                .ToListAsync();
            Assert.NotEmpty(contexts);
            Assert.All(contexts, text =>
            {
                Assert.StartsWith($"回放：使用錄製 #{recordingId} 的回應（未呼叫模型）", text, StringComparison.Ordinal);
                Assert.DoesNotContain("次呼叫模型", text, StringComparison.Ordinal);
            });
        }

        var trace = new AgentTraceService(factory, later, NullLogger<AgentTraceService>.Instance);
        var summary = Assert.Single((await trace.GetRunsAsync(new DataRequest { CurrentPage = 1, PageSize = 10 })).Result, x => x.SessionKey == key);
        Assert.True(summary.IsReplay);
        Assert.Equal(recordingId, summary.ReplayRecordingId);
    }

    [Fact]
    public async Task Replay_DoesNotTouchQuotaOrUsage()
    {
        var (replay, _) = await RecordResetAndReplayAsync();
        var rowsBefore = await RecordingRowCountAsync();
        var runner = replay.Runner;
        var key = await runner.StartOrResumeAsync();

        await SendAsync(runner, key, FirstLine);
        var card = (await SendAsync(runner, key, SecondLine)).Confirmation!;
        var last = await ConfirmAsync(runner, key, card.ConfirmationId, approve: true);

        Assert.Equal(AgentStopReason.Answered, last.StopReason);
        Assert.Empty(replay.Quota.Checked);
        Assert.Empty(replay.Quota.Notified);
        Assert.Empty(replay.Usage.Entries);
        Assert.Empty(replay.CallLog.Entries);
        // 回放中不錄製。
        Assert.Equal(rowsBefore, await RecordingRowCountAsync());
    }

    [Fact]
    public async Task Replay_ToolResultDiffers_StopsWithReplayDiverged()
    {
        // 重置後把這雙鞋的庫存全部改成 0：check_variant_stock 的結果與錄製時不同。
        var (replay, recorded) = await RecordResetAndReplayAsync(async () =>
        {
            await using var db = factory.CreateDbContext();
            var productId = await db.SalesOrderItem.Where(x => x.Id == black40).Select(x => x.ProductVariant!.ProductId).SingleAsync();
            await db.ProductVariant.Where(x => x.ProductId == productId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Stock, 0));
        });
        var runner = replay.Runner;
        var key = await runner.StartOrResumeAsync();

        Assert.Equal(recorded[0].ReplyText, (await SendAsync(runner, key, FirstLine)).ReplyText);
        var second = await SendAsync(runner, key, SecondLine);

        Assert.Equal(AgentStopReason.ReplayDiverged, second.StopReason);
        Assert.Equal(DivergedExplanation, second.ReplyText);
        Assert.Equal(DivergedExplanation, second.StopExplanation);
        Assert.Null(second.Confirmation);
        Assert.Equal(0, replay.InnerClientCreations);
        await using var db = factory.CreateDbContext();
        Assert.False(await db.AgentConfirmation.AnyAsync());
    }

    [Fact]
    public async Task Replay_Exhausted_StopsWithReplayFinished()
    {
        // 第二個動作沒有錄到任何模型呼叫（例如當時額度不足）：回放到這裡錄製內容就用完了。
        var recordingKey = Guid.NewGuid();
        var service = new AgentRecordingService(factory, new ManualTimeProvider(UtcNow), NullLogger<AgentRecordingService>.Instance);
        await service.AppendTurnAsync(recordingKey, customer1, AgentRecordedTurnKind.Send, "你好");
        await service.AppendCallAsync(
            recordingKey,
            ModelRequestDigest.Compute([new ChatMessage(ChatRole.User, "你好")]),
            JsonSerializer.Serialize(new ChatResponse(new ChatMessage(ChatRole.Assistant, "您好，很高興為您服務。")), AIJsonUtilities.DefaultOptions),
            null,
            "my-deployment");
        await service.AppendTurnAsync(recordingKey, customer1, AgentRecordedTurnKind.Send, "還在嗎");
        int recordingId;
        await using (var db = factory.CreateDbContext())
        {
            recordingId = await db.AgentRecording.Where(x => x.SessionKey == recordingKey).Select(x => x.Id).SingleAsync();
        }

        var replay = CreateHarness(await ReplayDemoAsync(recordingId), new ManualTimeProvider(UtcNow), modelAvailable: false);
        var runner = replay.Runner;
        var key = await runner.StartOrResumeAsync();

        var first = await SendAsync(runner, key, "你好");
        Assert.Equal("您好，很高興為您服務。", first.ReplyText);
        Assert.Equal(AgentStopReason.Answered, first.StopReason);

        var second = await SendAsync(runner, key, "還在嗎");
        Assert.Equal(AgentStopReason.ReplayFinished, second.StopReason);
        Assert.Equal(FinishedExplanation, second.ReplyText);
        Assert.Equal(FinishedExplanation, second.StopExplanation);

        // 錄製的動作也播完了：再送出一律回「已播完」，不呼叫模型。
        var script = await runner.GetReplayScriptAsync(key);
        Assert.True(script.IsReplay);
        Assert.Null(script.NextTurn);
        var third = await SendAsync(runner, key, "還在嗎");
        Assert.Equal(AgentStopReason.ReplayFinished, third.StopReason);
        Assert.Equal(FinishedExplanation, third.ReplyText);
        Assert.Equal(0, replay.InnerClientCreations);
        Assert.Equal(0, model.CallCount);
    }

    [Fact]
    public async Task Replay_ScriptProvidesNextCustomerLine()
    {
        // 即時模式：沒有回放劇本。
        var live = CreateHarness(TestDemoControl.Create(factory, enabled: true), new ManualTimeProvider(UtcNow), modelAvailable: true);
        var liveScript = await live.Runner.GetReplayScriptAsync(await live.Runner.StartOrResumeAsync());
        Assert.False(liveScript.IsReplay);
        Assert.Null(liveScript.NextTurn);

        var (replay, _) = await RecordResetAndReplayAsync();
        var runner = replay.Runner;
        var key = await runner.StartOrResumeAsync();

        var script = await runner.GetReplayScriptAsync(key);
        Assert.True(script.IsReplay);
        Assert.Equal(AgentRecordedTurnKind.Send, script.NextTurn!.Kind);
        Assert.Equal(FirstLine, script.NextTurn.CustomerText);

        // 送出的句子與錄製不同：程式固定回覆，不呼叫模型、不前進。
        var wrong = await SendAsync(runner, key, "我想退貨");
        Assert.Equal(AgentStopReason.ReplayActionMismatch, wrong.StopReason);
        Assert.Equal("回放中請送出錄製的句子", wrong.ReplyText);
        Assert.Equal(ActionMismatchExplanation, wrong.StopExplanation);
        Assert.Equal(FirstLine, (await runner.GetReplayScriptAsync(key)).NextTurn!.CustomerText);

        await SendAsync(runner, key, FirstLine);
        Assert.Equal(SecondLine, (await runner.GetReplayScriptAsync(key)).NextTurn!.CustomerText);

        var card = (await SendAsync(runner, key, SecondLine)).Confirmation!;
        var confirmStep = (await runner.GetReplayScriptAsync(key)).NextTurn!;
        Assert.Equal(AgentRecordedTurnKind.Confirm, confirmStep.Kind);
        Assert.Null(confirmStep.CustomerText);

        // 錄製時按的是確認：按取消會被擋下，確認卡仍然有效。
        var rejected = await ConfirmAsync(runner, key, card.ConfirmationId, approve: false);
        Assert.Equal(AgentStopReason.ReplayActionMismatch, rejected.StopReason);
        Assert.Equal(ActionMismatchExplanation, rejected.StopExplanation);
        Assert.Equal("回放中請按下錄製時的選擇：確認送出", rejected.ReplyText);
        Assert.Null(rejected.CreatedRequestNo);

        var confirmed = await ConfirmAsync(runner, key, card.ConfirmationId, approve: true);
        Assert.Matches(RequestNoPattern, confirmed.CreatedRequestNo!);
        var done = await runner.GetReplayScriptAsync(key);
        Assert.True(done.IsReplay);
        Assert.Null(done.NextTurn);
        Assert.Equal(0, replay.InnerClientCreations);
    }

    [Fact]
    public async Task Replay_StartNewOrDemoReset_RestartsCursor()
    {
        var (replay, recorded) = await RecordResetAndReplayAsync();
        var runner = replay.Runner;
        var key = await runner.StartOrResumeAsync();
        await SendAsync(runner, key, FirstLine);
        Assert.Equal(SecondLine, (await runner.GetReplayScriptAsync(key)).NextTurn!.CustomerText);

        // 開新對話：從錄製的第一句重新開始，模型回應也從第一筆開始。
        var fresh = await runner.StartNewAsync();
        Assert.Equal(FirstLine, (await runner.GetReplayScriptAsync(fresh)).NextTurn!.CustomerText);
        Assert.Equal(recorded[0].ReplyText, (await SendAsync(runner, fresh, FirstLine)).ReplyText);

        // 一鍵重置示範資料：所有游標清空。
        ((IDemoResetParticipant)cursors).OnDemoDataReset();
        Assert.Equal(FirstLine, (await runner.GetReplayScriptAsync(fresh)).NextTurn!.CustomerText);
    }

    [Fact]
    public async Task ReplayClient_ReplacesRecordedRequestNoWithCreatedOne()
    {
        static ChatResponse Recorded(string text) => new(new ChatMessage(ChatRole.Assistant, text));
        var created = ToolOutcome.Ok(new { request_id = 7, request_no = "AS202610240007", status = "pending" }).ToJsonElement();
        List<ChatMessage> withCreate =
        [
            new(ChatRole.User, "黑色那雙，沒穿過"),
            new(ChatRole.Assistant, [new FunctionCallContent("call-create", AfterSalesAgentTools.CreateAfterSalesRequestName, new Dictionary<string, object?>())]),
            new(ChatRole.Tool, [new FunctionResultContent("call-create", created)]),
        ];
        List<ChatMessage> withoutCreate = [new(ChatRole.User, "AS202610230002 的進度？")];

        AgentRecordedCallView Call(int sequence, IEnumerable<ChatMessage> request, string text) => new(
            sequence,
            ModelRequestDigest.Compute(request),
            JsonSerializer.Serialize(Recorded(text), AIJsonUtilities.DefaultOptions),
            null,
            UtcNow.DateTime);

        var store = new ReplayCursorStore();
        var client = new ReplayChatClient(
            [
                Call(1, withCreate, "已送出，申請編號 AS202610230002。"),
                Call(2, withoutCreate, "AS202610230002 處理中。"),
            ],
            store.Get(Guid.NewGuid(), recordingId: 1),
            TimeSpan.Zero,
            NullLogger.Instance);

        var replaced = new List<string>();
        await foreach (var update in client.GetStreamingResponseAsync(withCreate))
        {
            replaced.Add(update.Text);
        }

        Assert.Equal("已送出，申請編號 AS202610240007。", string.Concat(replaced));
        // 這次的請求裡沒有建立結果（不知道新編號）：原樣保留。
        Assert.Equal("AS202610230002 處理中。", (await client.GetResponseAsync(withoutCreate)).Text);
        await Assert.ThrowsAsync<ReplayExhaustedException>(() => client.GetResponseAsync(withoutCreate));
    }

    [Fact]
    public async Task ReplayClient_DigestMismatch_ThrowsDiverged()
    {
        var store = new ReplayCursorStore();
        var client = new ReplayChatClient(
            [
                new AgentRecordedCallView(
                    1,
                    ModelRequestDigest.Compute([new ChatMessage(ChatRole.User, "還有 41 號嗎？")]),
                    JsonSerializer.Serialize(new ChatResponse(new ChatMessage(ChatRole.Assistant, "有的。")), AIJsonUtilities.DefaultOptions),
                    null,
                    UtcNow.DateTime),
            ],
            store.Get(Guid.NewGuid(), recordingId: 1),
            TimeSpan.Zero,
            NullLogger.Instance);

        await Assert.ThrowsAsync<ReplayDivergedException>(() => client.GetResponseAsync([new ChatMessage(ChatRole.User, "還有 42 號嗎？")]));
        // 不一致的那筆不會被消耗：送出一致的請求仍可取得。
        Assert.Equal("有的。", (await client.GetResponseAsync([new ChatMessage(ChatRole.User, "還有 41 號嗎？")])).Text);
    }
}

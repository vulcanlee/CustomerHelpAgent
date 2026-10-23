using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
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
using CustomerHelpAgent.Models.Others;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Agents;
using CustomerHelpAgent.Share.Shop;
using CustomerHelpAgent.Web.Agents;
using CustomerHelpAgent.Web.Agents.AfterSales;
using CustomerHelpAgent.Web.Configuration;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 售後 Agent 執行底座：以腳本化模型（<see cref="AgentChatClientFactory.InnerClientFactory"/>）＋真實服務與 SQLite 記憶體資料庫，
/// 驗證確認關卡、停止原因、固定說明、會話承接與執行軌跡。資料取自 <see cref="DemoSeedData"/>（customer1：黑 40、白 41）。
/// </summary>
public sealed class AfterSalesAgentRunnerTests : IAsyncLifetime
{
    /// <summary>本地時間（UTC+8）2026/10/23 10:00。</summary>
    private static readonly DateTimeOffset UtcNow = new(2026, 10, 23, 2, 0, 0, TimeSpan.Zero);

    private static readonly Regex RequestNoPattern = new(@"^AS\d{12}$");

    private readonly string fileRoot = Path.Combine(Path.GetTempPath(), "AgentRunnerTests-" + Guid.NewGuid().ToString("N"));
    private readonly AgentPhotoInbox inbox = new();
    private readonly ScriptedAgentModel model = new();
    private readonly StubAiQuotaService quota = new();
    private readonly List<AgentTraceEvent> events = [];
    private readonly List<string> deltas = [];
    private readonly AfterSalesAgentSettings settings = new();
    private SqliteConnection connection = null!;
    private TestDbContextFactory factory = null!;
    private BackendDBContext permissionContext = null!;
    private int customer1;
    private int customer2;
    private int black40;
    private int white41;

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
            customer2 = (await db.MyUser.SingleAsync(x => x.Account == DemoSeedData.Customer2)).Id;
            black40 = await db.SalesOrderItem.Where(x => x.SalesOrder!.OrderNo == "SO-DEMO-0001").Select(x => x.Id).SingleAsync();
            white41 = await db.SalesOrderItem.Where(x => x.SalesOrder!.OrderNo == "SO-DEMO-0002").Select(x => x.Id).SingleAsync();
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

    /// <summary>每次呼叫都是全新的 Runner（等同換頁或另一個 circuit），共用同一個資料庫、照片暫存區與腳本化模型。</summary>
    private AfterSalesAgentRunner CreateRunner(
        Func<AiSettings, AgentApiStyle, IChatClient>? innerClientFactory = null,
        int? userId = null,
        IDbContextFactory<BackendDBContext>? sessionStoreFactory = null)
    {
        var time = new ManualTimeProvider(UtcNow);
        var currentUser = new CurrentUserService
        {
            CurrentUser = userId is { } id && id != customer1
                ? new CurrentUser { Id = id, Account = DemoSeedData.Customer2 }
                : new CurrentUser { Id = customer1, Account = DemoSeedData.Customer1 },
        };
        var audit = new RecordingAuditLogService();
        var systemSettings = new SystemSettings();
        systemSettings.ExternalFileSystem.AfterSalesFilePath = fileRoot;
        var policyService = new AfterSalesPolicyService(factory, audit, currentUser, time, NullLogger<AfterSalesPolicyService>.Instance);
        var orderService = new ShopOrderService(factory, NullLogger<ShopOrderService>.Instance);
        var requestService = new AfterSalesRequestService(
            factory,
            policyService,
            orderService,
            new AfterSalesFileStore(Options.Create(systemSettings), NullLogger<AfterSalesFileStore>.Instance),
            audit,
            currentUser,
            time,
            NullLogger<AfterSalesRequestService>.Instance);
        var chatFactory = new AgentChatClientFactory(
            new StaticOptionsMonitor<AiSettings>(new AiSettings
            {
                Provider = nameof(AiProvider.AzureOpenAI),
                Endpoint = "https://unit-test.openai.azure.com",
                ApiKey = "key",
                Model = "my-deployment",
            }),
            new StaticOptionsMonitor<SlowOperationSettings>(new SlowOperationSettings()),
            new RecordingTokenUsageRecorder(),
            new FakeAiCallLogRecorder(),
            quota,
            audit,
            currentUser,
            NullLoggerFactory.Instance)
        {
            InnerClientFactory = innerClientFactory ?? ((_, _) => model),
        };

        return new AfterSalesAgentRunner(
            chatFactory,
            new StubSystemPromptProvider(),
            new AgentSessionStore(sessionStoreFactory ?? factory, time, NullLogger<AgentSessionStore>.Instance),
            new AgentTraceService(factory, time, NullLogger<AgentTraceService>.Instance),
            orderService,
            policyService,
            requestService,
            new PermissionChecker(permissionContext, NullLogger<PermissionChecker>.Instance),
            inbox,
            currentUser,
            new StaticOptionsMonitor<AfterSalesAgentSettings>(settings),
            time,
            NullLoggerFactory.Instance);
    }

    private Task OnEvent(AgentTraceEvent e)
    {
        events.Add(e);
        return Task.CompletedTask;
    }

    private Task OnDelta(string text)
    {
        deltas.Add(text);
        return Task.CompletedTask;
    }

    private Task<AgentTurnResult> SendAsync(AfterSalesAgentRunner runner, Guid key, string text)
        => runner.SendAsync(key, text, OnEvent, OnDelta, CancellationToken.None);

    private Task<AgentTurnResult> ConfirmAsync(AfterSalesAgentRunner runner, Guid key, int confirmationId, bool approve)
        => runner.ConfirmAsync(key, confirmationId, approve, OnEvent, OnDelta, CancellationToken.None);

    private Dictionary<string, object?> SizeExchangeArgs() => new()
    {
        ["orderItemId"] = black40,
        ["issueType"] = "size_exchange",
        ["targetSize"] = "41",
        ["isWorn"] = false,
        ["description"] = "尺寸偏小，想換大一碼",
    };

    /// <summary>單一回合直接走到確認卡：查規則 → 提出建立申請（需確認）。</summary>
    private async Task<(AfterSalesAgentRunner Runner, Guid Key, ConfirmationCard Card)> ReachConfirmationAsync()
    {
        var runner = CreateRunner();
        var key = await runner.StartOrResumeAsync();
        model.Call("get_effective_policy", new() { ["orderItemId"] = black40, ["issueType"] = "size_exchange" });
        model.Call("create_after_sales_request", SizeExchangeArgs());

        var result = await SendAsync(runner, key, "黑色那雙 40 碼想換 41，沒穿過");

        Assert.Equal(AgentStopReason.AwaitingConfirmation, result.StopReason);
        return (runner, key, Assert.IsType<ConfirmationCard>(result.Confirmation));
    }

    private async Task<int> RequestCountAsync()
    {
        await using var db = factory.CreateDbContext();
        return await db.AfterSalesRequest.CountAsync();
    }

    [Fact]
    public async Task FlagshipA_AsksWhichPairThenCreatesAfterConfirm()
    {
        var runner = CreateRunner();
        var key = await runner.StartOrResumeAsync();

        model.Call("query_my_orders", []);
        model.Reply("您有兩雙鞋：黑色 40 碼、白色 41 碼。請問是黑 40 還是白 41？有穿過嗎？");
        var first = await SendAsync(runner, key, "剛買的鞋尺碼不合適，想換大一碼");

        Assert.Equal(AgentStopReason.AwaitingUserReply, first.StopReason);
        Assert.Contains("黑 40 還是白 41？", first.ReplyText);
        Assert.Null(first.Confirmation);
        Assert.NotEmpty(deltas);

        model.Call("get_effective_policy", new() { ["orderItemId"] = black40, ["issueType"] = "size_exchange" });
        model.Call("check_variant_stock", new() { ["orderItemId"] = black40 });
        model.Call("create_after_sales_request", SizeExchangeArgs());
        var second = await SendAsync(runner, key, "黑色那雙，沒穿過");

        Assert.Equal(AgentStopReason.AwaitingConfirmation, second.StopReason);
        var card = Assert.IsType<ConfirmationCard>(second.Confirmation);
        Assert.Equal("黑色 / 40", card.FromSpec);
        Assert.Equal("黑色 / 41", card.ToSpec);
        Assert.Equal("換尺碼", card.IssueLabel);
        Assert.Contains("第 1 版", card.PolicyLine);
        Assert.Equal(0, card.PhotoCount);
        Assert.Equal("SIZE-EXCHANGE", Assert.Single(second.PolicyCards).Code);
        Assert.Equal(0, await RequestCountAsync());

        model.Reply("已為您送出換尺碼申請。");
        var third = await ConfirmAsync(runner, key, card.ConfirmationId, approve: true);

        Assert.Matches(RequestNoPattern, third.CreatedRequestNo!);
        Assert.Equal(AgentStopReason.Answered, third.StopReason);
        await using var db = factory.CreateDbContext();
        var row = await db.AfterSalesRequest.SingleAsync();
        Assert.Equal(AfterSalesChannel.Agent, row.Channel);
        Assert.Equal(key, row.CaseKey);
        Assert.Equal(third.CreatedRequestNo, row.RequestNo);
        var confirmation = await db.AgentConfirmation.SingleAsync();
        Assert.True(confirmation.Approved);
        Assert.Equal(row.Id, confirmation.AfterSalesRequestId);
    }

    [Fact]
    public async Task Run_UserRepliesTextInsteadOfConfirm_NoRequestCreated()
    {
        var (runner, key, card) = await ReachConfirmationAsync();

        model.Reply("好的，有需要再告訴我。");
        var result = await SendAsync(runner, key, "我再想想");

        Assert.Null(result.Confirmation);
        Assert.Equal(0, await RequestCountAsync());
        await using (var db = factory.CreateDbContext())
        {
            Assert.False((await db.AgentConfirmation.SingleAsync()).Approved);
        }

        // 已作廢的卡片再按確認：不會執行工具，也不會再呼叫模型。
        var calls = model.CallCount;
        var late = await ConfirmAsync(runner, key, card.ConfirmationId, approve: true);

        Assert.Null(late.CreatedRequestNo);
        Assert.Equal(calls, model.CallCount);
        Assert.Equal(0, await RequestCountAsync());
    }

    [Fact]
    public async Task Confirm_Rejected_NoRequestCreated()
    {
        var (runner, key, card) = await ReachConfirmationAsync();

        model.Reply("好的，已為您取消。");
        var result = await ConfirmAsync(runner, key, card.ConfirmationId, approve: false);

        Assert.Equal(AgentStopReason.Answered, result.StopReason);
        Assert.Null(result.CreatedRequestNo);
        Assert.Equal(0, await RequestCountAsync());
        await using var db = factory.CreateDbContext();
        var confirmation = await db.AgentConfirmation.SingleAsync();
        Assert.False(confirmation.Approved);
        Assert.NotNull(confirmation.DecidedAt);
        Assert.Contains(events, e => e.Kind == AgentStepKind.ConfirmationDecided);
    }

    [Fact]
    public async Task Confirm_WhenEligibilityChangedSinceCard_DoesNotWrite()
    {
        var (runner, key, card) = await ReachConfirmationAsync();
        await using (var db = factory.CreateDbContext())
        {
            await db.SalesOrder
                .Where(x => x.OrderNo == "SO-DEMO-0001")
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.DeliveredAt, new DateTime(2026, 10, 13, 10, 0, 0)));
        }

        model.Reply("很抱歉，已超過換貨期限，無法為您建立申請。");
        var result = await ConfirmAsync(runner, key, card.ConfirmationId, approve: true);

        Assert.Null(result.CreatedRequestNo);
        Assert.Equal(0, await RequestCountAsync());
        Assert.Contains(events, e => e.Kind == AgentStepKind.ToolResult && e.RawJson!.Contains("\"status\":\"invalid\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Confirm_ThenQuotaBlocksNextModelCall_ReplyContainsRequestNo()
    {
        var (runner, key, card) = await ReachConfirmationAsync();
        quota.Block = new AiQuotaLimitStatus(
            AiQuotaScope.User, AiQuotaPeriod.Daily, 50, 51.25, new DateTime(2026, 10, 23), new DateTime(2026, 10, 24));

        var result = await ConfirmAsync(runner, key, card.ConfirmationId, approve: true);

        Assert.Equal(AgentStopReason.QuotaExceeded, result.StopReason);
        Assert.Matches(RequestNoPattern, result.CreatedRequestNo!);
        Assert.Contains($"申請已建立（{result.CreatedRequestNo}），但 AI 回覆暫時失敗，請到「我的申請」查看進度。", result.ReplyText);
        Assert.Contains("已達每人每日 AI 用量上限", result.StopExplanation);
        Assert.Equal(1, await RequestCountAsync());
    }

    [Fact]
    public async Task Confirm_ThenModelErrorAfterWrite_ReplyContainsRequestNoAndSessionRemembers()
    {
        var (runner, key, card) = await ReachConfirmationAsync();
        model.Throw(new InvalidOperationException("upstream broke"));

        var result = await ConfirmAsync(runner, key, card.ConfirmationId, approve: true);

        Assert.Equal(AgentStopReason.ModelError, result.StopReason);
        var requestNo = result.CreatedRequestNo!;
        Assert.Matches(RequestNoPattern, requestNo);
        Assert.Equal($"申請已建立（{requestNo}），但 AI 回覆暫時失敗，請到「我的申請」查看進度。", result.ReplyText);
        Assert.Equal(1, await RequestCountAsync());

        model.Reply("您的申請已建立，可以到「我的申請」查看。");
        var next = await SendAsync(CreateRunner(), key, "剛剛的申請有成功嗎？");

        Assert.Equal(AgentStopReason.Answered, next.StopReason);
        Assert.Contains(model.Requests[^1], m => m.Text.Contains(requestNo, StringComparison.Ordinal));
        Assert.Contains(await CreateRunner().GetHistoryAsync(key), e => e.Kind == AgentStepKind.ModelReply && e.PlainText.Contains(requestNo, StringComparison.Ordinal));
        Assert.Equal(1, await RequestCountAsync());
    }

    [Fact]
    public async Task Tool_Timeout_TerminatesWithFixedExplanation()
    {
        settings.ToolTimeoutSeconds = 1;
        var runner = CreateRunner();
        runner.ConfigureToolsForTesting = tools => tools.BeforeServiceCallForTesting = ct => Task.Delay(Timeout.InfiniteTimeSpan, ct);
        var key = await runner.StartOrResumeAsync();
        model.Respond(new ChatMessage(ChatRole.Assistant,
        [
            new TextContent("查無資料。"),
            new FunctionCallContent("call-timeout", "query_my_orders", new Dictionary<string, object?>()),
        ]));
        model.Fallback = _ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "查無資料，您沒有訂單。"));

        var result = await SendAsync(runner, key, "我的鞋子訂單呢？");

        Assert.Equal(AgentStopReason.Timeout, result.StopReason);
        Assert.Contains("不代表您沒有這筆訂單", result.StopExplanation);
        Assert.Equal(result.StopExplanation, result.ReplyText);
        Assert.DoesNotContain("查無資料", result.ReplyText);
        Assert.Equal(1, model.CallCount);
    }

    [Fact]
    public async Task Tool_Forbidden_TerminatesImmediately()
    {
        await using (var db = factory.CreateDbContext())
        {
            await db.UserRole.Where(x => x.MyUserId == customer1).ExecuteDeleteAsync();
            await db.MyUser.Where(x => x.Id == customer1).ExecuteUpdateAsync(s => s.SetProperty(x => x.RoleViewId, (int?)null));
        }

        var runner = CreateRunner();
        var key = await runner.StartOrResumeAsync();
        model.Call("query_my_orders", []);
        model.Call("track_shipment", new() { ["orderItemId"] = white41 });

        var result = await SendAsync(runner, key, "幫我查物流");

        Assert.Equal(AgentStopReason.Forbidden, result.StopReason);
        Assert.Equal("您的登入狀態已失效，請重新登入後再試。", result.ReplyText);
        Assert.Equal(1, model.CallCount);
        Assert.Single(events, e => e.Kind == AgentStepKind.ToolRequest);
    }

    [Fact]
    public async Task NotFound_ThenCreate_IsBlocked()
    {
        var runner = CreateRunner();
        var key = await runner.StartOrResumeAsync();
        model.Call("query_my_orders", new() { ["color"] = "紅色" });
        model.Call("create_after_sales_request", SizeExchangeArgs());
        model.Reply("找不到紅色的訂單，請問是哪一雙鞋呢？");

        var result = await SendAsync(runner, key, "紅色那雙換大一碼");

        Assert.Null(result.Confirmation);
        Assert.Equal(AgentStopReason.AwaitingUserReply, result.StopReason);
        Assert.Equal(0, await RequestCountAsync());
        Assert.Contains(events, e => e.Kind == AgentStepKind.ToolResult && e.RawJson!.Contains("\"status\":\"blocked\"", StringComparison.Ordinal));
        Assert.Contains(model.Requests[^1].SelectMany(m => m.Contents).OfType<FunctionResultContent>(),
            r => (r.Result?.ToString() ?? string.Empty).Contains("資訊不足或規則衝突", StringComparison.Ordinal));
        await using var db = factory.CreateDbContext();
        Assert.False(await db.AgentConfirmation.AnyAsync());
    }

    [Fact]
    public async Task StepLimit_StopsWithStepLimitReached()
    {
        settings.MaxSteps = 3;
        var runner = CreateRunner();
        var key = await runner.StartOrResumeAsync();
        model.Fallback = _ => ScriptedAgentModel.FunctionCall("query_my_orders", []);

        var result = await SendAsync(runner, key, "幫我看看訂單");

        Assert.Equal(AgentStopReason.StepLimitReached, result.StopReason);
        Assert.False(string.IsNullOrWhiteSpace(result.ReplyText));
        Assert.Equal(settings.MaxSteps + 1, model.CallCount);
    }

    [Fact]
    public async Task PhotoRequest_StopsAndWaits()
    {
        var runner = CreateRunner();
        var key = await runner.StartOrResumeAsync();
        model.Call("request_photo_upload", new() { ["orderItemId"] = white41, ["reason"] = "請拍攝開膠處" });
        model.Fallback = _ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "不應該再被呼叫"));

        var result = await SendAsync(runner, key, "白色那雙開膠了");

        Assert.Equal(AgentStopReason.AwaitingPhotoUpload, result.StopReason);
        Assert.False(string.IsNullOrWhiteSpace(result.ReplyText));
        Assert.Equal(1, model.CallCount);

        Assert.True(inbox.Add(key, "crack.jpg", [1, 2, 3]));
        model.Fallback = null;
        model.Reply("收到照片了。");
        var resumed = await runner.NotifyPhotosUploadedAsync(key, OnEvent, OnDelta, CancellationToken.None);

        Assert.Equal(AgentStopReason.Answered, resumed.StopReason);
        Assert.Contains(model.Requests[^1], m => m.Role == ChatRole.User && m.Text == "（已上傳 1 張照片）");
    }

    [Fact]
    public async Task QuotaExceeded_ReturnsQuotaMessage()
    {
        quota.Block = new AiQuotaLimitStatus(
            AiQuotaScope.User, AiQuotaPeriod.Daily, 50, 51.25, new DateTime(2026, 10, 23), new DateTime(2026, 10, 24));
        var runner = CreateRunner();
        var key = await runner.StartOrResumeAsync();

        var result = await SendAsync(runner, key, "你好");

        Assert.Equal(AgentStopReason.QuotaExceeded, result.StopReason);
        Assert.Contains("已達每人每日 AI 用量上限", result.StopExplanation);
        Assert.Equal(result.StopExplanation, result.ReplyText);
        Assert.Equal(0, model.CallCount);
    }

    [Fact]
    public async Task Session_PersistsAndRestoresHistory()
    {
        var first = CreateRunner();
        var key = await first.StartOrResumeAsync();
        model.Reply("您好，請問需要什麼協助？");
        await SendAsync(first, key, "你好");

        var second = CreateRunner();
        Assert.Equal(key, await second.StartOrResumeAsync());
        model.Reply("好的，請問是哪一雙？");
        await SendAsync(second, key, "我想換尺碼");

        var texts = model.Requests[1].Select(m => m.Text).ToList();
        Assert.Contains("你好", texts);
        Assert.Contains("您好，請問需要什麼協助？", texts);

        var history = await CreateRunner().GetHistoryAsync(key);
        Assert.Equal(
            [
                (AgentStepKind.UserMessage, "你好"),
                (AgentStepKind.ModelReply, "您好，請問需要什麼協助？"),
                (AgentStepKind.UserMessage, "我想換尺碼"),
                (AgentStepKind.ModelReply, "好的，請問是哪一雙？"),
            ],
            history.Select(x => (x.Kind, x.PlainText)));
    }

    [Fact]
    public async Task History_RestoresPendingConfirmationCard()
    {
        var (_, key, card) = await ReachConfirmationAsync();

        var history = await CreateRunner().GetHistoryAsync(key);

        var shown = Assert.Single(history, x => x.Kind == AgentStepKind.ConfirmationShown);
        var restored = ConfirmationCard.FromJson(shown.RawJson!);
        Assert.Equal(card with { Facts = [] }, restored with { Facts = [] });
        Assert.Equal(card.Facts, restored.Facts);
    }

    [Fact]
    public async Task Restore_ThenConfirm_CreatesExactlyOne()
    {
        var (_, key, card) = await ReachConfirmationAsync();

        var restored = CreateRunner();
        Assert.Equal(key, await restored.StartOrResumeAsync());
        model.Reply("已送出。");
        var result = await ConfirmAsync(restored, key, card.ConfirmationId, approve: true);
        var again = await ConfirmAsync(restored, key, card.ConfirmationId, approve: true);

        Assert.Matches(RequestNoPattern, result.CreatedRequestNo!);
        Assert.Null(again.CreatedRequestNo);
        Assert.Equal(1, await RequestCountAsync());
    }

    [Fact]
    public async Task StartNew_ClosesOldSessionAndClearsPhotos()
    {
        var runner = CreateRunner();
        var old = await runner.StartOrResumeAsync();
        Assert.True(inbox.Add(old, "a.jpg", [1]));

        var fresh = await runner.StartNewAsync();

        Assert.NotEqual(old, fresh);
        Assert.Equal(0, inbox.Count(old));
        Assert.Equal(fresh, await CreateRunner().StartOrResumeAsync());
        await using var db = factory.CreateDbContext();
        Assert.True((await db.AgentSessionRecord.SingleAsync(x => x.SessionKey == old)).IsClosed);
    }

    [Fact]
    public async Task FlagshipA_TraceContainsExpectedPlainSteps()
    {
        var runner = CreateRunner();
        var key = await runner.StartOrResumeAsync();
        model.Call("query_my_orders", new() { ["color"] = "黑色" });
        model.Reply("請問是黑 40 還是白 41？");

        await SendAsync(runner, key, "剛買的鞋尺碼不合適，想換大一碼");

        AgentStepKind[] expected =
        [
            AgentStepKind.UserMessage, AgentStepKind.ModelContext, AgentStepKind.ToolRequest, AgentStepKind.ProgramCheck,
            AgentStepKind.ToolResult, AgentStepKind.ModelReply, AgentStepKind.Stop,
        ];
        AssertSubsequence(expected, events.Select(x => x.Kind).ToList());
        Assert.Contains(events, e => e.Kind == AgentStepKind.ToolRequest && e.PlainText.Contains("Agent 決定先查你的訂單（顏色：黑色）", StringComparison.Ordinal));
        Assert.Contains(events, e => e.Kind == AgentStepKind.ProgramCheck && e.PlainText.Contains("身分", StringComparison.Ordinal));
        Assert.Contains(events, e => e.Kind == AgentStepKind.ToolResult && e.PlainText.Contains("未送出：", StringComparison.Ordinal) && e.PlainText.Contains("地址", StringComparison.Ordinal));
        Assert.All(events.Where(e => e.Kind == AgentStepKind.ModelContext), e =>
        {
            Assert.Contains("地址", e.PlainText);
            Assert.Contains("手機", e.PlainText);
        });

        var trace = new AgentTraceService(factory, TimeProvider.System, NullLogger<AgentTraceService>.Instance);
        var run = Assert.Single((await trace.GetRunsAsync(new DataRequest { CurrentPage = 1, PageSize = 10 })).Result);
        Assert.Equal(AgentStopReason.AwaitingUserReply, run.StopReason);
        Assert.Equal(2, run.ModelCallCount);
        Assert.Equal(1, run.ToolCallCount);
        var steps = await trace.GetStepsAsync(run.Id);
        Assert.Equal(events.Select(x => (x.Kind, x.PlainText)), steps.Select(x => (x.Kind, x.PlainText)));
    }

    [Fact]
    public async Task Trace_NeverContainsAddressOrPhone()
    {
        var runner = CreateRunner();
        var key = await runner.StartOrResumeAsync();
        model.Call("query_my_orders", []);
        model.Reply("請問是黑 40 還是白 41？");
        await SendAsync(runner, key, "剛買的鞋尺碼不合適，想換大一碼");
        model.Call("get_effective_policy", new() { ["orderItemId"] = black40, ["issueType"] = "size_exchange" });
        model.Call("check_variant_stock", new() { ["orderItemId"] = black40 });
        model.Call("track_shipment", new() { ["orderItemId"] = black40 });
        model.Call("create_after_sales_request", SizeExchangeArgs());
        var second = await SendAsync(runner, key, "黑色那雙，沒穿過");
        model.Reply("已送出。");
        await ConfirmAsync(runner, key, second.Confirmation!.ConfirmationId, approve: true);

        var trace = new AgentTraceService(factory, TimeProvider.System, NullLogger<AgentTraceService>.Instance);
        var texts = events.SelectMany(e => new[] { e.PlainText, e.RawJson ?? string.Empty }).ToList();
        foreach (var run in (await trace.GetRunsAsync(new DataRequest { CurrentPage = 1, PageSize = 50 })).Result)
        {
            texts.AddRange((await trace.GetStepsAsync(run.Id)).SelectMany(s => new[] { s.PlainText, s.RawJson ?? string.Empty }));
        }

        Assert.Contains(texts, t => t.Contains("SO-DEMO-0001", StringComparison.Ordinal));
        foreach (var forbidden in new[] { "示範路", "0900-000-001", "王大同" })
        {
            Assert.DoesNotContain(texts, t => t.Contains(forbidden, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task ModelRequest_Responses_ShouldNotStoreOnServer_AndSendConfiguredReasoningWithoutTemperature()
    {
        settings.ApiStyle = nameof(AgentApiStyle.Responses);
        settings.ReasoningEffort = "low";
        settings.Temperature = null;
        var handler = new StubHttpMessageHandler(_ => Sse(
            """{"type":"response.created","sequence_number":0,"response":{"id":"resp_1","object":"response","created_at":1,"status":"in_progress","model":"my-deployment","output":[]}}""",
            """{"type":"response.output_text.delta","sequence_number":1,"item_id":"msg_1","output_index":0,"content_index":0,"delta":"請問是哪一雙？"}""",
            """{"type":"response.completed","sequence_number":2,"response":{"id":"resp_1","object":"response","created_at":1,"status":"completed","model":"my-deployment","output":[{"id":"msg_1","type":"message","status":"completed","role":"assistant","content":[{"type":"output_text","annotations":[],"text":"請問是哪一雙？"}]}],"store":false,"usage":{"input_tokens":1,"input_tokens_details":{"cached_tokens":0},"output_tokens":1,"output_tokens_details":{"reasoning_tokens":0},"total_tokens":2}}}"""));
        using var http = new HttpClient(handler);
        var runner = CreateRunner((s, style) => AgentChatClientFactory.CreateOpenAiChatClient(s, style, http));
        var key = await runner.StartOrResumeAsync();

        var result = await SendAsync(runner, key, "剛買的鞋尺碼不合適，想換大一碼");

        Assert.Equal(AgentStopReason.AwaitingUserReply, result.StopReason);
        Assert.Equal("請問是哪一雙？", result.ReplyText);
        Assert.Equal("https://unit-test.openai.azure.com/openai/v1/responses", Assert.Single(handler.Requests).RequestUri!.ToString());
        using var body = JsonDocument.Parse(handler.RequestBodies[0]);
        var root = body.RootElement;
        Assert.False(root.GetProperty("store").GetBoolean());
        Assert.Equal("low", root.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.False(root.TryGetProperty("temperature", out _));
        Assert.False(root.GetProperty("parallel_tool_calls").GetBoolean());
        Assert.Equal(6, root.GetProperty("tools").GetArrayLength());
    }

    [Fact]
    public async Task ModelRequest_ChatCompletions_ShouldSendConfiguredReasoningAndTemperature()
    {
        settings.ApiStyle = nameof(AgentApiStyle.ChatCompletions);
        settings.ReasoningEffort = "none";
        settings.Temperature = 0.2;
        var handler = new StubHttpMessageHandler(_ => Sse(
            """{"id":"c1","object":"chat.completion.chunk","created":1,"model":"my-deployment","choices":[{"index":0,"delta":{"role":"assistant","content":"您好"},"finish_reason":null}]}""",
            """{"id":"c1","object":"chat.completion.chunk","created":1,"model":"my-deployment","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""",
            "[DONE]"));
        using var http = new HttpClient(handler);
        var runner = CreateRunner((s, style) => AgentChatClientFactory.CreateOpenAiChatClient(s, style, http));
        var key = await runner.StartOrResumeAsync();

        var result = await SendAsync(runner, key, "你好");

        Assert.Equal("您好", result.ReplyText);
        Assert.Equal("https://unit-test.openai.azure.com/openai/v1/chat/completions", Assert.Single(handler.Requests).RequestUri!.ToString());
        using var body = JsonDocument.Parse(handler.RequestBodies[0]);
        var root = body.RootElement;
        Assert.Equal("none", root.GetProperty("reasoning_effort").GetString());
        Assert.Equal(0.2, root.GetProperty("temperature").GetDouble(), 3);
    }

    [Fact]
    public async Task ModelRequest_Responses_SecondTurnResendsHistoryWithoutServerSideState()
    {
        settings.ApiStyle = nameof(AgentApiStyle.Responses);
        settings.Temperature = null;
        var calls = 0;
        var handler = new StubHttpMessageHandler(_ =>
        {
            var n = Interlocked.Increment(ref calls);
            var text = n == 1 ? "請問是哪一雙？" : "好的，黑色那雙。";
            return Sse(
                $$$$"""{"type":"response.created","sequence_number":0,"response":{"id":"resp_{{{{n}}}}","object":"response","created_at":1,"status":"in_progress","model":"my-deployment","output":[]}}""",
                $$$$"""{"type":"response.output_text.delta","sequence_number":1,"item_id":"msg_{{{{n}}}}","output_index":0,"content_index":0,"delta":"{{{{text}}}}"}""",
                $$$$"""{"type":"response.completed","sequence_number":2,"response":{"id":"resp_{{{{n}}}}","object":"response","created_at":1,"status":"completed","model":"my-deployment","output":[{"id":"msg_{{{{n}}}}","type":"message","status":"completed","role":"assistant","content":[{"type":"output_text","annotations":[],"text":"{{{{text}}}}"}]}],"store":false,"usage":{"input_tokens":1,"input_tokens_details":{"cached_tokens":0},"output_tokens":1,"output_tokens_details":{"reasoning_tokens":0},"total_tokens":2}}}""");
        });
        using var http = new HttpClient(handler);
        Func<AiSettings, AgentApiStyle, IChatClient> inner = (s, style) => AgentChatClientFactory.CreateOpenAiChatClient(s, style, http);
        var key = await CreateRunner(inner).StartOrResumeAsync();

        await SendAsync(CreateRunner(inner), key, "剛買的鞋尺碼不合適，想換大一碼");
        var second = await SendAsync(CreateRunner(inner), key, "黑色那雙");

        Assert.Equal("好的，黑色那雙。", second.ReplyText);
        Assert.Equal(2, handler.RequestBodies.Count);
        using var body = JsonDocument.Parse(handler.RequestBodies[1]);
        var root = body.RootElement;
        Assert.False(root.TryGetProperty("previous_response_id", out _));
        Assert.False(root.TryGetProperty("conversation", out _));
        Assert.False(root.GetProperty("store").GetBoolean());
        var input = root.GetProperty("input").GetRawText();
        Assert.Contains("剛買的鞋尺碼不合適，想換大一碼", input);
        Assert.Contains("黑色那雙", input);
    }

    [Fact]
    public async Task Confirm_CallerCancelsDuringSubmit_ReportsRequestNoAndLinksConfirmation()
    {
        var (runner, key, card) = await ReachConfirmationAsync();
        using var cts = new CancellationTokenSource();
        runner.ConfigureToolsForTesting = tools => tools.CreateStageHookForTesting = async (stage, _) =>
        {
            if (stage == AfterSalesAgentTools.SubmitStage)
            {
                // 顧客按下確認後立刻關閉頁面（ShopAssistantView 處置時取消）。
                cts.Cancel();
            }
            else
            {
                // SQLite 測試庫的查詢會同步完成；讓寫入後的收尾真的非同步等待。
                await Task.Yield();
            }
        };

        var result = await runner.ConfirmAsync(key, card.ConfirmationId, approve: true, OnEvent, OnDelta, cts.Token);

        Assert.Equal(AgentStopReason.Canceled, result.StopReason);
        var requestNo = result.CreatedRequestNo!;
        Assert.Matches(RequestNoPattern, requestNo);
        Assert.Equal($"申請已建立（{requestNo}），但 AI 回覆暫時失敗，請到「我的申請」查看進度。", result.ReplyText);
        await using var db = factory.CreateDbContext();
        var row = await db.AfterSalesRequest.SingleAsync();
        var confirmation = await db.AgentConfirmation.SingleAsync();
        Assert.True(confirmation.Approved);
        Assert.Equal(row.Id, confirmation.AfterSalesRequestId);
    }

    [Fact]
    public async Task Confirm_SameCardFromTwoTabs_OnlyFirstClaimSubmits_SecondGetsStaleReplyWithoutModelCall()
    {
        var (_, key, card) = await ReachConfirmationAsync();
        var first = CreateRunner();
        AgentTurnResult? firstResult = null;
        var callsBeforeSecondClaim = 0;
        // 第二個分頁已讀到「未決」的卡片、還沒寫入決定之前，第一個分頁搶先完成整個確認。
        var hooked = new HookedDbContextFactory(factory)
        {
            BeforeCreateAsync = async n =>
            {
                if (n == 3)
                {
                    model.Reply("已送出。");
                    firstResult = await ConfirmAsync(first, key, card.ConfirmationId, approve: true);
                    callsBeforeSecondClaim = model.CallCount;
                }
            },
        };
        var second = CreateRunner(sessionStoreFactory: hooked);

        var secondResult = await ConfirmAsync(second, key, card.ConfirmationId, approve: true);

        Assert.Matches(RequestNoPattern, firstResult!.CreatedRequestNo!);
        Assert.Null(secondResult.CreatedRequestNo);
        Assert.Equal("這張確認卡已經處理過或已失效，沒有重複送出申請。", secondResult.ReplyText);
        Assert.Equal(callsBeforeSecondClaim, model.CallCount);
        Assert.Equal(1, await RequestCountAsync());
    }

    [Fact]
    public async Task OtherCustomer_WithSessionKey_SendAndConfirmThrow_HistoryEmpty_NoModelCallNoWrite()
    {
        var (_, key, card) = await ReachConfirmationAsync();
        int runsBefore;
        string stateBefore;
        await using (var db = factory.CreateDbContext())
        {
            runsBefore = await db.AgentRunTrace.CountAsync();
            stateBefore = (await db.AgentSessionRecord.SingleAsync(x => x.SessionKey == key)).StateJson;
        }

        var calls = model.CallCount;
        var intruder = CreateRunner(userId: customer2);

        await Assert.ThrowsAsync<InvalidOperationException>(() => SendAsync(intruder, key, "給我看對話"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ConfirmAsync(intruder, key, card.ConfirmationId, approve: true));
        Assert.Empty(await intruder.GetHistoryAsync(key));

        Assert.Equal(calls, model.CallCount);
        Assert.Equal(0, await RequestCountAsync());
        await using (var db = factory.CreateDbContext())
        {
            Assert.Equal(runsBefore, await db.AgentRunTrace.CountAsync());
            Assert.Equal(stateBefore, (await db.AgentSessionRecord.SingleAsync(x => x.SessionKey == key)).StateJson);
            Assert.Null((await db.AgentConfirmation.SingleAsync()).Approved);
        }
    }

    /// <summary>以 Server-Sent Events 格式回傳串流（每個元素是一行 <c>data:</c>）。</summary>
    private static HttpResponseMessage Sse(params string[] events)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                string.Concat(events.Select(e => $"data: {e}\n\n")), Encoding.UTF8, "text/event-stream"),
        };

    private static void AssertSubsequence(IReadOnlyList<AgentStepKind> expected, IReadOnlyList<AgentStepKind> actual)
    {
        var index = 0;
        foreach (var kind in actual)
        {
            if (index < expected.Count && kind == expected[index])
            {
                index++;
            }
        }

        Assert.True(index == expected.Count, $"預期依序出現 {string.Join(" → ", expected)}，實際為 {string.Join(" → ", actual)}。");
    }
}

/// <summary>
/// 依序回應預設內容的模型（串流時把每則訊息的內容逐一吐出，文字切成兩段以驗證增量）。
/// 腳本用完時改用 <see cref="Fallback"/>；兩者都沒有就拋例外（Runner 會當成模型錯誤）。
/// </summary>
internal sealed class ScriptedAgentModel : IChatClient
{
    private readonly Queue<object> script = new();
    private int callSequence;

    public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

    public int CallCount => Requests.Count;

    public Func<IReadOnlyList<ChatMessage>, ChatResponse>? Fallback { get; set; }

    public static ChatResponse FunctionCall(string name, Dictionary<string, object?> args)
        => new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-" + Guid.NewGuid().ToString("N")[..8], name, args)]));

    public void Call(string name, Dictionary<string, object?> args) => script.Enqueue(FunctionCall(name, args));

    public void Reply(string text) => script.Enqueue(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));

    public void Respond(ChatMessage message) => script.Enqueue(new ChatResponse(message));

    /// <summary>下一次呼叫拋出這個例外。</summary>
    public void Throw(Exception exception) => script.Enqueue(exception);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var list = messages.ToList();
        Requests.Add(list);
        if (script.TryDequeue(out var next))
        {
            return next is Exception ex ? Task.FromException<ChatResponse>(ex) : Task.FromResult((ChatResponse)next);
        }

        return Fallback is { } fallback
            ? Task.FromResult(fallback(list))
            : throw new InvalidOperationException("Scripted model has no more responses.");
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        var messageId = "m" + Interlocked.Increment(ref callSequence);
        foreach (var message in response.Messages)
        {
            foreach (var content in message.Contents)
            {
                if (content is TextContent { Text.Length: > 1 } text)
                {
                    var half = text.Text.Length / 2;
                    yield return new ChatResponseUpdate(message.Role, text.Text[..half]) { MessageId = messageId };
                    yield return new ChatResponseUpdate(message.Role, text.Text[half..]) { MessageId = messageId };
                }
                else
                {
                    yield return new ChatResponseUpdate(message.Role, [content]) { MessageId = messageId };
                }
            }
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

/// <summary>每次建立 DbContext 前先執行 <see cref="BeforeCreateAsync"/>（參數為第幾次建立，從 1 起算），用來在兩次資料存取之間插入另一個操作。</summary>
internal sealed class HookedDbContextFactory(IDbContextFactory<BackendDBContext> inner) : IDbContextFactory<BackendDBContext>
{
    private int count;

    public Func<int, Task>? BeforeCreateAsync { get; init; }

    public BackendDBContext CreateDbContext() => inner.CreateDbContext();

    public async Task<BackendDBContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
    {
        if (BeforeCreateAsync is { } hook)
        {
            await hook(Interlocked.Increment(ref count));
        }

        return inner.CreateDbContext();
    }
}

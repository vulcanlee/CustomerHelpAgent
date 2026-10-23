using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Business.Startup;
using CustomerHelpAgent.Models.Others;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Agents;
using CustomerHelpAgent.Share.Demo;
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
        IDbContextFactory<BackendDBContext>? sessionStoreFactory = null,
        DemoControlService? demo = null)
    {
        var time = new ManualTimeProvider(UtcNow);
        demo ??= TestDemoControl.Off(factory);
        var currentUser = new CurrentUserService
        {
            CurrentUser = userId is { } id && id != customer1
                ? new CurrentUser { Id = id, Account = DemoSeedData.Customer2 }
                : new CurrentUser { Id = customer1, Account = DemoSeedData.Customer1 },
        };
        var audit = new RecordingAuditLogService();
        var systemSettings = new SystemSettings();
        systemSettings.ExternalFileSystem.AfterSalesFilePath = fileRoot;
        var policyService = new AfterSalesPolicyService(factory, audit, currentUser, demo, time, NullLogger<AfterSalesPolicyService>.Instance);
        var orderService = new ShopOrderService(factory, demo, NullLogger<ShopOrderService>.Instance);
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
            demo,
            new AgentRecordingService(factory, time, NullLogger<AgentRecordingService>.Instance),
            new ReplayCursorStore(),
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
    public async Task Confirm_AfterDemoReset_IsRejectedAndCreatesNothing()
    {
        var (runner, key, card) = await ReachConfirmationAsync();
        Assert.True(inbox.Add(key, "pending.jpg", new byte[8]));
        var systemSettings = new SystemSettings();
        systemSettings.ExternalFileSystem.AfterSalesFilePath = fileRoot;
        var reset = new DemoResetService(
            factory,
            new AfterSalesFileStore(Options.Create(systemSettings), NullLogger<AfterSalesFileStore>.Instance),
            Options.Create(new DemoSettings { Enabled = true, DemoUserPassword = "demo1234" }),
            new ManualTimeProvider(UtcNow),
            new RecordingAuditLogService(),
            new CurrentUserService { CurrentUser = new CurrentUser { Id = 1, Account = "support" } },
            [inbox],
            NullLogger<DemoResetService>.Instance);
        Assert.True((await reset.ResetAsync()).Success);

        // 會話已刪除：開著的頁面按下舊確認卡或再送出，Runner 拒絕（畫面以提醒顯示訊息），不呼叫模型、不建立申請。
        var calls = model.CallCount;
        var confirm = await Assert.ThrowsAsync<InvalidOperationException>(() => ConfirmAsync(runner, key, card.ConfirmationId, approve: true));
        var send = await Assert.ThrowsAsync<InvalidOperationException>(() => SendAsync(CreateRunner(), key, "還在嗎"));

        Assert.Contains("找不到這段對話", confirm.Message);
        Assert.Contains("找不到這段對話", send.Message);
        Assert.Equal(calls, model.CallCount);
        Assert.Equal(0, await RequestCountAsync());
        Assert.Equal(0, inbox.Count(key));
        Assert.NotEqual(key, await CreateRunner().StartOrResumeAsync());
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
        Assert.Contains(await CreateRunner().GetHistoryAsync(key), e => e.Kind == AgentStepKind.TurnResult && e.Text.Contains(requestNo, StringComparison.Ordinal));
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
                (AgentStepKind.TurnResult, "您好，請問需要什麼協助？"),
                (AgentStepKind.UserMessage, "我想換尺碼"),
                (AgentStepKind.TurnResult, "好的，請問是哪一雙？"),
            ],
            history.Select(x => (x.Kind, x.Text)));
        Assert.All(history.Where(x => x.Kind == AgentStepKind.TurnResult), x => Assert.False(x.IsProgramReply));
    }

    [Fact]
    public async Task History_RestoresProgramReplyAsSystemAndPolicyCards()
    {
        var runner = CreateRunner();
        var key = await runner.StartOrResumeAsync();
        model.Call("get_effective_policy", new() { ["orderItemId"] = white41, ["issueType"] = "quality_issue" });
        model.Call("request_photo_upload", new() { ["orderItemId"] = white41, ["reason"] = "請拍攝開膠處" });
        var result = await SendAsync(runner, key, "白色那雙開膠了");
        Assert.Equal(AgentStopReason.AwaitingPhotoUpload, result.StopReason);

        var history = await CreateRunner().GetHistoryAsync(key);

        Assert.Equal([AgentStepKind.UserMessage, AgentStepKind.TurnResult], history.Select(x => x.Kind));
        Assert.Equal("白色那雙開膠了", history[0].Text);
        var reply = history[1];
        Assert.True(reply.IsProgramReply);
        Assert.Equal(AgentDataSource.Program, reply.Source);
        Assert.Equal(result.ReplyText, reply.Text);
        Assert.Equal("QUALITY-ISSUE", Assert.Single(reply.PolicyCards).Code);
        Assert.Equal(Assert.Single(result.PolicyCards), reply.PolicyCards[0], new PolicyCardComparer());
    }

    [Fact]
    public async Task History_FixedReplyTurn_DoesNotShowReplacedModelText()
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
        var result = await SendAsync(runner, key, "我的鞋子訂單呢？");
        Assert.Equal(AgentStopReason.Timeout, result.StopReason);

        var history = await CreateRunner().GetHistoryAsync(key);

        Assert.DoesNotContain(history, x => x.Text.Contains("查無資料", StringComparison.Ordinal));
        var reply = Assert.Single(history, x => x.Kind == AgentStepKind.TurnResult);
        Assert.True(reply.IsProgramReply);
        Assert.Equal(result.ReplyText, reply.Text);
    }

    [Fact]
    public async Task TraceEvents_RestoreBackstage()
    {
        var runner = CreateRunner();
        var key = await runner.StartOrResumeAsync();
        model.Call("query_my_orders", []);
        model.Reply("請問是黑 40 還是白 41？");
        await SendAsync(runner, key, "剛買的鞋尺碼不合適，想換大一碼");
        model.Reply("好的，我來查規則。");
        await SendAsync(CreateRunner(), key, "黑色那雙");

        var restored = await CreateRunner().GetTraceEventsAsync(key);

        Assert.Equal(
            events.Select(e => (e.Kind, e.Source, e.PlainText, e.RawJson)),
            restored.Select(e => (e.Kind, e.Source, e.PlainText, e.RawJson)));
        Assert.Equal(2, restored.Count(e => e.Kind == AgentStepKind.TurnResult));
        Assert.Equal(AgentStepKind.TurnResult, restored[^1].Kind);
        Assert.Empty(await CreateRunner(userId: customer2).GetTraceEventsAsync(key));
    }

    [Fact]
    public async Task Send_WhenSessionUpdatedByAnotherTab_ThrowsConflictAndKeepsOtherTabsState()
    {
        var runner = CreateRunner();
        var key = await runner.StartOrResumeAsync();
        string? otherState = null;
        // 這一分頁讀到會話、還沒存回之前，另一個分頁完成了一整輪並存回（版本已前進）。
        var hooked = new HookedDbContextFactory(factory)
        {
            BeforeCreateAsync = async n =>
            {
                if (n == 3)
                {
                    model.Reply("另一個分頁的回覆。");
                    await SendAsync(CreateRunner(), key, "另一個分頁");
                    await using var db = factory.CreateDbContext();
                    otherState = (await db.AgentSessionRecord.SingleAsync(x => x.SessionKey == key)).StateJson;
                }
            },
        };
        model.Reply("這個分頁的回覆。");

        var ex = await Assert.ThrowsAsync<AgentSessionConflictException>(
            () => SendAsync(CreateRunner(sessionStoreFactory: hooked), key, "這個分頁"));

        Assert.Equal("這段對話已在其他分頁更新，請重新整理頁面。", ex.Message);
        await using (var db = factory.CreateDbContext())
        {
            Assert.Equal(otherState, (await db.AgentSessionRecord.SingleAsync(x => x.SessionKey == key)).StateJson);
        }

        // 還原時這一輪顯示的是衝突說明（與當時畫面一致），不是沒存進會話的模型回覆。
        var history = await CreateRunner().GetHistoryAsync(key);
        Assert.DoesNotContain(history, x => x.Text == "這個分頁的回覆。");
        Assert.Contains(history, x => x.Kind == AgentStepKind.TurnResult && x.IsProgramReply && x.Text == ex.Message);
    }

    [Fact]
    public async Task Confirm_ThenVersionConflict_ExceptionCarriesRequestNo()
    {
        var (runner, key, card) = await ReachConfirmationAsync();
        // 申請寫入後、存回會話前，另一個分頁先存回了（版本前進）。
        model.Fallback = _ =>
        {
            using var db = factory.CreateDbContext();
            db.AgentSessionRecord.Where(x => x.SessionKey == key)
                .ExecuteUpdate(s => s.SetProperty(x => x.StateVersion, x => x.StateVersion + 1));
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "已送出。"));
        };

        var ex = await Assert.ThrowsAsync<AgentSessionConflictException>(() => ConfirmAsync(runner, key, card.ConfirmationId, approve: true));

        var requestNo = Assert.IsType<string>(ex.CreatedRequestNo);
        Assert.Matches(RequestNoPattern, requestNo);
        Assert.Contains(requestNo, ex.Message, StringComparison.Ordinal);
        Assert.Contains("這段對話已在其他分頁更新，請重新整理頁面。", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, await RequestCountAsync());
    }

    [Fact]
    public async Task History_LegacySessionWithoutTurnResult_FallsBackToSessionText()
    {
        var runner = CreateRunner();
        var key = await runner.StartOrResumeAsync();
        model.Reply("您好，請問需要什麼協助？");
        await SendAsync(runner, key, "你好");
        await using (var db = factory.CreateDbContext())
        {
            // 1.0.5 之前的會話：軌跡裡沒有 TurnResult。
            await db.AgentRunStep.Where(x => x.Kind == AgentStepKind.TurnResult).ExecuteDeleteAsync();
        }

        var history = await CreateRunner().GetHistoryAsync(key);

        Assert.Equal(
            [(AgentStepKind.UserMessage, "你好"), (AgentStepKind.TurnResult, "您好，請問需要什麼協助？")],
            history.Select(x => (x.Kind, x.Text)));
    }

    [Fact]
    public async Task History_RestoresPendingConfirmationCard()
    {
        var (_, key, card) = await ReachConfirmationAsync();

        var history = await CreateRunner().GetHistoryAsync(key);

        var shown = Assert.Single(history, x => x.Kind == AgentStepKind.ConfirmationShown);
        Assert.Same(shown, history[^1]);
        var restored = shown.PendingCard!;
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

    [Fact]
    public async Task Card_WhenOpenRequestExists_NotShownAndModelToldWhy()
    {
        const string requestNo = "AS202610230099";
        await using (var db = factory.CreateDbContext())
        {
            // 先前（例如傳統表單）已送出、仍在處理中的同商品同類申請。
            db.AfterSalesRequest.Add(new AfterSalesRequest
            {
                RequestNo = requestNo,
                CaseKey = Guid.NewGuid(),
                CustomerUserId = customer1,
                SalesOrderItemId = black40,
                Type = AfterSalesType.SizeExchange,
                Status = AfterSalesStatus.Submitted,
                Channel = AfterSalesChannel.Traditional,
                TargetSize = "41",
                Description = "尺寸偏小",
                CreatedAt = new DateTime(2026, 10, 22, 9, 0, 0),
                UpdatedAt = new DateTime(2026, 10, 22, 9, 0, 0),
            });
            await db.SaveChangesAsync();
        }

        var runner = CreateRunner();
        var key = await runner.StartOrResumeAsync();
        model.Call("create_after_sales_request", SizeExchangeArgs());
        model.Reply("這雙鞋已經有處理中的換尺碼申請，可以到「我的申請」查看。");
        var result = await SendAsync(runner, key, "黑色那雙 40 碼想換 41，沒穿過");

        Assert.Null(result.Confirmation);
        Assert.Equal(AgentStopReason.Answered, result.StopReason);
        Assert.Equal(1, await RequestCountAsync());
        Assert.Contains(model.Requests[^1].SelectMany(m => m.Contents).OfType<FunctionResultContent>(),
            r => (r.Result?.ToString() ?? string.Empty).Contains($"這個商品已有處理中的同類申請（{requestNo}）", StringComparison.Ordinal));
        Assert.Contains(events, e => e.Kind == AgentStepKind.ProgramCheck && e.PlainText.Contains(requestNo, StringComparison.Ordinal));
        await using var check = factory.CreateDbContext();
        Assert.False(await check.AgentConfirmation.AnyAsync());
    }

    [Fact]
    public async Task TurnBudget_AcrossRejectedSubRuns_StopsAtLimit()
    {
        settings.MaxSteps = 3;
        var runner = CreateRunner();
        var key = await runner.StartOrResumeAsync();
        // 換尺碼沒有目標尺碼：程式重核失敗、不出卡，拒絕後續跑（3 次）；之後模型一直呼叫工具。
        var noTarget = new Dictionary<string, object?> { ["orderItemId"] = black40, ["issueType"] = "size_exchange", ["description"] = "想換" };
        model.Call("create_after_sales_request", noTarget);
        model.Call("create_after_sales_request", noTarget);
        model.Call("create_after_sales_request", noTarget);
        model.Fallback = _ => ScriptedAgentModel.FunctionCall("query_my_orders", []);

        var result = await SendAsync(runner, key, "幫我換大一碼");

        Assert.Equal(AgentStopReason.StepLimitReached, result.StopReason);
        Assert.False(string.IsNullOrWhiteSpace(result.ReplyText));
        Assert.True(model.CallCount <= settings.MaxSteps + 1, $"模型被呼叫 {model.CallCount} 次，超過每輪上限 {settings.MaxSteps + 1}。");
        var trace = new AgentTraceService(factory, TimeProvider.System, NullLogger<AgentTraceService>.Instance);
        var run = Assert.Single((await trace.GetRunsAsync(new DataRequest { CurrentPage = 1, PageSize = 10 })).Result);
        Assert.Equal(model.CallCount, run.ModelCallCount);
        Assert.Equal(AgentStopReason.StepLimitReached, run.StopReason);

        // 會話仍可接續：留在會話裡未回覆的核准請求由框架自動拒絕。
        model.Fallback = null;
        model.Reply("好的，有需要再告訴我。");
        var next = await SendAsync(runner, key, "那算了");
        Assert.Equal(AgentStopReason.Answered, next.StopReason);
    }

    [Fact]
    public async Task QuotaBlockedCall_NotCountedAsModelCall()
    {
        quota.Block = new AiQuotaLimitStatus(
            AiQuotaScope.User, AiQuotaPeriod.Daily, 50, 51.25, new DateTime(2026, 10, 23), new DateTime(2026, 10, 24));
        var runner = CreateRunner();
        var key = await runner.StartOrResumeAsync();

        var result = await SendAsync(runner, key, "你好");

        Assert.Equal(AgentStopReason.QuotaExceeded, result.StopReason);
        var trace = new AgentTraceService(factory, TimeProvider.System, NullLogger<AgentTraceService>.Instance);
        var run = Assert.Single((await trace.GetRunsAsync(new DataRequest { CurrentPage = 1, PageSize = 10 })).Result);
        Assert.Equal(0, run.ModelCallCount);
    }

    [Fact]
    public async Task NotFound_ThenModelConcludes_EmitsWarning()
    {
        var runner = CreateRunner();
        var key = await runner.StartOrResumeAsync();
        model.Call("query_my_orders", new() { ["color"] = "紅色" });
        model.Reply("您沒有紅色的訂單。");

        var result = await SendAsync(runner, key, "紅色那雙換大一碼");

        Assert.Equal(AgentStopReason.Answered, result.StopReason);
        var warning = Assert.Single(events, e => e.Kind == AgentStepKind.Warning);
        Assert.Equal(AgentDataSource.Program, warning.Source);
        Assert.Contains("直接下結論", warning.PlainText);
        var kinds = events.Select(e => e.Kind).ToList();
        Assert.True(kinds.IndexOf(AgentStepKind.Warning) < kinds.IndexOf(AgentStepKind.Stop));
    }

    [Fact]
    public async Task NotFound_ThenModelAsks_NoWarning()
    {
        var runner = CreateRunner();
        var key = await runner.StartOrResumeAsync();
        model.Call("query_my_orders", new() { ["color"] = "紅色" });
        model.Reply("找不到紅色的訂單，請問是哪一雙鞋呢？");

        var result = await SendAsync(runner, key, "紅色那雙換大一碼");

        Assert.Equal(AgentStopReason.AwaitingUserReply, result.StopReason);
        Assert.DoesNotContain(events, e => e.Kind == AgentStepKind.Warning);
    }

    [Fact]
    public async Task NextTurnAfterApprove_Works()
    {
        var (runner, key, card) = await ReachConfirmationAsync();
        model.Reply("已為您送出換尺碼申請。");
        var confirmed = await ConfirmAsync(runner, key, card.ConfirmationId, approve: true);
        var requestNo = confirmed.CreatedRequestNo!;
        Assert.Matches(RequestNoPattern, requestNo);

        model.Reply("不客氣，申請處理中。");
        var next = await SendAsync(runner, key, "謝謝");

        Assert.NotEqual(AgentStopReason.ModelError, next.StopReason);
        Assert.Equal(AgentStopReason.Answered, next.StopReason);
        Assert.Equal("不客氣，申請處理中。", next.ReplyText);
        Assert.Contains(model.Requests[^1].SelectMany(m => m.Contents).OfType<FunctionResultContent>(),
            r => (r.Result?.ToString() ?? string.Empty).Contains(requestNo, StringComparison.Ordinal));
        Assert.Equal(1, await RequestCountAsync());
    }

    [Fact]
    public async Task NextTurnAfterReject_Works()
    {
        var (runner, key, card) = await ReachConfirmationAsync();
        model.Reply("好的，已為您取消。");
        var canceled = await ConfirmAsync(runner, key, card.ConfirmationId, approve: false);
        Assert.Equal(AgentStopReason.Answered, canceled.StopReason);

        model.Reply("好的，有需要再告訴我。");
        var next = await SendAsync(runner, key, "那先不用了");

        Assert.Equal(AgentStopReason.Answered, next.StopReason);
        Assert.Equal("好的，有需要再告訴我。", next.ReplyText);
        Assert.Contains(model.Requests[^1], m => m.Text.Contains("已為您取消", StringComparison.Ordinal));
        Assert.Equal(0, await RequestCountAsync());
    }

    [Fact]
    public async Task NextTurnAfterApprove_FromRestoredSession_Works()
    {
        var (_, key, card) = await ReachConfirmationAsync();
        model.Reply("已為您送出換尺碼申請。");
        var confirmed = await ConfirmAsync(CreateRunner(), key, card.ConfirmationId, approve: true);
        var requestNo = confirmed.CreatedRequestNo!;
        Assert.Matches(RequestNoPattern, requestNo);

        model.Reply("不客氣。");
        var next = await SendAsync(CreateRunner(), key, "謝謝");

        Assert.Equal(AgentStopReason.Answered, next.StopReason);
        Assert.Contains(model.Requests[^1].SelectMany(m => m.Contents).OfType<FunctionResultContent>(),
            r => (r.Result?.ToString() ?? string.Empty).Contains(requestNo, StringComparison.Ordinal));
    }

    [Fact]
    public async Task FlagshipB_PhotoUpload_ThenContinue_Works()
    {
        var runner = CreateRunner();
        var key = await runner.StartOrResumeAsync();
        model.Call("request_photo_upload", new() { ["orderItemId"] = white41, ["reason"] = "請拍攝開膠處" });
        var waiting = await SendAsync(runner, key, "白色那雙開膠了");
        Assert.Equal(AgentStopReason.AwaitingPhotoUpload, waiting.StopReason);

        Assert.True(inbox.Add(key, "crack.jpg", [1, 2, 3]));
        model.Reply("收到照片了，請問鞋子有穿過嗎？");
        var resumed = await runner.NotifyPhotosUploadedAsync(key, OnEvent, OnDelta, CancellationToken.None);
        Assert.Equal(AgentStopReason.AwaitingUserReply, resumed.StopReason);

        model.Reply("了解，我來幫您查詢規則。");
        var next = await SendAsync(CreateRunner(), key, "穿過一次");

        Assert.Equal(AgentStopReason.Answered, next.StopReason);
        Assert.Contains(model.Requests[^1], m => m.Role == ChatRole.User && m.Text == "（已上傳 1 張照片）");
    }

    [Fact]
    public async Task Card_DedupeAfterRealConfirm_NotShown()
    {
        var (runner, key, card) = await ReachConfirmationAsync();
        model.Reply("已為您送出換尺碼申請。");
        var requestNo = (await ConfirmAsync(runner, key, card.ConfirmationId, approve: true)).CreatedRequestNo!;
        Assert.Matches(RequestNoPattern, requestNo);

        model.Call("create_after_sales_request", SizeExchangeArgs());
        model.Reply($"這雙鞋已經有處理中的換尺碼申請（{requestNo}）。");
        var again = await SendAsync(runner, key, "再幫我申請一次換 41");

        Assert.Equal(AgentStopReason.Answered, again.StopReason);
        Assert.Null(again.Confirmation);
        Assert.Contains(model.Requests[^1].SelectMany(m => m.Contents).OfType<FunctionResultContent>(),
            r => (r.Result?.ToString() ?? string.Empty).Contains($"這個商品已有處理中的同類申請（{requestNo}）", StringComparison.Ordinal));
        Assert.Equal(1, await RequestCountAsync());
        await using var db = factory.CreateDbContext();
        Assert.Single(await db.AgentConfirmation.ToListAsync());
    }

    [Fact]
    public async Task Conflict_ThenModelSuggestsHuman_NoWarning()
    {
        await using (var db = factory.CreateDbContext())
        {
            // 再加一條同類、同時生效的規則，製造衝突。
            db.AfterSalesPolicy.Add(new AfterSalesPolicy
            {
                Code = "SIZE-EXCHANGE-SHOES",
                Type = AfterSalesType.SizeExchange,
                Category = ProductCategories.Shoes,
                Title = "鞋類換尺碼",
                Content = "另一條規則",
                Source = "測試",
                Version = 1,
                EffectiveFrom = new DateTime(2026, 1, 1),
                CreatedAt = new DateTime(2026, 1, 1),
            });
            await db.SaveChangesAsync();
        }

        var runner = CreateRunner();
        var key = await runner.StartOrResumeAsync();
        model.Call("get_effective_policy", new() { ["orderItemId"] = black40, ["issueType"] = "size_exchange" });
        model.Reply("這個問題目前有多條規則同時適用，建議轉人工客服為您處理。");

        var result = await SendAsync(runner, key, "黑色那雙想換 41");

        Assert.Equal(AgentStopReason.PolicyConflict, result.StopReason);
        Assert.Contains(events, e => e.Kind == AgentStepKind.ToolResult && e.RawJson!.Contains("\"status\":\"conflict\"", StringComparison.Ordinal));
        Assert.DoesNotContain(events, e => e.Kind == AgentStepKind.Warning);
    }

    [Fact]
    public async Task NotFound_ThenSameToolOk_ThenConclusion_NoWarning()
    {
        var runner = CreateRunner();
        var key = await runner.StartOrResumeAsync();
        model.Call("query_my_orders", new() { ["color"] = "紅色" });
        model.Call("query_my_orders", []);
        model.Reply("您有黑色 40 碼與白色 41 碼兩雙鞋。");

        var result = await SendAsync(runner, key, "紅色那雙換大一碼");

        Assert.Equal(AgentStopReason.Answered, result.StopReason);
        Assert.Contains(events, e => e.Kind == AgentStepKind.ToolResult && e.RawJson!.Contains("\"status\":\"not_found\"", StringComparison.Ordinal));
        Assert.DoesNotContain(events, e => e.Kind == AgentStepKind.Warning);
    }

    /// <summary>以 Server-Sent Events 格式回傳串流（每個元素是一行 <c>data:</c>）。</summary>
    /// <summary>
    /// 模型直接提出建立申請（沒有先查詢），組確認卡的程式重查遇到故障：與工具同一個外框（逾時、身分與權限），
    /// 失敗時以 Guard 的固定說明與停止原因收尾，不顯示確認卡、不寫入。
    /// </summary>
    private async Task<(AgentTurnResult Result, TimeSpan Elapsed, Guid Key, AfterSalesAgentRunner Runner)> CardUnderFaultAsync(DemoFault fault)
    {
        var demo = TestDemoControl.Create(factory, enabled: true);
        await demo.SetFaultAsync(fault, true);
        var runner = CreateRunner(demo: demo);
        var key = await runner.StartOrResumeAsync();
        model.Call("create_after_sales_request", SizeExchangeArgs());
        model.Fallback = _ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "查無資料，您沒有這筆訂單。"));

        var watch = Stopwatch.StartNew();
        var result = await SendAsync(runner, key, "黑色那雙 40 碼想換 41，沒穿過");
        watch.Stop();

        Assert.Null(result.Confirmation);
        Assert.Equal(result.StopExplanation, result.ReplyText);
        Assert.Equal(1, model.CallCount);
        Assert.Equal(0, await RequestCountAsync());
        await using (var db = factory.CreateDbContext())
        {
            Assert.False(await db.AgentConfirmation.AnyAsync());
        }

        Assert.DoesNotContain(events, e => e.Kind == AgentStepKind.ConfirmationShown);
        await demo.SetFaultAsync(fault, false);
        return (result, watch.Elapsed, key, runner);
    }

    [Fact]
    public async Task Card_UnderOrderTimeoutFault_ReturnsTimeoutExplanationQuickly()
    {
        // 故障模擬的訂單讀取要等 30 秒（預設）才失敗；組卡的重查必須以工具逾時（1 秒）放棄等待。
        settings.ToolTimeoutSeconds = 1;

        var (result, elapsed, _, _) = await CardUnderFaultAsync(DemoFault.OrderServiceTimeout);

        Assert.Equal(AgentStopReason.Timeout, result.StopReason);
        Assert.Equal(AfterSalesAgentGuard.TimeoutExplanation, result.ReplyText);
        Assert.Contains("不代表您沒有這筆訂單", result.ReplyText);
        Assert.True(elapsed < TimeSpan.FromSeconds(10), $"組卡重查花了 {elapsed}，沒有受工具逾時約束。");
    }

    [Fact]
    public async Task Card_UnderPolicyUnavailableFault_ReturnsUnavailable()
    {
        var (result, _, _, _) = await CardUnderFaultAsync(DemoFault.PolicyServiceUnavailable);

        Assert.Equal(AgentStopReason.ServiceUnavailable, result.StopReason);
        Assert.Equal(AfterSalesAgentGuard.UnavailableExplanation, result.ReplyText);
    }

    [Fact]
    public async Task Card_UnderPermissionExpiredFault_NoCardForbidden()
    {
        var (result, _, key, runner) = await CardUnderFaultAsync(DemoFault.PermissionExpired);

        Assert.Equal(AgentStopReason.Forbidden, result.StopReason);
        Assert.Equal(AfterSalesAgentGuard.ForbiddenExplanation, result.ReplyText);

        // 會話仍可接續：留在會話裡未回覆的核准請求由框架自動拒絕。
        model.Fallback = null;
        model.Reply("好的，有需要再告訴我。");
        var next = await SendAsync(runner, key, "那算了");
        Assert.Equal(AgentStopReason.Answered, next.StopReason);
        Assert.Equal(0, await RequestCountAsync());
    }

    [Fact]
    public async Task Confirm_WhenApprovalRequestMissingFromSession_FailsClosed()
    {
        var (runner, key, card) = await ReachConfirmationAsync();
        // 模擬上一輪存回會話失敗：會話裡沒有這張卡的核准請求。
        await using (var db = factory.CreateDbContext())
        {
            var row = await db.AgentSessionRecord.SingleAsync(x => x.SessionKey == key);
            Assert.Contains(card.ApprovalRequestId, row.StateJson!, StringComparison.Ordinal);
            var state = JsonNode.Parse(row.StateJson!)!;
            RemoveObjectsWithValue(state, card.ApprovalRequestId);
            row.StateJson = state.ToJsonString();
            Assert.DoesNotContain(card.ApprovalRequestId, row.StateJson, StringComparison.Ordinal);
            await db.SaveChangesAsync();
        }

        var calls = model.CallCount;
        model.Reply("已為您送出換尺碼申請。");
        var result = await ConfirmAsync(runner, key, card.ConfirmationId, approve: true);

        Assert.Equal("這張確認卡已失效，請重新送出您的需求。", result.ReplyText);
        Assert.Null(result.CreatedRequestNo);
        Assert.Null(result.Confirmation);
        Assert.Equal(calls, model.CallCount);
        Assert.Equal(0, await RequestCountAsync());
        await using var check = factory.CreateDbContext();
        var confirmation = await check.AgentConfirmation.SingleAsync();
        Assert.False(confirmation.Approved);
        Assert.Null(confirmation.AfterSalesRequestId);
        Assert.DoesNotContain(await runner.GetHistoryAsync(key), x => x.PendingCard is not null);
    }

    /// <summary>移除 JSON 樹中任何直接含有指定字串值的物件（陣列元素）。</summary>
    private static void RemoveObjectsWithValue(JsonNode node, string value)
    {
        switch (node)
        {
            case JsonArray array:
                for (var i = array.Count - 1; i >= 0; i--)
                {
                    if (array[i] is JsonObject item
                        && item.Any(p => p.Value is JsonValue v && v.TryGetValue<string>(out var text) && text == value))
                    {
                        array.RemoveAt(i);
                    }
                    else if (array[i] is { } child)
                    {
                        RemoveObjectsWithValue(child, value);
                    }
                }

                break;

            case JsonObject obj:
                foreach (var property in obj.ToList())
                {
                    if (property.Value is { } child)
                    {
                        RemoveObjectsWithValue(child, value);
                    }
                }

                break;
        }
    }

    private static HttpResponseMessage Sse(params string[] events)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                string.Concat(events.Select(e => $"data: {e}\n\n")), Encoding.UTF8, "text/event-stream"),
        };

    /// <summary>規則卡含清單欄位，記錄相等比較的是參考；這裡逐欄比對內容。</summary>
    private sealed class PolicyCardComparer : IEqualityComparer<AgentPolicyCard>
    {
        public bool Equals(AgentPolicyCard? x, AgentPolicyCard? y)
            => x is not null && y is not null
                && x with { UnmetReasons = [], MissingInfo = [] } == y with { UnmetReasons = [], MissingInfo = [] }
                && x.UnmetReasons.SequenceEqual(y.UnmetReasons)
                && x.MissingInfo.SequenceEqual(y.MissingInfo);

        public int GetHashCode(AgentPolicyCard obj) => obj.PolicyId;
    }

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

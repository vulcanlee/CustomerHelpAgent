using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Business.Startup;
using CustomerHelpAgent.Models.Agents;
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
/// 售後 Agent 的多 Agent 交接模式（M5 Task 5，MAF Handoff 編排）：以腳本化模型（所有參與者共用一個佇列）＋真實服務、SQLite 記憶體資料庫
/// 與 SQLite checkpoint 驗證交接事件、工具子集、確認卡（含重啟後只建立一筆）、Guard 終止、步數上限、記憶注入，
/// 以及 Task 1 釘住的框架限制在 Runner 的處理（核准回覆前 TryPrepare、留下的核准請求先明確拒絕、出錯不存回、取消不存回）。
/// 資料取自 <see cref="DemoSeedData"/>（customer1：黑 40、白 41）。
/// </summary>
[Collection(nameof(HeavyWorkflowCollection))]
public sealed class AfterSalesHandoffRunnerTests : IAsyncLifetime
{
    /// <summary>本地時間（UTC+8）2026/10/23 10:00。</summary>
    private static readonly DateTimeOffset UtcNow = new(2026, 10, 23, 2, 0, 0, TimeSpan.Zero);

    private readonly string fileRoot = Path.Combine(Path.GetTempPath(), "HandoffRunnerTests-" + Guid.NewGuid().ToString("N"));
    private readonly AgentPhotoInbox inbox = new();
    private readonly ScriptedAgentModel model = new();
    private readonly StubAiQuotaService quota = new();
    private readonly RecordingTokenUsageRecorder usage = new();
    private readonly List<AgentTraceEvent> events = [];
    private readonly List<string> deltas = [];
    private readonly AfterSalesAgentSettings settings = new();
    // 多 Agent 的 Workflow 在背景執行緒寫 checkpoint、執行工具：每個 context 各開一條連線（共用 in-memory 連線會並行出錯）。
    private readonly TestFileDbContextFactory factory = new();
    private BackendDBContext permissionContext = null!;
    private int customer1;
    private int black40;
    private int white41;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(fileRoot);
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
            white41 = await db.SalesOrderItem.Where(x => x.SalesOrder!.OrderNo == "SO-DEMO-0002").Select(x => x.Id).SingleAsync();
        }

        permissionContext = factory.CreateDbContext();
    }

    public async Task DisposeAsync()
    {
        await permissionContext.DisposeAsync();
        factory.Dispose();
        if (Directory.Exists(fileRoot))
        {
            Directory.Delete(fileRoot, recursive: true);
        }
    }

    /// <summary>每次呼叫都是全新的 Runner（等同換頁、或應用程式重啟後的另一個 circuit），共用同一個資料庫、照片暫存區與腳本化模型。</summary>
    private AfterSalesAgentRunner CreateRunner(Func<AiSettings, AgentApiStyle, IChatClient>? innerClientFactory = null, DemoControlService? demo = null)
    {
        var time = new ManualTimeProvider(UtcNow);
        demo ??= TestDemoControl.Off(factory);
        var currentUser = new CurrentUserService { CurrentUser = new CurrentUser { Id = customer1, Account = DemoSeedData.Customer1 } };
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
            usage,
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

    private Task<AgentTurnResult> SendAsync(AfterSalesAgentRunner runner, Guid key, string text, CancellationToken ct = default)
        => runner.SendAsync(key, text, OnEvent, OnDelta, ct);

    private Task<AgentTurnResult> ConfirmAsync(AfterSalesAgentRunner runner, Guid key, int confirmationId, bool approve, CancellationToken ct = default)
        => runner.ConfirmAsync(key, confirmationId, approve, OnEvent, OnDelta, ct);

    private static Dictionary<string, object?> Handoff(string reason) => new() { ["reasonForHandoff"] = reason };

    private Dictionary<string, object?> SizeExchangeArgs() => new()
    {
        ["orderItemId"] = black40,
        ["issueType"] = "size_exchange",
        ["targetSize"] = "41",
        ["isWorn"] = false,
        ["description"] = "尺寸偏小，想換大一碼",
    };

    /// <summary>第 <paramref name="index"/> 次模型呼叫的系統指示（含交接說明與記憶注入）。</summary>
    private string InstructionsOf(int index) => model.RequestOptions[index]?.Instructions ?? string.Empty;

    private List<string> ToolsOf(int index) => [.. (model.RequestOptions[index]?.Tools ?? []).Select(t => t.Name)];

    private static string FirstLineOf(string instructions) => instructions.ReplaceLineEndings("\n").Split('\n')[0];

    private async Task<AgentSessionRecord> SessionAsync(Guid key)
    {
        await using var db = factory.CreateDbContext();
        return await db.AgentSessionRecord.AsNoTracking().SingleAsync(x => x.SessionKey == key);
    }

    private async Task<int> RequestCountAsync()
    {
        await using var db = factory.CreateDbContext();
        return await db.AfterSalesRequest.CountAsync();
    }

    /// <summary>分流 → 換退貨專員（查規則 → 提出建立申請）→ 顯示確認卡。</summary>
    private async Task<(AfterSalesAgentRunner Runner, Guid Key, ConfirmationCard Card)> ReachConfirmationAsync()
    {
        var runner = CreateRunner();
        var key = await runner.StartNewAsync(AgentMode.Handoff);
        model.Call("handoff_to_1", Handoff("顧客要換尺碼"));
        model.Call("get_effective_policy", new() { ["orderItemId"] = black40, ["issueType"] = "size_exchange" });
        model.Call("create_after_sales_request", SizeExchangeArgs());

        var result = await SendAsync(runner, key, "黑色那雙 40 碼想換 41，沒穿過");

        Assert.Equal(AgentStopReason.AwaitingConfirmation, result.StopReason);
        Assert.DoesNotContain(events, e => e.Kind == AgentStepKind.Stop && e.PlainText.Contains("模型呼叫失敗", StringComparison.Ordinal));
        return (runner, key, Assert.IsType<ConfirmationCard>(result.Confirmation));
    }

    // ───────────────────────── 交接事件 ─────────────────────────

    [Fact]
    public async Task Handoff_TriageToQuality_EmitsHandoffEvent()
    {
        var runner = CreateRunner();
        var key = await runner.StartNewAsync(AgentMode.Handoff);
        model.Call("handoff_to_2", Handoff("顧客反映鞋子開膠"));
        model.Reply("了解，請問是哪一雙鞋開膠呢？");

        var result = await SendAsync(runner, key, "我的鞋子開膠了");

        Assert.Equal(AgentStopReason.AwaitingUserReply, result.StopReason);
        Assert.Equal("了解，請問是哪一雙鞋開膠呢？", result.ReplyText);
        Assert.Equal("品質專員", result.Speaker);
        Assert.Equal("了解，請問是哪一雙鞋開膠呢？", string.Concat(deltas));
        // 只有一位發言者：不分段（頁面以 Speaker 標示）。
        Assert.Null(result.Segments);

        var handoff = Assert.Single(events, e => e.Kind == AgentStepKind.Handoff);
        Assert.Equal(AgentDataSource.Program, handoff.Source);
        Assert.Equal("分流 Agent → 品質專員：顧客反映鞋子開膠", handoff.PlainText);
        using (var raw = JsonDocument.Parse(handoff.RawJson!))
        {
            Assert.Equal("分流 Agent", raw.RootElement.GetProperty("from").GetString());
            Assert.Equal("品質專員", raw.RootElement.GetProperty("to").GetString());
            Assert.Equal(HandoffTopologyFactory.TriageId, raw.RootElement.GetProperty("fromId").GetString());
            Assert.Equal(HandoffTopologyFactory.QualityId, raw.RootElement.GetProperty("toId").GetString());
            Assert.Equal("顧客反映鞋子開膠", raw.RootElement.GetProperty("reason").GetString());
        }

        // 交接事件在接手者的文字之前；模型回覆事件帶發言者。
        Assert.True(events.IndexOf(handoff) < events.FindIndex(e => e.Kind == AgentStepKind.ModelReply));
        var reply = Assert.Single(events, e => e.Kind == AgentStepKind.ModelReply);
        Assert.Equal("{\"speaker\":\"品質專員\"}", reply.RawJson);

        // 分流與品質專員各自的指示；框架的英文交接說明換成固定中文版本（仍要求呼叫交接函式、不描述交接）。
        Assert.Equal(2, model.CallCount);
        Assert.Equal(FirstLineOf(AfterSalesHandoffPromptDefaults.TriageInstructions.Trim()), FirstLineOf(InstructionsOf(0)));
        Assert.Equal(FirstLineOf(AfterSalesHandoffPromptDefaults.QualityInstructions.Trim()), FirstLineOf(InstructionsOf(1)));
        Assert.All([InstructionsOf(0), InstructionsOf(1)], instructions =>
        {
            Assert.Contains("handoff_to_1、handoff_to_2", instructions, StringComparison.Ordinal);
            Assert.Contains("不要向顧客提到或描述交接", instructions, StringComparison.Ordinal);
            Assert.DoesNotContain("You are one agent", instructions, StringComparison.Ordinal);
            Assert.Contains(AiPromptHeadingProbe, instructions, StringComparison.Ordinal);
        });

        // 會話是 Workflow 會話、模式為多 Agent；只留下 lastCheckpoint 那一筆 checkpoint；用量記在「AI 售後多 Agent」。
        var row = await SessionAsync(key);
        Assert.Equal(AgentMode.Handoff, row.Mode);
        Assert.Equal(AgentMode.Handoff, await runner.GetModeAsync(key));
        await AssertOnlyLastCheckpointKeptAsync(row.StateJson);
        Assert.NotEmpty(usage.Entries);
        Assert.All(usage.Entries, x => Assert.Equal(TokenUsageOperations.AfterSalesAgentHandoff, x.Operation));

        // 重新整理：交接列（接手者）與帶發言者的回覆。
        var history = await CreateRunner().GetHistoryAsync(key);
        Assert.Collection(
            history,
            x => Assert.Equal(AgentStepKind.UserMessage, x.Kind),
            x =>
            {
                Assert.Equal(AgentStepKind.Handoff, x.Kind);
                Assert.Equal("品質專員", x.Speaker);
            },
            x =>
            {
                Assert.Equal(AgentStepKind.TurnResult, x.Kind);
                Assert.Equal("品質專員", x.Speaker);
                Assert.Equal("了解，請問是哪一雙鞋開膠呢？", x.Text);
            });
    }

    /// <summary>固定規則的標題：多 Agent 各參與者的提示詞一樣經 <c>IAiSystemPromptProvider</c>（含固定規則）。</summary>
    private const string AiPromptHeadingProbe = "以下規則由系統固定附加";

    [Fact]
    public async Task Handoff_ScenarioB_TwoSpecialists()
    {
        var runner = CreateRunner();
        var key = await runner.StartNewAsync(AgentMode.Handoff);
        model.Call("handoff_to_1", Handoff("先處理換尺碼"));
        model.Call("get_effective_policy", new() { ["orderItemId"] = black40, ["issueType"] = "size_exchange" });
        // 專員先回覆自己負責的部分，同一則回應裡交回分流（不交回就結束這一輪）。
        model.Respond(new ChatMessage(ChatRole.Assistant,
        [
            new TextContent("黑色 40 碼可以換 41，請看下方規則卡。"),
            new FunctionCallContent("call-back", "handoff_to_1", Handoff("顧客另有品質問題")),
        ]));
        model.Call("handoff_to_2", Handoff("白色那雙脫線"));
        model.Reply("白色那雙脫線的部分，請問方便上傳照片嗎？");

        var result = await SendAsync(runner, key, "黑色那雙想換大一號，另外白色那雙脫線了");

        Assert.Equal(
            ["分流 Agent → 換退貨專員：先處理換尺碼", "換退貨專員 → 分流 Agent：顧客另有品質問題", "分流 Agent → 品質專員：白色那雙脫線"],
            events.Where(e => e.Kind == AgentStepKind.Handoff).Select(e => e.PlainText));
        Assert.Equal(AgentStopReason.AwaitingUserReply, result.StopReason);
        Assert.Equal("品質專員", result.Speaker);
        Assert.Equal("黑色 40 碼可以換 41，請看下方規則卡。\n\n白色那雙脫線的部分，請問方便上傳照片嗎？", result.ReplyText);
        // 兩位專員各說一段：依發言者切分（頁面據此分段標示發言者、把「已轉給…」插在該專員的回覆之前）；還原時一樣拿得到。
        AgentReplySegment[] segments =
        [
            new("換退貨專員", "黑色 40 碼可以換 41，請看下方規則卡。"),
            new("品質專員", "白色那雙脫線的部分，請問方便上傳照片嗎？"),
        ];
        Assert.Equal(segments, result.Segments);
        Assert.Equal(segments, (await CreateRunner().GetHistoryAsync(key)).Single(x => x.Kind == AgentStepKind.TurnResult).Segments);
        Assert.Equal("SIZE-EXCHANGE", Assert.Single(result.PolicyCards).Code);
        // 工具請求的白話改寫成發出呼叫的專員；RawJson 帶 agent。
        var toolRequest = Assert.Single(events, e => e.Kind == AgentStepKind.ToolRequest);
        Assert.StartsWith("換退貨專員決定查詢", toolRequest.PlainText);
        Assert.Contains("\"agent\":\"換退貨專員\"", toolRequest.RawJson, StringComparison.Ordinal);
        Assert.Equal(5, model.CallCount);
        Assert.Equal(FirstLineOf(AfterSalesHandoffPromptDefaults.QualityInstructions.Trim()), FirstLineOf(InstructionsOf(4)));

        // Workflow 在背景執行緒發事件（Guard、模型鏈），Runner 同時發交接事件：軌跡仍是單一寫入者，順序號不重複、不跳號，且與畫面收到的順序一致。
        await using var db = factory.CreateDbContext();
        var steps = await db.AgentRunStep.OrderBy(x => x.Sequence).Select(x => new { x.Sequence, x.Kind, x.PlainText }).ToListAsync();
        Assert.Equal(Enumerable.Range(1, steps.Count), steps.Select(x => x.Sequence));
        Assert.Equal(events.Select(e => (e.Kind, e.PlainText)), steps.Select(x => (x.Kind, x.PlainText)));
    }

    // ───────────────────────── 工具子集 ─────────────────────────

    [Fact]
    public async Task Specialist_ToolSubset_Enforced()
    {
        var runner = CreateRunner();
        var key = await runner.StartNewAsync(AgentMode.Handoff);
        model.Call("handoff_to_3", Handoff("查物流"));
        // 物流專員亂編一個不屬於自己的工具（庫存）：不會被執行，模型只拿到「找不到這個函式」的結果。
        model.Call("check_variant_stock", new() { ["orderItemId"] = white41 });
        model.Reply("抱歉，我這邊查不到庫存，請問還有物流的問題嗎？");

        var result = await SendAsync(runner, key, "白色那雙到哪了？還有沒有 42 號");

        Assert.Equal(AgentStopReason.AwaitingUserReply, result.StopReason);
        Assert.Equal(3, model.CallCount);
        Assert.Equal(
            ["handoff_to_1", "handoff_to_2", "handoff_to_3", "query_my_orders", "remember_preference"],
            ToolsOf(0));
        Assert.Equal(
            ["handoff_to_1", "query_my_orders", "get_effective_policy", "track_shipment", "create_after_sales_request", "remember_preference"],
            ToolsOf(1));
        Assert.DoesNotContain("check_variant_stock", ToolsOf(2));
        Assert.DoesNotContain(events, e => e.Kind == AgentStepKind.ToolRequest);
        Assert.DoesNotContain(events, e => e.Kind == AgentStepKind.ToolResult);
        var unknown = model.Requests[2].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single();
        Assert.Contains("not found", unknown.Result?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("has_stock", unknown.Result?.ToString() ?? string.Empty, StringComparison.Ordinal);

        // 每位參與者拿到的工具＝拓樸表的子集（＋框架的交接函式）。
        foreach (var participant in HandoffTopologyFactory.Participants)
        {
            var targets = HandoffTopologyFactory.Routes[participant.Id];
            Assert.Equal(targets.Count, Enumerable.Range(1, targets.Count).Count(i => HandoffTopologyFactory.TargetOf(participant.Id, $"handoff_to_{i}") == targets[i - 1]));
            Assert.Null(HandoffTopologyFactory.TargetOf(participant.Id, $"handoff_to_{targets.Count + 1}"));
        }

        Assert.DoesNotContain(AfterSalesAgentTools.CheckVariantStockName, HandoffTopologyFactory.Find(HandoffTopologyFactory.LogisticsId)!.ToolNames);
        Assert.DoesNotContain(AfterSalesAgentTools.CreateAfterSalesRequestName, HandoffTopologyFactory.Find(HandoffTopologyFactory.TriageId)!.ToolNames);
    }

    // ───────────────────────── 確認卡 ─────────────────────────

    [Fact]
    public async Task Handoff_ConfirmCreatesRequest()
    {
        var (runner, key, card) = await ReachConfirmationAsync();

        // 外部 RequestId（"{長度}:{executorId}_UserInput:ficc_{CallId}"），原文存進確認卡；待回覆請求記在會話 StateBag。
        Assert.StartsWith("27:exchange_exchange_UserInput:ficc_", card.ApprovalRequestId, StringComparison.Ordinal);
        Assert.Equal("黑色 / 41", card.ToSpec);
        await using (var db = factory.CreateDbContext())
        {
            var row = await db.AgentConfirmation.SingleAsync();
            Assert.Contains(card.ApprovalRequestId, row.ApprovalRequestJson, StringComparison.Ordinal);
            Assert.Contains("create_after_sales_request", row.ApprovalRequestJson, StringComparison.Ordinal);
        }

        var pendingState = (await SessionAsync(key)).StateJson;
        Assert.Contains(AfterSalesAgentRunner.OpenApprovalsKey, pendingState, StringComparison.Ordinal);
        AssertOpenApprovalsMirrorFramework(pendingState);
        Assert.Equal(0, await RequestCountAsync());

        model.Reply("已為您送出換尺碼申請。");
        var calls = model.CallCount;
        var result = await ConfirmAsync(runner, key, card.ConfirmationId, approve: true);

        Assert.Equal(AgentStopReason.Answered, result.StopReason);
        Assert.Matches(@"^AS\d{12}$", result.CreatedRequestNo!);
        Assert.Equal("已為您送出換尺碼申請。", result.ReplyText);
        Assert.Equal("換退貨專員", result.Speaker);
        Assert.Equal(calls + 1, model.CallCount);
        Assert.Equal(FirstLineOf(AfterSalesHandoffPromptDefaults.ExchangeInstructions.Trim()), FirstLineOf(InstructionsOf(calls)));
        Assert.Equal(1, await RequestCountAsync());
        await using (var db = factory.CreateDbContext())
        {
            var request = await db.AfterSalesRequest.SingleAsync();
            Assert.Equal(key, request.CaseKey);
            // M5 Task 6：多 Agent 建立的申請走「AI 多 Agent」管道（一樣會跑 M4 初審）。
            Assert.Equal(AfterSalesChannel.AgentHandoff, request.Channel);
            var confirmation = await db.AgentConfirmation.SingleAsync();
            Assert.True(confirmation.Approved);
            Assert.Equal(request.Id, confirmation.AfterSalesRequestId);
        }

        var saved = (await SessionAsync(key)).StateJson;
        Assert.DoesNotContain(AfterSalesAgentRunner.OpenApprovalsKey, saved, StringComparison.Ordinal);
        AssertOpenApprovalsMirrorFramework(saved);
        await AssertOnlyLastCheckpointKeptAsync(saved);
    }

    /// <summary>
    /// M5 實機驗證劇本 H 的缺陷（Azure 回 HTTP 400）：確認卡按下確認（或取消）後，同一輪繼續交接——換退貨專員 → 分流 → 品質專員 → 分流。
    /// 分流<b>第二次</b>被呼叫時，送出的訊息必須同時有 create_after_sales_request 的呼叫與結果（腳本化模型比照 Responses API 檢查成對）；
    /// 下一輪再由分流接手也一樣。修正前框架把核准後重新送出的呼叫訊息標成「來自歷史」，其他參與者存歷史時把它丟掉、只留下結果。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Handoff_DecisionThenSameTurnHandoffs_ToolCallsStayPaired(bool approve)
    {
        var (runner, key, card) = await ReachConfirmationAsync();
        model.Call("handoff_to_1", Handoff("換尺碼處理完了，顧客另有開膠問題"));
        model.Call("handoff_to_2", Handoff("白色那雙鞋頭開膠"));
        model.Call("handoff_to_1", Handoff("需由分流重新判斷"));
        model.Reply("白色那雙開膠的部分，方便上傳照片嗎？");
        var calls = model.CallCount;
        events.Clear();

        var result = await ConfirmAsync(runner, key, card.ConfirmationId, approve);

        // 這一輪每次送出的訊息都成對；不成對時這裡直接顯示是哪個 CallId。
        Assert.All(model.Requests.Skip(calls), ResponsesApiPairing.EnsurePaired);
        Assert.Equal(AgentStopReason.AwaitingUserReply, result.StopReason);
        Assert.Equal("白色那雙開膠的部分，方便上傳照片嗎？", result.ReplyText);
        Assert.Equal(calls + 4, model.CallCount);
        Assert.Equal(
            ["換退貨專員 → 分流 Agent：換尺碼處理完了，顧客另有開膠問題", "分流 Agent → 品質專員：白色那雙鞋頭開膠", "品質專員 → 分流 Agent：需由分流重新判斷"],
            events.Where(e => e.Kind == AgentStepKind.Handoff).Select(e => e.PlainText));
        Assert.Equal(approve ? 1 : 0, await RequestCountAsync());
        Assert.Equal(approve, result.CreatedRequestNo is not null);
        // 分流第二次被呼叫（最後一次模型呼叫）：仍看得到建立申請的呼叫與結果（成對）。
        var lastTriage = model.Requests[^1].SelectMany(m => m.Contents).ToList();
        var create = Assert.Single(lastTriage.OfType<FunctionCallContent>(), c => c.Name == AfterSalesAgentTools.CreateAfterSalesRequestName);
        Assert.Single(lastTriage.OfType<FunctionResultContent>(), r => r.CallId == create.CallId);

        // 下一輪（重建 Runner、由 checkpoint 還原）由分流接手：一樣成對、不會模型錯誤。
        model.Reply("好的，還有其他需要嗎？");
        var next = await SendAsync(CreateRunner(), key, "我等等再拍照");
        Assert.Equal(AgentStopReason.AwaitingUserReply, next.StopReason);
        Assert.Equal("分流 Agent", next.Speaker);
        Assert.Equal(approve ? 1 : 0, await RequestCountAsync());
    }

    /// <summary>
    /// 劇本 H 的實機路線：確認那一輪品質專員第一次被呼叫（換退貨 → 分流 → 品質 → 分流）；下一輪分流再交給品質專員，
    /// 品質專員<b>第二次</b>被呼叫時，從自己的歷史載入的訊息也必須同時有 create_after_sales_request 的呼叫與結果（嚴格成對檢查開著）。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Handoff_QualitySpecialistSecondVisitAfterDecision_ToolCallsStayPaired(bool approve)
    {
        var (runner, key, card) = await ReachConfirmationAsync();
        model.Call("handoff_to_1", Handoff("換尺碼處理完了，顧客另有開膠問題"));
        model.Call("handoff_to_2", Handoff("白色那雙鞋頭開膠"));
        model.Call("handoff_to_1", Handoff("需由分流重新判斷"));
        model.Reply("白色那雙開膠的部分，方便上傳照片嗎？");
        Assert.Equal(AgentStopReason.AwaitingUserReply, (await ConfirmAsync(runner, key, card.ConfirmationId, approve)).StopReason);

        // 下一輪（重建 Runner、由 checkpoint 還原）：分流交給品質專員，品質專員第二次被呼叫。
        model.Call("handoff_to_2", Handoff("白色那雙開膠，顧客準備上傳照片"));
        model.Reply("好的，方便上傳開膠處的清楚照片嗎？");
        var calls = model.CallCount;
        events.Clear();

        var next = await SendAsync(CreateRunner(), key, "好，我拍給你");

        Assert.All(model.Requests.Skip(calls), ResponsesApiPairing.EnsurePaired);
        Assert.Equal(AgentStopReason.AwaitingUserReply, next.StopReason);
        Assert.Equal("品質專員", next.Speaker);
        Assert.Equal(calls + 2, model.CallCount);
        Assert.Equal(
            ["分流 Agent → 品質專員：白色那雙開膠，顧客準備上傳照片"],
            events.Where(e => e.Kind == AgentStepKind.Handoff).Select(e => e.PlainText));
        Assert.Equal(FirstLineOf(AfterSalesHandoffPromptDefaults.QualityInstructions.Trim()), FirstLineOf(InstructionsOf(calls + 1)));
        var quality = model.Requests[^1].SelectMany(m => m.Contents).ToList();
        var create = Assert.Single(quality.OfType<FunctionCallContent>(), c => c.Name == AfterSalesAgentTools.CreateAfterSalesRequestName);
        Assert.Single(quality.OfType<FunctionResultContent>(), r => r.CallId == create.CallId);
        Assert.Equal(approve ? 1 : 0, await RequestCountAsync());
    }

    [Fact]
    public async Task Handoff_CancelCard_NoRequestAndSessionContinues()
    {
        var (runner, key, card) = await ReachConfirmationAsync();
        model.Reply("好的，不建立申請。");

        var result = await ConfirmAsync(runner, key, card.ConfirmationId, approve: false);

        Assert.Equal(AgentStopReason.Answered, result.StopReason);
        Assert.Equal(0, await RequestCountAsync());
        var rejection = model.Requests[^1].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Last();
        Assert.Equal("Tool call invocation rejected. 顧客取消", rejection.Result?.ToString());

        model.Reply("還有什麼需要幫忙的嗎？");
        var next = await SendAsync(CreateRunner(), key, "謝謝");
        Assert.Equal(AgentStopReason.AwaitingUserReply, next.StopReason);
        Assert.Equal("換退貨專員", next.Speaker);
        Assert.Equal(0, await RequestCountAsync());
    }

    /// <summary>確認卡等待中應用程式重啟（全新的 Runner、Workflow 與 checkpoint 管理器；只剩資料庫）：按確認仍能建立申請，且只建立一筆。</summary>
    [Fact]
    public async Task Handoff_ConfirmAfterRestart_CreatesOnce()
    {
        var (_, key, card) = await ReachConfirmationAsync();

        model.Reply("已為您送出換尺碼申請。");
        var afterRestart = CreateRunner();
        var history = await afterRestart.GetHistoryAsync(key);
        var restoredCard = Assert.Single(history, x => x.PendingCard is not null).PendingCard!;
        Assert.Equal(card.ConfirmationId, restoredCard.ConfirmationId);

        var first = await ConfirmAsync(afterRestart, key, restoredCard.ConfirmationId, approve: true);

        Assert.Matches(@"^AS\d{12}$", first.CreatedRequestNo!);
        Assert.Equal(1, await RequestCountAsync());

        // 同一張卡再按一次（另一個分頁或重送）：卡片已處理，不會再送核准、不呼叫模型、不再建立。
        var calls = model.CallCount;
        var second = await ConfirmAsync(CreateRunner(), key, card.ConfirmationId, approve: true);
        Assert.Equal("這張確認卡已經處理過或已失效，沒有重複送出申請。", second.ReplyText);
        Assert.Null(second.CreatedRequestNo);
        Assert.Equal(calls, model.CallCount);
        Assert.Equal(1, await RequestCountAsync());

        // 之後的對話照常（框架沒有殘留的待決請求）。
        model.Reply("不客氣！");
        var next = await SendAsync(CreateRunner(), key, "謝謝");
        Assert.Equal(AgentStopReason.Answered, next.StopReason);
        Assert.DoesNotContain(events, e => e.Kind == AgentStepKind.Stop && e.PlainText.Contains("模型呼叫失敗", StringComparison.Ordinal));
        Assert.Equal(1, await RequestCountAsync());
    }

    /// <summary>會話裡已沒有這張卡的待回覆請求（例如上一輪沒有存回）：失敗關閉，作廢卡片，不送核准、不呼叫模型。</summary>
    [Fact]
    public async Task Handoff_ConfirmWhenRequestMissingFromSession_FailsClosed()
    {
        var (runner, key, card) = await ReachConfirmationAsync();
        await using (var db = factory.CreateDbContext())
        {
            await db.AgentConfirmation.ExecuteUpdateAsync(s => s.SetProperty(x => x.ApprovalRequestJson, (string?)null));
        }

        var calls = model.CallCount;
        var result = await ConfirmAsync(runner, key, card.ConfirmationId, approve: true);

        Assert.Equal("這張確認卡已失效，請重新送出您的需求。", result.ReplyText);
        Assert.Equal(calls, model.CallCount);
        Assert.Equal(0, await RequestCountAsync());
    }

    // ───────────────────────── 留下的核准請求：先明確拒絕 ─────────────────────────

    /// <summary>確認卡等待中改打字：Runner 先 TryPrepare＋拒絕（多一次模型呼叫），再送新訊息；不建立申請、會話不壞。</summary>
    [Fact]
    public async Task Handoff_NewMessageWhileCardPending_RejectsThenSends()
    {
        var (_, key, card) = await ReachConfirmationAsync();
        model.Reply("好的，先不建立申請。");
        model.Reply("請問是哪一筆訂單要查物流呢？");
        var calls = model.CallCount;

        var result = await SendAsync(CreateRunner(), key, "算了，我想先問物流");

        Assert.Equal(calls + 2, model.CallCount);
        var rejection = model.Requests[calls].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Last();
        Assert.Equal($"Tool call invocation rejected. {AfterSalesAgentRunner.AbandonedApprovalReason}", rejection.Result?.ToString());
        Assert.Contains(model.Requests[calls + 1], m => m.Text == "算了，我想先問物流");
        Assert.Equal("好的，先不建立申請。\n\n請問是哪一筆訂單要查物流呢？", result.ReplyText);
        Assert.Equal(AgentStopReason.AwaitingUserReply, result.StopReason);
        Assert.Contains(events, e => e.Kind == AgentStepKind.ConfirmationDecided && e.PlainText.Contains("作廢", StringComparison.Ordinal));
        Assert.Contains(events, e => e.Kind == AgentStepKind.ProgramCheck && e.PlainText.StartsWith("多 Agent 模式：先替尚未回覆的確認請求送出拒絕", StringComparison.Ordinal));
        Assert.Equal(0, await RequestCountAsync());
        await using (var db = factory.CreateDbContext())
        {
            Assert.False((await db.AgentConfirmation.SingleAsync(x => x.Id == card.ConfirmationId)).Approved);
        }

        var saved = (await SessionAsync(key)).StateJson;
        Assert.DoesNotContain(AfterSalesAgentRunner.OpenApprovalsKey, saved, StringComparison.Ordinal);

        model.Reply("好的。");
        var next = await SendAsync(CreateRunner(), key, "SO-DEMO-0002");
        Assert.Equal(AgentStopReason.Answered, next.StopReason);
    }

    /// <summary>組卡的程式重查失敗（權限過期）：不出卡、請求留在會話；下一次送出前 Runner 先明確拒絕它，會話照常續聊。</summary>
    [Fact]
    public async Task Handoff_StoppedApproval_NextTurnRejectsExplicitly()
    {
        var demo = TestDemoControl.Create(factory, enabled: true);
        await demo.SetFaultAsync(DemoFault.PermissionExpired, true);
        var runner = CreateRunner(demo: demo);
        var key = await runner.StartNewAsync(AgentMode.Handoff);
        model.Call("handoff_to_1", Handoff("換尺碼"));
        model.Call("create_after_sales_request", SizeExchangeArgs());

        var stopped = await SendAsync(runner, key, "黑色那雙 40 碼想換 41，沒穿過");

        Assert.Equal(AgentStopReason.Forbidden, stopped.StopReason);
        Assert.Equal(AfterSalesAgentGuard.ForbiddenExplanation, stopped.ReplyText);
        Assert.Null(stopped.Confirmation);
        Assert.Equal(2, model.CallCount);
        var state = (await SessionAsync(key)).StateJson;
        Assert.Contains(AfterSalesAgentRunner.OpenApprovalsKey, state, StringComparison.Ordinal);
        AssertOpenApprovalsMirrorFramework(state);

        await demo.SetFaultAsync(DemoFault.PermissionExpired, false);
        model.Reply("好的，沒有建立申請。");
        model.Reply("有需要再告訴我。");
        var next = await SendAsync(CreateRunner(demo: demo), key, "那算了");

        Assert.Equal(AgentStopReason.Answered, next.StopReason);
        Assert.Equal(4, model.CallCount);
        var rejection = model.Requests[2].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Last();
        Assert.Equal($"Tool call invocation rejected. {AfterSalesAgentRunner.AbandonedApprovalReason}", rejection.Result?.ToString());
        Assert.Equal(0, await RequestCountAsync());
        Assert.DoesNotContain(AfterSalesAgentRunner.OpenApprovalsKey, (await SessionAsync(key)).StateJson, StringComparison.Ordinal);
    }

    /// <summary>
    /// 按下確認後工具已執行（申請已建立），接著的模型呼叫被額度擋下：回覆帶申請編號；這一輪不存回（Workflow 會話回到確認卡等待中），
    /// 下一次送出前 Runner 以「已執行、勿重做」的理由拒絕那個留下的請求——不會重複建立，會話照常續聊。
    /// </summary>
    [Fact]
    public async Task Handoff_ConfirmThenQuota_ReplyHasRequestNo_NextTurnDoesNotRepeat()
    {
        var (runner, key, card) = await ReachConfirmationAsync();
        var before = await SessionAsync(key);
        quota.Block = new AiQuotaLimitStatus(
            AiQuotaScope.User, AiQuotaPeriod.Daily, 50, 51.25, new DateTime(2026, 10, 23), new DateTime(2026, 10, 24));

        var result = await ConfirmAsync(runner, key, card.ConfirmationId, approve: true);

        Assert.Equal(AgentStopReason.QuotaExceeded, result.StopReason);
        Assert.Matches(@"^AS\d{12}$", result.CreatedRequestNo!);
        Assert.Contains($"申請已建立（{result.CreatedRequestNo}），但 AI 回覆暫時失敗", result.ReplyText, StringComparison.Ordinal);
        Assert.Contains("已達每人每日 AI 用量上限", result.StopExplanation);
        Assert.Equal(1, await RequestCountAsync());
        var after = await SessionAsync(key);
        Assert.Equal(before.StateJson, after.StateJson);
        Assert.Equal(before.StateVersion, after.StateVersion);

        quota.Block = null;
        model.Reply("您的申請已經建立了，請到我的申請查看。");
        model.Reply("不客氣！");
        var calls = model.CallCount;
        var next = await SendAsync(CreateRunner(), key, "謝謝");

        Assert.Equal(AgentStopReason.Answered, next.StopReason);
        var rejection = model.Requests[calls].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Last();
        Assert.Equal(
            $"Tool call invocation rejected. {string.Format(AfterSalesAgentRunner.RequestCreatedApprovalReasonFormat, result.CreatedRequestNo)}",
            rejection.Result?.ToString());
        Assert.Equal(1, await RequestCountAsync());
    }

    /// <summary>
    /// 按下確認後、工具執行前就取消（例如顧客關閉頁面）：沒有建立申請；下一輪拒絕留下的請求時，理由是「結果不確定」，
    /// 不可告訴模型「已執行／已建立」（否則模型可能宣稱有申請）。
    /// </summary>
    [Fact]
    public async Task Handoff_ConfirmCanceledBeforeTool_NextTurnSaysUncertain()
    {
        var (runner, key, card) = await ReachConfirmationAsync();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var canceled = await ConfirmAsync(runner, key, card.ConfirmationId, approve: true, cts.Token);

        Assert.Equal(AgentStopReason.Canceled, canceled.StopReason);
        Assert.Null(canceled.CreatedRequestNo);
        Assert.Equal(0, await RequestCountAsync());
        await using (var db = factory.CreateDbContext())
        {
            var confirmation = await db.AgentConfirmation.SingleAsync();
            Assert.True(confirmation.Approved);
            Assert.Null(confirmation.AfterSalesRequestId);
        }

        model.Reply("了解，申請沒有完成的話可以再告訴我。");
        model.Reply("不客氣！");
        var calls = model.CallCount;
        var next = await SendAsync(CreateRunner(), key, "謝謝");

        Assert.Equal(AgentStopReason.Answered, next.StopReason);
        var rejection = model.Requests[calls].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Last();
        Assert.Equal($"Tool call invocation rejected. {AfterSalesAgentRunner.UncertainApprovalReason}", rejection.Result?.ToString());
        Assert.DoesNotContain("已建立", rejection.Result?.ToString() ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(0, await RequestCountAsync());
    }

    // ───────────────────────── 同一輪內程式拒絕後續跑 ─────────────────────────

    /// <summary>
    /// 本輪先查無資料（封鎖寫入），專員仍提出建立申請：Runner 不出卡，同一輪 TryPrepare＋拒絕（「資訊不足或規則衝突…」）後由專員續跑，
    /// 不出錯、不建立申請，會話存回且沒有留下待回覆請求，下一輪照常。
    /// </summary>
    [Fact]
    public async Task Handoff_WriteBlocked_RejectsInTurnAndContinues()
    {
        var runner = CreateRunner();
        var key = await runner.StartNewAsync(AgentMode.Handoff);
        model.Call("handoff_to_1", Handoff("換尺碼"));
        model.Call("query_my_orders", new() { ["color"] = "紅色" });
        model.Call("create_after_sales_request", SizeExchangeArgs());
        model.Reply("找不到紅色的訂單，請問是哪一雙鞋呢？");

        var result = await SendAsync(runner, key, "紅色那雙換大一碼");

        Assert.Equal(AgentStopReason.AwaitingUserReply, result.StopReason);
        Assert.Equal("找不到紅色的訂單，請問是哪一雙鞋呢？", result.ReplyText);
        Assert.Equal("換退貨專員", result.Speaker);
        Assert.Null(result.Confirmation);
        Assert.Equal(4, model.CallCount);
        var rejection = model.Requests[3].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Last();
        Assert.Equal($"Tool call invocation rejected. {AfterSalesAgentGuard.BlockedMessage}", rejection.Result?.ToString());
        Assert.Contains(events, e => e.Kind == AgentStepKind.ToolResult && e.RawJson!.Contains("\"status\":\"blocked\"", StringComparison.Ordinal));
        Assert.DoesNotContain(events, e => e.Kind == AgentStepKind.Stop && e.PlainText.Contains("模型呼叫失敗", StringComparison.Ordinal));
        Assert.Equal(0, await RequestCountAsync());
        await using (var db = factory.CreateDbContext())
        {
            Assert.False(await db.AgentConfirmation.AnyAsync());
        }

        var saved = (await SessionAsync(key)).StateJson;
        Assert.DoesNotContain(AfterSalesAgentRunner.OpenApprovalsKey, saved, StringComparison.Ordinal);
        AssertOpenApprovalsMirrorFramework(saved);

        model.Reply("好的，黑色那雙要換幾號？");
        var next = await SendAsync(CreateRunner(), key, "黑色那雙");
        Assert.Equal(AgentStopReason.AwaitingUserReply, next.StopReason);
        Assert.Equal(5, model.CallCount);
    }

    /// <summary>記憶內容含電話：程式重核不通過，不出卡、不寫入，同一輪拒絕後由分流續跑。</summary>
    [Fact]
    public async Task Handoff_MemoryWithPhone_RejectedInTurn()
    {
        var runner = CreateRunner();
        var key = await runner.StartNewAsync(AgentMode.Handoff);
        model.Call("remember_preference", new() { ["kind"] = "communication", ["value"] = "請打 0912345678 聯絡我" });
        model.Reply("抱歉，電話這類個人資料我不能記住，還有其他需要嗎？");

        var result = await SendAsync(runner, key, "以後請打 0912345678 聯絡我，記住喔");

        Assert.Equal(AgentStopReason.AwaitingUserReply, result.StopReason);
        Assert.Null(result.Confirmation);
        Assert.Equal("分流 Agent", result.Speaker);
        Assert.Equal(2, model.CallCount);
        var rejection = model.Requests[1].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Last();
        Assert.StartsWith("Tool call invocation rejected. 程式重核失敗：", rejection.Result?.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(events, e => e.PlainText.Contains("0912345678", StringComparison.Ordinal) && e.Kind != AgentStepKind.UserMessage);
        await using var db = factory.CreateDbContext();
        Assert.False(await db.CustomerMemory.AnyAsync());
        Assert.False(await db.AgentConfirmation.AnyAsync());
        Assert.DoesNotContain(AfterSalesAgentRunner.OpenApprovalsKey, (await SessionAsync(key)).StateJson, StringComparison.Ordinal);
    }

    /// <summary>
    /// 確認請求被程式拒絕超過上限（3 次）：本輪停在「連續失敗」，第 4 個請求留在會話（StateBag 與框架一致）；
    /// 下一次送出前 Runner 先明確拒絕它，會話照常續聊、不建立申請。
    /// </summary>
    [Fact]
    public async Task Handoff_RejectLimitExhausted_LeftoverRejectedNextTurn()
    {
        var runner = CreateRunner();
        var key = await runner.StartNewAsync(AgentMode.Handoff);
        model.Call("handoff_to_1", Handoff("換尺碼"));
        model.Call("query_my_orders", new() { ["color"] = "紅色" });
        for (var i = 0; i < 4; i++)
        {
            model.Call("create_after_sales_request", SizeExchangeArgs());
        }

        var result = await SendAsync(runner, key, "紅色那雙換大一碼");

        Assert.Equal(AgentStopReason.ConsecutiveToolFailures, result.StopReason);
        Assert.Null(result.Confirmation);
        Assert.Equal(6, model.CallCount);
        var state = (await SessionAsync(key)).StateJson;
        Assert.Contains(AfterSalesAgentRunner.OpenApprovalsKey, state, StringComparison.Ordinal);
        AssertOpenApprovalsMirrorFramework(state);

        model.Reply("好的，不建立申請。");
        model.Reply("請問是哪一雙鞋呢？");
        var next = await SendAsync(CreateRunner(), key, "那黑色那雙");

        Assert.Equal(AgentStopReason.AwaitingUserReply, next.StopReason);
        Assert.Equal(8, model.CallCount);
        var rejection = model.Requests[6].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Last();
        Assert.Equal($"Tool call invocation rejected. {AfterSalesAgentRunner.AbandonedApprovalReason}", rejection.Result?.ToString());
        Assert.Equal(0, await RequestCountAsync());
        Assert.DoesNotContain(AfterSalesAgentRunner.OpenApprovalsKey, (await SessionAsync(key)).StateJson, StringComparison.Ordinal);
    }

    /// <summary>
    /// 確認卡等待中改打字，但每次替留下的請求送出拒絕後模型都又提出新的核准請求，直到拒絕上限：顧客這句話一直沒有機會送出。
    /// 不可把「連續查詢失敗」或模型針對舊請求的文字當成對這句話的回覆——改回程式說明「這句話還沒有交給 AI 處理，請再送一次」；
    /// 會話存回（最後一個請求留在 StateBag），下一次送出前先明確拒絕它，再把顧客的新訊息送給模型。
    /// </summary>
    [Fact]
    public async Task Handoff_LeftoverRejectionsExhausted_CustomerMessageNotDropped()
    {
        var (_, key, _) = await ReachConfirmationAsync();
        for (var i = 0; i < 4; i++)
        {
            model.Call("create_after_sales_request", SizeExchangeArgs());
        }

        var calls = model.CallCount;

        var result = await SendAsync(CreateRunner(), key, "算了，我想先問物流");

        Assert.Equal(calls + 4, model.CallCount);
        Assert.DoesNotContain(model.Requests.Skip(calls), r => r.Any(m => m.Text.Contains("我想先問物流", StringComparison.Ordinal)));
        Assert.Equal(AfterSalesAgentRunner.CustomerMessageNotSentReply, result.ReplyText);
        Assert.Equal(AgentStopReason.ConsecutiveToolFailures, result.StopReason);
        Assert.Null(result.Confirmation);
        Assert.Null(result.Speaker);
        Assert.Equal(0, await RequestCountAsync());
        var state = (await SessionAsync(key)).StateJson;
        Assert.Contains(AfterSalesAgentRunner.OpenApprovalsKey, state, StringComparison.Ordinal);
        AssertOpenApprovalsMirrorFramework(state);

        model.Reply("好的，不建立申請。");
        model.Reply("請問是哪一筆訂單要查物流呢？");
        var next = await SendAsync(CreateRunner(), key, "算了，我想先問物流");

        Assert.Equal(AgentStopReason.AwaitingUserReply, next.StopReason);
        Assert.Contains(model.Requests[^1], m => m.Text == "算了，我想先問物流");
        Assert.DoesNotContain(AfterSalesAgentRunner.OpenApprovalsKey, (await SessionAsync(key)).StateJson, StringComparison.Ordinal);
    }

    // ───────────────────────── 無法接續與孤兒 checkpoint ─────────────────────────

    /// <summary>存下的 lastCheckpoint 已不存在：不呼叫模型、不存回，回程式說明請顧客開新對話（不會每輪默默模型錯誤）。</summary>
    [Fact]
    public async Task Handoff_MissingCheckpoint_FailsClosedWithNewConversationHint()
    {
        var runner = CreateRunner();
        var key = await runner.StartNewAsync(AgentMode.Handoff);
        model.Call("handoff_to_3", Handoff("查物流"));
        model.Reply("請問是哪一筆訂單？");
        await SendAsync(runner, key, "包裹到哪了");
        var before = await SessionAsync(key);
        await using (var db = factory.CreateDbContext())
        {
            await db.WorkflowCheckpoint.ExecuteDeleteAsync();
        }

        var calls = model.CallCount;
        var result = await SendAsync(CreateRunner(), key, "SO-DEMO-0002");

        Assert.Equal(AgentStopReason.ServiceUnavailable, result.StopReason);
        Assert.Equal(AfterSalesAgentRunner.UnrecoverableSessionExplanation, result.ReplyText);
        Assert.Contains("開新對話", result.ReplyText, StringComparison.Ordinal);
        Assert.Equal(calls, model.CallCount);
        Assert.Equal(before.StateVersion, (await SessionAsync(key)).StateVersion);
    }

    /// <summary>第一輪就失敗（不存回）：這一輪產生的新 Workflow 會話沒有人會再用，它的 checkpoint 一併刪掉；重試照常。</summary>
    [Fact]
    public async Task Handoff_FirstTurnFailure_DeletesOrphanCheckpoints()
    {
        var runner = CreateRunner();
        var key = await runner.StartNewAsync(AgentMode.Handoff);
        model.Call("handoff_to_3", Handoff("查物流"));
        model.Throw(new InvalidOperationException("boom"));

        var failed = await SendAsync(runner, key, "包裹到哪了");

        Assert.Equal(AgentStopReason.ModelError, failed.StopReason);
        Assert.Equal(string.Empty, (await SessionAsync(key)).StateJson);
        await using (var db = factory.CreateDbContext())
        {
            Assert.False(await db.WorkflowCheckpoint.AnyAsync());
        }

        model.Call("handoff_to_3", Handoff("查物流"));
        model.Reply("請問是哪一筆訂單？");
        var retry = await SendAsync(CreateRunner(), key, "包裹到哪了");
        Assert.Equal(AgentStopReason.AwaitingUserReply, retry.StopReason);
        await AssertOnlyLastCheckpointKeptAsync((await SessionAsync(key)).StateJson);
    }

    // ───────────────────────── Guard、步數、錯誤、取消 ─────────────────────────

    [Fact]
    public async Task Handoff_GuardForbidden_Terminates()
    {
        await using (var db = factory.CreateDbContext())
        {
            await db.UserRole.Where(x => x.MyUserId == customer1).ExecuteDeleteAsync();
            await db.MyUser.Where(x => x.Id == customer1).ExecuteUpdateAsync(s => s.SetProperty(x => x.RoleViewId, (int?)null));
        }

        var runner = CreateRunner();
        var key = await runner.StartNewAsync(AgentMode.Handoff);
        model.Call("handoff_to_3", Handoff("查物流"));
        model.Call("track_shipment", new() { ["orderItemId"] = white41 });
        model.Reply("不應被呼叫");

        var result = await SendAsync(runner, key, "幫我查物流");

        Assert.Equal(AgentStopReason.Forbidden, result.StopReason);
        Assert.Equal(AfterSalesAgentGuard.ForbiddenExplanation, result.ReplyText);
        Assert.Null(result.Speaker);
        Assert.Equal(2, model.CallCount);
        var toolRequest = Assert.Single(events, e => e.Kind == AgentStepKind.ToolRequest);
        Assert.StartsWith("物流專員決定查詢", toolRequest.PlainText);
        Assert.Contains(events, e => e.Kind == AgentStepKind.ProgramCheck && e.PlainText.Contains("未通過", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Handoff_StepBudget_Stops()
    {
        settings.HandoffMaxSteps = 3;
        var runner = CreateRunner();
        var key = await runner.StartNewAsync(AgentMode.Handoff);
        model.Fallback = _ => ScriptedAgentModel.FunctionCall("query_my_orders", []);

        var result = await SendAsync(runner, key, "幫我看看訂單");

        Assert.Equal(AgentStopReason.StepLimitReached, result.StopReason);
        Assert.Equal(settings.HandoffMaxSteps + 1, model.CallCount);
        Assert.Contains("步驟超過上限（3 步）", result.ReplyText, StringComparison.Ordinal);
        Assert.Equal(result.StopExplanation, result.ReplyText);
        Assert.Contains(events, e => e.Kind == AgentStepKind.Stop && e.PlainText.Contains("已達步數上限（3 步）", StringComparison.Ordinal));
        // 出錯的一輪不存回：會話仍是空白（第一輪），下一輪照常。
        Assert.Equal(string.Empty, (await SessionAsync(key)).StateJson);

        model.Fallback = null;
        model.Reply("好的，請問要查哪一筆？");
        var next = await SendAsync(CreateRunner(), key, "重來一次");
        Assert.Equal(AgentStopReason.AwaitingUserReply, next.StopReason);
        Assert.Equal("分流 Agent", next.Speaker);
    }

    /// <summary>模型錯誤（ErrorContent update）：程式說明取代回覆（不顯示錯誤文字）、這一輪不存回；下一輪不受影響（回滾）。</summary>
    [Fact]
    public async Task Handoff_ModelError_DoesNotCorruptNextTurn()
    {
        var runner = CreateRunner();
        var key = await runner.StartNewAsync(AgentMode.Handoff);
        model.Call("handoff_to_1", Handoff("換尺碼"));
        model.Reply("請問是哪一雙鞋？");
        await SendAsync(runner, key, "我想換尺碼");
        var good = await SessionAsync(key);

        model.Throw(new InvalidOperationException("upstream secret detail"));
        var failed = await SendAsync(CreateRunner(), key, "黑色那雙");

        Assert.Equal(AgentStopReason.ModelError, failed.StopReason);
        Assert.Equal("AI 服務暫時發生問題，這次沒有完成。請稍後再試，或洽客服人員協助。", failed.ReplyText);
        Assert.DoesNotContain(events, e => e.PlainText.Contains("upstream secret detail", StringComparison.Ordinal));
        var afterFailure = await SessionAsync(key);
        Assert.Equal(good.StateJson, afterFailure.StateJson);
        Assert.Equal(good.StateVersion, afterFailure.StateVersion);

        model.Reply("好的，黑色那雙要換幾號？");
        var next = await SendAsync(CreateRunner(), key, "黑色那雙");
        Assert.Equal(AgentStopReason.AwaitingUserReply, next.StopReason);
        Assert.Equal("好的，黑色那雙要換幾號？", next.ReplyText);
        Assert.Equal("換退貨專員", next.Speaker);
        Assert.Equal(FirstLineOf(AfterSalesHandoffPromptDefaults.ExchangeInstructions.Trim()), FirstLineOf(InstructionsOf(model.CallCount - 1)));
    }

    /// <summary>
    /// 執行中取消（Task 1 未驗證的項目）：Runner 回「已取消」、不存回（會話維持取消前的狀態與版本）；
    /// 背景的 Workflow 不會在取消後繼續呼叫模型，下一輪照常由上一位專員接手。
    /// </summary>
    [Fact]
    public async Task Handoff_CancelMidTurn_LeavesSessionConsistent()
    {
        using var cts = new CancellationTokenSource();
        var cancelOnCall = -1;
        var canceling = new CancelingChatClient(model, () => model.CallCount == cancelOnCall, cts);
        // 取消的那一輪結束時，框架的 StreamingRun 會等背景執行迴圈結束（StreamingRunEventStream.StopAsync 等待 run loop），
        // 所以 SendAsync 回來後，經過這個 client 的呼叫次數就是最終值，不必等待。
        var runner = CreateRunner((_, _) => canceling);
        var key = await runner.StartNewAsync(AgentMode.Handoff);
        model.Call("handoff_to_1", Handoff("換尺碼"));
        model.Reply("請問是哪一雙鞋？");
        await SendAsync(runner, key, "我想換尺碼");
        var good = await SessionAsync(key);

        cancelOnCall = model.CallCount; // 下一次呼叫（呼叫前的次數＝目前次數）時取消，取消的呼叫不會到達模型
        var invocationsBefore = canceling.Invocations;
        var canceled = await SendAsync(CreateRunner((_, _) => canceling), key, "黑色那雙", cts.Token);
        var invocationsAfterCancel = canceling.Invocations;

        Assert.Equal(AgentStopReason.Canceled, canceled.StopReason);
        Assert.Equal("已取消這次的處理。", canceled.ReplyText);
        var afterCancel = await SessionAsync(key);
        Assert.Equal(good.StateJson, afterCancel.StateJson);
        Assert.Equal(good.StateVersion, afterCancel.StateVersion);
        var callsAfterCancel = model.CallCount;
        Assert.Equal(invocationsBefore + 1, invocationsAfterCancel);

        model.Reply("好的，黑色那雙要換幾號？");
        var next = await SendAsync(CreateRunner(), key, "黑色那雙");
        Assert.Equal(AgentStopReason.AwaitingUserReply, next.StopReason);
        Assert.Equal("好的，黑色那雙要換幾號？", next.ReplyText);
        Assert.Equal("換退貨專員", next.Speaker);
        Assert.Equal(callsAfterCancel + 1, model.CallCount);
        // 取消那一輪的背景執行沒有在之後再呼叫模型（下一輪用的是另一條鏈，這個 client 的次數不變）。
        Assert.Equal(invocationsAfterCancel, canceling.Invocations);
        Assert.Contains(model.Requests[^1], m => m.Text == "黑色那雙");
    }

    /// <summary>
    /// 取消時模型呼叫不理會取消、稍後仍回傳「提出建立申請」（最壞情況：背景 Workflow 在 Runner 回「已取消」之後還收到模型輸出）：
    /// 已結束的那一輪不會再多出任何軌跡步驟、不會出確認卡、不會建立申請，會話維持取消前的狀態。
    /// 同時釘住框架行為：OffThread 執行在取消後不會被放著繼續跑——Runner 回來時那個拖延的模型呼叫已結束
    /// （StreamingRun 結束時等背景執行迴圈收尾），之後也沒有再呼叫模型。
    /// </summary>
    [Fact]
    public async Task Handoff_CancelWhileModelIgnoresToken_NoLateEventsOrRequest()
    {
        using var cts = new CancellationTokenSource();
        var stallOnCall = -1;
        var stalling = new TokenIgnoringStallClient(model, () => model.CallCount == stallOnCall, cts, TimeSpan.FromMilliseconds(500));
        var runner = CreateRunner((_, _) => stalling);
        var key = await runner.StartNewAsync(AgentMode.Handoff);
        model.Call("handoff_to_1", Handoff("換尺碼"));
        model.Reply("請問是哪一雙鞋？");
        await SendAsync(runner, key, "我想換尺碼");
        var good = await SessionAsync(key);

        stallOnCall = model.CallCount;
        model.Call("create_after_sales_request", SizeExchangeArgs());
        var canceled = await SendAsync(CreateRunner((_, _) => stalling), key, "黑色那雙 40 碼想換 41，沒穿過", cts.Token);
        var stallEndedBeforeReturn = stalling.StallEnded.IsCompleted;

        Assert.Equal(AgentStopReason.Canceled, canceled.StopReason);
        Assert.Equal("已取消這次的處理。", canceled.ReplyText);
        Assert.Null(canceled.Confirmation);
        Assert.True(stallEndedBeforeReturn, "Runner 回「已取消」時，背景那個不理會取消的模型呼叫應已結束（框架等背景執行迴圈收尾）。");

        int runId;
        int stepsAtReturn;
        await using (var db = factory.CreateDbContext())
        {
            var run = await db.AgentRunTrace.AsNoTracking().Where(x => x.SessionKey == key).OrderByDescending(x => x.Id).FirstAsync();
            runId = run.Id;
            Assert.Equal(AgentStopReason.Canceled, run.StopReason);
            Assert.NotNull(run.EndedAt);
            var steps = await db.AgentRunStep.AsNoTracking().Where(x => x.AgentRunTraceId == runId).OrderBy(x => x.Sequence).ToListAsync();
            stepsAtReturn = steps.Count;
            Assert.Equal(AgentStepKind.TurnResult, steps[^1].Kind);
        }

        // 給可能殘留的背景工作一點時間：若框架仍在背景處理那個模型輸出，會在這段時間內寫出工具／確認卡事件。
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        var invocations = stalling.Invocations;

        await using (var db = factory.CreateDbContext())
        {
            Assert.Equal(stepsAtReturn, await db.AgentRunStep.CountAsync(x => x.AgentRunTraceId == runId));
            Assert.False(await db.AgentConfirmation.AnyAsync());
        }

        Assert.Equal(0, await RequestCountAsync());
        var afterCancel = await SessionAsync(key);
        Assert.Equal(good.StateJson, afterCancel.StateJson);
        Assert.Equal(good.StateVersion, afterCancel.StateVersion);
        Assert.Equal(invocations, stalling.Invocations);
    }

    // ───────────────────────── 記憶 ─────────────────────────

    [Fact]
    public async Task Handoff_MemoryInjectedForSpecialists()
    {
        var memory = new CustomerMemoryService(factory, new RecordingAuditLogService(), new ManualTimeProvider(UtcNow), NullLogger<CustomerMemoryService>.Instance);
        Assert.True((await memory.UpsertAsync(customer1, MemoryKind.ShoeSize, "41", null)).Success);
        var runner = CreateRunner();
        var key = await runner.StartNewAsync(AgentMode.Handoff);
        model.Call("handoff_to_1", Handoff("換尺碼"));
        model.Reply("記得您平常穿 41 碼，這次也是換 41 嗎？");

        var result = await SendAsync(runner, key, "想換一雙鞋的尺寸");

        Assert.Equal("換退貨專員", result.Speaker);
        Assert.Equal(2, model.CallCount);
        for (var i = 0; i < model.CallCount; i++)
        {
            Assert.Contains(CustomerMemoryContextProvider.Header, InstructionsOf(i), StringComparison.Ordinal);
            Assert.Contains("- 鞋碼偏好：「41」", InstructionsOf(i), StringComparison.Ordinal);
            Assert.DoesNotContain(model.Requests[i], m => m.Text.Contains(CustomerMemoryContextProvider.Header, StringComparison.Ordinal));
        }

        Assert.Single(events, e => e.Kind == AgentStepKind.ModelContext && e.Source == AgentDataSource.Memory);
    }

    /// <summary>記憶工具在專員上照樣經確認卡（工具名稱分流到記憶卡），確認後才寫入。</summary>
    [Fact]
    public async Task Handoff_RememberPreference_GoesThroughMemoryCard()
    {
        var runner = CreateRunner();
        var key = await runner.StartNewAsync(AgentMode.Handoff);
        model.Call("remember_preference", new() { ["kind"] = "shoe_size", ["value"] = "41" });

        var shown = await SendAsync(runner, key, "我平常鞋子穿 41 碼，以後記得喔");

        var card = Assert.IsType<ConfirmationCard>(shown.Confirmation);
        Assert.Equal(ConfirmationCardKind.Memory, card.Kind);
        Assert.StartsWith("23:triage_triage_UserInput:ficc_", card.ApprovalRequestId, StringComparison.Ordinal);
        await using (var db = factory.CreateDbContext())
        {
            Assert.False(await db.CustomerMemory.AnyAsync());
        }

        model.Reply("好的，已經記住了。");
        var confirmed = await ConfirmAsync(CreateRunner(), key, card.ConfirmationId, approve: true);

        Assert.Equal(AgentStopReason.Answered, confirmed.StopReason);
        Assert.Equal("分流 Agent", confirmed.Speaker);
        await using var check = factory.CreateDbContext();
        Assert.Equal("41", (await check.CustomerMemory.SingleAsync()).Value);
    }

    // ───────────────────────── 單一模式不變、開新對話 ─────────────────────────

    [Fact]
    public async Task SingleMode_Unchanged()
    {
        var runner = CreateRunner();
        var key = await runner.StartOrResumeAsync();
        model.Reply("您好，請問需要什麼協助？");

        var result = await SendAsync(runner, key, "你好");

        Assert.Equal(AgentMode.Single, (await SessionAsync(key)).Mode);
        Assert.Null(result.Speaker);
        Assert.DoesNotContain(events, e => e.Kind == AgentStepKind.Handoff);
        Assert.Null(Assert.Single(events, e => e.Kind == AgentStepKind.ModelReply).RawJson);
        Assert.DoesNotContain("speaker", Assert.Single(events, e => e.Kind == AgentStepKind.TurnResult).RawJson, StringComparison.Ordinal);
        Assert.Equal(FirstLineOf(AfterSalesAgentPromptDefaults.Instructions.Trim()), FirstLineOf(InstructionsOf(0)));
        Assert.DoesNotContain("handoff_to_", InstructionsOf(0), StringComparison.Ordinal);
        Assert.Equal(7, ToolsOf(0).Count);
        Assert.All(usage.Entries, x => Assert.Equal(TokenUsageOperations.AfterSalesAgent, x.Operation));
        Assert.DoesNotContain(events, e => e.Kind == AgentStepKind.ToolRequest);
        await using var db = factory.CreateDbContext();
        Assert.False(await db.WorkflowCheckpoint.AnyAsync());
    }

    [Fact]
    public async Task StartNew_Handoff_ClosesPreviousAndDeletesItsCheckpoints()
    {
        var runner = CreateRunner();
        var first = await runner.StartNewAsync(AgentMode.Handoff);
        model.Call("handoff_to_3", Handoff("查物流"));
        model.Reply("請問是哪一筆訂單？");
        await SendAsync(runner, first, "包裹到哪了");
        await using (var db = factory.CreateDbContext())
        {
            Assert.True(await db.WorkflowCheckpoint.AnyAsync());
        }

        var second = await runner.StartNewAsync();

        Assert.NotEqual(first, second);
        Assert.True((await SessionAsync(first)).IsClosed);
        Assert.Equal(AgentMode.Single, (await SessionAsync(second)).Mode);
        await using var check = factory.CreateDbContext();
        Assert.False(await check.WorkflowCheckpoint.AnyAsync());
    }

    // ───────────────────────── 錄製與回放（Task 6） ─────────────────────────

    /// <summary>
    /// 即時（錄製中）以多 Agent 模式跑「分流 → 換退貨專員 → 確認卡 → 確認建立申請」→ 錄製記下模式 → 重置 → 回放：
    /// 以錄製的模式開新對話後，Handoff 結構重建（交接事件、發言者、確認卡、建立申請都與錄製時相同），
    /// 完全沒有建立模型 client、沒有 Token 用量。
    /// </summary>
    [Fact]
    public async Task Replay_HandoffRecording_RebuildsTopologyWithoutModel()
    {
        var live = CreateRunner(demo: TestDemoControl.Create(factory, enabled: true));
        var liveKey = await live.StartNewAsync(AgentMode.Handoff);
        model.Call("handoff_to_1", Handoff("顧客要換尺碼"));
        model.Call("get_effective_policy", new() { ["orderItemId"] = black40, ["issueType"] = "size_exchange" });
        model.Call("create_after_sales_request", SizeExchangeArgs());
        var liveFirst = await SendAsync(live, liveKey, ReplayLine);
        var liveCard = Assert.IsType<ConfirmationCard>(liveFirst.Confirmation);
        model.Reply("已為您送出換尺碼申請。");
        var liveSecond = await ConfirmAsync(live, liveKey, liveCard.ConfirmationId, approve: true);
        Assert.NotNull(liveSecond.CreatedRequestNo);
        var liveHandoffs = events.Where(e => e.Kind == AgentStepKind.Handoff).Select(e => e.PlainText).ToList();
        Assert.Single(liveHandoffs);
        var liveCalls = model.CallCount;
        var liveUsage = usage.Entries.Count;

        int recordingId;
        await using (var db = factory.CreateDbContext())
        {
            var recording = await db.AgentRecording.SingleAsync(x => x.SessionKey == liveKey);
            Assert.Equal(AgentMode.Handoff, recording.Mode);
            recordingId = recording.Id;
        }

        var recordings = new AgentRecordingService(factory, TimeProvider.System, NullLogger<AgentRecordingService>.Instance);
        Assert.Equal(AgentMode.Handoff, (await recordings.GetAsync(recordingId))!.Recording.Mode);

        await ResetDemoAsync();
        events.Clear();
        deltas.Clear();
        var replayDemo = TestDemoControl.Create(factory, enabled: true);
        await replayDemo.SetModeAsync(DemoMode.Replay, recordingId);
        var modelCreations = 0;
        var replay = CreateRunner(
            (_, _) =>
            {
                modelCreations++;
                throw new InvalidOperationException("The model must not be created during replay.");
            },
            replayDemo);

        // 頁面的做法：先續用（或建立）會話，讀回放劇本得知錄製的模式，再以該模式開新對話。
        var resumed = await replay.StartOrResumeAsync();
        var script = await replay.GetReplayScriptAsync(resumed);
        Assert.True(script.IsReplay);
        Assert.Equal(AgentMode.Handoff, script.Mode);
        var key = await replay.StartNewAsync(script.Mode!.Value);
        Assert.Equal(AgentMode.Handoff, await replay.GetModeAsync(key));

        var first = await SendAsync(replay, key, ReplayLine);
        var card = Assert.IsType<ConfirmationCard>(first.Confirmation);
        Assert.Equal(AgentStopReason.AwaitingConfirmation, first.StopReason);
        Assert.Equal(liveCard.ToSpec, card.ToSpec);
        Assert.Equal(liveHandoffs, events.Where(e => e.Kind == AgentStepKind.Handoff).Select(e => e.PlainText));

        var second = await ConfirmAsync(replay, key, card.ConfirmationId, approve: true);
        Assert.Equal(AgentStopReason.Answered, second.StopReason);
        Assert.Equal("換退貨專員", second.Speaker);
        Assert.Matches(@"^AS\d{12}$", second.CreatedRequestNo!);
        await using (var db = factory.CreateDbContext())
        {
            var request = await db.AfterSalesRequest.SingleAsync(x => x.CaseKey == key);
            Assert.Equal(AfterSalesChannel.AgentHandoff, request.Channel);
            Assert.Equal(recordingId, (await db.AgentRunTrace.Where(x => x.SessionKey == key).Select(x => x.ReplayRecordingId).Distinct().SingleAsync()));
        }

        Assert.Equal(0, modelCreations);
        Assert.Equal(liveCalls, model.CallCount);
        Assert.Equal(liveUsage, usage.Entries.Count);
    }

    /// <summary>
    /// 回放多 Agent 的錄製、但目前的會話是單一 Agent：不呼叫模型、不寫軌跡、不前進游標，回固定說明請顧客以錄製時的模式開新對話；
    /// 以正確模式開新對話後就能從第一個動作開始回放。反過來（單一錄製、多 Agent 會話）也一樣擋下。
    /// </summary>
    [Fact]
    public async Task Replay_ModeMismatch_StopsWithoutModelUntilRightMode()
    {
        var live = CreateRunner(demo: TestDemoControl.Create(factory, enabled: true));
        var liveKey = await live.StartNewAsync(AgentMode.Handoff);
        model.Call("handoff_to_2", Handoff("顧客反映鞋子開膠"));
        model.Reply("了解，請問是哪一雙鞋開膠呢？");
        await SendAsync(live, liveKey, "我的鞋子開膠了");
        int recordingId;
        await using (var db = factory.CreateDbContext())
        {
            recordingId = await db.AgentRecording.Where(x => x.SessionKey == liveKey).Select(x => x.Id).SingleAsync();
        }

        var liveCalls = model.CallCount;
        var replayDemo = TestDemoControl.Create(factory, enabled: true);
        await replayDemo.SetModeAsync(DemoMode.Replay, recordingId);
        var replay = CreateRunner((_, _) => throw new InvalidOperationException("The model must not be created during replay."), replayDemo);
        var single = await replay.StartNewAsync(AgentMode.Single);
        int stepsBefore;
        await using (var db = factory.CreateDbContext())
        {
            stepsBefore = await db.AgentRunStep.CountAsync();
        }

        var stopped = await SendAsync(replay, single, "我的鞋子開膠了");

        Assert.Equal(AgentStopReason.ReplayActionMismatch, stopped.StopReason);
        Assert.Contains("多 Agent", stopped.ReplyText, StringComparison.Ordinal);
        Assert.Contains("開新對話", stopped.ReplyText, StringComparison.Ordinal);
        await using (var db = factory.CreateDbContext())
        {
            Assert.Equal(stepsBefore, await db.AgentRunStep.CountAsync());
        }

        // 游標沒有前進：下一個待執行的動作仍是第一句。
        Assert.Equal("我的鞋子開膠了", (await replay.GetReplayScriptAsync(single)).NextTurn!.CustomerText);

        var handoff = await replay.StartNewAsync(AgentMode.Handoff);
        var result = await SendAsync(replay, handoff, "我的鞋子開膠了");
        Assert.Equal("了解，請問是哪一雙鞋開膠呢？", result.ReplyText);
        Assert.Equal("品質專員", result.Speaker);
        Assert.Equal(liveCalls, model.CallCount);

        // 反過來：單一模式的錄製不能在多 Agent 會話回放。
        var liveSingle = CreateRunner(demo: TestDemoControl.Create(factory, enabled: true));
        var singleKey = await liveSingle.StartNewAsync(AgentMode.Single);
        model.Reply("您好，請問需要什麼協助？");
        await SendAsync(liveSingle, singleKey, "你好");
        await using (var db = factory.CreateDbContext())
        {
            recordingId = await db.AgentRecording.Where(x => x.SessionKey == singleKey).Select(x => x.Id).SingleAsync();
            Assert.Equal(AgentMode.Single, await db.AgentRecording.Where(x => x.Id == recordingId).Select(x => x.Mode).SingleAsync());
        }

        await replayDemo.SetModeAsync(DemoMode.Replay, recordingId);
        var wrong = await replay.StartNewAsync(AgentMode.Handoff);
        var reverse = await SendAsync(replay, wrong, "你好");
        Assert.Equal(AgentStopReason.ReplayActionMismatch, reverse.StopReason);
        Assert.Contains("單一 Agent", reverse.ReplyText, StringComparison.Ordinal);
        Assert.Equal(AgentMode.Single, (await replay.GetReplayScriptAsync(wrong)).Mode);
    }

    private const string ReplayLine = "黑色那雙 40 碼想換 41，沒穿過";

    /// <summary>以真正的示範重置清掉業務資料（錄製保留），讓回放能再建立一次申請。</summary>
    private async Task ResetDemoAsync()
    {
        var systemSettings = new SystemSettings();
        systemSettings.ExternalFileSystem.AfterSalesFilePath = fileRoot;
        var reset = new DemoResetService(
            factory,
            TestDemoControl.Off(factory),
            new AfterSalesFileStore(Options.Create(systemSettings), NullLogger<AfterSalesFileStore>.Instance),
            Options.Create(new DemoSettings { Enabled = true, DemoUserPassword = "demo1234" }),
            new ManualTimeProvider(UtcNow),
            new RecordingAuditLogService(),
            new CurrentUserService { CurrentUser = new CurrentUser { Id = 1, Account = "support" } },
            [inbox],
            NullLogger<DemoResetService>.Instance);
        Assert.True((await reset.ResetAsync()).Success);
    }

    // ───────────────────────── 共用 ─────────────────────────

    /// <summary>每輪存回後只留下會話 <c>lastCheckpoint</c> 那一筆。</summary>
    private async Task AssertOnlyLastCheckpointKeptAsync(string stateJson)
    {
        using var state = JsonDocument.Parse(stateJson);
        var last = state.RootElement.GetProperty("lastCheckpoint");
        var sessionId = last.GetProperty("sessionId").GetString();
        await using var db = factory.CreateDbContext();
        var rows = await db.WorkflowCheckpoint.Where(x => x.SessionId == sessionId).Select(x => x.CheckpointId).ToListAsync();
        Assert.Equal([last.GetProperty("checkpointId").GetString()!], rows);
    }

    /// <summary>會話 StateBag 記錄的待回覆請求與框架自己的 <c>pendingRequests</c> 一致（Runner 的記錄不會與框架脫節）。</summary>
    private static void AssertOpenApprovalsMirrorFramework(string stateJson)
    {
        using var state = JsonDocument.Parse(stateJson);
        var framework = state.RootElement.TryGetProperty("pendingRequests", out var pending) && pending.ValueKind == JsonValueKind.Object
            ? pending.EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal).ToList()
            : [];
        var ours = new List<string>();
        if (state.RootElement.GetProperty("stateBag").TryGetProperty(AfterSalesAgentRunner.OpenApprovalsKey, out var bag))
        {
            // StateBag 的值：{ "requests": [ "<ToolApprovalRequestContent JSON>", ... ] }（可能再包一層 value）。逐筆解析出 requestId。
            var holder = bag.TryGetProperty("requests", out var direct) ? direct : bag.EnumerateObject().Single().Value.GetProperty("requests");
            ours = [.. holder.EnumerateArray()
                .Select(x => JsonDocument.Parse(x.GetString()!).RootElement.GetProperty("requestId").GetString()!)
                .Order(StringComparer.Ordinal)];
            Assert.NotEmpty(ours);
        }

        // 兩邊完全相同：框架有的我們都記著，我們也沒有多記框架已經沒有的請求。
        Assert.Equal(framework, ours);
    }

    /// <summary>
    /// 第 N 次模型呼叫時取消呼叫端的 <see cref="CancellationTokenSource"/>，但這次呼叫不理會取消：拖延一段時間後照樣回傳腳本內容
    /// （模擬不支援取消的模型端點）。<see cref="StallEnded"/> 在拖延的呼叫把內容全部吐完後完成。
    /// </summary>
    private sealed class TokenIgnoringStallClient(IChatClient inner, Func<bool> shouldStall, CancellationTokenSource cts, TimeSpan stall)
        : DelegatingChatClient(inner)
    {
        private readonly TaskCompletionSource stallEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int invocations;

        public Task StallEnded => stallEnded.Task;

        public int Invocations => Volatile.Read(ref invocations);

        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref invocations);
            var list = messages.ToList();
            if (!shouldStall())
            {
                await foreach (var update in base.GetStreamingResponseAsync(list, options, cancellationToken))
                {
                    yield return update;
                }

                yield break;
            }

            await cts.CancelAsync();
            await Task.Delay(stall, CancellationToken.None);
            try
            {
                await foreach (var update in base.GetStreamingResponseAsync(list, options, CancellationToken.None))
                {
                    yield return update;
                }
            }
            finally
            {
                stallEnded.TrySetResult();
            }
        }
    }

    /// <summary>第 N 次模型呼叫時取消呼叫端的 <see cref="CancellationTokenSource"/>，並以已取消的權杖呼叫下層（模擬顧客關閉頁面）。</summary>
    private sealed class CancelingChatClient(IChatClient inner, Func<bool> shouldCancel, CancellationTokenSource cts) : DelegatingChatClient(inner)
    {
        private int invocations;

        /// <summary>經過這個 client 的模型呼叫次數（含被取消的那一次）。</summary>
        public int Invocations => Volatile.Read(ref invocations);

        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref invocations);
            var list = messages.ToList();
            if (shouldCancel())
            {
                await cts.CancelAsync();
                cts.Token.ThrowIfCancellationRequested();
            }

            await foreach (var update in base.GetStreamingResponseAsync(list, options, cancellationToken))
            {
                yield return update;
            }
        }

        public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref invocations);
            if (shouldCancel())
            {
                cts.Cancel();
                cts.Token.ThrowIfCancellationRequested();
            }

            return base.GetResponseAsync(messages, options, cancellationToken);
        }
    }
}

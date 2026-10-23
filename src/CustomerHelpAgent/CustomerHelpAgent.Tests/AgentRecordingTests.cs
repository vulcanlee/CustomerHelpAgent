using System.Text.Json;
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
using CustomerHelpAgent.Models.Agents;
using CustomerHelpAgent.Models.Others;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Agents;
using CustomerHelpAgent.Share.Demo;
using CustomerHelpAgent.Web.Agents;
using CustomerHelpAgent.Web.Agents.AfterSales;
using CustomerHelpAgent.Web.Configuration;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 錄製（1.0.5 起）：示範模式開啟且為即時模式時，Runner 記下顧客動作（<see cref="AgentRecordedTurn"/>），
/// <see cref="RecordingChatClient"/> 記下每次模型呼叫的請求摘要與回應（<see cref="AgentRecordedCall"/>）。
/// 以腳本化模型＋真實服務與 SQLite 記憶體資料庫跑旗艦劇本 A；重置測試用真正的 <see cref="DemoResetService"/>。
/// </summary>
public sealed class AgentRecordingTests : IAsyncLifetime
{
    /// <summary>本地時間（UTC+8）2026/10/23 10:00。</summary>
    private static readonly DateTimeOffset UtcNow = new(2026, 10, 23, 2, 0, 0, TimeSpan.Zero);

    private static readonly Regex RequestNoPattern = new(@"^AS\d{12}$");

    private const string FirstLine = "剛買的鞋尺碼不合適，想換大一碼";

    private readonly string fileRoot = Path.Combine(Path.GetTempPath(), "AgentRecordingTests-" + Guid.NewGuid().ToString("N"));
    private readonly AgentPhotoInbox inbox = new();
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

    private AgentRecordingService CreateRecordingService(IDbContextFactory<BackendDBContext>? contextFactory = null, TimeProvider? time = null)
        => new(contextFactory ?? factory, time ?? new ManualTimeProvider(UtcNow), NullLogger<AgentRecordingService>.Instance);

    private AfterSalesAgentRunner CreateRunner(DemoControlService demo, TimeProvider? time = null, AgentRecordingService? recordingService = null)
    {
        time ??= new ManualTimeProvider(UtcNow);
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
            new StubAiQuotaService(),
            audit,
            currentUser,
            NullLoggerFactory.Instance)
        {
            InnerClientFactory = (_, _) => model,
        };

        return new AfterSalesAgentRunner(
            chatFactory,
            new StubSystemPromptProvider(),
            new AgentSessionStore(factory, time, NullLogger<AgentSessionStore>.Instance),
            new AgentTraceService(factory, time, NullLogger<AgentTraceService>.Instance),
            orderService,
            policyService,
            requestService,
            new PermissionChecker(permissionContext, NullLogger<PermissionChecker>.Instance),
            inbox,
            demo,
            recordingService ?? CreateRecordingService(time: time),
            new ReplayCursorStore(),
            currentUser,
            new StaticOptionsMonitor<AfterSalesAgentSettings>(new AfterSalesAgentSettings()),
            time,
            NullLoggerFactory.Instance);
    }

    private DemoControlService LiveDemo() => TestDemoControl.Create(factory, enabled: true);

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

    /// <summary>旗艦劇本 A：問哪一雙 → 查規則、庫存 → 確認卡 → 確認後建立申請。共 6 次模型呼叫。</summary>
    private async Task<Guid> RunFlagshipAsync(AfterSalesAgentRunner runner)
    {
        var key = await runner.StartOrResumeAsync();
        model.Call("query_my_orders", []);
        model.Reply("您有兩雙鞋：黑色 40 碼、白色 41 碼。請問是黑 40 還是白 41？有穿過嗎？");
        await SendAsync(runner, key, FirstLine);

        model.Call("get_effective_policy", new() { ["orderItemId"] = black40, ["issueType"] = "size_exchange" });
        model.Call("check_variant_stock", new() { ["orderItemId"] = black40 });
        model.Call("create_after_sales_request", SizeExchangeArgs());
        var second = await SendAsync(runner, key, "黑色那雙，沒穿過");
        var card = Assert.IsType<ConfirmationCard>(second.Confirmation);

        model.Reply("已為您送出換尺碼申請。");
        var third = await ConfirmAsync(runner, key, card.ConfirmationId, approve: true);
        Assert.Matches(RequestNoPattern, third.CreatedRequestNo!);
        return key;
    }

    private async Task<int> RecordingIdAsync(Guid sessionKey)
    {
        await using var db = factory.CreateDbContext();
        return await db.AgentRecording.Where(x => x.SessionKey == sessionKey).Select(x => x.Id).SingleAsync();
    }

    private async Task<int> RecordingRowCountAsync()
    {
        await using var db = factory.CreateDbContext();
        return await db.AgentRecording.CountAsync() + await db.AgentRecordedTurn.CountAsync() + await db.AgentRecordedCall.CountAsync();
    }

    private static string ResultText(IReadOnlyList<ChatMessage> request)
        => string.Join("\n", request.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(r => r.Result?.ToString()));

    [Fact]
    public async Task Recording_LiveRun_StoresTurnsAndCallsInOrder()
    {
        var service = CreateRecordingService();
        var key = await RunFlagshipAsync(CreateRunner(LiveDemo(), recordingService: service));

        var id = await RecordingIdAsync(key);
        var detail = Assert.IsType<AgentRecordingDetail>(await service.GetAsync(id));
        Assert.Equal(key, detail.Recording.SessionKey);
        Assert.Equal(FirstLine, detail.Recording.Title);
        Assert.Equal("my-deployment", detail.Recording.ModelName);
        Assert.False(detail.Recording.IsGolden);
        Assert.Equal([1, 2, 3], detail.Turns.Select(x => x.Sequence));
        Assert.Equal(
            [AgentRecordedTurnKind.Send, AgentRecordedTurnKind.Send, AgentRecordedTurnKind.Confirm],
            detail.Turns.Select(x => x.Kind));
        Assert.Equal([FirstLine, "黑色那雙，沒穿過", null], detail.Turns.Select(x => x.CustomerText));

        // 每次模型呼叫一筆，順序與摘要都對得上模型實際收到的請求。
        var calls = await service.GetCallsAsync(id);
        Assert.Equal(6, model.CallCount);
        Assert.Equal(Enumerable.Range(1, 6), calls.Select(x => x.Sequence));
        Assert.Equal(model.Requests.Select(ModelRequestDigest.Compute), calls.Select(x => x.RequestDigest));
        Assert.All(calls, c => Assert.Matches("^[0-9a-f]{64}$", c.RequestDigest));

        // 回應可原樣讀回（Task 6 回放用）：函式呼叫保留 CallId、名稱與參數，文字合併成完整一句。
        var first = JsonSerializer.Deserialize<ChatResponse>(calls[0].ResponseJson, AIJsonUtilities.DefaultOptions)!;
        var firstCall = Assert.Single(first.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>());
        var sentCall = model.Requests[1].SelectMany(m => m.Contents).OfType<FunctionCallContent>().Single();
        Assert.Equal(sentCall.CallId, firstCall.CallId);
        Assert.Equal("query_my_orders", firstCall.Name);

        var policy = JsonSerializer.Deserialize<ChatResponse>(calls[2].ResponseJson, AIJsonUtilities.DefaultOptions)!;
        var policyCall = Assert.Single(policy.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>());
        Assert.Equal("get_effective_policy", policyCall.Name);
        Assert.Equal(
            JsonSerializer.Serialize(new Dictionary<string, object?> { ["orderItemId"] = black40, ["issueType"] = "size_exchange" }),
            JsonSerializer.Serialize(policyCall.Arguments));

        var last = JsonSerializer.Deserialize<ChatResponse>(calls[5].ResponseJson, AIJsonUtilities.DefaultOptions)!;
        Assert.Equal("已為您送出換尺碼申請。", last.Text);
    }

    [Fact]
    public async Task Recording_RejectAndPhotosUploaded_RecordTurnKinds()
    {
        var service = CreateRecordingService();
        var runner = CreateRunner(LiveDemo(), recordingService: service);
        var key = await runner.StartOrResumeAsync();
        model.Call("get_effective_policy", new() { ["orderItemId"] = black40, ["issueType"] = "size_exchange" });
        model.Call("create_after_sales_request", SizeExchangeArgs());
        var card = Assert.IsType<ConfirmationCard>((await SendAsync(runner, key, "黑色那雙 40 碼想換 41，沒穿過")).Confirmation);

        model.Reply("好的，已為您取消。");
        await ConfirmAsync(runner, key, card.ConfirmationId, approve: false);
        Assert.True(inbox.Add(key, "photo.jpg", new byte[8]));
        model.Reply("收到照片了。");
        await runner.NotifyPhotosUploadedAsync(key, _ => Task.CompletedTask, _ => Task.CompletedTask, CancellationToken.None);

        var detail = (await service.GetAsync(await RecordingIdAsync(key)))!;
        Assert.Equal(
            [AgentRecordedTurnKind.Send, AgentRecordedTurnKind.Reject, AgentRecordedTurnKind.PhotosUploaded],
            detail.Turns.Select(x => x.Kind));
        Assert.Equal("（已上傳 1 張照片）", detail.Turns[2].CustomerText);
        Assert.Equal("黑色那雙 40 碼想換 41，沒穿過", detail.Recording.Title);
    }

    [Fact]
    public async Task Recording_ReplayMode_DoesNotRecord()
    {
        var demo = LiveDemo();
        await demo.SetModeAsync(DemoMode.Replay, recordingId: 999);
        var runner = CreateRunner(demo);
        var key = await runner.StartOrResumeAsync();
        model.Reply("您好，請問需要什麼協助？");

        await SendAsync(runner, key, "你好");

        Assert.Equal(0, await RecordingRowCountAsync());
    }

    [Fact]
    public async Task Recording_DemoDisabled_DoesNotRecord()
    {
        var runner = CreateRunner(TestDemoControl.Off(factory));
        var key = await runner.StartOrResumeAsync();
        model.Reply("您好，請問需要什麼協助？");

        await SendAsync(runner, key, "你好");

        Assert.Equal(0, await RecordingRowCountAsync());
    }

    [Fact]
    public async Task Recording_StoreFailure_DoesNotAffectConversation()
    {
        var broken = new HookedDbContextFactory(factory)
        {
            BeforeCreateAsync = _ => throw new InvalidOperationException("recording store is down"),
        };
        var runner = CreateRunner(LiveDemo(), recordingService: CreateRecordingService(broken));
        var key = await runner.StartOrResumeAsync();
        model.Call("query_my_orders", []);
        model.Reply("您有兩雙鞋，請問是哪一雙？");

        var result = await SendAsync(runner, key, FirstLine);

        Assert.Equal(AgentStopReason.AwaitingUserReply, result.StopReason);
        Assert.Contains("請問是哪一雙？", result.ReplyText);
        Assert.Equal(0, await RecordingRowCountAsync());
    }

    [Fact]
    public async Task Recording_TotalCost_SumsUsageOfTheSession()
    {
        var service = CreateRecordingService();
        var key = Guid.NewGuid();
        var other = Guid.NewGuid();
        await using (var db = factory.CreateDbContext())
        {
            var mine1 = Guid.NewGuid();
            var mine2 = Guid.NewGuid();
            var notMine = Guid.NewGuid();
            db.AiCallLog.AddRange(
                new AiCallLog { CallId = mine1, ConversationId = key, Operation = "op", Provider = "p", Model = "m" },
                new AiCallLog { CallId = mine2, ConversationId = key, Operation = "op", Provider = "p", Model = "m" },
                new AiCallLog { CallId = notMine, ConversationId = other, Operation = "op", Provider = "p", Model = "m" });
            db.TokenUsageLog.AddRange(
                new TokenUsageLog { CallId = mine1, CostTwd = 1.25, Operation = "op", CallKind = "Chat", Provider = "p", Model = "m" },
                new TokenUsageLog { CallId = mine2, CostTwd = 0.5, Operation = "op", CallKind = "Chat", Provider = "p", Model = "m" },
                new TokenUsageLog { CallId = notMine, CostTwd = 9, Operation = "op", CallKind = "Chat", Provider = "p", Model = "m" });
            await db.SaveChangesAsync();
        }

        await service.AppendTurnAsync(key, customer1, AgentRecordedTurnKind.Send, "我想換尺碼");
        await service.AppendCallAsync(key, new string('a', 64), "{}", null, "gpt-test");

        var row = Assert.Single((await service.GetRecordingsAsync(new DataRequest())).Result);
        Assert.Equal(1.75m, row.TotalCostTwd);
        Assert.Equal("gpt-test", row.ModelName);
        Assert.Equal(1, row.TurnCount);
        Assert.Equal(1, row.CallCount);
    }

    [Fact]
    public async Task Digest_SameDataAfterReset_IsStable()
    {
        var service = CreateRecordingService();
        var firstKey = await RunFlagshipAsync(CreateRunner(LiveDemo(), new ManualTimeProvider(UtcNow), service));
        var firstRequests = model.Requests.ToList();

        // 5 天後重置：簽收、出貨、規則生效日都以重置當下重算，申請 Id／編號也不同；再跑一次同樣的劇本。
        var later = new ManualTimeProvider(UtcNow.AddDays(5));
        var systemSettings = new SystemSettings();
        systemSettings.ExternalFileSystem.AfterSalesFilePath = fileRoot;
        var reset = new DemoResetService(
            factory,
            new AfterSalesFileStore(Options.Create(systemSettings), NullLogger<AfterSalesFileStore>.Instance),
            Options.Create(new DemoSettings { Enabled = true, DemoUserPassword = "demo1234" }),
            later,
            new RecordingAuditLogService(),
            new CurrentUserService { CurrentUser = new CurrentUser { Id = 1, Account = "support" } },
            [inbox],
            NullLogger<DemoResetService>.Instance);
        Assert.True((await reset.ResetAsync()).Success);

        var secondKey = await RunFlagshipAsync(CreateRunner(LiveDemo(), later, service));
        var secondRequests = model.Requests.Skip(firstRequests.Count).ToList();

        Assert.NotEqual(firstKey, secondKey);
        // 工具結果的原文確實不同（規則生效日、申請編號與 Id），摘要仍相同——這就是正規化的用意。
        Assert.NotEqual(firstRequests.Select(ResultText), secondRequests.Select(ResultText));
        var firstCalls = await service.GetCallsAsync(await RecordingIdAsync(firstKey));
        var secondCalls = await service.GetCallsAsync(await RecordingIdAsync(secondKey));
        Assert.Equal(6, firstCalls.Count);
        Assert.Equal(firstCalls.Select(x => x.RequestDigest), secondCalls.Select(x => x.RequestDigest));
    }

    [Fact]
    public void Digest_IgnoresSystemInstructions()
    {
        static FunctionResultContent Result(string callId) => new(callId, JsonDocument.Parse("""{"status":"ok","data":{"in_stock":true}}""").RootElement);

        var digest = ModelRequestDigest.Compute(
        [
            new ChatMessage(ChatRole.System, "你是售後客服。今天是 2026/10/23。"),
            new ChatMessage(ChatRole.User, "還有 41 號嗎？"),
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-1", "check_variant_stock", new Dictionary<string, object?> { ["orderItemId"] = 1 })]),
            new ChatMessage(ChatRole.Tool, [Result("call-1")]),
            new ChatMessage(ChatRole.Assistant, "有的，41 號有貨。"),
        ]);
        var otherInstructionsAndModelOutput = ModelRequestDigest.Compute(
        [
            new ChatMessage(ChatRole.System, "完全不同的系統指示。"),
            new ChatMessage(ChatRole.User, "還有 41 號嗎？"),
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-xyz", "check_variant_stock", new Dictionary<string, object?> { ["orderItemId"] = 1 })]),
            new ChatMessage(ChatRole.Tool, [Result("call-xyz")]),
            new ChatMessage(ChatRole.Assistant, "模型這次說法不一樣。"),
        ]);
        var otherCustomerText = ModelRequestDigest.Compute(
        [
            new ChatMessage(ChatRole.User, "還有 42 號嗎？"),
            new ChatMessage(ChatRole.Tool, [Result("call-1")]),
        ]);

        Assert.Matches("^[0-9a-f]{64}$", digest);
        Assert.Equal(digest, otherInstructionsAndModelOutput);
        Assert.NotEqual(digest, otherCustomerText);
    }

    [Fact]
    public void Digest_NormalizesRequestNumbersAndDates()
    {
        static string Digest(string resultJson, string userText = "幫我查一下申請")
            => ModelRequestDigest.Compute(
            [
                new ChatMessage(ChatRole.User, userText),
                new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c", JsonDocument.Parse(resultJson).RootElement)]),
            ]);

        var baseline = Digest("""{"status":"ok","data":{"request_no":"AS202610230001","request_id":12,"effective_from":"2026-07-25T10:00:00","events":["2026/10/19 10:00 臺北倉儲中心：商品已出貨"],"in_stock":true}}""");
        var afterReset = Digest("""{"status":"ok","data":{"request_no":"AS202610280007","request_id":31,"effective_from":"2026-07-30T10:00:00.1234567","events":["2026/10/24 10:00 臺北倉儲中心：商品已出貨"],"in_stock":true}}""");
        var differentStock = Digest("""{"status":"ok","data":{"request_no":"AS202610230001","request_id":12,"effective_from":"2026-07-25T10:00:00","events":["2026/10/19 10:00 臺北倉儲中心：商品已出貨"],"in_stock":false}}""");

        Assert.Equal(baseline, afterReset);
        Assert.NotEqual(baseline, differentStock);
        // 顧客訊息裡的申請編號同樣正規化。
        Assert.Equal(Digest("{}", "AS202610230001 的進度？"), Digest("{}", "AS202610289999 的進度？"));
    }

    [Fact]
    public void Digest_SameJsonWithDifferentEscaping_IsSame()
    {
        // 還原的會話裡工具結果是重新解析的 JSON（中文可能被跳脫成 \uXXXX），當輪則是工具剛產生的 JsonElement 或字串。
        static string Digest(object? result) => ModelRequestDigest.Compute(
            [new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c", result)])]);

        Assert.Equal(
            Digest(JsonDocument.Parse("""{"color":"黑"}""").RootElement),
            Digest(JsonDocument.Parse("""{"color":"\u9ed1"}""").RootElement));
        Assert.Equal(Digest("找不到函式"), Digest(JsonSerializer.SerializeToElement("找不到函式")));
    }

    [Fact]
    public async Task SetGolden_OnePerScenario()
    {
        var service = CreateRecordingService();
        var keys = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        foreach (var key in keys)
        {
            await service.AppendTurnAsync(key, customer1, AgentRecordedTurnKind.Send, "我想換尺碼");
        }

        var a = await RecordingIdAsync(keys[0]);
        var b = await RecordingIdAsync(keys[1]);
        var c = await RecordingIdAsync(keys[2]);

        Assert.True(await service.SetGoldenAsync(a, "flagship-a"));
        Assert.True(await service.SetGoldenAsync(b, "flagship-a"));
        Assert.True(await service.SetGoldenAsync(c, "flagship-b"));
        Assert.False(await service.SetGoldenAsync(9999, "flagship-a"));

        Assert.Equal(b, (await service.GetGoldenAsync("flagship-a"))!.Id);
        Assert.Equal(c, (await service.GetGoldenAsync("flagship-b"))!.Id);
        var demoted = (await service.GetAsync(a))!.Recording;
        Assert.False(demoted.IsGolden);
        Assert.Equal("flagship-a", demoted.ScenarioCode);

        Assert.True(await service.DeleteAsync(b));
        Assert.Null(await service.GetGoldenAsync("flagship-a"));
        Assert.Null(await service.GetAsync(b));
        Assert.Empty(await service.GetCallsAsync(b));
        Assert.False(await service.DeleteAsync(b));
    }

    [Fact]
    public async Task RecordedJson_NeverContainsAddressOrPhone()
    {
        string[] personal;
        await using (var db = factory.CreateDbContext())
        {
            personal = (await db.SalesOrder.Where(x => x.CustomerUserId == customer1).ToListAsync())
                .SelectMany(x => new[] { x.RecipientName, x.ShippingAddress, x.RecipientPhone })
                .Distinct()
                .ToArray();
        }

        Assert.NotEmpty(personal);
        var service = CreateRecordingService();
        var key = await RunFlagshipAsync(CreateRunner(LiveDemo(), recordingService: service));

        var id = await RecordingIdAsync(key);
        var calls = await service.GetCallsAsync(id);
        var detail = (await service.GetAsync(id))!;
        Assert.NotEmpty(calls);
        var stored = calls.SelectMany(c => new[] { c.ResponseJson, c.UsageJson ?? string.Empty })
            .Concat(detail.Turns.Select(t => t.CustomerText ?? string.Empty))
            .Append(detail.Recording.Title);
        Assert.All(stored, text => Assert.All(personal, value => Assert.DoesNotContain(value, text, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task RecordingClient_MergeFailureAfterStream_DoesNotFailTheCall()
    {
        // 串流全部取完後才合併（ToChatResponse）並寫入錄製：合併失敗也只能記 Warning，不可讓已計費的即時回合失敗。
        var update = new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = new UnreadableContents() };
        var client = new RecordingChatClient(
            new SingleUpdateChatClient(update), Guid.NewGuid(), "my-deployment", CreateRecordingService(), NullLogger.Instance);

        var received = new List<ChatResponseUpdate>();
        await foreach (var item in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "你好")]))
        {
            received.Add(item);
        }

        Assert.Same(update, Assert.Single(received));
        Assert.Equal(0, await RecordingRowCountAsync());
    }

    /// <summary>串流只送出一段（不讀它的內容）。</summary>
    private sealed class SingleUpdateChatClient(ChatResponseUpdate update) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return update;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>一讀就拋例外的內容清單：讓 <c>ToChatResponse</c> 合併時失敗。</summary>
    private sealed class UnreadableContents : IList<AIContent>
    {
        public int Count => throw Unreadable();

        public bool IsReadOnly => false;

        public AIContent this[int index]
        {
            get => throw Unreadable();
            set => throw Unreadable();
        }

        public IEnumerator<AIContent> GetEnumerator() => throw Unreadable();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => throw Unreadable();

        public int IndexOf(AIContent item) => throw Unreadable();

        public void Insert(int index, AIContent item) => throw Unreadable();

        public void RemoveAt(int index) => throw Unreadable();

        public void Add(AIContent item) => throw Unreadable();

        public void Clear() => throw Unreadable();

        public bool Contains(AIContent item) => throw Unreadable();

        public void CopyTo(AIContent[] array, int arrayIndex) => throw Unreadable();

        public bool Remove(AIContent item) => throw Unreadable();

        private static InvalidOperationException Unreadable() => new("contents are unreadable");
    }
}

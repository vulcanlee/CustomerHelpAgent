using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Web.Agents.AfterSales;
using CustomerHelpAgent.Web.Agents.PreReview;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// MAF 1.25 Handoff 編排與 <see cref="AIContextProvider"/> 的框架行為測試（M5 試跑，長期保留）。
/// ⚠️ 升級 Microsoft.Agents.AI／Workflows 套件時務必重跑：這裡釘住的是 M5 多 Agent 設計所依賴的「框架實際行為」，
/// 有些斷言刻意釘住與官方文件不同的實際行為（見各測試說明），升級後若改變要重新評估 Runner 的寫法。
/// 拓樸比照 M5 規劃：分流 → 兩位專員（物流、換退貨）、專員 → 分流；所有參與者共用同一條模型鏈（假模型 → UseFunctionInvocation）。
/// Runner 必須遵守的三件事（由下列測試證明）：
/// <list type="number">
/// <item>核准請求取 <c>RawRepresentation is RequestInfoEvent</c> 的那一份（外部 RequestId）；回覆前先 <c>WorkflowSessionCheckpointRecovery.TryPrepare()</c>。</item>
/// <item>確認卡等待中絕不直接送一般訊息：先送拒絕（同樣 TryPrepare），再送新訊息。</item>
/// <item>模型鏈例外不會拋出，而是 ErrorContent update；出現錯誤的那一輪不可存回 session（改用該輪之前的 JSON，或 TryPrepare 以空訊息續跑）。</item>
/// </list>
/// </summary>
[Collection(nameof(HeavyWorkflowCollection))]
public sealed class MafHandoffBehaviorTests : IAsyncLifetime
{
    private const string TriageInstructions = "你是分流 Agent。";
    private const string LogisticsInstructions = "你是物流專員。";
    private const string ReturnsInstructions = "你是換退貨專員。";
    private const string LogisticsDescription = "物流專員：查詢出貨與配送進度";
    private const string ReturnsDescription = "換退貨專員：處理換貨與退貨申請";
    private const string TriageDescription = "分流 Agent：判斷顧客需求並轉給專員";

    private readonly ITestOutputHelper output;
    private SqliteConnection connection = null!;
    private TestDbContextFactory dbFactory = null!;

    public MafHandoffBehaviorTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    public async Task InitializeAsync()
    {
        connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        dbFactory = new TestDbContextFactory(connection);
        await using var db = dbFactory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync() => await connection.DisposeAsync();

    /// <summary>M4 的 SQLite checkpoint 庫；同一個連線＝同一個資料庫，模擬應用程式重啟後仍讀得到。</summary>
    private CheckpointManager NewCheckpointManager()
        => CheckpointManager.CreateJson(new SqliteWorkflowCheckpointStore(
            new WorkflowCheckpointService(dbFactory, TimeProvider.System, NullLogger<WorkflowCheckpointService>.Instance)));

    // ───────────────────────── 1. AuthorName 與 handoff_to_N ─────────────────────────

    /// <summary>
    /// 包成 Agent（<c>AsAIAgent</c>）後：update 的 <c>AuthorName</c>／<c>AgentId</c> 分辨分流與專員；
    /// <c>handoff_to_N</c> 的 FunctionCallContent 會出現在 update 裡（作者是分流），N 依「該來源 Agent 的目標註冊順序」從 1 起，
    /// 工具宣告的 Description＝目標 Agent 的 Description，可據以對應目標。
    /// </summary>
    [Fact]
    public async Task Handoff_Wrapped_StreamsAuthorNamePerAgent()
    {
        var harness = new HandoffHarness();
        harness.Model.Call("handoff_to_2", new() { ["reasonForHandoff"] = "顧客要換貨" });
        harness.Model.Reply("可以為您辦理換貨");
        var agent = harness.Build(NewCheckpointManager(), returnToPrevious: false);
        var session = await agent.CreateSessionAsync();

        var updates = await RunTurnAsync(agent, session, new ChatMessage(ChatRole.User, "我要換貨"));

        // 分流收到的工具：自己的工具＋handoff_to_1（物流）、handoff_to_2（換退貨），描述＝目標的 Description。
        var triageCall = harness.Model.Calls[0];
        Assert.StartsWith(TriageInstructions, triageCall.Instructions);
        Assert.Contains(("handoff_to_1", LogisticsDescription), triageCall.Tools);
        Assert.Contains(("handoff_to_2", ReturnsDescription), triageCall.Tools);
        Assert.Contains(triageCall.Tools, t => t.Name == "query_my_orders");
        // 專員的交接目標只有分流（handoff_to_1），描述＝分流的 Description。
        var returnsCall = harness.Model.Calls[1];
        Assert.StartsWith(ReturnsInstructions, returnsCall.Instructions);
        Assert.Contains(("handoff_to_1", TriageDescription), returnsCall.Tools);
        Assert.Equal(2, harness.Model.Calls.Count);

        // update：交接呼叫由分流發出且看得到；"Transferred." 工具結果也以分流名義出現；專員的文字作者是專員。
        var handoffCall = updates.Single(u => u.Contents.OfType<FunctionCallContent>().Any(c => c.Name == "handoff_to_2"));
        Assert.Equal("triage", handoffCall.AuthorName);
        Assert.Equal("triage", handoffCall.AgentId);
        var transferred = updates.Single(u => u.Contents.OfType<FunctionResultContent>().Any());
        Assert.Equal("triage", transferred.AuthorName);
        Assert.Equal(ChatRole.Tool, transferred.Role);
        Assert.Equal("Transferred.", transferred.Contents.OfType<FunctionResultContent>().Single().Result);
        var textUpdates = updates.Where(u => !string.IsNullOrEmpty(u.Text)).ToList();
        Assert.NotEmpty(textUpdates);
        Assert.All(textUpdates, u => Assert.Equal("returns", u.AuthorName));
        Assert.All(textUpdates, u => Assert.Equal("returns", u.AgentId));
        Assert.Equal("可以為您辦理換貨", string.Concat(textUpdates.Select(u => u.Text)));
        Assert.DoesNotContain(updates, u => u.Contents.OfType<ErrorContent>().Any());

        // 交接呼叫的名稱 → 目標：以請求中的工具描述對應（Runner 也可依註冊順序自行對應）。
        var target = triageCall.Tools.Single(t => t.Name == "handoff_to_2").Description;
        Assert.Equal(ReturnsDescription, target);
    }

    // ───────────────────────── 2. 參與者上的函式中介層 ─────────────────────────

    /// <summary>
    /// 參與者上的 <c>AsBuilder().Use(guard)</c> 會執行，<c>context.Terminate=true</c> 結束該輪（不再呼叫模型）。
    /// 交接呼叫 <c>handoff_to_N</c> 只是工具宣告，不會經過 Guard。同一個 Guard 實例掛在所有參與者上，
    /// 中介層收到的 <c>agent</c> 參數就是發出呼叫的參與者。
    /// </summary>
    [Fact]
    public async Task Handoff_ParticipantMiddleware_RunsAndTerminates()
    {
        var harness = new HandoffHarness { UseGuard = true, TerminateWhen = ctx => ctx.Function.Name == "track_shipment" };
        harness.Model.Call("handoff_to_1", new() { ["reasonForHandoff"] = "查物流" });
        harness.Model.Call("track_shipment", new() { ["orderNo"] = "SO1001" });
        var agent = harness.Build(NewCheckpointManager(), returnToPrevious: true);
        var session = await agent.CreateSessionAsync();

        var updates = await RunTurnAsync(agent, session, new ChatMessage(ChatRole.User, "我的包裹到哪了"));

        Assert.Equal(["logistics:track_shipment"], harness.GuardLog);
        Assert.Equal(["track_shipment"], harness.ToolLog);
        // 終止後不會再呼叫模型（腳本只有兩筆，再呼叫就會拋例外變成 ErrorContent）。
        Assert.Equal(2, harness.Model.Calls.Count);
        Assert.DoesNotContain(updates, u => u.Contents.OfType<ErrorContent>().Any());
        var result = updates.SelectMany(u => u.Contents).OfType<FunctionResultContent>()
            .Single(r => r.Result?.ToString()?.Contains("已出貨") == true);
        Assert.NotNull(result);

        // 終止的那一輪結束後，會話仍可繼續：下一輪（EnableReturnToPrevious）交給物流專員。
        harness.Model.Reply("預計明天送達");
        var next = await RunTurnAsync(agent, session, new ChatMessage(ChatRole.User, "什麼時候到"));
        Assert.Equal(3, harness.Model.Calls.Count);
        Assert.StartsWith(LogisticsInstructions, harness.Model.Calls[2].Instructions);
        Assert.Equal("預計明天送達", string.Concat(next.Where(u => u.AuthorName == "logistics").Select(u => u.Text)));
    }

    // ───────────────────────── 3. 確認關卡與重啟續跑 ─────────────────────────

    /// <summary>
    /// 專員的 <see cref="ApprovalRequiredAIFunction"/> 以 <see cref="ToolApprovalRequestContent"/> 出現在 update（外部 RequestId）；
    /// session 序列化 → 丟掉所有 Agent → 以相同 id 重建（checkpoint 在 SQLite）→ 反序列化 → 送 <see cref="ToolApprovalResponseContent"/>，
    /// 工具執行一次，由同一位專員接續回覆；之後的下一輪也正常。
    /// ⚠️ 送核准回覆前必須先 <c>session.GetService&lt;WorkflowSessionCheckpointRecovery&gt;().TryPrepare()</c>（MAAI001 實驗性 API），
    /// 否則包裝層會多送一個 TurnToken 給 HandoffStart，與專員在同一個 superstep 同時更新共享狀態而失敗
    /// （見 <see cref="Handoff_ApprovalResponse_WithoutRecoveryPrepare_FailsWithSharedStateConflict"/>）。
    /// </summary>
    [Fact]
    public async Task Handoff_ApprovalSurfacesAndResumesAfterSessionRoundTrip()
    {
        var (stateJson, cardJson) = await StartApprovalAsync();

        var after = new HandoffHarness();
        after.Model.Reply("已為您建立換貨申請");
        after.Model.Reply("申請編號已寄到您的信箱");
        var rebuilt = after.Build(NewCheckpointManager(), returnToPrevious: true);
        var restored = await rebuilt.DeserializeSessionAsync(JsonSerializer.Deserialize<JsonElement>(stateJson));
        var savedApproval = Assert.IsType<ToolApprovalRequestContent>(JsonSerializer.Deserialize<AIContent>(cardJson, AIJsonUtilities.DefaultOptions));

#pragma warning disable MAAI001 // 實驗性 API：只續跑 checkpoint 中排隊的工作，不開新的一輪（不送 TurnToken）。
        Assert.True(restored.GetService<WorkflowSessionCheckpointRecovery>()!.TryPrepare());
#pragma warning restore MAAI001
        var resumed = await RunTurnAsync(rebuilt, restored, new ChatMessage(ChatRole.User, [savedApproval.CreateResponse(true)]));

        Assert.Equal(["create_request"], after.ToolLog);
        Assert.DoesNotContain(resumed, u => u.Contents.OfType<ErrorContent>().Any());
        Assert.Single(after.Model.Calls);
        Assert.StartsWith(ReturnsInstructions, after.Model.Calls[0].Instructions);
        Assert.Equal("已為您建立換貨申請", string.Concat(resumed.Where(u => u.AuthorName == "returns").Select(u => u.Text)));
        output.WriteLine("resumed request messages: " + Describe(after.Model.Calls[0].Messages));

        // 再一輪（再序列化、重建一次）：EnableReturnToPrevious → 換退貨專員直接接手，工具不會重複執行。
        var stateJson2 = (await rebuilt.SerializeSessionAsync(restored)).GetRawText();
        var rebuilt2 = after.Build(NewCheckpointManager(), returnToPrevious: true);
        var restored2 = await rebuilt2.DeserializeSessionAsync(JsonSerializer.Deserialize<JsonElement>(stateJson2));
        var next = await RunTurnAsync(rebuilt2, restored2, new ChatMessage(ChatRole.User, "謝謝"));
        Assert.DoesNotContain(next, u => u.Contents.OfType<ErrorContent>().Any());
        Assert.Equal(2, after.Model.Calls.Count);
        Assert.StartsWith(ReturnsInstructions, after.Model.Calls[1].Instructions);
        Assert.Equal(["create_request"], after.ToolLog);
    }

    /// <summary>
    /// 釘住 1.25 缺陷：不先 <c>TryPrepare()</c> 就送核准回覆時，包裝層（WorkflowSession）因回覆不是給起點 executor 而另送 TurnToken，
    /// HandoffStart 與專員在同一個 superstep 都更新 "SharedState" → <c>WorkflowErrorEvent</c>「Expected exactly one update for key 'SharedState'.」。
    /// 工具與專員回覆其實已執行；升級後若此測試失敗，代表框架已修正，可重新評估是否還需要 TryPrepare。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Handoff_ApprovalResponse_WithoutRecoveryPrepare_FailsWithSharedStateConflict(bool restart)
    {
        var harness = new HandoffHarness();
        harness.Model.Call("handoff_to_2", new() { ["reasonForHandoff"] = "顧客要換貨" });
        harness.Model.Call("create_request", new() { ["orderNo"] = "SO1001", ["reason"] = "尺寸不合" });
        harness.Model.Reply("已為您建立換貨申請");
        harness.Model.Reply("下一輪回覆");
        var agent = harness.Build(NewCheckpointManager(), returnToPrevious: true);
        var session = await agent.CreateSessionAsync();
        var updates = await RunTurnAsync(agent, session, new ChatMessage(ChatRole.User, "鞋子太小想換大一號"));
        var approval = ExternalApprovalOf(updates);
        if (restart)
        {
            var stateJson = (await agent.SerializeSessionAsync(session)).GetRawText();
            agent = harness.Build(NewCheckpointManager(), returnToPrevious: true);
            session = await agent.DeserializeSessionAsync(JsonSerializer.Deserialize<JsonElement>(stateJson));
        }

        var resumed = await RunTurnAsync(agent, session, new ChatMessage(ChatRole.User, [approval.CreateResponse(true)]));

        Assert.Equal(["create_request"], harness.ToolLog);
        Assert.Equal("已為您建立換貨申請", string.Concat(resumed.Where(u => u.AuthorName == "returns").Select(u => u.Text)));
        Assert.Equal(["Expected exactly one update for key 'SharedState'."], ErrorsOf(resumed));
        Assert.IsType<WorkflowErrorEvent>(resumed.Single(u => u.Contents.OfType<ErrorContent>().Any()).RawRepresentation);

        // 失敗之後的下一輪：只記錄、不斷言（實測同一行程內 15/15 壞掉、重建後 12/15 壞掉——競態，見報告）。
        var next = await RunTurnAsync(agent, session, new ChatMessage(ChatRole.User, "謝謝"));
        output.WriteLine($"model calls={harness.Model.Calls.Count} tools=[{string.Join(",", harness.ToolLog)}]");
        output.WriteLine("next-turn errors: " + string.Join(" / ", ErrorsOf(next)));
        if (harness.Model.Calls.Count > 3)
        {
            output.WriteLine("next-turn request: [" + harness.Model.Calls[3].Instructions?.Split('\n')[0] + "] " + Describe(harness.Model.Calls[3].Messages));
        }
    }

    /// <summary>第一輪：分流 → 換退貨專員 → 提出 create_request（待核准）。回傳 session JSON 與 JSON 往返的外部核准請求（模擬確認卡）。</summary>
    private async Task<(string StateJson, string CardJson)> StartApprovalAsync()
    {
        var before = new HandoffHarness();
        before.Model.Call("handoff_to_2", new() { ["reasonForHandoff"] = "顧客要換貨" });
        before.Model.Call("create_request", new() { ["orderNo"] = "SO1001", ["reason"] = "尺寸不合" });
        var agent = before.Build(NewCheckpointManager(), returnToPrevious: true);
        var session = await agent.CreateSessionAsync();

        var updates = await RunTurnAsync(agent, session, new ChatMessage(ChatRole.User, "鞋子太小想換大一號"));

        // ⚠️ 同一個核准請求在 update 裡出現兩次：
        // (1) 專員自己的串流（作者＝returns，RequestId＝FunctionInvokingChatClient 的內部 id "ficc_{CallId}"）——不能拿來回覆；
        // (2) RequestInfoEvent 轉成的 update（作者為 null，RawRepresentation 是 RequestInfoEvent，RequestId＝外部 id）——要用這個。
        var approvalUpdates = updates.Where(u => u.Contents.OfType<ToolApprovalRequestContent>().Any()).ToList();
        Assert.Equal(2, approvalUpdates.Count);
        var inner = approvalUpdates[0];
        Assert.Equal("returns", inner.AuthorName);
        Assert.Equal("returns", inner.AgentId);
        Assert.IsType<ChatResponseUpdate>(inner.RawRepresentation);
        Assert.Equal("ficc_call-2", inner.Contents.OfType<ToolApprovalRequestContent>().Single().RequestId);
        var external = approvalUpdates[1];
        Assert.Null(external.AuthorName);
        Assert.Null(external.AgentId);
        var requestInfo = Assert.IsType<RequestInfoEvent>(external.RawRepresentation);
        var approval = external.Contents.OfType<ToolApprovalRequestContent>().Single();
        const string PortId = "returns_returns_UserInput";
        Assert.Equal($"{PortId.Length}:{PortId}:ficc_call-2", approval.RequestId);
        Assert.Equal(approval.RequestId, requestInfo.Request.RequestId);
        var call = Assert.IsType<FunctionCallContent>(approval.ToolCall);
        Assert.Equal("create_request", call.Name);
        Assert.Equal("call-2", call.CallId);
        Assert.Empty(before.ToolLog);
        Assert.Equal(2, before.Model.Calls.Count);

        // 模擬重啟：只留下 session JSON、卡片資料（JSON 往返的核准請求）與 SQLite。
        var stateJson = (await agent.SerializeSessionAsync(session)).GetRawText();
        output.WriteLine($"pending-approval session json length={stateJson.Length}: {stateJson}");
        return (stateJson, JsonSerializer.Serialize<AIContent>(approval, AIJsonUtilities.DefaultOptions));
    }

    /// <summary>
    /// ⚠️ 釘住 1.25 行為：確認卡等待中，若直接送一般訊息（顧客不按確認、改打字），整個 Handoff 會話從此每一輪都失敗、模型完全不會被呼叫。
    /// <list type="bullet">
    /// <item>核准請求出現在第一輪（上一位發言者尚未記錄）→ 新訊息交給分流，分流在共享對話裡看到沒有回覆的核准請求 →
    /// 「ToolApprovalRequestContent found with FunctionCall.CallId(s) 'call-2' that have no matching ToolApprovalResponseContent.」</item>
    /// <item>核准請求出現在之後的輪次（上一位發言者＝換退貨專員）→ 新訊息交給仍在「進行中」的專員 →
    /// 「Cannot have multiple simultaneous conversations in Handoff Orchestration.」</item>
    /// </list>
    /// 單一 Agent 模式的「下一輪自動拒絕」在這裡不會發生。Runner 必須先送拒絕（見 <see cref="Handoff_RejectPendingApprovalThenNewMessage_Recovers"/>）。
    /// </summary>
    [Theory]
    [InlineData(false, "ToolApprovalRequestContent found with FunctionCall.CallId(s) 'call-2' that have no matching ToolApprovalResponseContent.")]
    [InlineData(true, "Cannot have multiple simultaneous conversations in Handoff Orchestration.")]
    public async Task Handoff_NewMessageWhileApprovalPending_BreaksSession(bool approvalInSecondTurn, string expectedError)
    {
        var harness = new HandoffHarness();
        harness.Model.Call("handoff_to_2", new() { ["reasonForHandoff"] = "顧客要換貨" });
        if (approvalInSecondTurn)
        {
            harness.Model.Reply("請問訂單編號？");
        }

        harness.Model.Call("create_request", new() { ["orderNo"] = "SO1001", ["reason"] = "尺寸不合" });
        harness.Model.Reply("不應被呼叫");
        var agent = harness.Build(NewCheckpointManager(), returnToPrevious: true);
        var session = await agent.CreateSessionAsync();
        await RunTurnAsync(agent, session, new ChatMessage(ChatRole.User, "鞋子太小想換大一號"));
        if (approvalInSecondTurn)
        {
            await RunTurnAsync(agent, session, new ChatMessage(ChatRole.User, "SO1001"));
        }

        var calls = harness.Model.Calls.Count;
        var abandon = await RunTurnAsync(agent, session, new ChatMessage(ChatRole.User, "算了，我先不換"));
        var after = await RunTurnAsync(agent, session, new ChatMessage(ChatRole.User, "謝謝"));

        Assert.Contains(expectedError, ErrorsOf(abandon));
        Assert.Contains(expectedError, ErrorsOf(after));
        Assert.Equal(calls, harness.Model.Calls.Count);
        Assert.Empty(harness.ToolLog);
    }

    /// <summary>
    /// 放棄確認卡的補救：先 <c>TryPrepare()</c> 送「拒絕」（<c>CreateResponse(false, 理由)</c>）結束待決的請求——
    /// 工具不執行，專員收到「Tool call invocation rejected. {理由}」的工具結果並再呼叫一次模型（多一次模型呼叫）；之後一般訊息正常。
    /// </summary>
    [Fact]
    public async Task Handoff_RejectPendingApprovalThenNewMessage_Recovers()
    {
        var harness = new HandoffHarness();
        harness.Model.Call("handoff_to_2", new() { ["reasonForHandoff"] = "顧客要換貨" });
        harness.Model.Call("create_request", new() { ["orderNo"] = "SO1001", ["reason"] = "尺寸不合" });
        harness.Model.Reply("好的，不建立申請");
        harness.Model.Reply("了解，還有其他需要嗎");
        var agent = harness.Build(NewCheckpointManager(), returnToPrevious: true);
        var session = await agent.CreateSessionAsync();
        var first = await RunTurnAsync(agent, session, new ChatMessage(ChatRole.User, "鞋子太小想換大一號"));
        var approval = ExternalApprovalOf(first);

#pragma warning disable MAAI001
        Assert.True(session.GetService<WorkflowSessionCheckpointRecovery>()!.TryPrepare());
#pragma warning restore MAAI001
        var reject = await RunTurnAsync(agent, session, new ChatMessage(ChatRole.User, [approval.CreateResponse(false, "顧客未確認")]));

        Assert.Empty(ErrorsOf(reject));
        Assert.Empty(harness.ToolLog);
        Assert.Equal(3, harness.Model.Calls.Count);
        Assert.StartsWith(ReturnsInstructions, harness.Model.Calls[2].Instructions);
        var rejected = harness.Model.Calls[2].Messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single();
        Assert.Equal("call-2", rejected.CallId);
        Assert.Equal("Tool call invocation rejected. 顧客未確認", rejected.Result?.ToString());
        Assert.Equal("好的，不建立申請", string.Concat(reject.Where(u => u.AuthorName == "returns").Select(u => u.Text)));

        var next = await RunTurnAsync(agent, session, new ChatMessage(ChatRole.User, "算了，我先不換"));
        Assert.Empty(ErrorsOf(next));
        Assert.Equal(4, harness.Model.Calls.Count);
        Assert.StartsWith(ReturnsInstructions, harness.Model.Calls[3].Instructions);
        Assert.Empty(harness.ToolLog);
    }

    /// <summary>
    /// 核准完成後，之後由分流接手（下一輪從分流開始，或專員交回分流）：分流收到的訊息不含核准請求／回覆，
    /// 只看得到專員的 create_request 呼叫與結果（HandoffOnly 過濾只去掉交接呼叫）；不會出錯、工具不重複執行。
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handoff_AfterApproval_OtherAgentsSeeNoApprovalContent(bool viaSpecialistHandBack)
    {
        var harness = new HandoffHarness();
        harness.Model.Call("handoff_to_2", new() { ["reasonForHandoff"] = "顧客要換貨" });
        harness.Model.Call("create_request", new() { ["orderNo"] = "SO1001", ["reason"] = "尺寸不合" });
        harness.Model.Reply("已建立");
        if (viaSpecialistHandBack)
        {
            harness.Model.Call("handoff_to_1", new() { ["reasonForHandoff"] = "顧客改問物流" });
        }

        harness.Model.Reply("我來協助物流問題");
        var agent = harness.Build(NewCheckpointManager(), returnToPrevious: viaSpecialistHandBack);
        var session = await agent.CreateSessionAsync();
        var first = await RunTurnAsync(agent, session, new ChatMessage(ChatRole.User, "鞋子太小想換大一號"));
#pragma warning disable MAAI001
        session.GetService<WorkflowSessionCheckpointRecovery>()!.TryPrepare();
#pragma warning restore MAAI001
        await RunTurnAsync(agent, session, new ChatMessage(ChatRole.User, [ExternalApprovalOf(first).CreateResponse(true)]));

        var next = await RunTurnAsync(agent, session, new ChatMessage(ChatRole.User, "另外我的包裹到哪了"));

        Assert.Empty(ErrorsOf(next));
        Assert.Equal(["create_request"], harness.ToolLog);
        var triageCall = harness.Model.Calls[^1];
        Assert.StartsWith(TriageInstructions, triageCall.Instructions);
        var contents = triageCall.Messages.SelectMany(m => m.Contents).ToList();
        Assert.DoesNotContain(contents, c => c is ToolApprovalRequestContent or ToolApprovalResponseContent);
        Assert.Contains(contents, c => c is FunctionCallContent { Name: "create_request" });
        Assert.Contains(contents, c => c is FunctionResultContent { CallId: "call-2" });
        Assert.Equal("我來協助物流問題", string.Concat(next.Where(u => u.AuthorName == "triage").Select(u => u.Text)));
    }

    /// <summary>
    /// ⚠️ 釘住 1.25（＋Microsoft.Extensions.AI 10.10）的缺陷，M5 實機劇本 H 的 HTTP 400 就是它：核准（或拒絕）後，專員的
    /// <c>FunctionInvokingChatClient</c> 以「從歷史載入的原始呼叫訊息」<c>Clone()</c> 重新送出呼叫訊息（<c>InformationalOnly</c>），
    /// 連同 <c>AdditionalProperties["_attribution"]</c>＝<see cref="AgentRequestMessageSourceType.ChatHistory"/> 一起帶出；Handoff 原封不動轉給下一位參與者。
    /// 對方第一次收到時看得到呼叫（分流第一次呼叫），但存進自己的 <c>InMemoryChatHistoryProvider</c> 時，預設過濾器把「來自歷史」的請求訊息丟掉，
    /// 結果訊息沒有標記而留下——同一位參與者<b>第二次</b>被呼叫（換退貨 → 分流 → 物流 → 分流）時只剩結果、沒有呼叫（Responses API 回 400）。
    /// 與 <c>HandoffToolCallFilteringBehavior</c> 無關（HandoffOnly 不動一般工具呼叫）。產品端的修正見
    /// <see cref="Handoff_AfterDecision_ScrubbedParticipants_KeepToolCallsPaired"/>；升級後若此測試失敗，代表框架已修正，可重新評估是否還需要。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Handoff_AfterDecision_ForwardedCallTaggedAsHistory_SecondVisitSeesOrphanResult(bool approve)
    {
        var harness = new HandoffHarness();
        harness.Model.StrictToolPairing = false;
        var calls = await RunDecisionThenRoundTripAsync(harness, approve);

        // #3：分流第一次被呼叫——呼叫與結果都在，但呼叫訊息帶著「來自歷史」的標記。
        Assert.StartsWith(TriageInstructions, calls[3].Instructions);
        var forwarded = calls[3].Messages.Single(m => m.Contents.OfType<FunctionCallContent>().Any(c => c.CallId == "call-2"));
        Assert.Equal(AgentRequestMessageSourceType.ChatHistory, forwarded.GetAgentRequestMessageSourceType());
        Assert.True(forwarded.Contents.OfType<FunctionCallContent>().Single().InformationalOnly);
        ResponsesApiPairing.EnsurePaired(calls[3].Messages);

        // #5：分流第二次被呼叫——呼叫不見了，只剩結果。
        Assert.StartsWith(TriageInstructions, calls[5].Instructions);
        var contents = calls[5].Messages.SelectMany(m => m.Contents).ToList();
        Assert.DoesNotContain(contents, c => c is FunctionCallContent { CallId: "call-2" });
        Assert.Contains(contents, c => c is FunctionResultContent { CallId: "call-2" });
        var error = Assert.Throws<InvalidOperationException>(() => ResponsesApiPairing.EnsurePaired(calls[5].Messages));
        Assert.Contains("call_id call-2", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 產品端的修正（<see cref="HandoffTopologyFactory.ScrubHistoryAttribution"/>）：參與者送出的 update 拿掉 <c>_attribution</c> 標記，
    /// 轉給其他參與者的訊息就是一般輸入、會被存進對方的歷史——分流第二次被呼叫時呼叫與結果仍成對（假模型照 Responses API 檢查）。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Handoff_AfterDecision_ScrubbedParticipants_KeepToolCallsPaired(bool approve)
    {
        var harness = new HandoffHarness { ScrubAttribution = true };
        var calls = await RunDecisionThenRoundTripAsync(harness, approve);

        Assert.StartsWith(TriageInstructions, calls[5].Instructions);
        var forwarded = calls[3].Messages.Single(m => m.Contents.OfType<FunctionCallContent>().Any(c => c.CallId == "call-2"));
        Assert.Equal(AgentRequestMessageSourceType.External, forwarded.GetAgentRequestMessageSourceType());
        var contents = calls[5].Messages.SelectMany(m => m.Contents).ToList();
        Assert.Contains(contents, c => c is FunctionCallContent { CallId: "call-2" });
        Assert.Contains(contents, c => c is FunctionResultContent { CallId: "call-2" });
    }

    /// <summary>
    /// 第一輪分流 → 換退貨專員提出 create_request（待核准）；第二輪送核准／拒絕，同一輪換退貨 → 分流 → 物流 → 分流。
    /// 回傳全部模型呼叫（#0 分流、#1 換退貨、#2 換退貨、#3 分流、#4 物流、#5 分流）。
    /// </summary>
    private async Task<List<HandoffModelCall>> RunDecisionThenRoundTripAsync(HandoffHarness harness, bool approve)
    {
        harness.Model.Call("handoff_to_2", new() { ["reasonForHandoff"] = "顧客要換貨" });
        harness.Model.Call("create_request", new() { ["orderNo"] = "SO1001", ["reason"] = "尺寸不合" });
        harness.Model.Call("handoff_to_1", new() { ["reasonForHandoff"] = "換貨處理完，顧客另問物流" });
        harness.Model.Call("handoff_to_1", new() { ["reasonForHandoff"] = "查包裹" });
        harness.Model.Call("handoff_to_1", new() { ["reasonForHandoff"] = "交回分流" });
        harness.Model.Reply("我來協助其他問題");
        var agent = harness.Build(NewCheckpointManager(), returnToPrevious: true);
        var session = await agent.CreateSessionAsync();
        var first = await RunTurnAsync(agent, session, new ChatMessage(ChatRole.User, "鞋子太小想換大一號，另外包裹到哪了"));
#pragma warning disable MAAI001
        Assert.True(session.GetService<WorkflowSessionCheckpointRecovery>()!.TryPrepare());
#pragma warning restore MAAI001

        var decided = await RunTurnAsync(agent, session, new ChatMessage(ChatRole.User, [ExternalApprovalOf(first).CreateResponse(approve, "顧客取消")]));

        Assert.Equal(6, harness.Model.Calls.Count);
        Assert.Equal(approve ? ["create_request"] : [], harness.ToolLog);
        output.WriteLine($"scrub={harness.ScrubAttribution} triage second visit: " + Describe(harness.Model.Calls[5].Messages));
        if (harness.Model.StrictToolPairing)
        {
            Assert.Empty(ErrorsOf(decided));
            Assert.Equal("我來協助其他問題", string.Concat(decided.Where(u => u.AuthorName == "triage").Select(u => u.Text)));
        }

        return harness.Model.Calls;
    }

    // ───────────────────────── 3b. 模型鏈例外 ─────────────────────────

    /// <summary>
    /// ⚠️ 參與者的模型鏈拋出的例外（配額、回放分歧、步數上限…）不會從包裝 Agent 的 <c>RunStreamingAsync</c> 拋出，
    /// 而是變成兩個 <see cref="ErrorContent"/> update：<c>ExecutorFailedEvent</c>（<c>Data</c>＝原始例外）與
    /// <c>WorkflowErrorEvent</c>（<c>Exception</c>＝包著原始例外的 <see cref="System.Reflection.TargetInvocationException"/>）。
    /// 之後的處理：
    /// <list type="bullet">
    /// <item><c>continue</c>：直接存下失敗後的 session、照常送下一輪 → 下一輪必定「Expected exactly one update for key 'SharedState'.」，之後是否恢復不確定（不可依賴）。</item>
    /// <item><c>rollback</c>：丟棄失敗這輪、改用這輪之前的 session JSON → 之後正常（失敗那則訊息不在框架歷史裡）。</item>
    /// <item><c>recover</c>：<c>TryPrepare()</c> 後送「空訊息」續跑中斷的工作（專員重新回覆失敗那輪）→ 之後正常。</item>
    /// </list>
    /// </summary>
    [Theory]
    [InlineData("continue")]
    [InlineData("rollback")]
    [InlineData("recover")]
    public async Task Handoff_ModelChainException_SurfacesAsErrorUpdates(string mode)
    {
        var harness = new HandoffHarness();
        harness.Model.Call("handoff_to_2", new() { ["reasonForHandoff"] = "顧客要換貨" });
        harness.Model.Reply("請問訂單編號？");
        harness.Model.Throw(new TimeZoneNotFoundException("budget"));
        harness.Model.Reply("R-A");
        harness.Model.Reply("R-B");
        harness.Model.Reply("R-C");
        var agent = harness.Build(NewCheckpointManager(), returnToPrevious: true);
        var session = await agent.CreateSessionAsync();
        await RunTurnAsync(agent, session, new ChatMessage(ChatRole.User, "鞋子太小想換大一號"));
        var goodJson = (await agent.SerializeSessionAsync(session)).GetRawText();

        var failed = await RunTurnAsync(agent, session, new ChatMessage(ChatRole.User, "SO1001"));

        var executorFailed = Assert.IsType<ExecutorFailedEvent>(failed.Single(u => u.RawRepresentation is ExecutorFailedEvent).RawRepresentation);
        Assert.IsType<TimeZoneNotFoundException>(executorFailed.Data);
        Assert.Equal("returns_returns", executorFailed.ExecutorId);
        var workflowError = Assert.IsType<WorkflowErrorEvent>(failed.Single(u => u.RawRepresentation is WorkflowErrorEvent).RawRepresentation);
        Assert.IsType<TimeZoneNotFoundException>(Assert.IsType<System.Reflection.TargetInvocationException>(workflowError.Exception).InnerException);
        Assert.Equal(["budget", "budget"], ErrorsOf(failed));

        var json = mode == "rollback" ? goodJson : (await agent.SerializeSessionAsync(session)).GetRawText();
        var rebuilt = harness.Build(NewCheckpointManager(), returnToPrevious: true);
        var restored = await rebuilt.DeserializeSessionAsync(JsonSerializer.Deserialize<JsonElement>(json));
        if (mode == "recover")
        {
#pragma warning disable MAAI001
            Assert.True(restored.GetService<WorkflowSessionCheckpointRecovery>()!.TryPrepare());
#pragma warning restore MAAI001
            var recovered = await RunTurnAsync(rebuilt, restored, []);
            Assert.Empty(ErrorsOf(recovered));
            Assert.Equal("R-A", string.Concat(recovered.Where(u => u.AuthorName == "returns").Select(u => u.Text)));
            Assert.Contains(harness.Model.Calls[^1].Messages, m => m.Text == "SO1001");
        }

        var next = await RunTurnAsync(rebuilt, restored, new ChatMessage(ChatRole.User, "再試一次"));
        var third = await RunTurnAsync(rebuilt, restored, new ChatMessage(ChatRole.User, "第三輪"));
        if (mode == "continue")
        {
            // 下一輪一定失敗；再下一輪則不確定（25 次實測約七成恢復、三成仍失敗——同一 superstep 內兩個 executor 的競態），只記錄不斷言。
            Assert.Equal(["Expected exactly one update for key 'SharedState'."], ErrorsOf(next));
            output.WriteLine("third-turn errors (nondeterministic): " + string.Join(" / ", ErrorsOf(third)));
        }
        else
        {
            Assert.Empty(ErrorsOf(next));
            Assert.Empty(ErrorsOf(third));
            Assert.StartsWith(ReturnsInstructions, harness.Model.Calls[^1].Instructions);
            Assert.Equal(mode == "rollback", !harness.Model.Calls[^1].Messages.Any(m => m.Text == "SO1001"));
        }
    }

    // ───────────────────────── 4. session JSON 大小 ─────────────────────────

    /// <summary>
    /// 有 <c>WithCheckpointing</c>（SQLite）時，session JSON 不含 checkpoint 內容（checkpointManager 為 null，大小只隨可見對話成長）；
    /// 沒有時，整個記憶體 checkpoint 庫會塞進 session JSON。
    /// </summary>
    [Fact]
    public async Task Handoff_SessionJson_StaysSmallWithCheckpointManager()
    {
        var withManager = await RunTwoTurnsAndMeasureAsync(NewCheckpointManager());
        var withoutManager = await RunTwoTurnsAndMeasureAsync(null);
        output.WriteLine($"with checkpoint manager: turn1={withManager.Sizes[0]} turn2={withManager.Sizes[1]}");
        output.WriteLine($"without checkpoint manager: turn1={withoutManager.Sizes[0]} turn2={withoutManager.Sizes[1]}");
        output.WriteLine("with manager json: " + withManager.Json);

        using (var doc = JsonDocument.Parse(withManager.Json))
        {
            output.WriteLine("with manager top-level: " + string.Join(", ", doc.RootElement.EnumerateObject().Select(p => $"{p.Name}:{p.Value.ValueKind}")));
            Assert.Equal(["sessionId", "lastCheckpoint", "stateBag", "pendingRequests"], doc.RootElement.EnumerateObject().Select(p => p.Name));
        }

        using (var doc = JsonDocument.Parse(withoutManager.Json))
        {
            output.WriteLine("without manager top-level: " + string.Join(", ", doc.RootElement.EnumerateObject().Select(p => $"{p.Name}:{p.Value.ValueKind}:{p.Value.GetRawText().Length}")));
            Assert.Equal(JsonValueKind.Object, doc.RootElement.GetProperty("checkpointManager").ValueKind);
        }

        // checkpoint 內容（各參與者的 AgentSession、共享對話狀態）只在沒有外部 checkpoint 庫時出現在 session JSON。
        // 但「可見對話」（包裝層的 WorkflowChatHistoryProvider：使用者訊息＋合併後的回應，含交接呼叫與 "Transferred."）一律在 stateBag 裡，隨對話成長。
        Assert.Contains("\"WorkflowChatHistoryProvider\"", withManager.Json);
        Assert.Contains("handoff_to_1", withManager.Json);
        Assert.Contains("Transferred.", withManager.Json);
        Assert.DoesNotContain("HandoffOrchestration", withManager.Json);
        Assert.Contains("HandoffOrchestration", withoutManager.Json);
        Assert.True(withManager.Sizes[1] * 3 < withoutManager.Sizes[1], $"with={withManager.Sizes[1]} without={withoutManager.Sizes[1]}");
        // 有外部 checkpoint 庫時，第二輪的成長只有可見對話（遠小於沒有時的成長）。
        Assert.True(withManager.Sizes[1] - withManager.Sizes[0] < (withoutManager.Sizes[1] - withoutManager.Sizes[0]) / 3);
    }

    private async Task<(int[] Sizes, string Json)> RunTwoTurnsAndMeasureAsync(CheckpointManager? manager)
    {
        var harness = new HandoffHarness();
        harness.Model.Call("handoff_to_1", new() { ["reasonForHandoff"] = "查物流" });
        harness.Model.Reply("您的包裹已出貨");
        harness.Model.Reply("預計明天送達");
        var agent = harness.Build(manager, returnToPrevious: true);
        var session = await agent.CreateSessionAsync();
        await RunTurnAsync(agent, session, new ChatMessage(ChatRole.User, "我的包裹到哪了"));
        var first = (await agent.SerializeSessionAsync(session)).GetRawText();
        await LogCheckpointRowsAsync(first, "turn1");
        await RunTurnAsync(agent, session, new ChatMessage(ChatRole.User, "什麼時候到"));
        var second = (await agent.SerializeSessionAsync(session)).GetRawText();
        await LogCheckpointRowsAsync(second, "turn2");
        return ([first.Length, second.Length], second);
    }

    /// <summary>記錄 SQLite 裡這個 session 的 checkpoint 筆數與 payload 總長度（每個 superstep 一筆，每筆含所有參與者的 session 與共享對話）。</summary>
    private async Task LogCheckpointRowsAsync(string sessionJson, string label)
    {
        var sessionId = JsonDocument.Parse(sessionJson).RootElement.GetProperty("sessionId").GetString();
        await using var db = dbFactory.CreateDbContext();
        var lengths = db.WorkflowCheckpoint.Where(x => x.SessionId == sessionId).Select(x => x.PayloadJson.Length).ToList();
        output.WriteLine($"{label}: sqlite checkpoints={lengths.Count} totalPayload={lengths.Sum()} largest={(lengths.Count == 0 ? 0 : lengths.Max())}");
    }

    // ───────────────────────── 5. EnableReturnToPrevious ─────────────────────────

    /// <summary>
    /// 開啟 <c>EnableReturnToPrevious</c> 後，下一輪直接由上一位專員接手（不經分流）；沒開時每輪都從分流開始。
    /// 兩輪之間比照 Runner：session 序列化、丟掉 Agent、重建、反序列化。
    /// </summary>
    [Theory]
    [InlineData(true, LogisticsInstructions, "logistics")]
    [InlineData(false, TriageInstructions, "triage")]
    public async Task Handoff_ReturnToPrevious_NextTurnGoesToSpecialist(bool returnToPrevious, string expectedInstructions, string expectedAuthor)
    {
        var harness = new HandoffHarness();
        harness.Model.Call("handoff_to_1", new() { ["reasonForHandoff"] = "查物流" });
        harness.Model.Reply("您的包裹已出貨");
        harness.Model.Reply("第二輪回覆");
        var agent = harness.Build(NewCheckpointManager(), returnToPrevious);
        var session = await agent.CreateSessionAsync();
        await RunTurnAsync(agent, session, new ChatMessage(ChatRole.User, "我的包裹到哪了"));
        var stateJson = (await agent.SerializeSessionAsync(session)).GetRawText();

        var rebuilt = harness.Build(NewCheckpointManager(), returnToPrevious);
        var restored = await rebuilt.DeserializeSessionAsync(JsonSerializer.Deserialize<JsonElement>(stateJson));
        var updates = await RunTurnAsync(rebuilt, restored, new ChatMessage(ChatRole.User, "什麼時候到"));

        Assert.Equal(3, harness.Model.Calls.Count);
        Assert.StartsWith(expectedInstructions, harness.Model.Calls[2].Instructions);
        Assert.Equal("第二輪回覆", string.Concat(updates.Where(u => u.AuthorName == expectedAuthor).Select(u => u.Text)));
        // 第二輪送給模型的訊息：接手的專員只拿到「自上次發言後的新訊息」＋自己 session 裡的舊歷史。
        output.WriteLine("turn2 request messages: " + Describe(harness.Model.Calls[2].Messages));
    }

    // ───────────────────────── 6. AIContextProvider ─────────────────────────

    /// <summary>
    /// <c>ProvideAIContextAsync</c> 回傳的 Instructions：單一 <see cref="ChatClientAgent"/> 與 Handoff 參與者都會出現在
    /// 送給模型的 <c>ChatOptions.Instructions</c>（接在 Agent 自己的指示＋交接指示之後），且不在任何訊息內。
    /// </summary>
    [Fact]
    public async Task ContextProvider_InjectsInstructions_SingleAndHandoff()
    {
        const string Memory = "以下來自顧客的長期記憶：鞋碼 42";

        // 單一 Agent
        var single = new HandoffScriptModel();
        single.Reply("好的");
        var singleProvider = new FixedInstructionsProvider(Memory);
        var singleAgent = new ChatClientAgent(single.AsBuilder().UseFunctionInvocation().Build(), new ChatClientAgentOptions
        {
            Id = "single",
            Name = "single",
            ChatOptions = new ChatOptions { Instructions = TriageInstructions },
            AIContextProviders = [singleProvider],
        });
        await singleAgent.RunAsync("你好", await singleAgent.CreateSessionAsync());
        output.WriteLine("single instructions: " + single.Calls[0].Instructions);
        Assert.Equal(TriageInstructions + "\n" + Memory, single.Calls[0].Instructions);
        Assert.DoesNotContain(single.Calls[0].Messages, m => m.Text.Contains(Memory, StringComparison.Ordinal));
        Assert.Equal(["single"], singleProvider.InvokedBy);

        // Handoff：同一個 provider 實例掛在所有參與者上。
        var provider = new FixedInstructionsProvider(Memory);
        var harness = new HandoffHarness { Providers = [provider] };
        harness.Model.Call("handoff_to_1", new() { ["reasonForHandoff"] = "查物流" });
        harness.Model.Reply("您的包裹已出貨");
        var agent = harness.Build(NewCheckpointManager(), returnToPrevious: true);
        await RunTurnAsync(agent, await agent.CreateSessionAsync(), new ChatMessage(ChatRole.User, "我的包裹到哪了"));

        Assert.Equal(2, harness.Model.Calls.Count);
        foreach (var (call, own) in harness.Model.Calls.Zip([TriageInstructions, LogisticsInstructions]))
        {
            output.WriteLine("handoff instructions: " + call.Instructions);
            Assert.StartsWith(own, call.Instructions);
            Assert.EndsWith("\n" + Memory, call.Instructions);
            Assert.DoesNotContain(call.Messages, m => m.Text.Contains(Memory, StringComparison.Ordinal));
        }

        Assert.Equal(["triage", "logistics"], provider.InvokedBy);
        output.WriteLine("provider session types: " + string.Join(", ", provider.SessionTypes));
    }

    // ───────────────────────── 7. 回放摘要 ─────────────────────────

    /// <summary>
    /// 同一段劇本（含專員交回分流、工具呼叫）跑兩次，每次模型請求的 <see cref="ModelRequestDigest"/> 序列相同。
    /// 另釘住摘要會涵蓋的框架自動內容：分流被交回時，框架加入一則 user 訊息
    /// 「Control has returned after handoff call '{分流當初的交接 CallId}'…」——含模型產生的 CallId，回放時沿用錄製的 CallId 所以仍穩定；
    /// 物流專員的 track_shipment 呼叫與結果（非交接工具）會原樣出現在之後換退貨專員的請求裡。
    /// </summary>
    [Fact]
    public async Task ReplayDigest_StableAcrossHandoff()
    {
        var (first, calls) = await RunDigestScenarioAsync();
        var (second, _) = await RunDigestScenarioAsync();
        output.WriteLine(string.Join("\n", first));
        Assert.Equal(5, first.Count);
        Assert.Equal(first, second);

        Assert.Equal(
            "Control has returned after handoff call 'call-1'. Continue the existing user request using the conversation and completed tool results. This is a continuation, not a new request. Do not repeat actions that have already completed.",
            calls[3].Messages[^1].Text);
        Assert.Equal(ChatRole.User, calls[3].Messages[^1].Role);
        var returnsContents = calls[4].Messages.SelectMany(m => m.Contents).ToList();
        Assert.Contains(returnsContents, c => c is FunctionCallContent { Name: "track_shipment" });
        Assert.DoesNotContain(returnsContents, c => c is FunctionCallContent call && call.Name.StartsWith("handoff_to_", StringComparison.Ordinal));
    }

    private async Task<(List<string> Digests, List<HandoffModelCall> Calls)> RunDigestScenarioAsync()
    {
        var harness = new HandoffHarness();
        harness.Model.Call("handoff_to_1", new() { ["reasonForHandoff"] = "查物流" });
        harness.Model.Call("track_shipment", new() { ["orderNo"] = "SO1001" });
        harness.Model.Call("handoff_to_1", new() { ["reasonForHandoff"] = "顧客改問換貨" });
        harness.Model.Call("handoff_to_2", new() { ["reasonForHandoff"] = "顧客要換貨" });
        harness.Model.Reply("可以為您辦理換貨");
        var agent = harness.Build(NewCheckpointManager(), returnToPrevious: true);
        var updates = await RunTurnAsync(agent, await agent.CreateSessionAsync(), new ChatMessage(ChatRole.User, "包裹到哪了？另外我想換貨"));
        Assert.DoesNotContain(updates, u => u.Contents.OfType<ErrorContent>().Any());
        foreach (var call in harness.Model.Calls)
        {
            output.WriteLine($"[{call.Instructions?.Split('\n')[0]}] {Describe(call.Messages)}");
        }

        return ([.. harness.Model.Calls.Select(c => ModelRequestDigest.Compute(c.Messages))], harness.Model.Calls);
    }

    // ───────────────────────── 共用 ─────────────────────────

    private Task<List<AgentResponseUpdate>> RunTurnAsync(AIAgent agent, AgentSession session, ChatMessage message)
        => RunTurnAsync(agent, session, [message]);

    private async Task<List<AgentResponseUpdate>> RunTurnAsync(AIAgent agent, AgentSession session, IEnumerable<ChatMessage> messages)
    {
        var updates = new List<AgentResponseUpdate>();
        await foreach (var update in agent.RunStreamingAsync(messages, session))
        {
            updates.Add(update);
        }

        output.WriteLine($"--- turn ({updates.Count} updates) ---");
        foreach (var u in updates.Where(u => u.Contents.Count > 0))
        {
            output.WriteLine($"author={u.AuthorName ?? "(null)"} agentId={u.AgentId ?? "(null)"} role={u.Role} raw={u.RawRepresentation?.GetType().Name ?? "(null)"} contents=[{string.Join("; ", u.Contents.Select(DescribeContent))}]");
        }

        output.WriteLine("empty (observability) updates: " + string.Join(", ", updates.Where(u => u.Contents.Count == 0)
            .Select(u => u.RawRepresentation?.GetType().Name ?? "(null)").GroupBy(x => x).Select(g => $"{g.Key}x{g.Count()}")));
        return updates;
    }

    private static List<string> ErrorsOf(IEnumerable<AgentResponseUpdate> updates)
        => [.. updates.SelectMany(u => u.Contents).OfType<ErrorContent>().Select(e => e.Message)];

    /// <summary>取 RequestInfoEvent 轉成的那一份核准請求（外部 RequestId），不是專員串流裡的內部那一份。</summary>
    private static ToolApprovalRequestContent ExternalApprovalOf(IEnumerable<AgentResponseUpdate> updates)
        => updates.Where(u => u.RawRepresentation is RequestInfoEvent).SelectMany(u => u.Contents).OfType<ToolApprovalRequestContent>().Single();

    private static string Describe(IEnumerable<ChatMessage> messages)
        => string.Join(" | ", messages.Select(m => $"{m.Role}({m.AuthorName}):{string.Join(",", m.Contents.Select(DescribeContent))}"));

    private static string DescribeContent(AIContent content) => content switch
    {
        TextContent t => $"text\"{t.Text}\"",
        FunctionCallContent c => $"call {c.Name}#{c.CallId}",
        FunctionResultContent r => $"result#{r.CallId}={r.Result}",
        ToolApprovalRequestContent a => $"approvalRequest {a.RequestId}",
        ToolApprovalResponseContent a => $"approvalResponse {a.RequestId} approved={a.Approved}",
        ErrorContent e => $"error \"{e.Message}\"",
        _ => content.GetType().Name,
    };

    /// <summary>每次叫用記下是哪位 Agent、session 型別；固定回傳同一段 Instructions。</summary>
    private sealed class FixedInstructionsProvider(string instructions) : AIContextProvider
    {
        public List<string> InvokedBy { get; } = [];

        public List<string> SessionTypes { get; } = [];

        protected override ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken cancellationToken = default)
        {
            InvokedBy.Add(context.Agent.Name ?? context.Agent.Id);
            SessionTypes.Add(context.Session?.GetType().Name ?? "(null)");
            return ValueTask.FromResult(new AIContext { Instructions = instructions });
        }
    }

    /// <summary>
    /// 比照 M5 規劃建 Handoff：所有參與者共用同一條模型鏈與同一個 Guard 實例；固定的 Id／Name；
    /// 分流 → [物流(1), 換退貨(2)]，專員 → 分流(1)。
    /// </summary>
    private sealed class HandoffHarness
    {
        public HandoffScriptModel Model { get; } = new();

        public List<string> ToolLog { get; } = [];

        public List<string> GuardLog { get; } = [];

        public bool UseGuard { get; init; }

        public Func<FunctionInvocationContext, bool>? TerminateWhen { get; init; }

        public List<AIContextProvider>? Providers { get; init; }

        /// <summary>比照產品：每位參與者外面再包 <see cref="HandoffTopologyFactory.ScrubHistoryAttribution"/>。</summary>
        public bool ScrubAttribution { get; init; }

        public AIAgent Build(CheckpointManager? manager, bool returnToPrevious)
        {
            var chain = Model.AsBuilder().UseFunctionInvocation().Build();
            var triage = Participant(chain, "triage", TriageDescription, TriageInstructions,
                [AIFunctionFactory.Create(() => { ToolLog.Add("query_my_orders"); return new { orders = new[] { "SO1001" } }; }, "query_my_orders", "查詢我的訂單")]);
            var logistics = Participant(chain, "logistics", LogisticsDescription, LogisticsInstructions,
                [AIFunctionFactory.Create((string orderNo) => { ToolLog.Add("track_shipment"); return new { order_no = orderNo, status = "已出貨" }; }, "track_shipment", "查詢物流")]);
            var returns = Participant(chain, "returns", ReturnsDescription, ReturnsInstructions,
                [new ApprovalRequiredAIFunction(AIFunctionFactory.Create((string orderNo, string reason) =>
                {
                    ToolLog.Add("create_request");
                    return new { request_id = 7, request_no = "AS202610230001", order_no = orderNo, reason };
                }, "create_request", "建立換退貨申請"))]);

            var builder = AgentWorkflowBuilder.CreateHandoffBuilderWith(triage)
                .WithHandoffs(triage, [logistics, returns])
                .WithHandoffs([logistics, returns], triage);
            if (returnToPrevious)
            {
                builder = builder.EnableReturnToPrevious();
            }

            var workflow = builder.Build();
            var environment = manager is null ? InProcessExecution.OffThread : InProcessExecution.OffThread.WithCheckpointing(manager);
            return workflow.AsAIAgent(id: "aftersales-handoff", name: "aftersales-handoff", executionEnvironment: environment, includeExceptionDetails: true);
        }

        private AIAgent Participant(IChatClient chain, string id, string description, string instructions, IList<AITool> tools)
        {
            var agent = new ChatClientAgent(chain, new ChatClientAgentOptions
            {
                Id = id,
                Name = id,
                Description = description,
                ChatOptions = new ChatOptions { Instructions = instructions, Tools = tools, AllowMultipleToolCalls = false },
                AIContextProviders = Providers,
            });
            var participant = UseGuard ? agent.AsBuilder().Use(GuardAsync).Build() : agent;
            return ScrubAttribution ? HandoffTopologyFactory.ScrubHistoryAttribution(participant) : participant;
        }

        private async ValueTask<object?> GuardAsync(
            AIAgent agent,
            FunctionInvocationContext context,
            Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next,
            CancellationToken cancellationToken)
        {
            GuardLog.Add($"{agent.Name}:{context.Function.Name}");
            var result = await next(context, cancellationToken);
            if (TerminateWhen?.Invoke(context) == true)
            {
                context.Terminate = true;
            }

            return result;
        }
    }
}

/// <summary>
/// Handoff 試跑用的腳本化模型：所有參與者共用一個佇列；每次請求記下 Instructions、訊息與工具（名稱＋描述）。
/// CallId 與 MessageId 依序產生（可重現），串流時文字切成兩段。
/// </summary>
internal sealed class HandoffScriptModel : IChatClient
{
    private readonly Queue<object> script = new();
    private int callIdSequence;
    private int responseSequence;

    public List<HandoffModelCall> Calls { get; } = [];

    /// <summary>比照 Responses API 檢查工具呼叫與結果成對（預設開啟，不成對時拋例外＝實機的 HTTP 400），見 <see cref="ResponsesApiPairing"/>。</summary>
    public bool StrictToolPairing { get; set; } = true;

    public void Call(string name, Dictionary<string, object?> args)
        => script.Enqueue(new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("call-" + Interlocked.Increment(ref callIdSequence), name, args)])));

    public void Reply(string text) => script.Enqueue(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));

    /// <summary>下一次呼叫拋出這個例外（模擬配額、回放分歧、步數上限等由模型鏈拋出的例外）。</summary>
    public void Throw(Exception exception) => script.Enqueue(exception);

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add(new HandoffModelCall(
            options?.Instructions,
            [.. messages.Select(m => m.Clone())],
            [.. (options?.Tools ?? []).Select(t => (t.Name, t.Description))]));
        if (StrictToolPairing)
        {
            ResponsesApiPairing.EnsurePaired(messages);
        }

        return script.TryDequeue(out var next)
            ? next is Exception ex ? Task.FromException<ChatResponse>(ex) : Task.FromResult((ChatResponse)next)
            : throw new InvalidOperationException($"Scripted handoff model has no more responses (call #{Calls.Count}).");
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        var sequence = Interlocked.Increment(ref responseSequence);
        var messageId = "m" + sequence;
        var responseId = "resp" + sequence;
        foreach (var message in response.Messages)
        {
            foreach (var content in message.Contents)
            {
                if (content is TextContent { Text.Length: > 1 } text)
                {
                    var half = text.Text.Length / 2;
                    yield return new ChatResponseUpdate(message.Role, text.Text[..half]) { MessageId = messageId, ResponseId = responseId };
                    yield return new ChatResponseUpdate(message.Role, text.Text[half..]) { MessageId = messageId, ResponseId = responseId };
                }
                else
                {
                    yield return new ChatResponseUpdate(message.Role, [content]) { MessageId = messageId, ResponseId = responseId };
                }
            }
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

internal sealed record HandoffModelCall(string? Instructions, IReadOnlyList<ChatMessage> Messages, IReadOnlyList<(string Name, string Description)> Tools);

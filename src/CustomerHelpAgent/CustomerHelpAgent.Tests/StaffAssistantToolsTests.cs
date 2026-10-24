using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Shop;
using CustomerHelpAgent.Share.Agents;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Share.Shop;
using CustomerHelpAgent.Web.Agents.AfterSales;
using CustomerHelpAgent.Web.Agents.PreReview;
using CustomerHelpAgent.Web.Agents.StaffAssistant;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 客服 AI 助手的測試資料：<see cref="PreReviewTestHost"/>（Alice 的訂單收件人王小明、地址、手機）＋一位客服、
/// Alice 的 Email，以及另一位顧客 Bob 的申請（「別的案件」）。顧客端 Agent 的軌跡以正式的序列化格式寫入。
/// </summary>
internal sealed class StaffAssistantKit : IAsyncDisposable
{
    public const string AliceEmail = "alice@example.com";

    /// <summary>不可出現在任何工具結果或交接摘要裡的個資（顧客姓名、帳號、Email、收件人、地址、手機）。</summary>
    public static readonly string[] PersonalValues = ["Alice", "alice", AliceEmail, "王小明", "台北市某路1號", "0912345678"];

    private StaffAssistantKit(PreReviewTestHost host)
    {
        Host = host;
    }

    public PreReviewTestHost Host { get; }

    public int StaffId { get; private set; }

    public int Bob { get; private set; }

    public static async Task<StaffAssistantKit> CreateAsync()
    {
        var host = await PreReviewTestHost.CreateAsync(configure: services =>
        {
            services.AddScoped<AgentTraceService>();
            services.AddScoped<HandoverSummaryBuilder>();
        });
        var kit = new StaffAssistantKit(host);
        await using var ctx = host.Factory.CreateDbContext();
        var staff = new MyUser { Account = "agent7", Password = "x", Name = "客服七號" };
        var bob = new MyUser { Account = "bob", Password = "x", Name = "Bob" };
        ctx.MyUser.AddRange(staff, bob);
        await ctx.SaveChangesAsync();
        await ctx.MyUser.Where(x => x.Id == host.Alice).ExecuteUpdateAsync(s => s.SetProperty(x => x.Email, AliceEmail));
        kit.StaffId = staff.Id;
        kit.Bob = bob.Id;
        return kit;
    }

    /// <summary>綁定 <paramref name="requestId"/> 的工具；目前登入使用者預設是開啟助手的客服。</summary>
    public StaffAssistantTools Tools(int requestId, IPermissionChecker checker, int? currentUserId = null)
    {
        var sp = Host.Scope(currentUserId ?? StaffId, "agent7");
        return new StaffAssistantTools(
            sp.GetRequiredService<AfterSalesRequestService>(),
            sp.GetRequiredService<AfterSalesPolicyService>(),
            sp.GetRequiredService<ShopOrderService>(),
            sp.GetRequiredService<PreReviewRunService>(),
            sp.GetRequiredService<HandoverSummaryBuilder>(),
            checker,
            sp.GetRequiredService<CurrentUserService>(),
            Host.Time,
            NullLogger<StaffAssistantTools>.Instance,
            new StaffAssistantToolContext { RequestId = requestId, StaffUserId = StaffId });
    }

    public HandoverSummaryBuilder Builder => Host.Scope().GetRequiredService<HandoverSummaryBuilder>();

    public async Task<AfterSalesRequestView> DetailAsync(int requestId)
        => await Host.Scope().GetRequiredService<AfterSalesRequestService>().GetReviewDetailAsync(requestId)
            ?? throw new InvalidOperationException("request missing");

    /// <summary>以顧客端 Agent 的一輪執行寫入軌跡步驟。</summary>
    public async Task SeedTrailAsync(Guid caseKey, int customerUserId, params (AgentStepKind Kind, string Text, string? RawJson)[] steps)
    {
        var traces = Host.Scope().GetRequiredService<AgentTraceService>();
        var runId = await traces.StartRunAsync(caseKey, customerUserId);
        foreach (var (kind, text, raw) in steps)
        {
            await traces.AppendStepAsync(runId, kind, AgentDataSource.Program, text, raw);
        }
    }

    /// <summary>另一位顧客（Bob）的換尺碼申請（Agent 管道），回傳申請 Id、編號與訂單編號。</summary>
    public async Task<(int Id, string RequestNo, string OrderNo, Guid CaseKey)> AddBobRequestAsync()
    {
        await using var ctx = Host.Factory.CreateDbContext();
        var variant = await ctx.ProductVariant.SingleAsync(x => x.ProductId == Host.ProductId && x.Size == "26");
        var order = new SalesOrder
        {
            OrderNo = "BOB-ORDER-01",
            CustomerUserId = Bob,
            OrderedAt = PreReviewTestHost.LocalNow.AddDays(-4),
            DeliveredAt = PreReviewTestHost.LocalNow.AddDays(-1),
            RecipientName = "鮑伯本人",
            ShippingAddress = "高雄市另一路9號",
            RecipientPhone = "0987654321",
            Items = [new SalesOrderItem { ProductVariantId = variant.Id, Quantity = 1, UnitPrice = 1999m }],
        };
        ctx.SalesOrder.Add(order);
        await ctx.SaveChangesAsync();
        var row = new AfterSalesRequest
        {
            RequestNo = "AS-BOB-0001",
            CaseKey = Guid.NewGuid(),
            CustomerUserId = Bob,
            SalesOrderItemId = order.Items[0].Id,
            Type = AfterSalesType.SizeExchange,
            Status = AfterSalesStatus.Submitted,
            Channel = AfterSalesChannel.Agent,
            TargetSize = "28",
            Description = "鮑伯的祕密說明",
            CreatedAt = PreReviewTestHost.LocalNow,
            UpdatedAt = PreReviewTestHost.LocalNow,
        };
        ctx.AfterSalesRequest.Add(row);
        await ctx.SaveChangesAsync();
        return (row.Id, row.RequestNo, order.OrderNo, row.CaseKey);
    }

    /// <summary>顧客端工具呼叫的兩步（與 <see cref="AfterSalesAgentGuard"/> 的格式相同）：ToolRequest＋ToolResult。</summary>
    public static (AgentStepKind, string, string?)[] ToolCall(string tool, ToolOutcome outcome)
        =>
        [
            (AgentStepKind.ToolRequest, $"Agent 決定呼叫 {tool}", JsonSerializer.Serialize(new { tool, arguments = new { } })),
            (AgentStepKind.ToolResult, "結果", JsonSerializer.Serialize(outcome.ToJsonElement())),
        ];

    public async ValueTask DisposeAsync() => await Host.DisposeAsync();
}

/// <summary>權限檢查的替身：固定回答，並記下被問了哪些鍵。</summary>
internal sealed class FakePermissionChecker(bool allowed) : IPermissionChecker
{
    public List<(int UserId, string Key)> Checks { get; } = [];

    public Task<bool> HasPermissionAsync(int userId, string permissionKey)
    {
        lock (Checks)
        {
            Checks.Add((userId, permissionKey));
        }

        return Task.FromResult(allowed);
    }

    public Task<IReadOnlyCollection<string>> GetEffectivePermissionKeysAsync(int userId)
        => Task.FromResult<IReadOnlyCollection<string>>([]);
}

/// <summary>
/// 客服 AI 助手的 6 個工具（M4 Task 8）：案件由程式綁定（工具沒有參數、多傳的引數被忽略）、每次呼叫重查「售後審核」權限、全部只讀、
/// 結果沿用 <see cref="ToolOutcome"/> 信封且不含個資。
/// </summary>
public sealed class StaffAssistantToolsTests : IAsyncLifetime
{
    private static readonly string[] ToolNames =
    [
        "get_case", "get_effective_policy", "check_stock",
        "get_customer_history", "get_customer_agent_trail", "get_pre_review_result",
    ];

    private StaffAssistantKit kit = null!;

    public async Task InitializeAsync() => kit = await StaffAssistantKit.CreateAsync();

    public async Task DisposeAsync() => await kit.DisposeAsync();

    private static async Task<Dictionary<string, (ToolOutcome Outcome, string Json)>> InvokeAllAsync(
        StaffAssistantTools tools, Dictionary<string, object?>? args = null)
    {
        var results = new Dictionary<string, (ToolOutcome, string)>();
        foreach (var function in tools.CreateFunctions())
        {
            var result = await function.InvokeAsync(new AIFunctionArguments(args ?? []));
            Assert.True(ToolOutcome.TryParse(result, out var outcome), function.Name);
            results[function.Name] = (outcome!, JsonSerializer.Serialize(result, ToolOutcome.JsonOptions));
        }

        return results;
    }

    [Fact]
    public async Task Tools_CaseBoundToOpenRequest_CannotQueryOthers()
    {
        // 開啟的這一筆（Alice）＋她過去的另一筆；別人的（Bob）。兩個案件都有顧客端軌跡與初審。
        var openId = await kit.Host.AddRequestAsync();
        var aliceOlderId = await kit.Host.AddRequestAsync(status: AfterSalesStatus.Rejected);
        var open = await kit.DetailAsync(openId);
        var aliceOlder = await kit.DetailAsync(aliceOlderId);
        var bob = await kit.AddBobRequestAsync();
        await kit.SeedTrailAsync(open.CaseKey, kit.Host.Alice, (AgentStepKind.UserMessage, "我的鞋太小，沒穿過", null));
        await kit.SeedTrailAsync(bob.CaseKey, kit.Bob, (AgentStepKind.UserMessage, "鮑伯在對話裡說的話", null));
        var runs = kit.Host.Runs;
        var openRun = await runs.CreateAsync(openId);
        var bobRun = await runs.CreateAsync(bob.Id);
        await runs.SaveFindingAsync(bobRun!.Id, PreReviewNode.RuleCheck, JsonSerializer.Serialize(
            new PreReviewFinding(PreReviewNode.RuleCheck, FindingStatus.Ok, "鮑伯案件的規則結論", "{}"), PreReviewJsonContext.Default.PreReviewFinding));

        var tools = kit.Tools(openId, new FakePermissionChecker(true));

        // 工具沒有任何參數：模型無從指定案件或顧客。
        foreach (var function in tools.CreateFunctions())
        {
            var schema = function.JsonSchema;
            var hasProperties = schema.TryGetProperty("properties", out var properties) && properties.EnumerateObject().Any();
            Assert.False(hasProperties, $"{function.Name} must not take parameters");
        }

        // 模型硬塞別的案件與顧客：一律被忽略，查到的還是開啟的這一筆。
        var results = await InvokeAllAsync(tools, new Dictionary<string, object?>
        {
            ["requestId"] = bob.Id,
            ["request_id"] = bob.Id,
            ["requestNo"] = bob.RequestNo,
            ["customerUserId"] = kit.Bob,
            ["orderNo"] = bob.OrderNo,
        });

        Assert.All(results.Values, x => Assert.Equal(ToolOutcomeStatus.Ok, x.Outcome.Status));
        var caseData = Assert.IsType<JsonElement>(results["get_case"].Outcome.Data);
        Assert.Equal(open.RequestNo, caseData.GetProperty("request_no").GetString());
        var history = Assert.IsType<JsonElement>(results["get_customer_history"].Outcome.Data);
        Assert.Equal(1, history.GetProperty("prior_count").GetInt32());
        Assert.Equal(aliceOlder.RequestNo, history.GetProperty("requests")[0].GetProperty("request_no").GetString());
        Assert.Contains("我的鞋太小", results["get_customer_agent_trail"].Json, StringComparison.Ordinal);
        Assert.Equal(openRun!.Attempt, Assert.IsType<JsonElement>(results["get_pre_review_result"].Outcome.Data).GetProperty("attempt").GetInt32());

        var all = string.Join("\n", results.Values.Select(x => x.Json));
        foreach (var other in new[] { bob.RequestNo, bob.OrderNo, "鮑伯", "Bob", "高雄市另一路9號", "0987654321" })
        {
            Assert.DoesNotContain(other, all, StringComparison.Ordinal);
        }

        // 也不含開啟這一筆的顧客個資；不送的欄位只列名稱。
        foreach (var personal in StaffAssistantKit.PersonalValues)
        {
            Assert.DoesNotContain(personal, all, StringComparison.Ordinal);
        }

        Assert.Contains("地址", results["get_case"].Outcome.WithheldFields);
        Assert.Contains("庫存數量", results["check_stock"].Outcome.WithheldFields);
    }

    [Fact]
    public async Task Tools_NoPermission_Forbidden()
    {
        var requestId = await kit.Host.AddRequestAsync();

        // 權限已被收回：6 個工具都回 forbidden，且每次都以「售後審核:view」即時查權限。
        var denied = new FakePermissionChecker(false);
        var results = await InvokeAllAsync(kit.Tools(requestId, denied));

        Assert.Equal(ToolNames.Order(), results.Keys.Order());
        Assert.All(results.Values, x => Assert.Equal(ToolOutcomeStatus.Forbidden, x.Outcome.Status));
        Assert.Equal(6, denied.Checks.Count);
        Assert.All(denied.Checks, x =>
        {
            Assert.Equal(kit.StaffId, x.UserId);
            Assert.Equal(PermissionKey.For(MagicObjectHelper.角色_售後審核, PermissionActions.View), x.Key);
        });

        // 登入使用者不是開啟助手的客服（例如 circuit 換人）：不查權限就拒絕。
        var allowed = new FakePermissionChecker(true);
        var otherUser = await InvokeAllAsync(kit.Tools(requestId, allowed, currentUserId: kit.Host.Alice));
        Assert.All(otherUser.Values, x => Assert.Equal(ToolOutcomeStatus.Forbidden, x.Outcome.Status));
        Assert.Empty(allowed.Checks);
    }

    [Fact]
    public async Task Tools_AreAllReadOnly()
    {
        var requestId = await kit.Host.AddRequestAsync();
        var detail = await kit.DetailAsync(requestId);
        await kit.SeedTrailAsync(detail.CaseKey, kit.Host.Alice, (AgentStepKind.UserMessage, "想換 27 號", null));
        await kit.Host.Runs.CreateAsync(requestId);
        var tools = kit.Tools(requestId, new FakePermissionChecker(true));

        // 只有這 6 個工具，沒有任何需要確認的寫入工具。
        var functions = tools.CreateFunctions();
        Assert.Equal(ToolNames, functions.Select(x => x.Name));
        Assert.DoesNotContain(functions, x => x is ApprovalRequiredAIFunction);

        var before = await SnapshotAsync();
        var results = await InvokeAllAsync(tools);
        var after = await SnapshotAsync();

        Assert.All(results.Values, x => Assert.Equal(ToolOutcomeStatus.Ok, x.Outcome.Status));
        Assert.Equal(before, after);
        Assert.Empty(kit.Host.Audit.Entries);
        Assert.Empty(kit.Host.Sender.Requests);
    }

    [Fact]
    public async Task Tools_CallTag_ShowsWhatWasLookedUp()
    {
        var requestId = await kit.Host.AddRequestAsync();
        var tools = kit.Tools(requestId, new FakePermissionChecker(true));

        var policy = await tools.GetEffectivePolicyAsync();
        var stock = await tools.CheckStockAsync();

        Assert.Equal("查了：規則 v1", StaffAssistantTools.DescribeCall(StaffAssistantTools.GetEffectivePolicyName, policy));
        Assert.Equal("查了：庫存", StaffAssistantTools.DescribeCall(StaffAssistantTools.CheckStockName, stock));
        Assert.Equal("查了：AI 初審（查無資料）",
            StaffAssistantTools.DescribeCall(StaffAssistantTools.GetPreReviewResultName, await tools.GetPreReviewResultAsync()));
        // 權限被收回時用客服端的說法。
        Assert.Equal("查了：申請內容（目前沒有審核權限）", StaffAssistantTools.DescribeCall(
            StaffAssistantTools.GetCaseName, ToolOutcome.Fail(ToolOutcomeStatus.Forbidden, "登入狀態已失效或沒有使用權限。")));
        // 換 27 號有庫存：目標規格由程式標出。
        var data = Assert.IsType<StaffStockResult>(stock.Data);
        Assert.Equal("黑 / 27", data.TargetSpec);
        Assert.True(data.TargetInStock);
    }

    [Fact]
    public async Task Tools_PreReviewResult_RedactsPersonalData()
    {
        // 開啟這一筆的初審結論（模型寫的自由文字）轉述了顧客打出的個資。
        var requestId = await kit.Host.AddRequestAsync();
        var runs = kit.Host.Runs;
        var run = await runs.CreateAsync(requestId) ?? throw new InvalidOperationException("no run");
        const string Leak = "Alice（alice，alice@example.com）請寄到台北市某路1號給王小明，電話 0912345678";
        await SaveAsync(runs, run.Id, PreReviewNode.PhotoReview, "照片：" + Leak,
            JsonSerializer.Serialize(new PhotoReviewResult(PhotoReviewResult.Consistent, ["鞋底可見" + Leak], "medium"), PreReviewJsonContext.Default.PhotoReviewResult));
        await SaveAsync(runs, run.Id, PreReviewNode.RuleCheck, "規則：" + Leak,
            JsonSerializer.Serialize(new RuleCheckResult(1, "鞋類換尺碼", 1, 2, false, ["不符：" + Leak], ["缺：" + Leak], true), PreReviewJsonContext.Default.RuleCheckResult));
        await SaveAsync(runs, run.Id, PreReviewNode.HistoryReview, "歷史：" + Leak,
            JsonSerializer.Serialize(new HistoryReviewResult(0, ["注意：" + Leak], ["顧客說：" + Leak]), PreReviewJsonContext.Default.HistoryReviewResult));
        await SaveAsync(runs, run.Id, PreReviewNode.Summary, "摘要：" + Leak,
            JsonSerializer.Serialize(new PreReviewSummary(PreReviewRecommendations.Manual, "標題：" + Leak, ["理由：" + Leak], ["檢查：" + Leak], "警示：" + Leak),
                PreReviewJsonContext.Default.PreReviewSummary));

        var result = await kit.Tools(requestId, new FakePermissionChecker(true)).GetPreReviewResultAsync();
        var json = JsonSerializer.Serialize(result.ToJsonElement(), ToolOutcome.JsonOptions);

        Assert.Equal(ToolOutcomeStatus.Ok, result.Status);
        foreach (var personal in StaffAssistantKit.PersonalValues)
        {
            Assert.DoesNotContain(personal, json, StringComparison.Ordinal);
        }

        // 每個自由文字欄位都還在（只是遮蔽了）：標題、理由、檢查清單、警示、三項結論標題、觀察、注意事項、顧客陳述、規則原因。
        foreach (var label in new[] { "照片：", "規則：", "歷史：", "標題：", "理由：", "檢查：", "警示：", "鞋底可見", "不符：", "缺：", "注意：", "顧客說：" })
        {
            Assert.Contains(label, json, StringComparison.Ordinal);
        }

        Assert.Contains(PreReviewPrivacy.CustomerAlias, json, StringComparison.Ordinal);
        Assert.Contains(PreReviewPrivacy.Masked, json, StringComparison.Ordinal);
    }

    private static Task SaveAsync(PreReviewRunService runs, int runId, PreReviewNode node, string headline, string detailJson)
        => runs.SaveFindingAsync(runId, node, JsonSerializer.Serialize(
            new PreReviewFinding(node, FindingStatus.Ok, headline, detailJson), PreReviewJsonContext.Default.PreReviewFinding));

    /// <summary>會被寫入的資料：申請（狀態、備註、更新時間）、庫存、處理單、初審、軌跡、通知、稽核的筆數。</summary>
    private async Task<string> SnapshotAsync()
    {
        await using var ctx = kit.Host.Factory.CreateDbContext();
        var requests = await ctx.AfterSalesRequest.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => $"{x.Id}:{x.Status}:{x.StaffNote}:{x.UpdatedAt:O}").ToListAsync();
        var stocks = await ctx.ProductVariant.AsNoTracking().OrderBy(x => x.Id).Select(x => $"{x.Id}:{x.Stock}").ToListAsync();
        var counts = new[]
        {
            await ctx.AfterSalesFulfillment.CountAsync(),
            await ctx.PreReviewRun.CountAsync(),
            await ctx.PreReviewNodeState.CountAsync(),
            await ctx.AgentRunTrace.CountAsync(),
            await ctx.AgentRunStep.CountAsync(),
            await ctx.AuditLog.CountAsync(),
        };
        return string.Join("|", requests) + "#" + string.Join("|", stocks) + "#" + string.Join(",", counts);
    }
}

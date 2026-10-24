using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Others;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Agents;
using CustomerHelpAgent.Share.Shop;
using CustomerHelpAgent.Web.Agents;
using CustomerHelpAgent.Web.Agents.AfterSales;
using CustomerHelpAgent.Web.Agents.PreReview;
using CustomerHelpAgent.Web.Configuration;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 測試用：組出 AI 初審的三個模型節點（真正的 <see cref="PreReviewChatClientProvider"/>、<see cref="AgentChatClientFactory"/>、
/// 錄製與回放服務），模型換成腳本化的 <see cref="IChatClient"/>。<paramref name="model"/> 為 null 時，建立模型 client 就丟例外（回放不可以走到這裡）。
/// </summary>
internal sealed class PreReviewNodeKit
{
    private PreReviewNodeKit()
    {
    }

    public required ModelPhotoReviewer Photo { get; init; }

    public required ModelHistoryReviewer History { get; init; }

    public required ModelPreReviewSummarizer Summary { get; init; }

    public required PreReviewRunService Runs { get; init; }

    public required AfterSalesRequestService Requests { get; init; }

    public required RecordingTokenUsageRecorder Usage { get; init; }

    /// <summary>每次建立模型 client 時送進去的模型名稱（<c>AiSettings.Model</c>）。</summary>
    public required List<string> CreatedModels { get; init; }

    public required TimeProvider Time { get; init; }

    public static PreReviewNodeKit Create(
        IDbContextFactory<BackendDBContext> factory,
        string fileRoot,
        IChatClient? model,
        DemoControlService demo,
        ReplayCursorStore cursors,
        TimeProvider time,
        AfterSalesAgentSettings? settings = null)
    {
        // 背景執行：沒有登入者（系統呼叫）。
        var currentUser = new CurrentUserService();
        var audit = new RecordingAuditLogService();
        var systemSettings = new SystemSettings();
        systemSettings.ExternalFileSystem.AfterSalesFilePath = fileRoot;
        var policies = new AfterSalesPolicyService(factory, audit, currentUser, TestDemoControl.Off(factory), time, NullLogger<AfterSalesPolicyService>.Instance);
        var orders = new ShopOrderService(factory, TestDemoControl.Off(factory), NullLogger<ShopOrderService>.Instance);
        var requests = new AfterSalesRequestService(
            factory,
            policies,
            orders,
            new AfterSalesFileStore(Options.Create(systemSettings), NullLogger<AfterSalesFileStore>.Instance),
            audit,
            currentUser,
            time,
            NullLogger<AfterSalesRequestService>.Instance);
        var usage = new RecordingTokenUsageRecorder();
        var created = new List<string>();
        var chatFactory = new AgentChatClientFactory(
            new StaticOptionsMonitor<AiSettings>(new AiSettings
            {
                Provider = nameof(AiProvider.AzureOpenAI),
                Endpoint = "https://unit-test.openai.azure.com",
                ApiKey = model is null ? string.Empty : "key",
                Model = "my-deployment",
            }),
            new StaticOptionsMonitor<SlowOperationSettings>(new SlowOperationSettings()),
            usage,
            new FakeAiCallLogRecorder(),
            new StubAiQuotaService(),
            audit,
            currentUser,
            NullLoggerFactory.Instance)
        {
            InnerClientFactory = (ai, _) =>
            {
                created.Add(ai.Model);
                return model ?? throw new InvalidOperationException("The model must not be created during replay.");
            },
        };
        var runs = new PreReviewRunService(factory, time, NullLogger<PreReviewRunService>.Instance);
        var traces = new AgentTraceService(factory, time, NullLogger<AgentTraceService>.Instance);
        var provider = new PreReviewChatClientProvider(
            chatFactory,
            new StubSystemPromptProvider(),
            new AgentRecordingService(factory, time, NullLogger<AgentRecordingService>.Instance),
            traces,
            cursors,
            runs,
            demo,
            new StaticOptionsMonitor<AfterSalesAgentSettings>(settings ?? new AfterSalesAgentSettings()),
            NullLoggerFactory.Instance);

        return new PreReviewNodeKit
        {
            Photo = new ModelPhotoReviewer(provider, requests, NullLogger<ModelPhotoReviewer>.Instance),
            History = new ModelHistoryReviewer(provider, requests, traces, runs),
            Summary = new ModelPreReviewSummarizer(provider, requests),
            Runs = runs,
            Requests = requests,
            Usage = usage,
            CreatedModels = created,
            Time = time,
        };
    }

    /// <summary>比照 Intake：替這筆申請開一輪初審並組出申請事實（不含個資）。</summary>
    public async Task<PreReviewCase> CaseAsync(int requestId)
    {
        var run = await Runs.CreateAsync(requestId) ?? throw new InvalidOperationException("run not created");
        var detail = await Requests.GetReviewDetailAsync(requestId) ?? throw new InvalidOperationException("request missing");
        return new PreReviewCase(
            run.Id,
            requestId,
            detail.CaseKey,
            detail.Type,
            detail.ProductName,
            detail.ProductCategory,
            detail.Color,
            detail.Size,
            detail.TargetColor,
            detail.TargetSize,
            AfterSalesEligibility.DaysSinceDelivery(detail.DeliveredAt, Time.GetLocalNow().DateTime),
            detail.IsWorn,
            detail.Description,
            detail.Attachments.Select(x => x.Id).ToList());
    }

    /// <summary>模型的結構化輸出回應（camelCase 的 JSON 文字）。</summary>
    public static ChatMessage Json(object value)
        => new(ChatRole.Assistant, JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    /// <summary>依序排好照片、歷史、摘要三個節點的模型回應（錄製與回放測試共用）。</summary>
    public static void ScriptThreeNodes(ScriptedAgentModel model)
    {
        model.Respond(Json(new { verdict = "consistent", observations = new[] { "鞋面可見明顯尺寸不合的擠壓" }, confidence = "medium" }));
        model.Respond(Json(new { priorRequestCount = 0, notes = Array.Empty<string>(), customerStatements = new[] { "此顧客說沒穿過" } }));
        model.Respond(Json(new { recommendation = "approve", headline = "建議核准換尺碼", reasons = new[] { "符合規則" }, staffChecklist = new[] { "確認庫存" } }));
    }

    /// <summary>依序跑照片、歷史、摘要三個節點（規則核對以固定的「符合資格」結論代替）。</summary>
    public async Task<(PreReviewFinding Photo, PreReviewFinding History, PreReviewSummary Summary)> RunThreeNodesAsync(PreReviewCase reviewCase)
    {
        var photo = await Photo.ReviewAsync(reviewCase, CancellationToken.None);
        var history = await History.ReviewAsync(reviewCase, CancellationToken.None);
        var rule = new PreReviewFinding(PreReviewNode.RuleCheck, FindingStatus.Ok, "符合規則",
            JsonSerializer.Serialize(new RuleCheckResult(1, "換尺碼", 1, 2, true, [], [], true), PreReviewJsonContext.Default.RuleCheckResult));
        var summary = await Summary.SummarizeAsync(reviewCase, [photo, rule, history], CancellationToken.None);
        return (photo, history, summary);
    }

    /// <summary>在檔案根目錄寫一張照片並掛到申請上，回傳照片的位元組。</summary>
    public static async Task<byte[]> AddPhotoAsync(IDbContextFactory<BackendDBContext> factory, string fileRoot, int requestId, string name, DateTime now)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("fake-jpeg-" + name);
        var relative = $"prereview/{name}";
        Directory.CreateDirectory(Path.Combine(fileRoot, "prereview"));
        await File.WriteAllBytesAsync(Path.Combine(fileRoot, "prereview", name), bytes);
        await using var ctx = await factory.CreateDbContextAsync();
        ctx.AfterSalesAttachment.Add(new AfterSalesAttachment
        {
            AfterSalesRequestId = requestId,
            OriginalFileName = name,
            StoredFileName = name,
            RelativePath = relative,
            ContentType = "image/jpeg",
            FileSize = bytes.Length,
            CreatedAt = now,
        });
        await ctx.SaveChangesAsync();
        return bytes;
    }
}

/// <summary>
/// AI 初審的三個模型節點（M4 Task 5）：送給模型的內容（照片為 <see cref="DataContent"/>、沒有個資）、結構化輸出的對應、
/// 看圖模型的設定。摘要的降級與結構化輸出無效時的程式摘要要跑整個 Workflow，在 <see cref="PreReviewWorkflowTests"/>。
/// </summary>
public sealed class PreReviewModelNodeTests : IAsyncLifetime
{
    private static readonly DateTimeOffset UtcNow = new(2026, 10, 23, 2, 0, 0, TimeSpan.Zero);
    private static readonly DateTime Now = new(2026, 10, 23, 10, 0, 0);

    private const string CustomerName = "王小明";
    private const string CustomerAccount = "wang-account";
    private const string CustomerEmail = "wang@example.com";
    private const string RecipientPhone = "0912345678";
    private const string ShippingAddress = "台北市信義區松仁路 100 號";
    private const string RecipientName = "陳收件";

    private readonly string fileRoot = Path.Combine(Path.GetTempPath(), "PreReviewModelNodeTests-" + Guid.NewGuid().ToString("N"));
    private readonly ScriptedAgentModel model = new();
    private SqliteConnection connection = null!;
    private TestDbContextFactory factory = null!;
    private int customerId;
    private int variantId;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(fileRoot);
        connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        factory = new TestDbContextFactory(connection);
        await using var ctx = factory.CreateDbContext();
        await ctx.Database.EnsureCreatedAsync();
        var user = new MyUser { Account = CustomerAccount, Password = "x", Name = CustomerName, Email = CustomerEmail };
        ctx.MyUser.Add(user);
        var product = new Product { Code = "P1", Name = "跑鞋", Category = ProductCategories.Shoes, Price = 1999m };
        var variant = new ProductVariant { Product = product, Color = "黑", Size = "26", Stock = 5 };
        ctx.ProductVariant.Add(variant);
        await ctx.SaveChangesAsync();
        customerId = user.Id;
        variantId = variant.Id;
    }

    public async Task DisposeAsync()
    {
        await connection.DisposeAsync();
        if (Directory.Exists(fileRoot))
        {
            Directory.Delete(fileRoot, recursive: true);
        }
    }

    private PreReviewNodeKit Kit(AfterSalesAgentSettings? settings = null)
        => PreReviewNodeKit.Create(factory, fileRoot, model, TestDemoControl.Off(factory), new ReplayCursorStore(), new ManualTimeProvider(UtcNow), settings);

    private async Task<int> AddRequestAsync(
        AfterSalesType type = AfterSalesType.QualityIssue,
        AfterSalesStatus status = AfterSalesStatus.Submitted,
        string description = "鞋底開膠",
        Guid? caseKey = null,
        DateTime? createdAt = null)
    {
        await using var ctx = factory.CreateDbContext();
        var order = new SalesOrder
        {
            OrderNo = "A" + Guid.NewGuid().ToString("N")[..8],
            CustomerUserId = customerId,
            OrderedAt = Now.AddDays(-5),
            DeliveredAt = Now.AddDays(-2),
            RecipientName = RecipientName,
            ShippingAddress = ShippingAddress,
            RecipientPhone = RecipientPhone,
            Items = [new SalesOrderItem { ProductVariantId = variantId, Quantity = 1, UnitPrice = 1999m }],
        };
        ctx.SalesOrder.Add(order);
        await ctx.SaveChangesAsync();
        var row = new AfterSalesRequest
        {
            RequestNo = "AS" + Guid.NewGuid().ToString("N")[..12],
            CaseKey = caseKey ?? Guid.NewGuid(),
            CustomerUserId = customerId,
            SalesOrderItemId = order.Items[0].Id,
            Type = type,
            Status = status,
            Channel = AfterSalesChannel.Agent,
            IsWorn = false,
            Description = description,
            CreatedAt = createdAt ?? Now,
            UpdatedAt = createdAt ?? Now,
        };
        ctx.AfterSalesRequest.Add(row);
        await ctx.SaveChangesAsync();
        return row.Id;
    }

    private static string PromptText(IReadOnlyList<ChatMessage> request)
        => string.Join("\n", request.SelectMany(m => m.Contents).OfType<TextContent>().Select(x => x.Text));

    private void AssertNoPersonalData(string text)
    {
        Assert.DoesNotContain(CustomerName, text);
        Assert.DoesNotContain(CustomerAccount, text);
        Assert.DoesNotContain(CustomerEmail, text);
        Assert.DoesNotContain(RecipientPhone, text);
        Assert.DoesNotContain("0912-345-678", text);
        Assert.DoesNotContain(ShippingAddress, text);
        Assert.DoesNotContain(RecipientName, text);
        Assert.DoesNotContain($"\"{customerId}\"", text);
    }

    /// <summary>顧客自己打的字裡夾帶全部個資（姓名、收件人、帳號、Email、地址、手機）。</summary>
    private const string TextWithPersonalData =
        $"我是{CustomerName}，收件人{RecipientName}，帳號 {CustomerAccount}，寄到{ShippingAddress}，電話 0912-345-678，Email {CustomerEmail}";

    /// <summary><see cref="TextWithPersonalData"/> 遮蔽後的樣子。</summary>
    private const string RedactedText = "我是此顧客，收件人此顧客，帳號 ［已遮蔽］，寄到［已遮蔽］，電話 ［已遮蔽］，Email ［已遮蔽］";

    [Fact]
    public async Task Photo_SendsImagesAsDataContent_NoPersonalData()
    {
        var requestId = await AddRequestAsync(description: "鞋底開膠。" + TextWithPersonalData);
        var first = await PreReviewNodeKit.AddPhotoAsync(factory, fileRoot, requestId, "a.jpg", Now);
        var second = await PreReviewNodeKit.AddPhotoAsync(factory, fileRoot, requestId, "b.jpg", Now);
        var kit = Kit();
        var reviewCase = await kit.CaseAsync(requestId);
        model.Respond(PreReviewNodeKit.Json(new { verdict = "consistent", observations = new[] { "鞋底前緣開膠" }, confidence = "high" }));

        var finding = await kit.Photo.ReviewAsync(reviewCase, CancellationToken.None);

        var request = Assert.Single(model.Requests);
        var images = request.SelectMany(m => m.Contents).OfType<DataContent>().ToList();
        Assert.Equal(2, images.Count);
        Assert.Equal(first, images[0].Data.ToArray());
        Assert.Equal(second, images[1].Data.ToArray());
        Assert.All(images, x => Assert.Equal("image/jpeg", x.MediaType));
        var text = PromptText(request);
        Assert.Contains(AfterSalesLabels.TypeText(AfterSalesType.QualityIssue), text);
        Assert.Contains("顧客描述：鞋底開膠。" + RedactedText, text);
        AssertNoPersonalData(text);

        Assert.Equal(PreReviewNode.PhotoReview, finding.Node);
        Assert.Equal(FindingStatus.Ok, finding.Status);
        Assert.Equal("照片與描述一致（信心：高）", finding.Headline);
        var result = JsonSerializer.Deserialize(finding.Json, PreReviewJsonContext.Default.PhotoReviewResult)!;
        Assert.Equal(PhotoReviewResult.Consistent, result.Verdict);
        Assert.Equal(["鞋底前緣開膠"], result.Observations);

        // 背景的系統呼叫：記在 AI 初審作業下、沒有使用者；同一個案件以 CaseKey 串起來。
        var usage = Assert.Single(kit.Usage.Entries);
        Assert.Equal(TokenUsageOperations.AfterSalesPreReview, usage.Operation);
        Assert.Null(usage.UserId);
    }

    [Fact]
    public async Task Photo_NoAttachments_NoModelCall()
    {
        var kit = Kit();
        var reviewCase = await kit.CaseAsync(await AddRequestAsync());

        var finding = await kit.Photo.ReviewAsync(reviewCase, CancellationToken.None);

        Assert.Equal(FindingStatus.NotApplicable, finding.Status);
        Assert.Equal(0, model.CallCount);
        Assert.Empty(kit.CreatedModels);
    }

    /// <summary>一張照片檔不見了：略過那一張，其他照片照送，節點仍是 Ok；一張都讀不到才失敗。</summary>
    [Fact]
    public async Task Photo_OneFileMissing_SkippedOthersSent_AllMissing_Throws()
    {
        var requestId = await AddRequestAsync();
        await PreReviewNodeKit.AddPhotoAsync(factory, fileRoot, requestId, "gone.jpg", Now);
        var kept = await PreReviewNodeKit.AddPhotoAsync(factory, fileRoot, requestId, "kept.jpg", Now);
        File.Delete(Path.Combine(fileRoot, "prereview", "gone.jpg"));
        var kit = Kit();
        var reviewCase = await kit.CaseAsync(requestId);
        model.Respond(PreReviewNodeKit.Json(new { verdict = "consistent", observations = Array.Empty<string>(), confidence = "medium" }));

        var finding = await kit.Photo.ReviewAsync(reviewCase, CancellationToken.None);

        Assert.Equal(FindingStatus.Ok, finding.Status);
        var image = Assert.Single(Assert.Single(model.Requests).SelectMany(m => m.Contents).OfType<DataContent>());
        Assert.Equal(kept, image.Data.ToArray());

        File.Delete(Path.Combine(fileRoot, "prereview", "kept.jpg"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Photo.ReviewAsync(reviewCase, CancellationToken.None));
        Assert.Single(model.Requests);
    }

    [Fact]
    public async Task Photo_ModelVerdictOutsideAllowedValues_BecomesUnclearLow()
    {
        var requestId = await AddRequestAsync();
        await PreReviewNodeKit.AddPhotoAsync(factory, fileRoot, requestId, "a.jpg", Now);
        var kit = Kit();
        model.Respond(PreReviewNodeKit.Json(new { verdict = "approve", observations = Array.Empty<string>(), confidence = "100%" }));

        var finding = await kit.Photo.ReviewAsync(await kit.CaseAsync(requestId), CancellationToken.None);

        var result = JsonSerializer.Deserialize(finding.Json, PreReviewJsonContext.Default.PhotoReviewResult)!;
        Assert.Equal(PhotoReviewResult.Unclear, result.Verdict);
        Assert.Equal("low", result.Confidence);
        Assert.Equal("照片無法判斷（信心：低）", finding.Headline);
    }

    [Fact]
    public async Task History_PromptExcludesNameAddressPhone()
    {
        var caseKey = Guid.NewGuid();
        await AddRequestAsync(AfterSalesType.SizeExchange, AfterSalesStatus.Approved, "太小", createdAt: Now.AddDays(-30));
        var requestId = await AddRequestAsync(caseKey: caseKey);
        var traces = new AgentTraceService(factory, new ManualTimeProvider(UtcNow), NullLogger<AgentTraceService>.Instance);
        var runId = await traces.StartRunAsync(caseKey, customerId);
        await traces.AppendStepAsync(runId, AgentStepKind.UserMessage, AgentDataSource.Conversation, TextWithPersonalData, null);
        await traces.AppendStepAsync(runId, AgentStepKind.ToolResult, AgentDataSource.Database, "查到 1 筆訂單：跑鞋 黑 / 26", "{}");
        await traces.AppendStepAsync(runId, AgentStepKind.ModelReply, AgentDataSource.Conversation, "模型的回覆不送給歷史初審", null);
        var kit = Kit();
        model.Respond(PreReviewNodeKit.Json(new
        {
            priorRequestCount = 5,
            notes = new[] { "30 天前曾換尺碼並核准" },
            customerStatements = new[] { "此顧客說鞋底開膠" },
        }));

        var finding = await kit.History.ReviewAsync(await kit.CaseAsync(requestId), CancellationToken.None);

        var text = PromptText(Assert.Single(model.Requests));
        AssertNoPersonalData(text);
        Assert.Contains("- 顧客：" + RedactedText, text);
        Assert.Contains("查到 1 筆訂單：跑鞋 黑 / 26", text);
        Assert.Contains($"{AfterSalesLabels.TypeText(AfterSalesType.SizeExchange)}（{ProductCategories.Shoes}）：{AfterSalesLabels.StatusText(AfterSalesStatus.Approved)}", text);
        Assert.DoesNotContain("模型的回覆", text);
        Assert.Empty(model.Requests[0].SelectMany(m => m.Contents).OfType<DataContent>());

        // 過往筆數以程式查到的為準。
        var result = JsonSerializer.Deserialize(finding.Json, PreReviewJsonContext.Default.HistoryReviewResult)!;
        Assert.Equal(1, result.PriorRequestCount);
        Assert.Equal(["此顧客說鞋底開膠"], result.CustomerStatements);
        Assert.Equal("此顧客過往售後申請 1 筆，1 項注意事項", finding.Headline);
    }

    /// <summary>
    /// 送件掛勾在「建立申請」工具裡（commit 之後）就建立這一輪並入列，守門員要等工具回來才寫它的結果；顧客之後也可能繼續聊。
    /// 歷史節點只取這一輪建立當下（含）以前的軌跡、不含建立申請工具自己的結果 → 背景早跑或晚跑，提示詞都一樣（回放的請求摘要才穩定）。
    /// </summary>
    [Fact]
    public async Task History_PromptSameBeforeAndAfterCreateToolResult()
    {
        var caseKey = Guid.NewGuid();
        var requestId = await AddRequestAsync(caseKey: caseKey);
        var time = new ManualTimeProvider(UtcNow);
        var traces = new AgentTraceService(factory, time, NullLogger<AgentTraceService>.Instance);
        var traceRunId = await traces.StartRunAsync(caseKey, customerId);
        await traces.AppendStepAsync(traceRunId, AgentStepKind.UserMessage, AgentDataSource.Conversation, "鞋底開膠了", null);
        await traces.AppendStepAsync(traceRunId, AgentStepKind.ToolResult, AgentDataSource.Database, "查到 1 筆訂單商品", "{}");
        await traces.AppendStepAsync(traceRunId, AgentStepKind.ToolRequest, AgentDataSource.Conversation, "你已確認，Agent 送出「品質問題」售後申請",
            JsonSerializer.Serialize(new { tool = AfterSalesAgentTools.CreateAfterSalesRequestName, arguments = new { issueType = "quality_issue" } }));
        var kit = PreReviewNodeKit.Create(factory, fileRoot, model, TestDemoControl.Off(factory), new ReplayCursorStore(), time);
        var reviewCase = await kit.CaseAsync(requestId);
        var reply = PreReviewNodeKit.Json(new { priorRequestCount = 0, notes = Array.Empty<string>(), customerStatements = Array.Empty<string>() });

        // 背景搶先跑：建立申請的工具還沒回來。
        model.Respond(reply);
        await kit.History.ReviewAsync(reviewCase, CancellationToken.None);

        // 工具回來後才寫的結果（與這一輪同一個時間點，時間分不出先後），以及之後顧客繼續聊的內容。
        await traces.AppendStepAsync(traceRunId, AgentStepKind.ProgramCheck, AgentDataSource.Program, "程式核對你的登入身分：與這段對話的顧客相符", null);
        await traces.AppendStepAsync(traceRunId, AgentStepKind.ToolResult, AgentDataSource.Database, "售後申請已建立並回查確認，申請編號 AS202610230001", "{}");
        time.Advance(TimeSpan.FromMinutes(1));
        var laterRunId = await traces.StartRunAsync(caseKey, customerId);
        await traces.AppendStepAsync(laterRunId, AgentStepKind.UserMessage, AgentDataSource.Conversation, "請問多久會處理？", null);
        model.Respond(reply);
        await kit.History.ReviewAsync(reviewCase, CancellationToken.None);

        Assert.Equal(2, model.Requests.Count);
        var early = PromptText(model.Requests[0]);
        var late = PromptText(model.Requests[1]);
        Assert.Equal(early, late);
        Assert.Contains("- 顧客：鞋底開膠了", late);
        Assert.Contains("- 查詢結果：查到 1 筆訂單商品", late);
        Assert.DoesNotContain("申請編號", late);
        Assert.DoesNotContain("多久會處理", late);
    }

    /// <summary>顧客長期記憶（M5）只是偏好、不是業務事實：記憶工具的結果（成功或失敗）不送歷史初審；其他查詢結果照送。</summary>
    [Fact]
    public async Task HistoryReviewer_ExcludesMemoryResults()
    {
        var caseKey = Guid.NewGuid();
        var requestId = await AddRequestAsync(caseKey: caseKey);
        var traces = new AgentTraceService(factory, new ManualTimeProvider(UtcNow), NullLogger<AgentTraceService>.Instance);
        var runId = await traces.StartRunAsync(caseKey, customerId);
        var rememberRequest = JsonSerializer.Serialize(new { tool = AfterSalesAgentTools.RememberPreferenceName, arguments = new { kind = "shoe_size", value = "41" } });
        await traces.AppendStepAsync(runId, AgentStepKind.UserMessage, AgentDataSource.Conversation, "鞋底開膠了", null);
        await traces.AppendStepAsync(runId, AgentStepKind.ToolRequest, AgentDataSource.Conversation, "你已確認，Agent 記住你的鞋碼偏好", rememberRequest);
        await traces.AppendStepAsync(runId, AgentStepKind.ToolResult, AgentDataSource.Memory, "已記住偏好：鞋碼 41", "{}");
        await traces.AppendStepAsync(runId, AgentStepKind.ToolRequest, AgentDataSource.Conversation, "你已確認，Agent 記住你的溝通偏好", rememberRequest);
        await traces.AppendStepAsync(runId, AgentStepKind.ToolResult, AgentDataSource.Memory, "不符條件：溝通偏好請在 40 字以內。", "{}");
        await traces.AppendStepAsync(runId, AgentStepKind.ToolRequest, AgentDataSource.Conversation, "Agent 決定先查你的訂單",
            JsonSerializer.Serialize(new { tool = AfterSalesAgentTools.QueryMyOrdersName, arguments = new { } }));
        await traces.AppendStepAsync(runId, AgentStepKind.ToolResult, AgentDataSource.Database, "查到 1 筆訂單商品", "{}");
        var kit = Kit();
        model.Respond(PreReviewNodeKit.Json(new { priorRequestCount = 0, notes = Array.Empty<string>(), customerStatements = Array.Empty<string>() }));

        await kit.History.ReviewAsync(await kit.CaseAsync(requestId), CancellationToken.None);

        var text = PromptText(Assert.Single(model.Requests));
        Assert.Contains("- 顧客：鞋底開膠了", text);
        Assert.Contains("- 查詢結果：查到 1 筆訂單商品", text);
        Assert.DoesNotContain("記住", text);
        Assert.DoesNotContain("鞋碼", text);
        Assert.DoesNotContain("溝通偏好", text);
    }

    [Fact]
    public async Task VisionModel_Empty_FallsBackToMainModel()
    {
        var requestId = await AddRequestAsync();
        await PreReviewNodeKit.AddPhotoAsync(factory, fileRoot, requestId, "a.jpg", Now);
        var photo = PreReviewNodeKit.Json(new { verdict = "unclear", observations = Array.Empty<string>(), confidence = "low" });
        var history = PreReviewNodeKit.Json(new { priorRequestCount = 0, notes = Array.Empty<string>(), customerStatements = Array.Empty<string>() });

        var fallback = Kit(new AfterSalesAgentSettings { VisionModel = string.Empty });
        model.Respond(photo);
        await fallback.Photo.ReviewAsync(await fallback.CaseAsync(requestId), CancellationToken.None);
        Assert.Equal(["my-deployment"], fallback.CreatedModels);

        // 有填看圖模型：只有照片節點改用它，用量記的是實際的模型；其他節點仍用 AiSettings:Model。
        var vision = Kit(new AfterSalesAgentSettings { VisionModel = "vision-deployment" });
        var reviewCase = await vision.CaseAsync(requestId);
        model.Respond(photo);
        await vision.Photo.ReviewAsync(reviewCase, CancellationToken.None);
        model.Respond(history);
        await vision.History.ReviewAsync(reviewCase, CancellationToken.None);
        Assert.Equal(["vision-deployment", "my-deployment"], vision.CreatedModels);
        Assert.Equal(["vision-deployment", "my-deployment"], vision.Usage.Entries.Select(x => x.Model));
    }

    [Fact]
    public async Task Summary_SendsFindingsJson_WithoutPolicyId_MapsStructuredOutput()
    {
        var kit = Kit();
        var reviewCase = await kit.CaseAsync(await AddRequestAsync());
        var rule = new RuleCheckResult(987654, "鞋類品質問題", 2, 2, true, [], [], null);
        IReadOnlyList<PreReviewFinding> findings =
        [
            new(PreReviewNode.PhotoReview, FindingStatus.Failed, "照片初審失敗，請客服自行檢視照片", "{}"),
            new(PreReviewNode.RuleCheck, FindingStatus.Ok, "符合「鞋類品質問題」（第 2 版）",
                JsonSerializer.Serialize(rule, PreReviewJsonContext.Default.RuleCheckResult)),
            // 歷史節點的模型轉述了顧客原話（含個資）：摘要節點送出前同樣要遮蔽。
            new(PreReviewNode.HistoryReview, FindingStatus.Ok, "此顧客沒有過往售後申請",
                JsonSerializer.Serialize(new HistoryReviewResult(0, [], [TextWithPersonalData]), PreReviewJsonContext.Default.HistoryReviewResult)),
        ];
        model.Respond(PreReviewNodeKit.Json(new
        {
            recommendation = "approve",
            headline = "符合規則，建議核准",
            reasons = new[] { "簽收 2 天" },
            staffChecklist = new[] { "檢視照片" },
        }));

        var summary = await kit.Summary.SummarizeAsync(reviewCase, findings, CancellationToken.None);

        var text = PromptText(Assert.Single(model.Requests));
        Assert.Contains("rule_check", text);
        Assert.Contains("鞋類品質問題", text);
        Assert.DoesNotContain("987654", text);
        Assert.Contains("Failed", text);
        AssertNoPersonalData(text);
        Assert.Contains(RedactedText, text);
        Assert.Equal(PreReviewRecommendations.Approve, summary.Recommendation);
        Assert.Equal("符合規則，建議核准", summary.Headline);
        Assert.Equal(["簽收 2 天"], summary.Reasons);
        Assert.Equal(["檢視照片"], summary.StaffChecklist);
        Assert.Null(summary.Warning);
    }
}

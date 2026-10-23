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
using CustomerHelpAgent.Models.Others;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Agents;
using CustomerHelpAgent.Share.Shop;
using CustomerHelpAgent.Web.Agents.AfterSales;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 售後 Agent 的 6 個受控工具：身分只來自登入使用者（不來自模型參數）、只回模型需要的欄位、
/// 失敗一律以 <see cref="ToolOutcome"/> 信封回報。資料取自 <see cref="DemoSeedData"/>（與正式啟動相同的 seeder）。
/// </summary>
public sealed class AfterSalesAgentToolsTests : IAsyncLifetime
{
    /// <summary>本地時間（UTC+8）2026/10/23 10:00。</summary>
    private static readonly DateTimeOffset UtcNow = new(2026, 10, 23, 2, 0, 0, TimeSpan.Zero);

    private static readonly string[] ToolNames =
    [
        "query_my_orders", "get_effective_policy", "check_variant_stock",
        "track_shipment", "request_photo_upload", "create_after_sales_request",
    ];

    private readonly string fileRoot = Path.Combine(Path.GetTempPath(), "AgentToolsTests-" + Guid.NewGuid().ToString("N"));
    private readonly RecordingAuditLogService audit = new();
    private readonly AgentPhotoInbox inbox = new();
    private readonly Guid sessionKey = Guid.NewGuid();
    private SqliteConnection connection = null!;
    private TestDbContextFactory factory = null!;
    private BackendDBContext permissionContext = null!;
    private int customer1;
    private int customer2;

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

    private AfterSalesAgentTools CreateTools(int? currentUserId = null, AfterSalesToolContext? context = null)
    {
        var time = new ManualTimeProvider(UtcNow);
        var currentUser = new CurrentUserService
        {
            CurrentUser = new CurrentUser { Id = currentUserId ?? customer1, Account = "customer1" },
        };
        var settings = new SystemSettings();
        settings.ExternalFileSystem.AfterSalesFilePath = fileRoot;
        var policyService = new AfterSalesPolicyService(factory, audit, currentUser, TestDemoControl.Off(factory), time, NullLogger<AfterSalesPolicyService>.Instance);
        var orderService = new ShopOrderService(factory, TestDemoControl.Off(factory), NullLogger<ShopOrderService>.Instance);
        var requestService = new AfterSalesRequestService(
            factory,
            policyService,
            orderService,
            new AfterSalesFileStore(Options.Create(settings), NullLogger<AfterSalesFileStore>.Instance),
            audit,
            currentUser,
            time,
            NullLogger<AfterSalesRequestService>.Instance);
        return new AfterSalesAgentTools(
            orderService,
            policyService,
            requestService,
            new PermissionChecker(permissionContext, NullLogger<PermissionChecker>.Instance),
            currentUser,
            inbox,
            TestDemoControl.Off(factory),
            time,
            NullLogger<AfterSalesAgentTools>.Instance,
            context ?? new AfterSalesToolContext { CustomerUserId = customer1, SessionKey = sessionKey });
    }

    private static AIFunction Function(AfterSalesAgentTools tools, string name)
        => tools.CreateFunctions().Single(x => x.Name == name);

    private static async Task<(ToolOutcome Outcome, string Json)> InvokeAsync(
        AfterSalesAgentTools tools, string name, Dictionary<string, object?>? args = null)
    {
        var result = await Function(tools, name).InvokeAsync(new AIFunctionArguments(args ?? []));
        Assert.True(ToolOutcome.TryParse(result, out var outcome));
        return (outcome!, JsonSerializer.Serialize(result, ToolOutcome.JsonOptions));
    }

    private async Task<int> ItemIdAsync(string orderNo)
    {
        await using var db = factory.CreateDbContext();
        return await db.SalesOrderItem.Where(x => x.SalesOrder!.OrderNo == orderNo).Select(x => x.Id).SingleAsync();
    }

    private static JsonElement Data(ToolOutcome outcome) => Assert.IsType<JsonElement>(outcome.Data);

    [Fact]
    public async Task QueryMyOrders_ReturnsOnlyOwnItemsWithoutAddressOrPhone()
    {
        var (outcome, json) = await InvokeAsync(CreateTools(), "query_my_orders");

        Assert.Equal(ToolOutcomeStatus.Ok, outcome.Status);
        var items = Data(outcome).EnumerateArray().ToList();
        Assert.Equal(["SO-DEMO-0001", "SO-DEMO-0002"], items.Select(x => x.GetProperty("order_no").GetString()).Order());
        Assert.Contains("\"status\":\"ok\"", json);
        Assert.DoesNotContain("示範路", json);
        Assert.DoesNotContain("0900-000-001", json);
        Assert.DoesNotContain("王大同", json);
        Assert.DoesNotContain("2680", json);
        Assert.Contains("地址", outcome.WithheldFields);
        Assert.Contains("手機", outcome.WithheldFields);
    }

    [Fact]
    public async Task QueryMyOrders_ColorFilter_MatchesColorName()
    {
        var (outcome, _) = await InvokeAsync(CreateTools(), "query_my_orders", new() { ["color"] = "黑色" });

        var item = Assert.Single(Data(outcome).EnumerateArray());
        Assert.Equal("40", item.GetProperty("size").GetString());
    }

    [Fact]
    public async Task QueryMyOrders_ExtraUserIdArgument_IsIgnored()
    {
        var (outcome, _) = await InvokeAsync(CreateTools(), "query_my_orders", new()
        {
            ["customerUserId"] = customer2,
            ["userId"] = customer2,
        });

        Assert.Equal(ToolOutcomeStatus.Ok, outcome.Status);
        Assert.Equal(["SO-DEMO-0001", "SO-DEMO-0002"], Data(outcome).EnumerateArray().Select(x => x.GetProperty("order_no").GetString()).Order());
    }

    [Fact]
    public async Task Tool_OtherCustomersItem_ReturnsNotFound()
    {
        var otherItem = await ItemIdAsync("SO-DEMO-0004");
        var tools = CreateTools();

        foreach (var (name, args) in new (string, Dictionary<string, object?>)[]
        {
            ("get_effective_policy", new() { ["orderItemId"] = otherItem, ["issueType"] = "shipment_urge" }),
            ("check_variant_stock", new() { ["orderItemId"] = otherItem }),
            ("track_shipment", new() { ["orderItemId"] = otherItem }),
            ("request_photo_upload", new() { ["orderItemId"] = otherItem, ["reason"] = "品質問題" }),
            ("create_after_sales_request", new() { ["orderItemId"] = otherItem, ["issueType"] = "shipment_urge", ["description"] = "催件" }),
        })
        {
            var (outcome, json) = await InvokeAsync(tools, name, args);
            Assert.Equal(ToolOutcomeStatus.NotFound, outcome.Status);
            Assert.Contains("\"status\":\"not_found\"", json);
            Assert.DoesNotContain("範例街", json);
        }

        await using var db = factory.CreateDbContext();
        Assert.False(await db.AfterSalesRequest.AnyAsync());
    }

    [Fact]
    public async Task GetEffectivePolicy_ComputesDaysByProgram()
    {
        var black40 = await ItemIdAsync("SO-DEMO-0001");

        var (outcome, _) = await InvokeAsync(CreateTools(), "get_effective_policy", new()
        {
            ["orderItemId"] = black40,
            ["issueType"] = "size_exchange",
        });

        Assert.Equal(ToolOutcomeStatus.Ok, outcome.Status);
        var card = Data(outcome);
        Assert.Equal("SIZE-EXCHANGE", card.GetProperty("code").GetString());
        Assert.Equal(3, card.GetProperty("days_since_delivery").GetInt32());
        Assert.Contains("是否穿過", card.GetProperty("missing_info").EnumerateArray().Select(x => x.GetString()));
        Assert.Empty(card.GetProperty("unmet_reasons").EnumerateArray());
        Assert.Equal(DemoSeedData.PolicySource, card.GetProperty("source").GetString());
    }

    [Fact]
    public async Task GetEffectivePolicy_WithIsWorn_NoLongerMissingWornInfo()
    {
        var black40 = await ItemIdAsync("SO-DEMO-0001");
        var tools = CreateTools();

        var (unworn, _) = await InvokeAsync(tools, "get_effective_policy", new()
        {
            ["orderItemId"] = black40,
            ["issueType"] = "size_exchange",
            ["isWorn"] = false,
        });
        var (worn, _) = await InvokeAsync(tools, "get_effective_policy", new()
        {
            ["orderItemId"] = black40,
            ["issueType"] = "size_exchange",
            ["isWorn"] = true,
        });

        Assert.Equal(ToolOutcomeStatus.Ok, unworn.Status);
        Assert.Empty(Data(unworn).GetProperty("missing_info").EnumerateArray());
        Assert.Empty(Data(unworn).GetProperty("unmet_reasons").EnumerateArray());
        Assert.DoesNotContain("是否穿過", Data(worn).GetProperty("missing_info").EnumerateArray().Select(x => x.GetString()));
        Assert.NotEmpty(Data(worn).GetProperty("unmet_reasons").EnumerateArray());
        var description = Function(tools, "get_effective_policy").JsonSchema
            .GetProperty("properties").GetProperty("isWorn").GetProperty("description").GetString();
        Assert.Contains("明確", description);
    }

    [Fact]
    public async Task GetEffectivePolicy_Conflict_ReturnsConflictStatus()
    {
        var black40 = await ItemIdAsync("SO-DEMO-0001");
        await using (var db = factory.CreateDbContext())
        {
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

        var (outcome, json) = await InvokeAsync(CreateTools(), "get_effective_policy", new()
        {
            ["orderItemId"] = black40,
            ["issueType"] = "size_exchange",
        });

        Assert.Equal(ToolOutcomeStatus.Conflict, outcome.Status);
        Assert.Contains("\"status\":\"conflict\"", json);
    }

    [Fact]
    public async Task GetEffectivePolicy_NoPolicy_ReturnsNoPolicy()
    {
        var black40 = await ItemIdAsync("SO-DEMO-0001");
        await using (var db = factory.CreateDbContext())
        {
            await db.AfterSalesPolicy
                .Where(x => x.Type == AfterSalesType.ReturnRefund)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.EffectiveTo, new DateTime(2026, 1, 1)));
        }

        var (outcome, json) = await InvokeAsync(CreateTools(), "get_effective_policy", new()
        {
            ["orderItemId"] = black40,
            ["issueType"] = "return_refund",
        });

        Assert.Equal(ToolOutcomeStatus.NoPolicy, outcome.Status);
        Assert.Contains("\"status\":\"no_policy\"", json);
    }

    [Fact]
    public async Task GetEffectivePolicy_UnknownIssueType_ReturnsInvalid()
    {
        var black40 = await ItemIdAsync("SO-DEMO-0001");

        var (outcome, _) = await InvokeAsync(CreateTools(), "get_effective_policy", new()
        {
            ["orderItemId"] = black40,
            ["issueType"] = "refund_everything",
        });

        Assert.Equal(ToolOutcomeStatus.Invalid, outcome.Status);
    }

    [Fact]
    public async Task CheckVariantStock_ReturnsAvailabilityNotQuantity()
    {
        var black40 = await ItemIdAsync("SO-DEMO-0001");

        var (outcome, json) = await InvokeAsync(CreateTools(), "check_variant_stock", new() { ["orderItemId"] = black40 });

        Assert.Equal(ToolOutcomeStatus.Ok, outcome.Status);
        var options = Data(outcome).EnumerateArray().ToList();
        Assert.Equal(14, options.Count);
        bool InStock(string color, string size) => options
            .Single(x => x.GetProperty("color").GetString() == color && x.GetProperty("size").GetString() == size)
            .GetProperty("in_stock").GetBoolean();
        Assert.False(InStock("白", "42"));
        Assert.True(InStock("黑", "41"));
        Assert.DoesNotContain("\"stock\"", json);
        Assert.Contains("庫存數量", outcome.WithheldFields);
    }

    [Fact]
    public async Task TrackShipment_UndeliveredOverThreeDays_CanUrge()
    {
        var greyL = await ItemIdAsync("SO-DEMO-0004");

        var (outcome, json) = await InvokeAsync(CreateTools(customer2, new AfterSalesToolContext { CustomerUserId = customer2, SessionKey = sessionKey }),
            "track_shipment", new() { ["orderItemId"] = greyL });

        Assert.Equal(ToolOutcomeStatus.Ok, outcome.Status);
        var shipment = Data(outcome);
        Assert.True(shipment.GetProperty("can_urge").GetBoolean());
        Assert.Equal("示範物流", shipment.GetProperty("carrier").GetString());
        Assert.Equal(3, shipment.GetProperty("events").GetArrayLength());
        Assert.DoesNotContain("範例街", json);
        Assert.DoesNotContain("陳怡君", json);
        Assert.DoesNotContain("0900-000-002", json);
    }

    [Fact]
    public async Task Tool_WhenUserLostPermission_ReturnsForbidden()
    {
        await using (var db = factory.CreateDbContext())
        {
            await db.UserRole.Where(x => x.MyUserId == customer1).ExecuteDeleteAsync();
            await db.MyUser.Where(x => x.Id == customer1).ExecuteUpdateAsync(s => s.SetProperty(x => x.RoleViewId, (int?)null));
        }

        var (outcome, json) = await InvokeAsync(CreateTools(), "query_my_orders");

        Assert.Equal(ToolOutcomeStatus.Forbidden, outcome.Status);
        Assert.Contains("\"status\":\"forbidden\"", json);
        Assert.DoesNotContain("SO-DEMO", json);
    }

    [Fact]
    public async Task Tool_WhenSignedOutOrSessionBelongsToSomeoneElse_ReturnsForbidden()
    {
        var (signedOut, _) = await InvokeAsync(CreateTools(currentUserId: 0), "query_my_orders");
        var (otherSession, _) = await InvokeAsync(
            CreateTools(customer1, new AfterSalesToolContext { CustomerUserId = customer2, SessionKey = sessionKey }),
            "query_my_orders");

        Assert.Equal(ToolOutcomeStatus.Forbidden, signedOut.Status);
        Assert.Equal(ToolOutcomeStatus.Forbidden, otherSession.Status);
    }

    [Fact]
    public async Task Tool_WhenServiceSlow_ReturnsTimeout()
    {
        var tools = CreateTools(context: new AfterSalesToolContext
        {
            CustomerUserId = customer1,
            SessionKey = sessionKey,
            ToolTimeout = TimeSpan.FromMilliseconds(50),
        });
        tools.BeforeServiceCallForTesting = ct => Task.Delay(TimeSpan.FromSeconds(30), CancellationToken.None);

        var (outcome, json) = await InvokeAsync(tools, "query_my_orders");

        Assert.Equal(ToolOutcomeStatus.Timeout, outcome.Status);
        Assert.True(outcome.Retryable);
        Assert.Contains("\"status\":\"timeout\"", json);
    }

    [Fact]
    public async Task Tool_WhenCallerCancels_Throws()
    {
        var tools = CreateTools();
        tools.BeforeServiceCallForTesting = ct => Task.Delay(TimeSpan.FromSeconds(30), ct);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Function(tools, "query_my_orders").InvokeAsync(new AIFunctionArguments(), cts.Token).AsTask());
    }

    [Fact]
    public void ToolSchemas_DoNotExposeUserIdentifiers()
    {
        var functions = CreateTools().CreateFunctions();

        Assert.Equal(ToolNames.Order(), functions.Select(x => x.Name).Order());
        foreach (var function in functions)
        {
            var schema = function.JsonSchema.GetRawText();
            Assert.DoesNotContain("user", schema, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("customer", schema, StringComparison.OrdinalIgnoreCase);
        }

        Assert.IsType<ApprovalRequiredAIFunction>(functions.Single(x => x.Name == "create_after_sales_request"));
        Assert.All(functions.Where(x => x.Name != "create_after_sales_request"), f => Assert.IsNotType<ApprovalRequiredAIFunction>(f));
    }

    [Fact]
    public async Task RequestPhotoUpload_OwnItem_SetsAwaitingPhotoUpload()
    {
        var context = new AfterSalesToolContext { CustomerUserId = customer1, SessionKey = sessionKey };
        var white41 = await ItemIdAsync("SO-DEMO-0002");

        var (outcome, _) = await InvokeAsync(CreateTools(context: context), "request_photo_upload", new()
        {
            ["orderItemId"] = white41,
            ["reason"] = "請上傳開膠處照片",
        });

        Assert.Equal(ToolOutcomeStatus.Ok, outcome.Status);
        Assert.Equal(AgentStopReason.AwaitingPhotoUpload, context.StopReason);
    }

    [Fact]
    public async Task CreateAfterSalesRequest_Eligible_SubmitsAgentRequestAndNotifiesHook()
    {
        var created = new List<int>();
        var events = new List<AgentTraceEvent>();
        var context = new AfterSalesToolContext
        {
            CustomerUserId = customer1,
            SessionKey = sessionKey,
            Emit = e =>
            {
                events.Add(e);
                return Task.CompletedTask;
            },
            OnRequestCreated = id =>
            {
                created.Add(id);
                return Task.CompletedTask;
            },
        };
        var black40 = await ItemIdAsync("SO-DEMO-0001");

        var (outcome, _) = await InvokeAsync(CreateTools(context: context), "create_after_sales_request", new()
        {
            ["orderItemId"] = black40,
            ["issueType"] = "size_exchange",
            ["targetSize"] = "41",
            ["isWorn"] = false,
            ["description"] = "尺寸偏小",
        });

        Assert.Equal(ToolOutcomeStatus.Ok, outcome.Status);
        var requestNo = Data(outcome).GetProperty("request_no").GetString();
        Assert.Matches(new Regex(@"^AS\d{12}$"), requestNo!);
        await using var db = factory.CreateDbContext();
        var row = await db.AfterSalesRequest.SingleAsync();
        Assert.Equal(requestNo, row.RequestNo);
        Assert.Equal(AfterSalesChannel.Agent, row.Channel);
        Assert.Equal(sessionKey, row.CaseKey);
        Assert.Equal(customer1, row.CustomerUserId);
        Assert.Equal("41", row.TargetSize);
        Assert.Equal([row.Id], created);
        Assert.Contains(events, e => e.Kind == AgentStepKind.ProgramCheck && e.Source == AgentDataSource.Program);
    }

    [Fact]
    public async Task CreateAfterSalesRequest_RecheckFails_ReturnsInvalidWithoutWriting()
    {
        var black40 = await ItemIdAsync("SO-DEMO-0001");

        var (outcome, _) = await InvokeAsync(CreateTools(), "create_after_sales_request", new()
        {
            ["orderItemId"] = black40,
            ["issueType"] = "size_exchange",
            ["targetSize"] = "41",
            ["isWorn"] = true,
            ["description"] = "尺寸偏小",
        });

        Assert.Equal(ToolOutcomeStatus.Invalid, outcome.Status);
        Assert.Contains("穿過", outcome.Message);
        await using var db = factory.CreateDbContext();
        Assert.False(await db.AfterSalesRequest.AnyAsync());
    }

    [Fact]
    public async Task CreateAfterSalesRequest_QualityIssue_AttachesInboxPhotos()
    {
        var white41 = await ItemIdAsync("SO-DEMO-0002");
        Assert.True(inbox.Add(sessionKey, "crack.jpg", [1, 2, 3]));

        var (outcome, _) = await InvokeAsync(CreateTools(), "create_after_sales_request", new()
        {
            ["orderItemId"] = white41,
            ["issueType"] = "quality_issue",
            ["description"] = "鞋底開膠",
        });

        Assert.Equal(ToolOutcomeStatus.Ok, outcome.Status);
        Assert.Equal(0, inbox.Count(sessionKey));
        await using var db = factory.CreateDbContext();
        var attachment = await db.AfterSalesAttachment.SingleAsync();
        Assert.Equal("crack.jpg", attachment.OriginalFileName);
    }

    private async Task<Dictionary<string, object?>> SizeExchangeArgsAsync() => new()
    {
        ["orderItemId"] = await ItemIdAsync("SO-DEMO-0001"),
        ["issueType"] = "size_exchange",
        ["targetSize"] = "41",
        ["isWorn"] = false,
        ["description"] = "尺寸偏小",
    };

    private async Task<int> RequestCountAsync()
    {
        await using var db = factory.CreateDbContext();
        return await db.AfterSalesRequest.CountAsync();
    }

    [Fact]
    public async Task Create_VerifyTimesOutAfterCommit_StillReturnsOk()
    {
        var created = new List<int>();
        var tools = CreateTools(context: new AfterSalesToolContext
        {
            CustomerUserId = customer1,
            SessionKey = sessionKey,
            ToolTimeout = TimeSpan.FromSeconds(1),
            OnRequestCreated = id =>
            {
                created.Add(id);
                return Task.CompletedTask;
            },
        });
        tools.CreateStageHookForTesting = (stage, _) => stage == AfterSalesAgentTools.VerifyStage
            ? Task.Delay(TimeSpan.FromSeconds(30), CancellationToken.None)
            : Task.CompletedTask;

        var (outcome, _) = await InvokeAsync(tools, "create_after_sales_request", await SizeExchangeArgsAsync());

        Assert.Equal(ToolOutcomeStatus.Ok, outcome.Status);
        Assert.Matches(new Regex(@"^AS\d{12}$"), Data(outcome).GetProperty("request_no").GetString()!);
        var requestId = Data(outcome).GetProperty("request_id").GetInt32();
        Assert.Equal([requestId], created);
        Assert.Equal(1, await RequestCountAsync());
    }

    [Fact]
    public async Task Create_HookThrowsAfterCommit_StillReturnsOk()
    {
        var tools = CreateTools(context: new AfterSalesToolContext
        {
            CustomerUserId = customer1,
            SessionKey = sessionKey,
            OnRequestCreated = _ => throw new InvalidOperationException("confirmation store down"),
        });

        var (outcome, _) = await InvokeAsync(tools, "create_after_sales_request", await SizeExchangeArgsAsync());

        Assert.Equal(ToolOutcomeStatus.Ok, outcome.Status);
        Assert.Matches(new Regex(@"^AS\d{12}$"), Data(outcome).GetProperty("request_no").GetString()!);
        Assert.Equal(1, await RequestCountAsync());
    }

    [Fact]
    public async Task Create_CallerCancelsDuringSubmit_StillCompletesAndLinksConfirmation()
    {
        var created = new List<int>();
        var tools = CreateTools(context: new AfterSalesToolContext
        {
            CustomerUserId = customer1,
            SessionKey = sessionKey,
            OnRequestCreated = id =>
            {
                created.Add(id);
                return Task.CompletedTask;
            },
        });
        using var cts = new CancellationTokenSource();
        tools.CreateStageHookForTesting = async (stage, _) =>
        {
            if (stage == AfterSalesAgentTools.SubmitStage)
            {
                // 顧客在送件當下關閉或重新整理頁面。
                cts.Cancel();
            }
            else
            {
                // 讓寫入後的收尾真的非同步等待（SQLite 測試庫的查詢會同步完成，看不出是否綁了呼叫端的取消）。
                await Task.Yield();
            }
        };

        var result = await Function(tools, "create_after_sales_request")
            .InvokeAsync(new AIFunctionArguments(await SizeExchangeArgsAsync()), cts.Token);

        Assert.True(ToolOutcome.TryParse(result, out var outcome));
        Assert.Equal(ToolOutcomeStatus.Ok, outcome!.Status);
        var requestId = Data(outcome).GetProperty("request_id").GetInt32();
        Assert.Equal([requestId], created);
        Assert.Equal(1, await RequestCountAsync());
    }

    [Fact]
    public async Task Create_SubmitThrows_ReturnsNonRetryableUnavailable()
    {
        Assert.True(inbox.Add(sessionKey, "a.jpg", [1, 2, 3]));
        var tools = CreateTools();
        tools.CreateStageHookForTesting = (stage, _) => stage == AfterSalesAgentTools.SubmitStage
            ? throw new InvalidOperationException("database down")
            : Task.CompletedTask;

        var (outcome, json) = await InvokeAsync(tools, "create_after_sales_request", await SizeExchangeArgsAsync());

        Assert.Equal(ToolOutcomeStatus.Unavailable, outcome.Status);
        Assert.False(outcome.Retryable);
        Assert.Equal("申請可能未完成，請到「我的申請」確認，請勿重複送出。", outcome.Message);
        Assert.Contains("\"status\":\"unavailable\"", json);
        Assert.Equal(1, inbox.Count(sessionKey));
        Assert.Equal(0, await RequestCountAsync());
    }

    [Fact]
    public void PhotoInbox_RejectsFourthPhotoAndOversize()
    {
        var box = new AgentPhotoInbox();
        var key = Guid.NewGuid();

        Assert.False(box.Add(key, "big.jpg", new byte[AfterSalesRequestService.MaxFileSize + 1]));
        Assert.True(box.Add(key, "1.jpg", [1]));
        Assert.True(box.Add(key, "2.jpg", [2]));
        Assert.True(box.Add(key, "3.jpg", [3]));
        Assert.False(box.Add(key, "4.jpg", [4]));
        Assert.Equal(3, box.Count(key));
        Assert.Equal(0, box.Count(Guid.NewGuid()));

        var files = box.Take(key);
        Assert.Equal(["1.jpg", "2.jpg", "3.jpg"], files.Select(x => x.FileName));
        Assert.All(files, f => Assert.Equal(1, f.FileSize));
        Assert.Equal(0, box.Count(key));

        Assert.True(box.Add(key, "5.jpg", [5]));
        box.Clear(key);
        Assert.Empty(box.Take(key));
    }
}

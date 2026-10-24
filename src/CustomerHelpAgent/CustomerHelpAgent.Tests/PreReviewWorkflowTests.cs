using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Others;
using CustomerHelpAgent.Share.Demo;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Agents;
using CustomerHelpAgent.Share.Shop;
using CustomerHelpAgent.Web.Agents.AfterSales;
using CustomerHelpAgent.Web.Agents.PreReview;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 初審 Workflow 圖（假 reviewer）：三路併行、fan-in 摘要只跑一次、人工決策前不寫入、核准執行與通知、
/// 執行失敗回到人工決策、重啟後從 SQLite checkpoint 續跑（已完成的節點不重跑）。
/// 用檔案型 SQLite（每個操作各自一條連線）：三個併行節點同時寫入節點狀態，共用單一 in-memory 連線會互相干擾。
/// </summary>
public sealed class PreReviewWorkflowTests : IAsyncLifetime
{
    // UTC 02:00 = 本地（UTC+8）10:00
    private static readonly DateTimeOffset UtcNow = new(2026, 10, 23, 2, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);
    private const int StaffUserId = 900;

    private readonly string dbPath = Path.Combine(Path.GetTempPath(), "PreReviewWorkflowTests-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly string fileRoot = Path.Combine(Path.GetTempPath(), "PreReviewWorkflowFiles-" + Guid.NewGuid().ToString("N"));
    private readonly RecordingAuditLogService audit = new();
    private readonly RecordingNotificationSender sender = new();
    private readonly PreReviewSignal signal = new(NullLogger<PreReviewSignal>.Instance);
    private FileDbContextFactory factory = null!;
    private int alice;
    private int productId;
    private int blackSize27;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(fileRoot);
        factory = new FileDbContextFactory(dbPath);
        await using var ctx = factory.CreateDbContext();
        await ctx.Database.EnsureCreatedAsync();

        var user = new MyUser { Account = "alice", Password = "x", Name = "Alice" };
        ctx.MyUser.Add(user);
        var product = new Product { Code = "P1", Name = "跑鞋", Category = ProductCategories.Shoes, Price = 1999m };
        var v26 = new ProductVariant { Product = product, Color = "黑", Size = "26", Stock = 5 };
        var v27 = new ProductVariant { Product = product, Color = "黑", Size = "27", Stock = 2 };
        var v28 = new ProductVariant { Product = product, Color = "黑", Size = "28", Stock = 0 };
        ctx.ProductVariant.AddRange(v26, v27, v28);
        ctx.AfterSalesPolicy.Add(new AfterSalesPolicy
        {
            Code = "EX-SIZE",
            Type = AfterSalesType.SizeExchange,
            Category = ProductCategories.Shoes,
            Title = "鞋類換尺碼",
            Content = "簽收 7 天內、未穿著可換尺碼。",
            Source = "售後規則",
            Version = 1,
            EffectiveFrom = Now().AddDays(-100),
            WindowDays = 7,
            RequiresUnworn = true,
            RequiresPhoto = false,
            CreatedAt = Now().AddDays(-100),
        });
        await ctx.SaveChangesAsync();
        alice = user.Id;
        productId = product.Id;
        blackSize27 = v27.Id;
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { dbPath, dbPath + "-wal", dbPath + "-shm", dbPath + "-journal" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        if (Directory.Exists(fileRoot))
        {
            Directory.Delete(fileRoot, recursive: true);
        }

        return Task.CompletedTask;
    }

    private static DateTime Now() => new(2026, 10, 23, 10, 0, 0);

    [Fact]
    public async Task FanOut_ThreeReviewersRunInSameSuperstep()
    {
        var run = await CreateRunAsync(await AddRequestAsync(withPhoto: true));
        var photoStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var historyStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // 兩個假 reviewer 互相等對方開始：依序執行時先跑的那個等不到對方而逾時，結論變成 Failed。
        var fakes = new Fakes
        {
            Photo = async (_, ct) =>
            {
                photoStarted.TrySetResult();
                await historyStarted.Task.WaitAsync(WaitLimit, ct);
                return Finding(PreReviewNode.PhotoReview, "照片與描述一致");
            },
            History = async (_, ct) =>
            {
                historyStarted.TrySetResult();
                await photoStarted.Task.WaitAsync(WaitLimit, ct);
                return Finding(PreReviewNode.HistoryReview, "沒有過往申請");
            },
        };
        var harness = CreateHarness(fakes);

        var step = 0;
        var invokedAtStep = new Dictionary<string, int>();
        await using (var streaming = await InProcessExecution.RunStreamingAsync(
            harness.Factory.Create(run), new PreReviewStart(run.Id, run.AfterSalesRequestId)))
        {
            await foreach (var evt in streaming.WatchStreamAsync(blockOnPendingRequest: false))
            {
                switch (evt)
                {
                    case SuperStepStartedEvent:
                        step++;
                        break;
                    case ExecutorInvokedEvent invoked when invoked.ExecutorId is not null:
                        invokedAtStep.TryAdd(invoked.ExecutorId, step);
                        break;
                }
            }
        }

        var reviewerSteps = new[]
        {
            invokedAtStep[PreReviewWorkflowFactory.PhotoReviewId],
            invokedAtStep[PreReviewWorkflowFactory.RuleCheckId],
            invokedAtStep[PreReviewWorkflowFactory.HistoryReviewId],
        };
        Assert.Single(reviewerSteps.Distinct());
        Assert.True(reviewerSteps[0] > invokedAtStep[PreReviewWorkflowFactory.IntakeId]);
        Assert.True(invokedAtStep[PreReviewWorkflowFactory.SummaryId] > reviewerSteps[0]);

        var saved = await LoadRunAsync(run.Id);
        Assert.Equal(FindingStatus.Ok, ReadFinding(saved.PhotoJson).Status);
        Assert.Equal(FindingStatus.Ok, ReadFinding(saved.HistoryJson).Status);
    }

    [Fact]
    public async Task FanIn_SummaryRunsOnceWithAllThreeFindings()
    {
        var run = await CreateRunAsync(await AddRequestAsync(withPhoto: true));
        var fakes = new Fakes();
        var harness = CreateHarness(fakes);
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = signal.Subscribe(run.AfterSalesRequestId, () =>
        {
            published.TrySetResult();
            return Task.CompletedTask;
        });

        await harness.Runner.StartAsync(run.Id);

        Assert.Equal(1, fakes.SummaryCalls);
        var findings = Assert.Single(fakes.SummaryInputs);
        Assert.Equal(
            [PreReviewNode.PhotoReview, PreReviewNode.RuleCheck, PreReviewNode.HistoryReview],
            findings.Select(x => x.Node).Order().ToArray());
        Assert.All(findings, x => Assert.False(string.IsNullOrEmpty(x.Headline)));
        var saved = await LoadRunAsync(run.Id);
        Assert.Equal(PreReviewStatus.AwaitingDecision, saved.Status);
        Assert.Equal(PreReviewRecommendations.Approve, saved.Recommendation);
        Assert.False(saved.HasWarning);
        Assert.Equal(NodeStatus.Done, NodeOf(saved, PreReviewNode.Intake));
        Assert.Equal(NodeStatus.Done, NodeOf(saved, PreReviewNode.Summary));
        Assert.Equal(NodeStatus.Running, NodeOf(saved, PreReviewNode.StaffDecision));
        var rule = JsonSerializer.Deserialize(ReadFinding(saved.RuleJson).Json, PreReviewJsonContext.Default.RuleCheckResult)!;
        Assert.True(rule.IsEligible);
        Assert.True(rule.TargetInStock);
        Assert.Equal(2, rule.DaysSinceDelivery);
        await published.Task.WaitAsync(WaitLimit);
    }

    [Fact]
    public async Task BeforeDecision_NoStatusChangeOrFulfilment()
    {
        var requestId = await AddRequestAsync();
        var run = await CreateRunAsync(requestId);
        var harness = CreateHarness(new Fakes());

        await harness.Runner.StartAsync(run.Id);

        await using var ctx = factory.CreateDbContext();
        var request = await ctx.AfterSalesRequest.AsNoTracking().SingleAsync(x => x.Id == requestId);
        Assert.Equal(AfterSalesStatus.Submitted, request.Status);
        Assert.Null(request.DecidedAt);
        Assert.False(await ctx.AfterSalesFulfillment.AnyAsync());
        Assert.Equal(2, (await ctx.ProductVariant.AsNoTracking().SingleAsync(x => x.Id == blackSize27)).Stock);
        Assert.Empty(sender.Requests);
        var saved = await LoadRunAsync(run.Id);
        Assert.Equal(PreReviewStatus.AwaitingDecision, saved.Status);
        Assert.Null(NodeOf(saved, PreReviewNode.Execute));
        Assert.Null(NodeOf(saved, PreReviewNode.Notify));
    }

    [Fact]
    public async Task Decision_Approve_ExecutesAndCompletes()
    {
        var requestId = await AddRequestAsync();
        var run = await CreateRunAsync(requestId);
        var harness = CreateHarness(new Fakes());
        await harness.Runner.StartAsync(run.Id);

        var result = await harness.Runner.SubmitDecisionAsync(run.Id, new StaffDecision(StaffDecisionKind.Approve, "同意換貨", StaffUserId));

        Assert.True(result.Success, result.Message);
        Assert.Equal(AfterSalesStatus.Approved, result.NewStatus);
        Assert.StartsWith("EX", result.FulfillmentNo);
        await using var ctx = factory.CreateDbContext();
        var request = await ctx.AfterSalesRequest.AsNoTracking().SingleAsync(x => x.Id == requestId);
        Assert.Equal(AfterSalesStatus.Approved, request.Status);
        Assert.Equal(StaffUserId, request.DecidedByUserId);
        Assert.Equal(1, await ctx.AfterSalesFulfillment.CountAsync());
        Assert.Equal(1, (await ctx.ProductVariant.AsNoTracking().SingleAsync(x => x.Id == blackSize27)).Stock);
        Assert.Single(sender.Requests);
        var saved = await LoadRunAsync(run.Id);
        Assert.Equal(PreReviewStatus.Completed, saved.Status);
        Assert.NotNull(saved.CompletedAt);
        Assert.Equal(NodeStatus.Done, NodeOf(saved, PreReviewNode.StaffDecision));
        Assert.Equal(NodeStatus.Done, NodeOf(saved, PreReviewNode.Execute));
        Assert.Equal(NodeStatus.Done, NodeOf(saved, PreReviewNode.Notify));

        // 已經執行完：再送一次決定不會再執行。
        var again = await harness.Runner.SubmitDecisionAsync(run.Id, new StaffDecision(StaffDecisionKind.Approve, null, StaffUserId));
        Assert.False(again.Success);
        Assert.Equal(1, await ctx.AfterSalesFulfillment.CountAsync());
    }

    [Fact]
    public async Task Reviewer_Throws_FindingFailed_StillReachesDecision()
    {
        var run = await CreateRunAsync(await AddRequestAsync(withPhoto: true));
        var fakes = new Fakes { Photo = (_, _) => throw new InvalidOperationException("vision model down") };
        var harness = CreateHarness(fakes);

        await harness.Runner.StartAsync(run.Id);

        var saved = await LoadRunAsync(run.Id);
        Assert.Equal(PreReviewStatus.AwaitingDecision, saved.Status);
        var photo = ReadFinding(saved.PhotoJson);
        Assert.Equal(FindingStatus.Failed, photo.Status);
        Assert.DoesNotContain("vision model down", photo.Headline);
        Assert.Equal(NodeStatus.Failed, NodeOf(saved, PreReviewNode.PhotoReview));
        Assert.Equal(3, Assert.Single(fakes.SummaryInputs).Count);
    }

    [Fact]
    public async Task HistoryReviewer_Throws_FindingFailed_StillReachesDecision()
    {
        var run = await CreateRunAsync(await AddRequestAsync());
        var fakes = new Fakes { History = (_, _) => throw new TimeoutException("history model timeout") };
        var harness = CreateHarness(fakes);

        await harness.Runner.StartAsync(run.Id);

        var saved = await LoadRunAsync(run.Id);
        Assert.Equal(PreReviewStatus.AwaitingDecision, saved.Status);
        var history = ReadFinding(saved.HistoryJson);
        Assert.Equal(FindingStatus.Failed, history.Status);
        Assert.DoesNotContain("timeout", history.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(NodeStatus.Failed, NodeOf(saved, PreReviewNode.HistoryReview));
        Assert.Equal(NodeStatus.Running, NodeOf(saved, PreReviewNode.StaffDecision));
        Assert.Equal(1, fakes.SummaryCalls);
    }

    [Fact]
    public async Task PhotoReview_NoAttachments_NotApplicableWithoutModelCall()
    {
        var run = await CreateRunAsync(await AddRequestAsync(withPhoto: false));
        var fakes = new Fakes();
        var harness = CreateHarness(fakes);

        await harness.Runner.StartAsync(run.Id);

        Assert.Equal(0, fakes.PhotoCalls);
        var saved = await LoadRunAsync(run.Id);
        Assert.Equal(FindingStatus.NotApplicable, ReadFinding(saved.PhotoJson).Status);
        Assert.Equal(NodeStatus.Skipped, NodeOf(saved, PreReviewNode.PhotoReview));
        Assert.Equal(PreReviewStatus.AwaitingDecision, saved.Status);
    }

    /// <summary>
    /// Review Focus 5：簽收 40 天、超過 7 天期限，規則核對不符資格；真正的摘要節點（<see cref="ModelPreReviewSummarizer"/>）
    /// 收到的模型結構化輸出仍建議核准 → 程式降為需人工判斷並標警示。
    /// </summary>
    [Fact]
    public async Task Summary_ApproveButIneligible_DowngradedWithWarning()
    {
        var run = await CreateRunAsync(await AddRequestAsync(deliveredDaysAgo: 40));
        var model = new ScriptedAgentModel();
        model.Respond(PreReviewNodeKit.Json(new
        {
            recommendation = "approve",
            headline = "建議核准換貨",
            reasons = new[] { "顧客描述合理" },
            staffChecklist = Array.Empty<string>(),
        }));
        var summarizer = RealSummarizer(model);
        var fakes = new Fakes { Summarize = summarizer.SummarizeAsync };
        var harness = CreateHarness(fakes);

        await harness.Runner.StartAsync(run.Id);

        Assert.Equal(1, model.CallCount);
        Assert.Contains("rule_check", string.Join("\n", model.Requests[0].Select(x => x.Text)));
        var saved = await LoadRunAsync(run.Id);
        Assert.Equal(PreReviewRecommendations.Manual, saved.Recommendation);
        Assert.True(saved.HasWarning);
        var rule = JsonSerializer.Deserialize(ReadFinding(saved.RuleJson).Json, PreReviewJsonContext.Default.RuleCheckResult)!;
        Assert.False(rule.IsEligible);
        Assert.Contains(rule.UnmetReasons, x => x.Contains("7 天", StringComparison.Ordinal));
        var summary = JsonSerializer.Deserialize(ReadFinding(saved.SummaryJson).Json, PreReviewJsonContext.Default.PreReviewSummary)!;
        Assert.Equal(PreReviewRecommendations.Manual, summary.Recommendation);
        Assert.Equal(SummaryExecutor.IneligibleWarning, summary.Warning);
    }

    /// <summary>
    /// 品質問題核准時補寄同規格：同規格沒貨，核准一定失敗 → 規則核對判不符資格（同規格無庫存），AI 的核准建議被降為需人工判斷。
    /// </summary>
    [Fact]
    public async Task RuleCheck_QualityIssueSameSpecOutOfStock_Ineligible()
    {
        await using (var ctx = factory.CreateDbContext())
        {
            ctx.AfterSalesPolicy.Add(new AfterSalesPolicy
            {
                Code = "QI-SHOES",
                Type = AfterSalesType.QualityIssue,
                Category = ProductCategories.Shoes,
                Title = "鞋類品質問題",
                Content = "簽收 30 天內可申請。",
                Source = "售後規則",
                Version = 1,
                EffectiveFrom = Now().AddDays(-100),
                WindowDays = 30,
                RequiresUnworn = false,
                RequiresPhoto = false,
                CreatedAt = Now().AddDays(-100),
            });
            await ctx.SaveChangesAsync();
            await ctx.ProductVariant.Where(x => x.ProductId == productId && x.Size == "26").ExecuteUpdateAsync(s => s.SetProperty(x => x.Stock, 0));
        }

        var run = await CreateRunAsync(await AddRequestAsync(type: AfterSalesType.QualityIssue));
        var harness = CreateHarness(new Fakes());

        await harness.Runner.StartAsync(run.Id);

        var saved = await LoadRunAsync(run.Id);
        var rule = JsonSerializer.Deserialize(ReadFinding(saved.RuleJson).Json, PreReviewJsonContext.Default.RuleCheckResult)!;
        Assert.Equal("鞋類品質問題", rule.PolicyTitle);
        Assert.False(rule.IsEligible);
        Assert.False(rule.TargetInStock);
        Assert.Contains("同規格 黑 / 26 已無庫存，無法補寄", rule.UnmetReasons);
        Assert.Equal(PreReviewRecommendations.Manual, saved.Recommendation);
        var summary = JsonSerializer.Deserialize(ReadFinding(saved.SummaryJson).Json, PreReviewJsonContext.Default.PreReviewSummary)!;
        Assert.Equal(SummaryExecutor.IneligibleWarning, summary.Warning);
    }

    [Fact]
    public async Task RuleCheck_Throws_FindingFailed_ApproveDowngradedWithWarning()
    {
        // 規則服務故障：規則核對節點失敗，沒有規則依據，假摘要的「建議核准」要被降為需人工判斷。
        var run = await CreateRunAsync(await AddRequestAsync());
        var demo = TestDemoControl.Create(factory, enabled: true, new ManualTimeProvider(UtcNow));
        await demo.SetFaultAsync(DemoFault.PolicyServiceUnavailable, true);
        var fakes = new Fakes();
        var harness = CreateHarness(fakes, demo);

        await harness.Runner.StartAsync(run.Id);

        var saved = await LoadRunAsync(run.Id);
        Assert.Equal(PreReviewStatus.AwaitingDecision, saved.Status);
        Assert.Equal(FindingStatus.Failed, ReadFinding(saved.RuleJson).Status);
        Assert.Equal(NodeStatus.Failed, NodeOf(saved, PreReviewNode.RuleCheck));
        Assert.Equal(PreReviewRecommendations.Manual, saved.Recommendation);
        Assert.True(saved.HasWarning);
        var summary = JsonSerializer.Deserialize(ReadFinding(saved.SummaryJson).Json, PreReviewJsonContext.Default.PreReviewSummary)!;
        Assert.Equal(SummaryExecutor.RuleIncompleteWarning, summary.Warning);
        Assert.Equal(1, fakes.SummaryCalls);
    }

    [Fact]
    public async Task Summary_ModelWarning_IsIgnored()
    {
        var run = await CreateRunAsync(await AddRequestAsync());
        var fakes = new Fakes
        {
            Summarize = (_, _, _) => Task.FromResult(new PreReviewSummary(
                PreReviewRecommendations.Approve, "建議核准", ["符合規則"], [], Warning: "模型自己寫的警示")),
        };

        await CreateHarness(fakes).Runner.StartAsync(run.Id);

        var saved = await LoadRunAsync(run.Id);
        Assert.Equal(PreReviewRecommendations.Approve, saved.Recommendation);
        Assert.False(saved.HasWarning);
        Assert.Null(JsonSerializer.Deserialize(ReadFinding(saved.SummaryJson).Json, PreReviewJsonContext.Default.PreReviewSummary)!.Warning);
    }

    /// <summary>摘要模型回的不是合法的結構化輸出（不是 JSON）：真正的摘要節點拋出，executor 改用程式摘要，仍走到人工決策。</summary>
    [Fact]
    public async Task Summary_StructuredOutputInvalid_FallsBackToProgramSummary()
    {
        var run = await CreateRunAsync(await AddRequestAsync());
        var model = new ScriptedAgentModel();
        model.Reply("## 建議核准\n這不是 JSON。");
        var summarizer = RealSummarizer(model);
        var harness = CreateHarness(new Fakes { Summarize = summarizer.SummarizeAsync });

        await harness.Runner.StartAsync(run.Id);

        Assert.Equal(1, model.CallCount);
        var saved = await LoadRunAsync(run.Id);
        Assert.Equal(PreReviewStatus.AwaitingDecision, saved.Status);
        Assert.Equal(PreReviewRecommendations.Manual, saved.Recommendation);
        var finding = ReadFinding(saved.SummaryJson);
        Assert.Equal(FindingStatus.Failed, finding.Status);
        var summary = JsonSerializer.Deserialize(finding.Json, PreReviewJsonContext.Default.PreReviewSummary)!;
        Assert.Equal("AI 摘要無法產生，請依各項初審結果人工判斷", summary.Headline);
        Assert.DoesNotContain("這不是 JSON", finding.Json);
        Assert.Equal(NodeStatus.Failed, NodeOf(saved, PreReviewNode.Summary));
    }

    [Fact]
    public async Task Summarizer_Throws_FallsBackToProgramSummary()
    {
        var run = await CreateRunAsync(await AddRequestAsync());
        var fakes = new Fakes { Summarize = (_, _, _) => throw new InvalidOperationException("bad structured output") };
        var harness = CreateHarness(fakes);

        await harness.Runner.StartAsync(run.Id);

        var saved = await LoadRunAsync(run.Id);
        Assert.Equal(PreReviewStatus.AwaitingDecision, saved.Status);
        Assert.Equal(PreReviewRecommendations.Manual, saved.Recommendation);
        var finding = ReadFinding(saved.SummaryJson);
        Assert.Equal(FindingStatus.Failed, finding.Status);
        var summary = JsonSerializer.Deserialize(finding.Json, PreReviewJsonContext.Default.PreReviewSummary)!;
        Assert.Equal(3, summary.Reasons.Count);
        Assert.Equal(NodeStatus.Failed, NodeOf(saved, PreReviewNode.Summary));
    }

    [Fact]
    public async Task Execute_Failure_LoopsBackToDecision()
    {
        // 換 28 號：無庫存，核准會失敗，申請仍是已提交 → 回到人工決策。
        var requestId = await AddRequestAsync(targetSize: "28");
        var run = await CreateRunAsync(requestId);
        var harness = CreateHarness(new Fakes());
        await harness.Runner.StartAsync(run.Id);

        var failed = await harness.Runner.SubmitDecisionAsync(run.Id, new StaffDecision(StaffDecisionKind.Approve, null, StaffUserId));

        Assert.False(failed.Success);
        Assert.Equal(AfterSalesDecisionService.OutOfStockMessage, failed.Message);
        var saved = await LoadRunAsync(run.Id);
        Assert.Equal(PreReviewStatus.AwaitingDecision, saved.Status);
        Assert.Equal(NodeStatus.Failed, NodeOf(saved, PreReviewNode.Execute));
        Assert.Equal(AfterSalesDecisionService.OutOfStockMessage, saved.Nodes.Single(x => x.Node == PreReviewNode.Execute).Note);
        Assert.Equal(NodeStatus.Running, NodeOf(saved, PreReviewNode.StaffDecision));
        // 上一次失敗的原因：Workflow 內的 DecisionRequest 帶著它，並存在人工決策節點的 Note 供審核頁顯示。
        Assert.Equal(AfterSalesDecisionService.OutOfStockMessage, saved.Nodes.Single(x => x.Node == PreReviewNode.StaffDecision).Note);
        var pending = await ReadPendingDecisionAsync(harness, saved);
        Assert.NotNull(pending);
        Assert.Equal(AfterSalesDecisionService.OutOfStockMessage, pending.PreviousFailure);
        await using (var ctx = factory.CreateDbContext())
        {
            Assert.Equal(AfterSalesStatus.Submitted, (await ctx.AfterSalesRequest.AsNoTracking().SingleAsync(x => x.Id == requestId)).Status);
            Assert.False(await ctx.AfterSalesFulfillment.AnyAsync());
        }

        var rejected = await harness.Runner.SubmitDecisionAsync(run.Id, new StaffDecision(StaffDecisionKind.Reject, "目標尺碼無庫存", StaffUserId));

        Assert.True(rejected.Success, rejected.Message);
        Assert.Equal(AfterSalesStatus.Rejected, rejected.NewStatus);
        saved = await LoadRunAsync(run.Id);
        Assert.Equal(PreReviewStatus.Completed, saved.Status);
        Assert.Equal(NodeStatus.Done, NodeOf(saved, PreReviewNode.Execute));
        Assert.Equal(NodeStatus.Done, NodeOf(saved, PreReviewNode.Notify));
    }

    [Fact]
    public async Task Execute_StatusChangedElsewhere_EndsRunSuperseded()
    {
        var requestId = await AddRequestAsync();
        var run = await CreateRunAsync(requestId);
        var harness = CreateHarness(new Fakes());
        await harness.Runner.StartAsync(run.Id);
        // 另一條路徑（例如另一位客服）已經決定了這筆申請。
        Assert.True((await harness.Decisions.ApplyAsync(requestId, StaffDecisionKind.Reject, "已駁回", StaffUserId)).Success);

        var result = await harness.Runner.SubmitDecisionAsync(run.Id, new StaffDecision(StaffDecisionKind.Approve, null, StaffUserId));

        Assert.False(result.Success);
        Assert.Equal(AfterSalesRequestService.StatusChangedMessage, result.Message);
        var saved = await LoadRunAsync(run.Id);
        Assert.Equal(PreReviewStatus.Superseded, saved.Status);
        Assert.Null(NodeOf(saved, PreReviewNode.Notify));
        // 不再等待人工：Workflow 已結束，沒有可回應的決策。
        var again = await harness.Runner.SubmitDecisionAsync(run.Id, new StaffDecision(StaffDecisionKind.Approve, null, StaffUserId));
        Assert.False(again.Success);
    }

    [Fact]
    public async Task Restart_ResumesFromCheckpoint_CompletedNodesNotRerun()
    {
        var run = await CreateRunAsync(await AddRequestAsync(withPhoto: true));
        // 第一次：三個初審節點都完成（該 superstep 已寫入 checkpoint）後，在摘要節點執行中「當機」。
        var summaryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new Fakes
        {
            Summarize = async (_, _, ct) =>
            {
                summaryEntered.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                throw new InvalidOperationException("unreachable");
            },
        };
        using (var crash = new CancellationTokenSource())
        {
            var firstHarness = CreateHarness(first);
            var start = firstHarness.Runner.StartAsync(run.Id, crash.Token);
            await summaryEntered.Task.WaitAsync(WaitLimit);
            await crash.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start).WaitAsync(WaitLimit);
        }

        Assert.Equal(PreReviewStatus.Running, (await LoadRunAsync(run.Id)).Status);

        // 第二次：全新的 runner／Workflow／服務，只靠資料庫裡的 checkpoint 續跑。
        var second = new Fakes();
        await CreateHarness(second).Runner.ResumeAsync(run.Id).WaitAsync(WaitLimit);

        Assert.Equal(1, first.PhotoCalls + second.PhotoCalls);
        Assert.Equal(1, first.HistoryCalls + second.HistoryCalls);
        Assert.Equal(1, second.SummaryCalls);
        var findings = Assert.Single(second.SummaryInputs);
        Assert.Equal(
            [PreReviewNode.PhotoReview, PreReviewNode.RuleCheck, PreReviewNode.HistoryReview],
            findings.Select(x => x.Node).Order().ToArray());
        Assert.Contains(findings, x => x.Headline == "照片與描述一致");
        var saved = await LoadRunAsync(run.Id);
        Assert.Equal(PreReviewStatus.AwaitingDecision, saved.Status);
        Assert.Equal(PreReviewRecommendations.Approve, saved.Recommendation);
    }

    [Fact]
    public async Task Restart_SummarySavedButFieldsMissing_ResavesRecommendation()
    {
        var run = await CreateRunAsync(await AddRequestAsync());
        // 第一次：三路初審完成後，在摘要節點執行中「當機」。
        var summaryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new Fakes
        {
            Summarize = async (_, _, ct) =>
            {
                summaryEntered.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                throw new InvalidOperationException("unreachable");
            },
        };
        using (var crash = new CancellationTokenSource())
        {
            var start = CreateHarness(first).Runner.StartAsync(run.Id, crash.Token);
            await summaryEntered.Task.WaitAsync(WaitLimit);
            await crash.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start).WaitAsync(WaitLimit);
        }

        // 模擬「摘要結論已存、建議與警示欄位還沒寫」就當機（兩次寫入之間）。
        var summary = new PreReviewSummary(
            PreReviewRecommendations.Manual, "建議人工判斷", ["規則核對未完成"], ["自行核對規則"], SummaryExecutor.RuleIncompleteWarning);
        var finding = new PreReviewFinding(
            PreReviewNode.Summary, FindingStatus.Ok, summary.Headline, JsonSerializer.Serialize(summary, PreReviewJsonContext.Default.PreReviewSummary));
        var runs = new PreReviewRunService(factory, new ManualTimeProvider(UtcNow), NullLogger<PreReviewRunService>.Instance);
        await runs.SaveFindingAsync(run.Id, PreReviewNode.Summary, JsonSerializer.Serialize(finding, PreReviewJsonContext.Default.PreReviewFinding));
        Assert.Null((await LoadRunAsync(run.Id)).Recommendation);

        var second = new Fakes();
        await CreateHarness(second).Runner.ResumeAsync(run.Id).WaitAsync(WaitLimit);

        Assert.Equal(0, second.SummaryCalls);
        var saved = await LoadRunAsync(run.Id);
        Assert.Equal(PreReviewStatus.AwaitingDecision, saved.Status);
        Assert.Equal(PreReviewRecommendations.Manual, saved.Recommendation);
        Assert.True(saved.HasWarning);
    }

    [Fact]
    public async Task Restart_DuringReviewers_FinishedReviewerNotRerun()
    {
        var run = await CreateRunAsync(await AddRequestAsync(withPhoto: true));
        // 第一次：照片與規則都完成並存檔，歷史紀錄還卡著時「當機」——這個 superstep 沒完成，checkpoint 停在進件之後。
        var historyEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new Fakes
        {
            History = async (_, ct) =>
            {
                historyEntered.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                throw new InvalidOperationException("unreachable");
            },
        };
        using (var crash = new CancellationTokenSource())
        {
            var start = CreateHarness(first).Runner.StartAsync(run.Id, crash.Token);
            await historyEntered.Task.WaitAsync(WaitLimit);
            await WaitUntilAsync(async () => await LoadRunAsync(run.Id) is { PhotoJson: not null, RuleJson: not null });
            await crash.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start).WaitAsync(WaitLimit);
        }

        Assert.Equal(PreReviewStatus.Running, (await LoadRunAsync(run.Id)).Status);

        // 第二次：全新的 runner。照片節點看到已存的結論就直接重送，不再呼叫模型；只有歷史紀錄重跑。
        var second = new Fakes();
        await CreateHarness(second).Runner.ResumeAsync(run.Id).WaitAsync(WaitLimit);

        Assert.Equal(1, first.PhotoCalls + second.PhotoCalls);
        Assert.Equal(1, second.HistoryCalls);
        Assert.Equal(1, second.SummaryCalls);
        var findings = Assert.Single(second.SummaryInputs);
        Assert.Contains(findings, x => x.Node == PreReviewNode.PhotoReview && x.Headline == "照片與描述一致");
        var saved = await LoadRunAsync(run.Id);
        Assert.Equal(PreReviewStatus.AwaitingDecision, saved.Status);
        Assert.Equal(NodeStatus.Done, NodeOf(saved, PreReviewNode.PhotoReview));
        Assert.Equal(NodeStatus.Running, NodeOf(saved, PreReviewNode.StaffDecision));
    }

    [Fact]
    public async Task Restart_AwaitingDecision_StillAcceptsDecision()
    {
        var requestId = await AddRequestAsync(withPhoto: true);
        var run = await CreateRunAsync(requestId);
        var first = new Fakes();
        await CreateHarness(first).Runner.StartAsync(run.Id);
        Assert.Equal(PreReviewStatus.AwaitingDecision, (await LoadRunAsync(run.Id)).Status);

        var second = new Fakes();
        var result = await CreateHarness(second).Runner.SubmitDecisionAsync(
            run.Id, new StaffDecision(StaffDecisionKind.Approve, null, StaffUserId));

        Assert.True(result.Success, result.Message);
        Assert.Equal(0, second.PhotoCalls + second.HistoryCalls + second.SummaryCalls);
        Assert.Equal(PreReviewStatus.Completed, (await LoadRunAsync(run.Id)).Status);
        await using var ctx = factory.CreateDbContext();
        Assert.Equal(AfterSalesStatus.Approved, (await ctx.AfterSalesRequest.AsNoTracking().SingleAsync(x => x.Id == requestId)).Status);
    }

    [Fact]
    public async Task Signal_SubscribeAndDispose_CleansUpAndIsolatesFailures()
    {
        var calls = 0;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (signal.Subscribe(7, () => throw new InvalidOperationException("gone")))
        using (signal.Subscribe(7, () =>
        {
            Interlocked.Increment(ref calls);
            done.TrySetResult();
            return Task.CompletedTask;
        }))
        {
            signal.Publish(8);
            signal.Publish(7);
            await done.Task.WaitAsync(WaitLimit);
            Assert.Equal(2, signal.SubscriberCount);
        }

        Assert.Equal(1, calls);
        Assert.Equal(0, signal.KeyCount);
    }

    private static PreReviewFinding Finding(PreReviewNode node, string headline)
        => new(node, FindingStatus.Ok, headline, "{}");

    private static PreReviewFinding ReadFinding(string? json)
        => JsonSerializer.Deserialize(json ?? throw new InvalidOperationException("finding not saved"), PreReviewJsonContext.Default.PreReviewFinding)!;

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + WaitLimit;
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not met in time");
            await Task.Delay(50);
        }
    }

    /// <summary>直接從 checkpoint 續跑，讀出重播的人工決策請求（不回應）。</summary>
    private async Task<DecisionRequest?> ReadPendingDecisionAsync(Harness harness, PreReviewRun run)
    {
        var time = new ManualTimeProvider(UtcNow);
        var manager = CheckpointManager.CreateJson(
            new SqliteWorkflowCheckpointStore(new WorkflowCheckpointService(factory, time, NullLogger<WorkflowCheckpointService>.Instance)),
            PreReviewJsonContext.CheckpointOptions);
        var latest = await manager.GetLatestCheckpointAsync(run.WorkflowSessionId) ?? throw new InvalidOperationException("no checkpoint");
        DecisionRequest? found = null;
        await using var streaming = await InProcessExecution.ResumeStreamingAsync(harness.Factory.Create(run), latest, manager);
        await foreach (var evt in streaming.WatchStreamAsync(blockOnPendingRequest: false))
        {
            if (evt is RequestInfoEvent info && info.Request.TryGetDataAs<DecisionRequest>(out var request))
            {
                found = request;
            }
        }

        return found;
    }

    private static NodeStatus? NodeOf(PreReviewRun run, PreReviewNode node)
        => run.Nodes.SingleOrDefault(x => x.Node == node)?.Status;

    private async Task<PreReviewRun> CreateRunAsync(int requestId)
        => await new PreReviewRunService(factory, new ManualTimeProvider(UtcNow), NullLogger<PreReviewRunService>.Instance).CreateAsync(requestId)
            ?? throw new InvalidOperationException("run not created");

    private async Task<PreReviewRun> LoadRunAsync(int runId)
    {
        var service = new PreReviewRunService(factory, new ManualTimeProvider(UtcNow), NullLogger<PreReviewRunService>.Instance);
        var run = await service.GetAsync(runId) ?? throw new InvalidOperationException("run missing");
        return (await service.GetLatestForRequestAsync(run.AfterSalesRequestId))!;
    }

    /// <summary>每筆申請各自一張訂單（簽收天數可調）；預設換 27 號（有庫存）、未穿過。</summary>
    private async Task<int> AddRequestAsync(
        string targetSize = "27", bool withPhoto = false, int deliveredDaysAgo = 2, AfterSalesType type = AfterSalesType.SizeExchange)
    {
        await using var ctx = factory.CreateDbContext();
        var variant26 = await ctx.ProductVariant.SingleAsync(x => x.ProductId == productId && x.Size == "26");
        var order = new SalesOrder
        {
            OrderNo = "A" + Guid.NewGuid().ToString("N")[..8],
            CustomerUserId = alice,
            OrderedAt = Now().AddDays(-deliveredDaysAgo - 3),
            DeliveredAt = Now().AddDays(-deliveredDaysAgo),
            RecipientName = "王小明",
            ShippingAddress = "台北市某路1號",
            RecipientPhone = "0912345678",
            Items = [new SalesOrderItem { ProductVariantId = variant26.Id, Quantity = 1, UnitPrice = 1999m }],
        };
        ctx.SalesOrder.Add(order);
        await ctx.SaveChangesAsync();

        var row = new AfterSalesRequest
        {
            RequestNo = Guid.NewGuid().ToString("N"),
            CaseKey = Guid.NewGuid(),
            CustomerUserId = alice,
            SalesOrderItemId = order.Items[0].Id,
            Type = type,
            Status = AfterSalesStatus.Submitted,
            Channel = AfterSalesChannel.Agent,
            TargetSize = type == AfterSalesType.SizeExchange ? targetSize : null,
            IsWorn = false,
            Description = "尺碼太小",
            CreatedAt = Now(),
            UpdatedAt = Now(),
        };
        if (withPhoto)
        {
            row.Attachments.Add(new AfterSalesAttachment
            {
                OriginalFileName = "shoe.jpg",
                StoredFileName = "shoe.jpg",
                RelativePath = "x/shoe.jpg",
                ContentType = "image/jpeg",
                FileSize = 10,
                CreatedAt = Now(),
            });
        }

        ctx.AfterSalesRequest.Add(row);
        await ctx.SaveChangesAsync();
        row.RequestNo = $"AS20261023{row.Id:D4}";
        await ctx.SaveChangesAsync();
        return row.Id;
    }

    private Harness CreateHarness(Fakes fakes, DemoControlService? demo = null)
    {
        var time = new ManualTimeProvider(UtcNow);
        demo ??= TestDemoControl.Off(factory);
        var currentUser = new CurrentUserService { CurrentUser = new CurrentUser { Id = StaffUserId, Account = "agent1" } };
        var settings = new SystemSettings();
        settings.ExternalFileSystem.AfterSalesFilePath = fileRoot;
        var policies = new AfterSalesPolicyService(factory, audit, currentUser, demo, time, NullLogger<AfterSalesPolicyService>.Instance);
        var orders = new ShopOrderService(factory, demo, NullLogger<ShopOrderService>.Instance);
        var decisions = TestDecisionService.Create(factory, audit, currentUser, time, sender);
        var requests = new AfterSalesRequestService(
            factory,
            policies,
            orders,
            new AfterSalesFileStore(Options.Create(settings), NullLogger<AfterSalesFileStore>.Instance),
            audit,
            currentUser,
            time,
            NullLogger<AfterSalesRequestService>.Instance);
        var runs = new PreReviewRunService(factory, time, NullLogger<PreReviewRunService>.Instance);
        var workflowFactory = new PreReviewWorkflowFactory(
            runs,
            requests,
            policies,
            orders,
            decisions,
            new FakePhotoReviewer(fakes),
            new FakeHistoryReviewer(fakes),
            new FakeSummarizer(fakes),
            signal,
            time,
            NullLoggerFactory.Instance);
        var runner = new PreReviewWorkflowRunner(
            runs,
            requests,
            new WorkflowCheckpointService(factory, time, NullLogger<WorkflowCheckpointService>.Instance),
            workflowFactory,
            signal,
            NullLogger<PreReviewWorkflowRunner>.Instance);
        return new Harness(runner, workflowFactory, decisions);
    }

    /// <summary>真正的摘要模型節點（模型換成腳本化的 client），交給 <see cref="Fakes.Summarize"/> 使用。</summary>
    private ModelPreReviewSummarizer RealSummarizer(ScriptedAgentModel model)
        => PreReviewNodeKit.Create(factory, fileRoot, model, TestDemoControl.Off(factory), new ReplayCursorStore(), new ManualTimeProvider(UtcNow)).Summary;

    private sealed record Harness(PreReviewWorkflowRunner Runner, PreReviewWorkflowFactory Factory, AfterSalesDecisionService Decisions);

    /// <summary>假 AI 節點的行為與呼叫計數（可在多個 runner 之間比較）。</summary>
    private sealed class Fakes
    {
        public int PhotoCalls;
        public int HistoryCalls;
        public int SummaryCalls;

        public List<IReadOnlyList<PreReviewFinding>> SummaryInputs { get; } = [];

        public Func<PreReviewCase, CancellationToken, Task<PreReviewFinding>> Photo { get; init; }
            = (_, _) => Task.FromResult(Finding(PreReviewNode.PhotoReview, "照片與描述一致"));

        public Func<PreReviewCase, CancellationToken, Task<PreReviewFinding>> History { get; init; }
            = (_, _) => Task.FromResult(Finding(PreReviewNode.HistoryReview, "沒有過往申請"));

        public Func<PreReviewCase, IReadOnlyList<PreReviewFinding>, CancellationToken, Task<PreReviewSummary>> Summarize { get; init; }
            = (_, _, _) => Task.FromResult(new PreReviewSummary(
                PreReviewRecommendations.Approve, "建議核准換貨", ["簽收 2 天、未穿過"], ["確認目標尺碼庫存"]));
    }

    private sealed class FakePhotoReviewer(Fakes fakes) : IPhotoReviewer
    {
        public Task<PreReviewFinding> ReviewAsync(PreReviewCase reviewCase, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref fakes.PhotoCalls);
            return fakes.Photo(reviewCase, cancellationToken);
        }
    }

    private sealed class FakeHistoryReviewer(Fakes fakes) : IHistoryReviewer
    {
        public Task<PreReviewFinding> ReviewAsync(PreReviewCase reviewCase, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref fakes.HistoryCalls);
            return fakes.History(reviewCase, cancellationToken);
        }
    }

    private sealed class FakeSummarizer(Fakes fakes) : IPreReviewSummarizer
    {
        public Task<PreReviewSummary> SummarizeAsync(
            PreReviewCase reviewCase, IReadOnlyList<PreReviewFinding> findings, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref fakes.SummaryCalls);
            lock (fakes.SummaryInputs)
            {
                fakes.SummaryInputs.Add(findings.ToList());
            }

            return fakes.Summarize(reviewCase, findings, cancellationToken);
        }
    }

    private sealed class FileDbContextFactory(string path) : IDbContextFactory<BackendDBContext>
    {
        public BackendDBContext CreateDbContext()
            => new(new DbContextOptionsBuilder<BackendDBContext>()
                .UseSqlite($"Data Source={path};Pooling=False")
                .Options);
    }
}

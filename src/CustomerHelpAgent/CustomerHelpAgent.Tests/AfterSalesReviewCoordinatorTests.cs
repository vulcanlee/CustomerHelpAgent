using Microsoft.Extensions.DependencyInjection;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Models.Shop;
using CustomerHelpAgent.Share.Agents;
using CustomerHelpAgent.Share.Shop;
using CustomerHelpAgent.Web.Agents.PreReview;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 審核頁的決策唯一入口：Agent 管道停在人工決策 → 認領後送進 Workflow；兩位客服同時決定只成功一次；
/// 初審還在跑就直接決定 → 決定生效、這一輪標為已由人工處理，AI 跑完也不會再摘要、執行或通知；傳統管道直接決定；重新初審。
/// </summary>
public sealed class AfterSalesReviewCoordinatorTests
{
    private const int StaffUserId = 900;
    private const int OtherStaffUserId = 901;

    /// <summary>
    /// 決策唯一入口：審核頁經協調器，寫入只在 <see cref="AfterSalesDecisionService.ApplyAsync"/>。
    /// <see cref="AfterSalesRequestService"/> 不再有繞過協調器（不認領、不標記初審）的決策包裝方法，也不再依賴決策服務。
    /// </summary>
    [Fact]
    public void Decisions_OnlyThroughCoordinator_NoLegacyWrappersOnRequestService()
    {
        var methods = typeof(AfterSalesRequestService)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
            .Select(x => x.Name)
            .ToList();

        Assert.DoesNotContain("DecideAsync", methods);
        Assert.DoesNotContain("RequestMoreInfoAsync", methods);
        Assert.DoesNotContain(
            Assert.Single(typeof(AfterSalesRequestService).GetConstructors()).GetParameters(),
            x => x.ParameterType == typeof(AfterSalesDecisionService));
    }

    [Fact]
    public async Task Coordinator_AwaitingDecision_ResumesWorkflow()
    {
        await using var host = await PreReviewTestHost.CreateAsync();
        var requestId = await host.AddRequestAsync();
        var run = (await host.Runs.CreateAsync(requestId))!;
        await host.Worker.ProcessAsync(run.Id, CancellationToken.None);
        Assert.Equal(PreReviewStatus.AwaitingDecision, (await host.Runs.GetAsync(run.Id))!.Status);

        var result = await host.Coordinator(StaffUserId).DecideAsync(requestId, StaffDecisionKind.Approve, null);

        Assert.True(result.Success, result.Message);
        Assert.Equal(AfterSalesStatus.Approved, result.NewStatus);
        Assert.StartsWith("EX", result.FulfillmentNo);
        var saved = await host.LatestRunAsync(requestId);
        Assert.Equal(PreReviewStatus.Completed, saved.Status);
        Assert.Equal(NodeStatus.Done, PreReviewTestHost.NodeOf(saved, PreReviewNode.StaffDecision));
        Assert.Equal(NodeStatus.Done, PreReviewTestHost.NodeOf(saved, PreReviewNode.Execute));
        Assert.Equal(NodeStatus.Done, PreReviewTestHost.NodeOf(saved, PreReviewNode.Notify));
        var request = await host.LoadRequestAsync(requestId);
        Assert.Equal(AfterSalesStatus.Approved, request.Status);
        Assert.Equal(StaffUserId, request.DecidedByUserId);
        Assert.Equal(1, await host.FulfilmentCountAsync(requestId));
        Assert.Single(host.Sender.Requests);
        Assert.Contains(host.Audit.Entries, x => x.Action == AuditActions.Shop.RequestDecide && x.ActorUserId == StaffUserId && x.ActorAccount == $"agent{StaffUserId}");
        // 決定之後的執行是純程式：AI 節點只在初審時各跑一次。
        Assert.Equal(1, host.Fakes.SummaryCalls);
    }

    [Fact]
    public async Task Coordinator_ConcurrentDecision_ResumesOnce()
    {
        await using var host = await PreReviewTestHost.CreateAsync();
        var requestId = await host.AddRequestAsync();
        var run = (await host.Runs.CreateAsync(requestId))!;
        await host.Worker.ProcessAsync(run.Id, CancellationToken.None);
        var first = host.Coordinator(StaffUserId);
        var second = host.Coordinator(OtherStaffUserId);
        var stockBefore = await host.StockAsync("27");
        // 兩位都已讀到「等待決策」，在認領那一刻相遇。
        host.Factory.ArmBarrier(typeof(PreReviewRunService), nameof(PreReviewRunService.TryClaimDecisionAsync), participants: 2);
        // 若輸的一方沒被認領擋下，兩個 Runner 會在這裡相遇、同時續跑同一個 checkpoint；正確時只有一方到達，等 1 秒後自行放行。
        host.Factory.ArmBarrier(typeof(PreReviewWorkflowRunner), nameof(PreReviewWorkflowRunner.SubmitDecisionAsync), participants: 2, TimeSpan.FromSeconds(1));

        var results = await Task.WhenAll(
            Task.Run(() => first.DecideAsync(requestId, StaffDecisionKind.Approve, null)),
            Task.Run(() => second.DecideAsync(requestId, StaffDecisionKind.Approve, null)));

        Assert.Single(results, x => x.Success);
        // 只有認領成功的一方把決定送進 Workflow：執行節點只跑一次，輸的一方根本沒有執行。
        Assert.Equal(1, host.Factory.WorkflowApplyCalls);
        Assert.Equal(1, host.Factory.ApplyCalls);
        // 輸的一方在認領就被擋下：沒有第二個 Runner 同時續跑同一個 checkpoint（那會以例外收場，再靠事後判斷補救）。
        Assert.Empty(host.Errors.Entries);
        var loser = Assert.Single(results, x => !x.Success);
        Assert.Equal(AfterSalesRequestService.StatusChangedMessage, loser.Message);
        Assert.Equal(1, await host.FulfilmentCountAsync(requestId));
        Assert.Equal(stockBefore - 1, await host.StockAsync("27"));
        Assert.Single(host.Sender.Requests);
        Assert.Equal(PreReviewStatus.Completed, (await host.Runs.GetAsync(run.Id))!.Status);
    }

    [Fact]
    public async Task Decision_WhileRunning_SupersedesRun_NoLateExecution()
    {
        await using var host = await PreReviewTestHost.CreateAsync();
        var requestId = await host.AddRequestAsync();
        var run = (await host.Runs.CreateAsync(requestId))!;
        var historyEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHistory = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Fakes.History = async (_, _) =>
        {
            historyEntered.TrySetResult();
            await releaseHistory.Task;
            return new PreReviewFinding(PreReviewNode.HistoryReview, FindingStatus.Ok, "沒有過往申請", "{}");
        };
        var background = host.Worker.ProcessAsync(run.Id, CancellationToken.None);
        await historyEntered.Task.WaitAsync(PreReviewTestHost.WaitLimit);

        // 客服沒等 AI，直接核准。
        var result = await host.Coordinator(StaffUserId).DecideAsync(requestId, StaffDecisionKind.Approve, null);

        Assert.True(result.Success, result.Message);
        Assert.NotNull(result.FulfillmentNo);
        Assert.Equal(PreReviewStatus.Superseded, (await host.Runs.GetAsync(run.Id))!.Status);
        Assert.Single(host.Sender.Requests);

        // AI 現在才跑完：之後的節點一個都不執行。
        releaseHistory.SetResult();
        await background.WaitAsync(PreReviewTestHost.WaitLimit);

        var saved = await host.LatestRunAsync(requestId);
        Assert.Equal(PreReviewStatus.Superseded, saved.Status);
        Assert.Equal(0, host.Fakes.SummaryCalls);
        Assert.Null(saved.SummaryJson);
        Assert.Null(saved.Recommendation);
        Assert.Null(PreReviewTestHost.NodeOf(saved, PreReviewNode.Summary));
        Assert.Null(PreReviewTestHost.NodeOf(saved, PreReviewNode.Execute));
        Assert.Null(PreReviewTestHost.NodeOf(saved, PreReviewNode.Notify));
        Assert.Equal(1, await host.FulfilmentCountAsync(requestId));
        Assert.Equal(1, host.Factory.ApplyCalls);
        Assert.Equal(0, host.Factory.WorkflowApplyCalls);
        Assert.Single(host.Sender.Requests);
        Assert.Single(host.Audit.Entries, x => x.Action == AuditActions.Shop.RequestDecide);
        // 已由人工處理的一輪不會再續跑：晚跑完的節點寫下的 checkpoint 也在 Runner 收尾時清掉。
        Assert.Equal(0, await host.CheckpointCountAsync(run.Id));
    }

    /// <summary>直接決定執行失敗（目標規格無庫存）：申請仍待審，初審維持原狀，不可顯示「已由人工處理」。</summary>
    [Fact]
    public async Task Coordinator_DirectDecisionFails_RunNotSuperseded()
    {
        await using var host = await PreReviewTestHost.CreateAsync();
        var requestId = await host.AddRequestAsync(targetSize: "28");
        var run = (await host.Runs.CreateAsync(requestId))!;
        await host.Runs.SetStatusAsync(run.Id, PreReviewStatus.Failed, "boom");

        var result = await host.Coordinator(StaffUserId).DecideAsync(requestId, StaffDecisionKind.Approve, null);

        Assert.False(result.Success);
        Assert.Equal(AfterSalesDecisionService.OutOfStockMessage, result.Message);
        Assert.Equal(AfterSalesStatus.Submitted, (await host.LoadRequestAsync(requestId)).Status);
        Assert.Equal(PreReviewStatus.Failed, (await host.Runs.GetAsync(run.Id))!.Status);

        // 改駁回成功：這時才標為已由人工處理。
        Assert.True((await host.Coordinator(StaffUserId).DecideAsync(requestId, StaffDecisionKind.Reject, "無庫存")).Success);
        Assert.Equal(PreReviewStatus.Superseded, (await host.Runs.GetAsync(run.Id))!.Status);
    }

    /// <summary>等待決策時保留 checkpoint（決定要從它續跑）；這一輪跑完（Completed）就清掉。</summary>
    [Fact]
    public async Task Checkpoints_KeptWhileAwaitingDecision_DeletedWhenCompleted()
    {
        await using var host = await PreReviewTestHost.CreateAsync();
        var requestId = await host.AddRequestAsync();
        var run = (await host.Runs.CreateAsync(requestId))!;
        await host.Worker.ProcessAsync(run.Id, CancellationToken.None);
        Assert.True(await host.CheckpointCountAsync(run.Id) > 0);

        var result = await host.Coordinator(StaffUserId).DecideAsync(requestId, StaffDecisionKind.Approve, null);

        Assert.True(result.Success, result.Message);
        Assert.Equal(PreReviewStatus.Completed, (await host.Runs.GetAsync(run.Id))!.Status);
        Assert.Equal(0, await host.CheckpointCountAsync(run.Id));
    }

    /// <summary>重新初審取代舊的一輪、直接決定取代失敗的一輪：被取代（Superseded）的一輪不會再續跑，checkpoint 清掉；失敗時保留（診斷用）。</summary>
    [Fact]
    public async Task Checkpoints_DeletedWhenSupersededByRetryOrDirectDecision()
    {
        await using var host = await PreReviewTestHost.CreateAsync();
        var requestId = await host.AddRequestAsync();
        var first = (await host.Runs.CreateAsync(requestId))!;
        await host.Worker.ProcessAsync(first.Id, CancellationToken.None);
        Assert.True(await host.CheckpointCountAsync(first.Id) > 0);

        Assert.True((await host.Coordinator(StaffUserId).RetryAsync(requestId)).Success);

        Assert.Equal(PreReviewStatus.Superseded, (await host.Runs.GetAsync(first.Id))!.Status);
        Assert.Equal(0, await host.CheckpointCountAsync(first.Id));

        var second = Assert.Single(host.DrainQueue());
        await host.Worker.ProcessAsync(second, CancellationToken.None);
        await host.Runs.SetStatusAsync(second, PreReviewStatus.Failed, "boom");
        Assert.True(await host.CheckpointCountAsync(second) > 0);

        Assert.True((await host.Coordinator(StaffUserId).DecideAsync(requestId, StaffDecisionKind.Reject, "超過鑑賞期")).Success);

        Assert.Equal(PreReviewStatus.Superseded, (await host.Runs.GetAsync(second))!.Status);
        Assert.Equal(0, await host.CheckpointCountAsync(second));
    }

    [Fact]
    public async Task Coordinator_RunnerThrowsAfterCommit_ReportsSuccess()
    {
        await using var host = await PreReviewTestHost.CreateAsync();
        var requestId = await host.AddRequestAsync();
        var run = (await host.Runs.CreateAsync(requestId))!;
        await host.Worker.ProcessAsync(run.Id, CancellationToken.None);
        // 決定已執行完（開單、通知），Runner 收尾時資料庫出錯。
        host.Factory.Arm(typeof(PreReviewWorkflowRunner), "FinishAsync", () => Task.FromException(new InvalidOperationException("db down")));

        var result = await host.Coordinator(StaffUserId).DecideAsync(requestId, StaffDecisionKind.Approve, null);

        Assert.True(result.Success, result.Message);
        Assert.StartsWith("EX", result.FulfillmentNo);
        Assert.Equal(PreReviewStatus.Completed, (await host.Runs.GetAsync(run.Id))!.Status);
        Assert.Equal(1, await host.FulfilmentCountAsync(requestId));
    }

    [Fact]
    public async Task Retry_RacingDirectDecision_IntakeEndsRunWithoutModelCalls()
    {
        await using var host = await PreReviewTestHost.CreateAsync();
        var requestId = await host.AddRequestAsync();
        var failed = (await host.Runs.CreateAsync(requestId))!;
        await host.Runs.SetStatusAsync(failed.Id, PreReviewStatus.Failed, "boom");
        Assert.True((await host.Coordinator(StaffUserId).RetryAsync(requestId)).Success);
        var retry = Assert.Single(host.DrainQueue());
        // 同一時間另一位客服直接決定，而且他的 Supersede 早於重新初審建立新的一輪：新的一輪留在 Pending、申請已被決定。
        var applied = await host.Scope(OtherStaffUserId).GetRequiredService<AfterSalesDecisionService>()
            .ApplyAsync(requestId, StaffDecisionKind.Reject, "超過鑑賞期", OtherStaffUserId);
        Assert.True(applied.Success, applied.Message);

        await host.Worker.ProcessAsync(retry, CancellationToken.None);

        var saved = await host.LatestRunAsync(requestId);
        Assert.Equal(retry, saved.Id);
        Assert.Equal(PreReviewStatus.Superseded, saved.Status);
        Assert.Equal(NodeStatus.Skipped, PreReviewTestHost.NodeOf(saved, PreReviewNode.Intake));
        Assert.Null(PreReviewTestHost.NodeOf(saved, PreReviewNode.RuleCheck));
        Assert.Equal(0, host.Fakes.HistoryCalls + host.Fakes.SummaryCalls + host.Fakes.PhotoCalls);
    }

    [Fact]
    public async Task Coordinator_TraditionalChannel_DecidesDirectlyWithoutPreReview()
    {
        await using var host = await PreReviewTestHost.CreateAsync();
        var requestId = await host.AddRequestAsync(AfterSalesChannel.Traditional);

        var result = await host.Coordinator(StaffUserId).DecideAsync(requestId, StaffDecisionKind.Reject, "超過鑑賞期");

        Assert.True(result.Success, result.Message);
        Assert.Equal(AfterSalesStatus.Rejected, (await host.LoadRequestAsync(requestId)).Status);
        Assert.Empty(await host.RunsOfAsync(requestId));
        Assert.Single(host.Sender.Requests);
    }

    [Fact]
    public async Task Coordinator_FailedRun_DecidesDirectly()
    {
        await using var host = await PreReviewTestHost.CreateAsync();
        var requestId = await host.AddRequestAsync();
        var run = (await host.Runs.CreateAsync(requestId))!;
        await host.Runs.SetStatusAsync(run.Id, PreReviewStatus.Failed, "boom");

        var result = await host.Coordinator(StaffUserId).DecideAsync(requestId, StaffDecisionKind.NeedMoreInfo, "請補鞋底照片");

        Assert.True(result.Success, result.Message);
        Assert.Equal(AfterSalesStatus.NeedMoreInfo, (await host.LoadRequestAsync(requestId)).Status);
        Assert.Equal(PreReviewStatus.Superseded, (await host.Runs.GetAsync(run.Id))!.Status);
    }

    [Fact]
    public async Task Coordinator_NeedMoreInfoWithoutNote_RejectedBeforeClaim()
    {
        await using var host = await PreReviewTestHost.CreateAsync();
        var requestId = await host.AddRequestAsync();
        var run = (await host.Runs.CreateAsync(requestId))!;
        await host.Worker.ProcessAsync(run.Id, CancellationToken.None);

        var result = await host.Coordinator(StaffUserId).DecideAsync(requestId, StaffDecisionKind.NeedMoreInfo, "  ");

        Assert.False(result.Success);
        Assert.Equal(AfterSalesDecisionService.MoreInfoRequiredMessage, result.Message);
        var saved = await host.LatestRunAsync(requestId);
        Assert.Equal(PreReviewStatus.AwaitingDecision, saved.Status);
        Assert.Null(PreReviewTestHost.NodeOf(saved, PreReviewNode.Execute));
    }

    [Fact]
    public async Task Coordinator_ExecutingRun_ReportsStatusChanged()
    {
        await using var host = await PreReviewTestHost.CreateAsync();
        var requestId = await host.AddRequestAsync();
        var run = (await host.Runs.CreateAsync(requestId))!;
        await host.Worker.ProcessAsync(run.Id, CancellationToken.None);
        Assert.True(await host.Runs.TryClaimDecisionAsync(run.Id));

        var result = await host.Coordinator(OtherStaffUserId).DecideAsync(requestId, StaffDecisionKind.Reject, "x");

        Assert.False(result.Success);
        Assert.Equal(AfterSalesRequestService.StatusChangedMessage, result.Message);
        Assert.Equal(AfterSalesStatus.Submitted, (await host.LoadRequestAsync(requestId)).Status);
    }

    [Fact]
    public async Task Retry_FailedRun_CreatesNewAttemptAndEnqueues()
    {
        await using var host = await PreReviewTestHost.CreateAsync();
        var requestId = await host.AddRequestAsync();
        var run = (await host.Runs.CreateAsync(requestId))!;
        await host.Runs.SetStatusAsync(run.Id, PreReviewStatus.Failed, "boom");

        var result = await host.Coordinator(StaffUserId).RetryAsync(requestId);

        Assert.True(result.Success, result.Message);
        var runs = await host.RunsOfAsync(requestId);
        Assert.Equal([1, 2], runs.Select(x => x.Attempt));
        Assert.Equal(PreReviewStatus.Pending, runs[1].Status);
        Assert.Equal([runs[1].Id], host.DrainQueue());
        // 傳統管道不能重新初審。
        var traditional = await host.Coordinator(StaffUserId).RetryAsync(await host.AddRequestAsync(AfterSalesChannel.Traditional));
        Assert.False(traditional.Success);
        Assert.Empty(host.DrainQueue());
    }
}

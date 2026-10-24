using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Shop;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Agents;
using CustomerHelpAgent.Share.Shop;
using CustomerHelpAgent.Web.Agents.PreReview;
using CustomerHelpAgent.Web.Diagnostics;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// AI 初審的背景執行：送件掛勾（只有 Agent 管道入列）、worker 在自己的 scope 以系統身分執行、
/// 啟動時續跑未完成／補建漏掉的初審、救回執行到一半當機的決策、佇列滿時不遺失。
/// </summary>
public sealed class PreReviewWorkerTests
{
    private const int StaffUserId = 900;

    [Fact]
    public async Task AgentSubmit_EnqueuesPreReview()
    {
        await using var host = await PreReviewTestHost.CreateAsync();
        var itemId = await host.AddOrderItemAsync();

        var result = await host.Scope(host.Alice, "alice").GetRequiredService<AfterSalesRequestService>()
            .SubmitAsync(host.Alice, SizeExchange(itemId, AfterSalesChannel.Agent), []);

        Assert.True(result.Success, result.Message);
        var run = Assert.Single(await host.RunsOfAsync(result.RequestId!.Value));
        Assert.Equal(1, run.Attempt);
        Assert.Equal(PreReviewStatus.Pending, run.Status);
        Assert.Equal([run.Id], host.DrainQueue());
    }

    [Fact]
    public async Task TraditionalSubmit_NoPreReview()
    {
        await using var host = await PreReviewTestHost.CreateAsync();
        var itemId = await host.AddOrderItemAsync();

        var result = await host.Scope(host.Alice, "alice").GetRequiredService<AfterSalesRequestService>()
            .SubmitAsync(host.Alice, SizeExchange(itemId, AfterSalesChannel.Traditional), []);

        Assert.True(result.Success, result.Message);
        Assert.Empty(await host.RunsOfAsync(result.RequestId!.Value));
        Assert.Empty(host.DrainQueue());
    }

    [Fact]
    public async Task Submit_HookThrows_SubmissionStillSucceeds()
    {
        await using var host = await PreReviewTestHost.CreateAsync(
            configure: s => s.AddScoped<IAfterSalesSubmittedHook>(_ => new ThrowingHook()));
        var itemId = await host.AddOrderItemAsync();

        var result = await host.Scope(host.Alice, "alice").GetRequiredService<AfterSalesRequestService>()
            .SubmitAsync(host.Alice, SizeExchange(itemId, AfterSalesChannel.Agent), []);

        Assert.True(result.Success, result.Message);
        Assert.Equal(AfterSalesStatus.Submitted, (await host.LoadRequestAsync(result.RequestId!.Value)).Status);
    }

    [Fact]
    public async Task Resubmit_CreatesNewAttempt()
    {
        await using var host = await PreReviewTestHost.CreateAsync();
        var requestId = await host.AddRequestAsync(AfterSalesChannel.Agent, AfterSalesStatus.NeedMoreInfo);
        var first = (await host.Runs.CreateAsync(requestId))!;
        await host.Runs.SetStatusAsync(first.Id, PreReviewStatus.Executing);
        await host.Runs.SetStatusAsync(first.Id, PreReviewStatus.Completed);

        var result = await host.Scope(host.Alice, "alice").GetRequiredService<AfterSalesRequestService>()
            .ResubmitAsync(host.Alice, requestId, isWorn: false, "補上鞋盒照片說明", []);

        Assert.True(result.Success, result.Message);
        var runs = await host.RunsOfAsync(requestId);
        Assert.Equal([1, 2], runs.Select(x => x.Attempt));
        Assert.Equal(PreReviewStatus.Completed, runs[0].Status);
        Assert.Equal(PreReviewStatus.Pending, runs[1].Status);
        Assert.Equal([runs[1].Id], host.DrainQueue());
    }

    [Fact]
    public async Task Startup_RequeuesUnfinishedAndMissing()
    {
        await using var host = await PreReviewTestHost.CreateAsync();
        var runs = host.Runs;
        var pending = (await runs.CreateAsync(await host.AddRequestAsync()))!;
        var running = (await runs.CreateAsync(await host.AddRequestAsync()))!;
        await runs.SetStatusAsync(running.Id, PreReviewStatus.Running);
        var awaiting = (await runs.CreateAsync(await host.AddRequestAsync()))!;
        await runs.SetStatusAsync(awaiting.Id, PreReviewStatus.AwaitingDecision);
        var missing = await host.AddRequestAsync();
        var traditional = await host.AddRequestAsync(AfterSalesChannel.Traditional);
        var waitingCustomer = await host.AddRequestAsync(AfterSalesChannel.Agent, AfterSalesStatus.NeedMoreInfo);

        await host.Worker.RecoverAsync(CancellationToken.None);

        var created = Assert.Single(await host.RunsOfAsync(missing));
        Assert.Equal(PreReviewStatus.Pending, created.Status);
        Assert.Equal([pending.Id, running.Id, created.Id], host.DrainQueue());
        Assert.Empty(await host.RunsOfAsync(traditional));
        Assert.Empty(await host.RunsOfAsync(waitingCustomer));
        Assert.Equal(PreReviewStatus.AwaitingDecision, (await runs.GetAsync(awaiting.Id))!.Status);
    }

    [Fact]
    public async Task Worker_RecordsModelCallsAsSystemWithCaseConversationId()
    {
        var model = new ScriptedAgentModel();
        ExceptionContext? contextDuringCall = null;
        await using var host = await PreReviewTestHost.CreateAsync(model);
        var accessor = host.Services.GetRequiredService<ExceptionContextAccessor>();
        // 沒附照片：照片節點不呼叫模型；歷史、摘要依序各呼叫一次。
        model.Fallback = _ =>
        {
            contextDuringCall ??= accessor.Current;
            return model.CallCount == 1
                ? new ChatResponse(PreReviewNodeKit.Json(new { priorRequestCount = 0, notes = Array.Empty<string>(), customerStatements = Array.Empty<string>() }))
                : new ChatResponse(PreReviewNodeKit.Json(new { recommendation = "approve", headline = "建議核准換尺碼", reasons = new[] { "符合規則" }, staffChecklist = new[] { "確認庫存" } }));
        };
        var itemId = await host.AddOrderItemAsync();
        // 顧客在自己的 circuit 送件（目前使用者是顧客）；初審要在 worker 自己的 scope 以系統身分跑。
        var submit = await host.Scope(host.Alice, "alice").GetRequiredService<AfterSalesRequestService>()
            .SubmitAsync(host.Alice, SizeExchange(itemId, AfterSalesChannel.Agent), []);
        var runId = Assert.Single(host.DrainQueue());

        await host.Worker.ProcessAsync(runId, CancellationToken.None);

        var request = await host.LoadRequestAsync(submit.RequestId!.Value);
        var run = await host.LatestRunAsync(request.Id);
        Assert.Equal(PreReviewStatus.AwaitingDecision, run.Status);
        Assert.Equal(PreReviewRecommendations.Approve, run.Recommendation);
        Assert.Equal(2, model.CallCount);
        Assert.Equal(2, host.Usage.Entries.Count);
        Assert.All(host.Usage.Entries, x =>
        {
            Assert.Null(x.UserId);
            Assert.Null(x.Account);
        });
        Assert.Equal(2, host.CallLog.Entries.Count);
        Assert.All(host.CallLog.Entries, x =>
        {
            Assert.Equal(request.CaseKey, x.ConversationId);
            Assert.Null(x.UserId);
        });
        Assert.NotNull(contextDuringCall);
        Assert.Equal(ExceptionSources.Background, contextDuringCall.Source);
        Assert.Equal("PreReview", contextDuringCall.Page);
    }

    [Fact]
    public async Task Worker_QueueFull_RunStaysPendingAndIsRescanned()
    {
        await using var host = await PreReviewTestHost.CreateAsync(
            configure: s => s.AddSingleton(new PreReviewQueue(NullLogger<PreReviewQueue>.Instance, capacity: 1)));
        var first = (await host.Runs.CreateAsync(await host.AddRequestAsync()))!;
        var second = (await host.Runs.CreateAsync(await host.AddRequestAsync()))!;
        Assert.True(host.Queue.TryEnqueue(first.Id));
        Assert.False(host.Queue.TryEnqueue(second.Id));
        Assert.Equal(PreReviewStatus.Pending, (await host.Runs.GetAsync(second.Id))!.Status);

        IHostedService worker = host.Worker;
        await worker.StartAsync(CancellationToken.None);
        try
        {
            // 啟動掃描與溢出後的重新掃描都會把第二輪補進佇列：兩輪最後都停在人工決策。
            await PreReviewTestHost.WaitUntilAsync(async () =>
                (await host.Runs.GetAsync(first.Id))!.Status == PreReviewStatus.AwaitingDecision
                && (await host.Runs.GetAsync(second.Id))!.Status == PreReviewStatus.AwaitingDecision);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        Assert.Equal(2, host.Fakes.SummaryCalls);
    }

    [Fact]
    public async Task Startup_ExecutingRunWithoutResponse_ReturnsToAwaitingDecision()
    {
        await using var host = await PreReviewTestHost.CreateAsync();
        var requestId = await host.AddRequestAsync();
        var run = (await host.Runs.CreateAsync(requestId))!;
        await host.Worker.ProcessAsync(run.Id, CancellationToken.None);
        // 客服認領了決策，還沒把決定送進 Workflow 就當機。
        Assert.True(await host.Runs.TryClaimDecisionAsync(run.Id));

        await host.Worker.RecoverAsync(CancellationToken.None);

        var recovered = await host.LatestRunAsync(requestId);
        Assert.Equal(PreReviewStatus.AwaitingDecision, recovered.Status);
        Assert.Equal(NodeStatus.Running, PreReviewTestHost.NodeOf(recovered, PreReviewNode.StaffDecision));
        Assert.Equal(AfterSalesStatus.Submitted, (await host.LoadRequestAsync(requestId)).Status);
        // 重啟後仍可決策，而且只執行一次。
        var result = await host.Coordinator(StaffUserId).DecideAsync(requestId, StaffDecisionKind.Approve, null);
        Assert.True(result.Success, result.Message);
        Assert.Equal(PreReviewStatus.Completed, (await host.LatestRunAsync(requestId)).Status);
        Assert.Equal(1, await host.FulfilmentCountAsync(requestId));
    }

    [Fact]
    public async Task Startup_ExecutingRunWhoseRequestWasDecided_EndsSuperseded()
    {
        await using var host = await PreReviewTestHost.CreateAsync();
        var requestId = await host.AddRequestAsync();
        var run = (await host.Runs.CreateAsync(requestId))!;
        await host.Worker.ProcessAsync(run.Id, CancellationToken.None);
        Assert.True(await host.Runs.TryClaimDecisionAsync(run.Id));
        // 決定還沒送進 Workflow，申請卻已被決定（例如執行已 commit、checkpoint 還沒寫就當機）。
        var applied = await host.Scope(StaffUserId).GetRequiredService<AfterSalesDecisionService>()
            .ApplyAsync(requestId, StaffDecisionKind.Reject, "超過鑑賞期", StaffUserId);
        Assert.True(applied.Success, applied.Message);

        await host.Worker.RecoverAsync(CancellationToken.None);

        // 不再回到等待決策（申請已經不能決定了）。
        Assert.Equal(PreReviewStatus.Superseded, (await host.Runs.GetAsync(run.Id))!.Status);
        Assert.Equal(AfterSalesStatus.Rejected, (await host.LoadRequestAsync(requestId)).Status);
        Assert.Equal(0, host.Factory.WorkflowApplyCalls);
    }

    [Fact]
    public async Task Startup_ExecutingRunCrashedInsideExecute_CompletesOnce()
    {
        await using var host = await PreReviewTestHost.CreateAsync();
        var requestId = await host.AddRequestAsync();
        var run = (await host.Runs.CreateAsync(requestId))!;
        await host.Worker.ProcessAsync(run.Id, CancellationToken.None);
        // 決定已送進 Workflow、執行節點正在 ApplyAsync（尚未 commit）時「當機」：這個 Runner 就此卡住不再前進。
        var crashed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Factory.Arm(typeof(AfterSalesDecisionService), nameof(AfterSalesDecisionService.ApplyAsync), () =>
        {
            crashed.TrySetResult();
            return never.Task;
        });
        var zombie = host.Coordinator(StaffUserId).DecideAsync(requestId, StaffDecisionKind.Approve, null);
        await crashed.Task.WaitAsync(PreReviewTestHost.WaitLimit);
        Assert.Equal(PreReviewStatus.Executing, (await host.Runs.GetAsync(run.Id))!.Status);

        // 「重啟」：新的 worker 掃描時救回這一輪。
        await host.Worker.RecoverAsync(CancellationToken.None);

        var recovered = await host.LatestRunAsync(requestId);
        var request = await host.LoadRequestAsync(requestId);
        Assert.Equal(PreReviewStatus.Completed, recovered.Status);
        Assert.Equal(AfterSalesStatus.Approved, request.Status);
        Assert.Equal(StaffUserId, request.DecidedByUserId);
        Assert.Equal(1, await host.FulfilmentCountAsync(requestId));
        Assert.Single(host.Sender.Requests);
        // checkpoint 已含客服的決定：救回時執行節點用原本的決定再跑一次（舊 Runner 那次卡在 commit 之前）。
        Assert.Equal(2, host.Factory.WorkflowApplyCalls);

        // 收掉卡住的舊 Runner：它的 ApplyAsync 只會拿到「狀態已變更」，不會再開單，也蓋不掉已完成的狀態。
        never.SetException(new InvalidOperationException("process died"));
        await zombie.WaitAsync(PreReviewTestHost.WaitLimit);
        Assert.Equal(PreReviewStatus.Completed, (await host.Runs.GetAsync(run.Id))!.Status);
        Assert.Equal(1, await host.FulfilmentCountAsync(requestId));
    }

    [Fact]
    public async Task Startup_CrashAfterApplyCommitted_CompletesWithoutSecondExecution()
    {
        await using var host = await PreReviewTestHost.CreateAsync();
        var requestId = await host.AddRequestAsync();
        var run = (await host.Runs.CreateAsync(requestId))!;
        await host.Worker.ProcessAsync(run.Id, CancellationToken.None);
        // 執行節點的 ApplyAsync 已 commit、也已通知顧客，這個 superstep 還沒寫進 checkpoint 就「當機」。
        var crashed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Sender.ArmAfterSend(() =>
        {
            crashed.TrySetResult();
            return never.Task;
        });
        var zombie = host.Coordinator(StaffUserId).DecideAsync(requestId, StaffDecisionKind.Approve, null);
        await crashed.Task.WaitAsync(PreReviewTestHost.WaitLimit);
        Assert.Equal(AfterSalesStatus.Approved, (await host.LoadRequestAsync(requestId)).Status);
        Assert.Equal(PreReviewStatus.Executing, (await host.Runs.GetAsync(run.Id))!.Status);

        await host.Worker.RecoverAsync(CancellationToken.None);

        // 救回時重跑執行節點只拿到「狀態已變更」，但申請正是這個決定生效後的樣子：當成成功收尾，不再開單、不再通知。
        var recovered = await host.LatestRunAsync(requestId);
        Assert.Equal(PreReviewStatus.Completed, recovered.Status);
        Assert.Equal(NodeStatus.Done, PreReviewTestHost.NodeOf(recovered, PreReviewNode.Execute));
        Assert.Equal(NodeStatus.Done, PreReviewTestHost.NodeOf(recovered, PreReviewNode.Notify));
        Assert.Equal(2, host.Factory.WorkflowApplyCalls);
        Assert.Equal(1, await host.FulfilmentCountAsync(requestId));
        Assert.Single(host.Sender.Requests);

        never.SetResult();
        Assert.True((await zombie.WaitAsync(PreReviewTestHost.WaitLimit)).Success);
        Assert.Equal(PreReviewStatus.Completed, (await host.Runs.GetAsync(run.Id))!.Status);
        Assert.Equal(1, await host.FulfilmentCountAsync(requestId));
        Assert.Single(host.Sender.Requests);
    }

    private static SubmitAfterSalesInput SizeExchange(int itemId, AfterSalesChannel channel) => new()
    {
        CaseKey = Guid.NewGuid(),
        OrderItemId = itemId,
        Type = AfterSalesType.SizeExchange,
        TargetSize = "27",
        IsWorn = false,
        Description = "尺碼太小",
        Channel = channel,
    };

    private sealed class ThrowingHook : IAfterSalesSubmittedHook
    {
        public Task OnSubmittedAsync(int requestId, AfterSalesChannel channel) => throw new InvalidOperationException("hook down");
    }
}

/// <summary>Web 主機的接線：掛勾換成初審版本、協調器可解析、worker 註冊在例外寫入器之後。</summary>
[Collection(nameof(IntegrationHostCollection))]
public sealed class PreReviewWiringTests : IClassFixture<ApiTestApplicationFactory>
{
    private readonly ApiTestApplicationFactory factory;

    public PreReviewWiringTests(ApiTestApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public void PreReview_HookCoordinatorAndWorker_AreWired()
    {
        using var scope = factory.Services.CreateScope();
        Assert.IsType<PreReviewSubmittedHook>(scope.ServiceProvider.GetRequiredService<IAfterSalesSubmittedHook>());
        // 送件服務真的拿到初審版本的掛勾（選填參數沒有退回 no-op）。
        var requests = scope.ServiceProvider.GetRequiredService<AfterSalesRequestService>();
        var hook = typeof(AfterSalesRequestService).GetField("submittedHook", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(requests);
        Assert.IsType<PreReviewSubmittedHook>(hook);
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<AfterSalesReviewCoordinator>());

        var hosted = factory.Services.GetServices<IHostedService>().ToList();
        var worker = hosted.FindIndex(x => x is PreReviewWorker);
        var writer = hosted.FindIndex(x => x is ExceptionLogWriter);
        Assert.True(worker > writer && writer >= 0, $"PreReviewWorker（{worker}）必須註冊在 ExceptionLogWriter（{writer}）之後。");
    }
}

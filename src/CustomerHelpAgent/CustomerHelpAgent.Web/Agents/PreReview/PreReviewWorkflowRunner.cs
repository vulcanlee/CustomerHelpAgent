using Microsoft.Agents.AI.Workflows;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Models.Shop;
using CustomerHelpAgent.Share.Agents;
using CustomerHelpAgent.Share.Shop;

namespace CustomerHelpAgent.Web.Agents.PreReview;

/// <summary>
/// 執行初審 Workflow（scoped；背景 worker 每筆工作在自己的 scope 解析）。每次呼叫都重建 Workflow，
/// checkpoint 存在 SQLite（<see cref="SqliteWorkflowCheckpointStore"/>，SessionId＝<c>PreReviewRun.WorkflowSessionId</c>），
/// 所以「開始 → 停在人工決策 → 數小時後（或重啟後）客服決定」之間不必留任何記憶體物件。
/// ⚠️ 這裡不負責決策併發：兩位客服同時決定，由呼叫端先以 <c>PreReviewRunService.TryClaimDecisionAsync</c> 認領，
/// 只有贏家呼叫 <see cref="SubmitDecisionAsync"/>（執行本身另有 <c>ApplyAsync</c> 的狀態守門）。
/// </summary>
public sealed class PreReviewWorkflowRunner
{
    public const string WorkflowFailedMessage = "AI 初審流程發生錯誤，請重新整理後再試。";

    private readonly PreReviewRunService runs;
    private readonly AfterSalesRequestService requests;
    private readonly WorkflowCheckpointService checkpoints;
    private readonly PreReviewWorkflowFactory factory;
    private readonly PreReviewSignal signal;
    private readonly ILogger<PreReviewWorkflowRunner> logger;

    public PreReviewWorkflowRunner(
        PreReviewRunService runs,
        AfterSalesRequestService requests,
        WorkflowCheckpointService checkpoints,
        PreReviewWorkflowFactory factory,
        PreReviewSignal signal,
        ILogger<PreReviewWorkflowRunner> logger)
    {
        this.runs = runs;
        this.requests = requests;
        this.checkpoints = checkpoints;
        this.factory = factory;
        this.signal = signal;
        this.logger = logger;
    }

    /// <summary>
    /// 從頭跑一輪（<see cref="PreReviewStatus.Pending"/>／<see cref="PreReviewStatus.Running"/> 才會跑），跑到人工決策就結束本次執行，
    /// 狀態設為 <see cref="PreReviewStatus.AwaitingDecision"/>。取消（應用程式關閉）時拋 <see cref="OperationCanceledException"/>，
    /// 狀態維持 Running，下次以 <see cref="ResumeAsync"/> 從最新 checkpoint 續跑。
    /// </summary>
    public async Task StartAsync(int runId, CancellationToken cancellationToken = default)
    {
        var run = await BeginAsync(runId);
        if (run is not null)
        {
            await StartFreshAsync(run, CreateCheckpointManager(), cancellationToken);
        }
    }

    /// <summary>重啟後續跑：從最新 checkpoint 接著跑（已完成的 superstep 不重跑）；還沒有 checkpoint 就從頭跑。</summary>
    public async Task ResumeAsync(int runId, CancellationToken cancellationToken = default)
    {
        var run = await BeginAsync(runId);
        if (run is null)
        {
            return;
        }

        var manager = CreateCheckpointManager();
        var latest = await manager.GetLatestCheckpointAsync(run.WorkflowSessionId, cancellationToken);
        if (latest is null)
        {
            await StartFreshAsync(run, manager, cancellationToken);
            return;
        }

        logger.LogInformation("Pre-review workflow resuming from checkpoint. RunId={RunId}", run.Id);
        await using var streaming = await InProcessExecution.ResumeStreamingAsync(factory.Create(run), latest, manager, cancellationToken);
        await FinishAsync(run, await DrainAsync(streaming, run.Id, cancellationToken), answeredRequestId: null);
    }

    /// <summary>
    /// 送出客服決定：從最新 checkpoint 續跑 → 收到重播的人工決策請求 → 回應 → 跑到結束或再次等待（執行失敗時）。
    /// 回傳執行結果；這一輪沒有等待中的決策（已執行、已由人工處理、流程失敗）時回失敗，不會執行。
    /// </summary>
    public async Task<DecisionResult> SubmitDecisionAsync(int runId, StaffDecision decision, CancellationToken cancellationToken = default)
    {
        var run = await runs.GetAsync(runId);
        if (run is null || run.Status is not (PreReviewStatus.AwaitingDecision or PreReviewStatus.Executing))
        {
            logger.LogInformation("Pre-review decision rejected because the run is not awaiting a decision. RunId={RunId}, Status={Status}", runId, run?.Status);
            return Fail(AfterSalesRequestService.StatusChangedMessage);
        }

        var manager = CreateCheckpointManager();
        var latest = await manager.GetLatestCheckpointAsync(run.WorkflowSessionId, cancellationToken);
        if (latest is null)
        {
            // 等待決策（或已認領）卻沒有 checkpoint：流程資料壞了。標為失敗，客服改走直接決定，不讓這一輪卡在 Executing。
            logger.LogError("Pre-review decision rejected because no checkpoint exists. RunId={RunId}", run.Id);
            await runs.SetStatusAsync(run.Id, PreReviewStatus.Failed, WorkflowFailedMessage);
            signal.Publish(run.AfterSalesRequestId);
            return Fail(WorkflowFailedMessage);
        }

        // ⚠️ 執行階段不綁呼叫端的取消：決定一旦送出就要跑完（開單、扣庫存、通知是一體的），
        // 中途取消會讓那個 superstep 沒寫進 checkpoint，續跑時再執行一次就變成「狀態已變更」。只有等待重播請求時可以取消。
        await using var streaming = await InProcessExecution.ResumeStreamingAsync(factory.Create(run), latest, manager, CancellationToken.None);
        // 續跑後框架會重播 checkpoint 裡等待中的請求；串流在「等待回應」時結束（blockOnPendingRequest: false）。
        var replay = await DrainAsync(streaming, run.Id, cancellationToken);
        var request = replay.Requests.FirstOrDefault(x => x.PortInfo.PortId == PreReviewWorkflowFactory.DecisionPortId);
        if (replay.Failed || request is null)
        {
            logger.LogWarning("Pre-review decision rejected because no pending decision was found. RunId={RunId}", run.Id);
            await FinishAsync(run, replay, answeredRequestId: null);
            return Fail(replay.Failed ? WorkflowFailedMessage : AfterSalesRequestService.StatusChangedMessage);
        }

        await streaming.SendResponseAsync(request.CreateResponse(decision));
        var outcome = await DrainAsync(streaming, run.Id, CancellationToken.None);
        await FinishAsync(run, outcome, request.RequestId);
        return outcome.Result ?? Fail(outcome.Failed ? WorkflowFailedMessage : AfterSalesRequestService.StatusChangedMessage);
    }

    /// <summary>
    /// 啟動時救回「決策已認領（<see cref="PreReviewStatus.Executing"/>）、執行到一半當機」的一輪：從最新 checkpoint 續跑。
    /// <list type="bullet">
    /// <item>checkpoint 已含客服的決定 → 執行節點照常跑完（執行有 <c>ApplyAsync</c> 的狀態守門，已生效的決定不會再生效一次）。</item>
    /// <item>checkpoint 只有等待中的決策請求（決定還沒送進 Workflow）→ 申請仍是已提交就回到 <see cref="PreReviewStatus.AwaitingDecision"/>，
    /// 讓客服再決定一次；申請已被決定（例如執行已 commit、checkpoint 還沒寫就當機）→ 標為已由人工處理，不再等待。</item>
    /// </list>
    /// </summary>
    public async Task RecoverExecutingAsync(int runId, CancellationToken cancellationToken = default)
    {
        var run = await runs.GetAsync(runId);
        if (run?.Status != PreReviewStatus.Executing)
        {
            logger.LogInformation("Pre-review recovery skipped. RunId={RunId}, Status={Status}", runId, run?.Status);
            return;
        }

        var manager = CreateCheckpointManager();
        var latest = await manager.GetLatestCheckpointAsync(run.WorkflowSessionId, cancellationToken);
        if (latest is null)
        {
            logger.LogError("Pre-review recovery found no checkpoint. RunId={RunId}", run.Id);
            await runs.SetStatusAsync(run.Id, PreReviewStatus.Failed, WorkflowFailedMessage);
            signal.Publish(run.AfterSalesRequestId);
            return;
        }

        logger.LogInformation("Pre-review workflow recovering a claimed decision from checkpoint. RunId={RunId}", run.Id);
        // 與 SubmitDecisionAsync 相同：執行一旦開始就不綁取消，讓它跑完。
        await using var streaming = await InProcessExecution.ResumeStreamingAsync(factory.Create(run), latest, manager, CancellationToken.None);
        var outcome = await DrainAsync(streaming, run.Id, CancellationToken.None);
        if (!outcome.Failed
            && outcome.Requests.Count > 0
            && (await requests.GetReviewDetailAsync(run.AfterSalesRequestId))?.Status != AfterSalesStatus.Submitted)
        {
            logger.LogInformation("Pre-review recovery ended the run because the request was already decided. RunId={RunId}", run.Id);
            await runs.SetStatusAsync(run.Id, PreReviewStatus.Superseded);
            await checkpoints.DeleteForEndedRunsAsync(run.AfterSalesRequestId);
            signal.Publish(run.AfterSalesRequestId);
            return;
        }

        await FinishAsync(run, outcome, answeredRequestId: null);
    }

    /// <summary>可以跑的一輪才回傳（並設為 Running）；不存在、已等待決策、已結束、已由人工處理都回 null。</summary>
    private async Task<PreReviewRun?> BeginAsync(int runId)
    {
        var run = await runs.GetAsync(runId);
        if (run is null || run.Status is not (PreReviewStatus.Pending or PreReviewStatus.Running))
        {
            logger.LogInformation("Pre-review run skipped. RunId={RunId}, Status={Status}", runId, run?.Status);
            return null;
        }

        if (!await runs.SetStatusAsync(run.Id, PreReviewStatus.Running))
        {
            logger.LogInformation("Pre-review run skipped because it was superseded. RunId={RunId}", run.Id);
            return null;
        }

        signal.Publish(run.AfterSalesRequestId);
        return run;
    }

    private async Task StartFreshAsync(PreReviewRun run, CheckpointManager manager, CancellationToken cancellationToken)
    {
        logger.LogInformation("Pre-review workflow starting. RunId={RunId}", run.Id);
        await using var streaming = await InProcessExecution.RunStreamingAsync(
            factory.Create(run), new PreReviewStart(run.Id, run.AfterSalesRequestId), manager, run.WorkflowSessionId, cancellationToken);
        await FinishAsync(run, await DrainAsync(streaming, run.Id, cancellationToken), answeredRequestId: null);
    }

    private CheckpointManager CreateCheckpointManager()
        => CheckpointManager.CreateJson(new SqliteWorkflowCheckpointStore(checkpoints), PreReviewJsonContext.CheckpointOptions);

    /// <summary>
    /// 讀事件直到串流結束（跑完，或停在等待回應）。節點失敗由 executor 自己吞下，這裡看到的錯誤是流程本身壞了。
    /// 取消時串流直接結束（不拋例外），這裡改拋 <see cref="OperationCanceledException"/>：不可把半途的狀態當成結果。
    /// </summary>
    private async Task<DrainOutcome> DrainAsync(StreamingRun streaming, int runId, CancellationToken cancellationToken)
    {
        var requests = new List<ExternalRequest>();
        DecisionResult? result = null;
        var failed = false;
        await foreach (var evt in streaming.WatchStreamAsync(blockOnPendingRequest: false, cancellationToken))
        {
            switch (evt)
            {
                case RequestInfoEvent info:
                    requests.Add(info.Request);
                    break;
                case WorkflowOutputEvent { Data: ExecutionResult execution }:
                    result = execution.Result;
                    break;
                case ExecutorFailedEvent executorFailed:
                    failed = true;
                    logger.LogError(executorFailed.Data, "Pre-review executor failed. RunId={RunId}, ExecutorId={ExecutorId}", runId, executorFailed.ExecutorId);
                    break;
                case WorkflowErrorEvent workflowError when !failed:
                    // 同一個失敗框架會先發 ExecutorFailedEvent 再發包了一層的 WorkflowErrorEvent：只記第一筆，避免例外紀錄重複。
                    failed = true;
                    logger.LogError(workflowError.Exception, "Pre-review workflow failed. RunId={RunId}", runId);
                    break;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new DrainOutcome(requests, result, failed);
    }

    /// <summary>
    /// 流程失敗 → Failed；有新的等待中決策 → AwaitingDecision。跑完（Completed）與已由人工處理（Superseded）由節點自己設定，
    /// 這裡在串流結束後（最後一個 superstep 的 checkpoint 已寫入）清掉已結束各輪的 checkpoint。
    /// </summary>
    private async Task FinishAsync(PreReviewRun run, DrainOutcome outcome, string? answeredRequestId)
    {
        if (outcome.Failed)
        {
            await runs.SetStatusAsync(run.Id, PreReviewStatus.Failed, WorkflowFailedMessage);
            signal.Publish(run.AfterSalesRequestId);
            return;
        }

        var pending = outcome.Requests.FirstOrDefault(x => x.RequestId != answeredRequestId);
        if (pending is not null)
        {
            if (await runs.SetStatusAsync(run.Id, PreReviewStatus.AwaitingDecision))
            {
                // 上一次執行失敗的原因（例如目標規格無庫存）放在人工決策節點的 Note，審核頁顯示在決策按鈕旁；第一次等待時為 null。
                var previousFailure = pending.TryGetDataAs<DecisionRequest>(out var request) ? request?.PreviousFailure : null;
                await runs.SetNodeAsync(run.Id, PreReviewNode.StaffDecision, NodeStatus.Running, previousFailure);
                signal.Publish(run.AfterSalesRequestId);
                logger.LogInformation("Pre-review workflow awaiting staff decision. RunId={RunId}", run.Id);
            }

            return;
        }

        // 串流結束、沒有等待中的決策，節點卻沒有把這一輪收尾：流程卡住了（例如訊息還原失敗）。標為失敗，不讓畫面永遠顯示初審中。
        if ((await runs.GetAsync(run.Id))?.Status is PreReviewStatus.Running or PreReviewStatus.Executing)
        {
            logger.LogError("Pre-review workflow ended without reaching a decision or completion. RunId={RunId}", run.Id);
            await runs.SetStatusAsync(run.Id, PreReviewStatus.Failed, WorkflowFailedMessage);
            signal.Publish(run.AfterSalesRequestId);
        }

        await checkpoints.DeleteForEndedRunsAsync(run.AfterSalesRequestId);
    }

    private static DecisionResult Fail(string message) => new(false, message, null, null);

    private sealed record DrainOutcome(IReadOnlyList<ExternalRequest> Requests, DecisionResult? Result, bool Failed);
}

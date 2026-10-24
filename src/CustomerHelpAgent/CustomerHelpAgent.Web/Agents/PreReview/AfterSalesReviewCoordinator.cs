using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Shop;
using CustomerHelpAgent.Share.Agents;
using CustomerHelpAgent.Share.Shop;

namespace CustomerHelpAgent.Web.Agents.PreReview;

/// <summary>
/// 審核頁的決策唯一入口（scoped，在客服的 circuit 內使用；傳統與 Agent 兩個管道都走這裡）。
/// <list type="bullet">
/// <item>Agent 管道、最新一輪停在人工決策（<see cref="PreReviewStatus.AwaitingDecision"/>）→ 先認領（<c>TryClaimDecisionAsync</c>，
/// 兩位客服同時按只有一位成功）再把決定送進 Workflow；Workflow 的執行節點呼叫 <c>ApplyAsync</c>。</item>
/// <item>其他情況（傳統管道、初審還在跑、初審失敗、沒有初審）→ 直接 <c>ApplyAsync</c>，成功後才把未完成的初審標為已由人工處理
/// （之後的節點不再執行）；執行失敗時初審維持原狀。</item>
/// </list>
/// ⚠️ 決策路徑在呼叫端的 scope 內直接 await（執行是純程式、不呼叫模型，數百毫秒內完成），不 <c>Task.Run</c>；
/// 要呼叫模型的「重新初審」一律放進 <see cref="PreReviewQueue"/> 由背景執行。
/// </summary>
public sealed class AfterSalesReviewCoordinator
{
    public const string RequestNotFoundMessage = "找不到這筆售後申請。";
    public const string RetryNotAvailableMessage = "只有 Agent 管道、尚在審核中的申請可以重新初審。";
    public const string RetryQueuedMessage = "已重新排入 AI 初審。";

    private readonly PreReviewRunService runs;
    private readonly WorkflowCheckpointService checkpoints;
    private readonly AfterSalesRequestService requests;
    private readonly AfterSalesDecisionService decisions;
    private readonly PreReviewWorkflowRunner runner;
    private readonly PreReviewQueue queue;
    private readonly PreReviewSignal signal;
    private readonly CurrentUserService currentUserService;
    private readonly ILogger<AfterSalesReviewCoordinator> logger;

    public AfterSalesReviewCoordinator(
        PreReviewRunService runs,
        WorkflowCheckpointService checkpoints,
        AfterSalesRequestService requests,
        AfterSalesDecisionService decisions,
        PreReviewWorkflowRunner runner,
        PreReviewQueue queue,
        PreReviewSignal signal,
        CurrentUserService currentUserService,
        ILogger<AfterSalesReviewCoordinator> logger)
    {
        this.runs = runs;
        this.checkpoints = checkpoints;
        this.requests = requests;
        this.decisions = decisions;
        this.runner = runner;
        this.queue = queue;
        this.signal = signal;
        this.currentUserService = currentUserService;
        this.logger = logger;
    }

    /// <summary>客服決定（核准／駁回／要求補件）。回傳是否成功、給客服看的訊息與（核准時）處理單號。</summary>
    public async Task<DecisionResult> DecideAsync(int requestId, StaffDecisionKind kind, string? note)
    {
        // 補件說明必填：先擋下，不要讓 Workflow 為了輸入錯誤跑一次「執行失敗 → 回到人工決策」。
        if (kind == StaffDecisionKind.NeedMoreInfo && string.IsNullOrWhiteSpace(note))
        {
            return Fail(AfterSalesDecisionService.MoreInfoRequiredMessage);
        }

        var request = await requests.GetReviewDetailAsync(requestId);
        if (request is null)
        {
            return Fail(RequestNotFoundMessage);
        }

        // 已經被決定（例如另一位客服剛核准）：不必再走任何一條路。之後的競爭由認領與 ApplyAsync 的狀態守門處理。
        if (request.Status != AfterSalesStatus.Submitted)
        {
            return Fail(AfterSalesRequestService.StatusChangedMessage);
        }

        var staffUserId = currentUserService.CurrentUser.Id;
        var latest = request.Channel == AfterSalesChannel.Agent ? await runs.GetLatestForRequestAsync(requestId) : null;
        switch (latest?.Status)
        {
            case PreReviewStatus.AwaitingDecision:
                return await DecideThroughWorkflowAsync(latest, kind, note, staffUserId);
            case PreReviewStatus.Executing:
                // 另一位客服的決定正在執行。
                logger.LogInformation("Staff decision rejected because another decision is executing. RequestId={RequestId}, RunId={RunId}", requestId, latest.Id);
                return Fail(AfterSalesRequestService.StatusChangedMessage);
        }

        // 先執行、成功後才把未完成的初審標為已由人工處理：執行失敗（例如目標規格無庫存）時申請仍待審，初審不可顯示「已由人工處理」。
        // 執行期間初審即使走到等待決策，AI 路徑也要先認領才能執行，而 ApplyAsync 的狀態守門只讓一方生效，所以晚一點標記是安全的。
        var result = await decisions.ApplyAsync(requestId, kind, note, staffUserId > 0 ? staffUserId : null);
        if (result.Success && await runs.SupersedeAsync(requestId) > 0)
        {
            await checkpoints.DeleteForEndedRunsAsync(requestId);
            signal.Publish(requestId);
        }

        return result;
    }

    /// <summary>
    /// 重新初審：替 Agent 管道、仍待審（已提交）的申請建立新的一輪並入列（上一輪失敗或已跑完都可以）。
    /// 上一輪若還沒結束（初審中、等待決策）就標為已由人工處理，畫面與決策只看最新一輪。決定正在執行時不可重跑。
    /// </summary>
    public async Task<DecisionResult> RetryAsync(int requestId)
    {
        var request = await requests.GetReviewDetailAsync(requestId);
        if (request is null)
        {
            return Fail(RequestNotFoundMessage);
        }

        if (request.Channel != AfterSalesChannel.Agent || request.Status != AfterSalesStatus.Submitted)
        {
            return Fail(RetryNotAvailableMessage);
        }

        if ((await runs.GetLatestForRequestAsync(requestId))?.Status == PreReviewStatus.Executing)
        {
            return Fail(AfterSalesRequestService.StatusChangedMessage);
        }

        if (await runs.SupersedeAsync(requestId) > 0)
        {
            await checkpoints.DeleteForEndedRunsAsync(requestId);
        }

        var run = await runs.CreateAsync(requestId);
        if (run is null)
        {
            return Fail(RequestNotFoundMessage);
        }

        logger.LogInformation("Pre-review retry requested. RequestId={RequestId}, RunId={RunId}, Attempt={Attempt}", requestId, run.Id, run.Attempt);
        // 佇列滿時這一輪留在 Pending，worker 清空佇列後會重新掃描補上，所以仍回成功。
        queue.TryEnqueue(run.Id);
        signal.Publish(requestId);
        return new DecisionResult(true, RetryQueuedMessage, null, null);
    }

    /// <summary>
    /// 認領成功才送進 Workflow（只有贏家呼叫 Runner）。Runner 拋出例外時先看申請現在的樣子再收尾，不會卡在 <see cref="PreReviewStatus.Executing"/>：
    /// 這個決定其實已生效（例外發生在 commit 之後）→ 這一輪完成、回成功；申請已被別處決定 → 已由人工處理、回「狀態已變更」；
    /// 申請仍待審 → 這一輪失敗、回「審核失敗」，客服可以改走直接決定。
    /// </summary>
    private async Task<DecisionResult> DecideThroughWorkflowAsync(PreReviewRun run, StaffDecisionKind kind, string? note, int staffUserId)
    {
        if (!await runs.TryClaimDecisionAsync(run.Id))
        {
            logger.LogInformation("Staff decision rejected because the pre-review decision was already claimed. RequestId={RequestId}, RunId={RunId}", run.AfterSalesRequestId, run.Id);
            return Fail(AfterSalesRequestService.StatusChangedMessage);
        }

        signal.Publish(run.AfterSalesRequestId);
        try
        {
            return await runner.SubmitDecisionAsync(run.Id, new StaffDecision(kind, note, staffUserId));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Pre-review workflow decision failed. RequestId={RequestId}, RunId={RunId}", run.AfterSalesRequestId, run.Id);
            return await SettleAfterFailureAsync(run, kind, note, staffUserId);
        }
    }

    private async Task<DecisionResult> SettleAfterFailureAsync(PreReviewRun run, StaffDecisionKind kind, string? note, int staffUserId)
    {
        DecisionResult result;
        if (await decisions.GetAppliedResultAsync(run.AfterSalesRequestId, kind, note, staffUserId > 0 ? staffUserId : null) is { } applied)
        {
            await runs.SetStatusAsync(run.Id, PreReviewStatus.Completed);
            result = applied;
        }
        else if ((await requests.GetReviewDetailAsync(run.AfterSalesRequestId))?.Status is { } status && status != AfterSalesStatus.Submitted)
        {
            await runs.SetStatusAsync(run.Id, PreReviewStatus.Superseded, AfterSalesRequestService.StatusChangedMessage);
            result = Fail(AfterSalesRequestService.StatusChangedMessage);
        }
        else
        {
            await runs.SetStatusAsync(run.Id, PreReviewStatus.Failed, PreReviewWorkflowRunner.WorkflowFailedMessage);
            result = Fail(AfterSalesDecisionService.FailedMessage);
        }

        await checkpoints.DeleteForEndedRunsAsync(run.AfterSalesRequestId);
        signal.Publish(run.AfterSalesRequestId);
        return result;
    }

    private static DecisionResult Fail(string message) => new(false, message, null, null);
}

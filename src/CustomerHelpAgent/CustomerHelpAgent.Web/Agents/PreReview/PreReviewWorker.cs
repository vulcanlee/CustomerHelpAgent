using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Agents;
using CustomerHelpAgent.Web.Diagnostics;

namespace CustomerHelpAgent.Web.Agents.PreReview;

/// <summary>
/// AI 初審的背景執行器：從 <see cref="PreReviewQueue"/> 逐筆取出、在<b>自己的 scope</b> 跑 Workflow（一次一筆，依入列順序）。
/// <list type="bullet">
/// <item>scope 裡的 <c>CurrentUserService.CurrentUser</c> 保持空白：模型呼叫記為系統呼叫（Token 用量、AI 對話紀錄的使用者為空），
/// 會話識別是案件的 CaseKey（由 <see cref="PreReviewChatClientProvider"/> 帶入）。</item>
/// <item>例外情境設為 <c>ExceptionContext(背景作業, "PreReview")</c>（比照 <c>EmailDispatchWorker</c>），錯誤記在背景作業之下。</item>
/// </list>
/// 啟動時：先救回執行到一半當機的決策（<see cref="PreReviewStatus.Executing"/>）、再把未完成的初審全部重新入列、最後替漏建初審的 Agent 申請補建。
/// 入列失敗（佇列滿）時那一輪留在資料庫，清空佇列後重新掃描未完成的初審補上；不另設計時器（速查表：不再新增 PeriodicTimer 背景服務）。
/// </summary>
public sealed class PreReviewWorker : BackgroundService
{
    internal const string ContextPage = "PreReview";

    private readonly PreReviewQueue queue;
    private readonly IServiceScopeFactory scopeFactory;
    private readonly ExceptionContextAccessor contextAccessor;
    private readonly ILogger<PreReviewWorker> logger;

    public PreReviewWorker(
        PreReviewQueue queue,
        IServiceScopeFactory scopeFactory,
        ExceptionContextAccessor contextAccessor,
        ILogger<PreReviewWorker> logger)
    {
        this.queue = queue;
        this.scopeFactory = scopeFactory;
        this.contextAccessor = contextAccessor;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Pre-review worker started.");
        try
        {
            await RecoverAsync(stoppingToken);
            while (await queue.Reader.WaitToReadAsync(stoppingToken))
            {
                while (queue.Reader.TryRead(out var runId))
                {
                    await ProcessAsync(runId, stoppingToken);
                }

                if (queue.TakeOverflow())
                {
                    await ScanAsync("requeue-overflow", RequeueUnfinishedAsync, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 正常關機：跑到一半的初審維持 Running，下次啟動從最新 checkpoint 續跑；佇列中的 Id 不必保留（狀態在資料庫）。
        }

        logger.LogInformation("Pre-review worker stopped.");
    }

    /// <summary>
    /// 啟動時的復原。⚠️ 只能在啟動時做：<see cref="PreReviewStatus.Executing"/> 平常代表客服正在決定（在他的 circuit 裡執行），
    /// 只有剛啟動、還沒有任何 circuit 時，它才一定是上次當機留下的。
    /// </summary>
    internal async Task RecoverAsync(CancellationToken stoppingToken)
    {
        List<int> executing = [];
        await ScanAsync("find-executing", async sp => executing = (await sp.GetRequiredService<PreReviewRunService>().GetExecutingAsync()).ConvertAll(x => x.Id), stoppingToken);
        foreach (var runId in executing)
        {
            await RunInScopeAsync(runId, sp => sp.GetRequiredService<PreReviewWorkflowRunner>().RecoverExecutingAsync(runId, stoppingToken), stoppingToken);
        }

        await ScanAsync("requeue-unfinished", RequeueUnfinishedAsync, stoppingToken);
        await ScanAsync("create-missing", async sp =>
        {
            var runs = sp.GetRequiredService<PreReviewRunService>();
            var missing = await runs.GetAgentRequestIdsWithoutRunAsync();
            foreach (var requestId in missing)
            {
                if (await runs.CreateAsync(requestId) is { } run)
                {
                    queue.TryEnqueue(run.Id);
                }
            }

            if (missing.Count > 0)
            {
                logger.LogWarning("Pre-review runs created for agent requests that had none. Count={Count}", missing.Count);
            }
        }, stoppingToken);
    }

    /// <summary>跑一輪：Pending 從頭跑；Running（重啟前跑到一半）從最新 checkpoint 續跑，沒有 checkpoint 就從頭跑。其他狀態略過。</summary>
    internal Task ProcessAsync(int runId, CancellationToken stoppingToken)
        => RunInScopeAsync(runId, async sp =>
        {
            var runner = sp.GetRequiredService<PreReviewWorkflowRunner>();
            switch ((await sp.GetRequiredService<PreReviewRunService>().GetAsync(runId))?.Status)
            {
                case PreReviewStatus.Pending:
                    await runner.StartAsync(runId, stoppingToken);
                    break;
                case PreReviewStatus.Running:
                    await runner.ResumeAsync(runId, stoppingToken);
                    break;
                case var status:
                    logger.LogInformation("Queued pre-review run skipped. RunId={RunId}, Status={Status}", runId, status);
                    break;
            }
        }, stoppingToken);

    private async Task RequeueUnfinishedAsync(IServiceProvider services)
    {
        var unfinished = await services.GetRequiredService<PreReviewRunService>().GetUnfinishedAsync();
        foreach (var run in unfinished)
        {
            queue.TryEnqueue(run.Id);
        }

        if (unfinished.Count > 0)
        {
            logger.LogInformation("Unfinished pre-review runs queued. Count={Count}", unfinished.Count);
        }
    }

    /// <summary>
    /// 在新的 scope 與背景作業情境下處理一輪。非取消的例外記 Error，並盡力把這一輪標為失敗
    /// （畫面不會永遠停在「初審中」；客服仍可直接決定或按重新初審）。
    /// </summary>
    private async Task RunInScopeAsync(int runId, Func<IServiceProvider, Task> work, CancellationToken stoppingToken)
    {
        contextAccessor.Set(new ExceptionContext(ExceptionSources.Background, ContextPage, null, null));
        try
        {
            using var scope = scopeFactory.CreateScope();
            await work(scope.ServiceProvider);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Background pre-review failed. RunId={RunId}", runId);
            await MarkFailedAsync(runId);
        }
        finally
        {
            contextAccessor.Clear();
        }
    }

    private async Task MarkFailedAsync(int runId)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var runs = scope.ServiceProvider.GetRequiredService<PreReviewRunService>();
            if (await runs.GetAsync(runId) is { } run && await runs.SetStatusAsync(runId, PreReviewStatus.Failed, PreReviewWorkflowRunner.WorkflowFailedMessage))
            {
                scope.ServiceProvider.GetRequiredService<PreReviewSignal>().Publish(run.AfterSalesRequestId);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to mark the background pre-review as failed. RunId={RunId}", runId);
        }
    }

    /// <summary>
    /// 掃描資料庫的步驟（新的 scope、背景作業情境）。⚠️ 例外只記 Error 不外拋：BackgroundService 拋出未處理例外預設會讓整個主機停止，
    /// 啟動時資料庫一時出錯不該讓網站起不來；漏掉的初審下次啟動會再掃到。
    /// </summary>
    private async Task ScanAsync(string step, Func<IServiceProvider, Task> work, CancellationToken stoppingToken)
    {
        contextAccessor.Set(new ExceptionContext(ExceptionSources.Background, ContextPage, null, null));
        try
        {
            using var scope = scopeFactory.CreateScope();
            await work(scope.ServiceProvider);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Pre-review background scan failed. Step={Step}", step);
        }
        finally
        {
            contextAccessor.Clear();
        }
    }
}

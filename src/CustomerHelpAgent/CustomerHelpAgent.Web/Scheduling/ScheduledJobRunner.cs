using Microsoft.Extensions.Options;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Web.Configuration;
using CustomerHelpAgent.Web.Diagnostics;

namespace CustomerHelpAgent.Web.Scheduling;

/// <summary>一次執行請求的處理結果。</summary>
public enum ScheduledJobRunOutcome
{
    /// <summary>作業執行了（不論成功或失敗，結果在執行紀錄）。</summary>
    Executed,

    /// <summary>沒搶到排程時段：別的行程已經跑過、或作業已停用。不寫執行紀錄。</summary>
    ClaimLost,

    /// <summary>同一個作業的另一次執行還沒結束，這次略過（寫一筆 Skipped 紀錄）。</summary>
    Skipped,
}

/// <summary>
/// 執行一次排程作業（singleton，0.9.96 起）。順序很重要：
/// <list type="number">
/// <item>排程與補跑先**搶占時段**（資料庫一條 UPDATE）：兩個行程同一時段只有一個搶得到。手動執行不搶、也不消耗時段。</item>
/// <item>再**試著取得作業的鎖**：同一個作業不重疊執行；拿不到就記一筆 Skipped，不默默丟掉。</item>
/// <item>拿到鎖代表沒有人在跑它，殘留的 Running 一定是上個行程執行到一半被結束，改成 Interrupted。</item>
/// <item>先寫入 Running 並提交，再執行；結果以 <see cref="CancellationToken.None"/> 寫回，關機中斷也記得到。</item>
/// </list>
/// 每次執行都有新的錯誤追蹤碼與背景作業的 ExceptionContext，未處理的例外會進系統例外紀錄，執行紀錄記下追蹤碼。
/// </summary>
public sealed class ScheduledJobRunner
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly ScheduledJobRunService runService;
    private readonly JobLockProvider lockProvider;
    private readonly ExceptionContextAccessor contextAccessor;
    private readonly IOptionsMonitor<ScheduledJobSettings> options;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<ScheduledJobRunner> logger;

    public ScheduledJobRunner(
        IServiceScopeFactory scopeFactory,
        ScheduledJobRunService runService,
        JobLockProvider lockProvider,
        ExceptionContextAccessor contextAccessor,
        IOptionsMonitor<ScheduledJobSettings> options,
        TimeProvider timeProvider,
        ILogger<ScheduledJobRunner> logger)
    {
        this.scopeFactory = scopeFactory;
        this.runService = runService;
        this.lockProvider = lockProvider;
        this.contextAccessor = contextAccessor;
        this.options = options;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    public async Task<ScheduledJobRunOutcome> RunAsync(
        ScheduledJobDescriptor descriptor,
        string trigger,
        DateTime? scheduledForUtc,
        string? account,
        CancellationToken cancellationToken,
        int? triggeredByUserId = null)
    {
        if (trigger != JobRunTriggers.Manual)
        {
            var slot = scheduledForUtc ?? throw new ArgumentException("排程與補跑必須指定時段。", nameof(scheduledForUtc));
            if (!await runService.TryClaimSlotAsync(descriptor.Name, ScheduleCalculator.AsUtc(slot), cancellationToken))
            {
                logger.LogInformation("Scheduled job slot not claimed (already run elsewhere or disabled). JobName={JobName}, Trigger={Trigger}", descriptor.Name, trigger);
                return ScheduledJobRunOutcome.ClaimLost;
            }
        }

        using var jobLock = lockProvider.TryAcquire(descriptor.Name);
        if (jobLock is null)
        {
            var now = UtcNow();
            await runService.InsertRunAsync(new JobRun
            {
                JobName = descriptor.Name,
                Trigger = trigger,
                TriggeredByAccount = account,
                ScheduledForUtc = scheduledForUtc,
                StartedAtUtc = now,
                FinishedAtUtc = now,
                DurationMs = 0,
                Status = JobRunStatuses.Skipped,
                Message = "同一個作業的另一次執行還沒結束，這次略過。",
            }, cancellationToken);
            logger.LogInformation("Scheduled job skipped because another run is in progress. JobName={JobName}, Trigger={Trigger}", descriptor.Name, trigger);
            return ScheduledJobRunOutcome.Skipped;
        }

        await RecoverStaleRunsCoreAsync(descriptor.Name);
        await ExecuteCoreAsync(descriptor, trigger, scheduledForUtc, account, triggeredByUserId, cancellationToken);
        return ScheduledJobRunOutcome.Executed;
    }

    /// <summary>啟動時呼叫：拿得到鎖（沒有人在跑）才把殘留的 Running 改成 Interrupted。</summary>
    public async Task RecoverStaleRunsAsync(ScheduledJobDescriptor descriptor)
    {
        using var jobLock = lockProvider.TryAcquire(descriptor.Name);
        if (jobLock is not null)
        {
            await RecoverStaleRunsCoreAsync(descriptor.Name);
        }
    }

    private async Task RecoverStaleRunsCoreAsync(string jobName)
    {
        var interrupted = await runService.MarkStaleRunningInterruptedAsync(jobName, UtcNow());
        if (interrupted > 0)
        {
            logger.LogWarning("Stale scheduled job runs marked as interrupted. JobName={JobName}, Rows={Rows}", jobName, interrupted);
        }
    }

    private async Task ExecuteCoreAsync(ScheduledJobDescriptor descriptor, string trigger, DateTime? scheduledForUtc, string? account, int? triggeredByUserId, CancellationToken cancellationToken)
    {
        // 每次執行一個新的追蹤碼：不可沿用呼叫端的（例如手動觸發時畫面那一次互動的碼）。
        var traceId = TraceCode.New();
        using var traceScope = TraceCode.Begin(traceId);
        contextAccessor.Set(new ExceptionContext(ExceptionSources.Background, descriptor.Name, account, null));

        var startTimestamp = timeProvider.GetTimestamp();
        var runId = 0;
        var status = JobRunStatuses.Failed;
        string? message = null;

        try
        {
            runId = await runService.InsertRunAsync(new JobRun
            {
                JobName = descriptor.Name,
                Trigger = trigger,
                TriggeredByAccount = account,
                ScheduledForUtc = scheduledForUtc,
                StartedAtUtc = UtcNow(),
                Status = JobRunStatuses.Running,
                TraceId = traceId,
            }, cancellationToken);

            logger.LogInformation("Scheduled job started. JobName={JobName}, Trigger={Trigger}, RunId={RunId}", descriptor.Name, trigger, runId);

            await using var scope = scopeFactory.CreateAsyncScope();
            var job = (IScheduledJob)scope.ServiceProvider.GetRequiredService(descriptor.JobType);
            var result = await job.ExecuteAsync(new ScheduledJobContext(runId, trigger, scheduledForUtc, account, triggeredByUserId), cancellationToken);

            status = result.Succeeded ? JobRunStatuses.Succeeded : JobRunStatuses.Failed;
            message = result.Message;
            if (result.Succeeded)
            {
                logger.LogInformation("Scheduled job succeeded. JobName={JobName}, RunId={RunId}", descriptor.Name, runId);
            }
            else
            {
                // 作業自己回報的失敗：服務層已經記過錯誤，這裡只留警告，不再進系統例外紀錄。
                logger.LogWarning("Scheduled job reported a failure. JobName={JobName}, RunId={RunId}", descriptor.Name, runId);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            status = JobRunStatuses.Interrupted;
            message = "網站關閉，執行被中斷。";
            logger.LogWarning("Scheduled job interrupted by shutdown. JobName={JobName}, RunId={RunId}", descriptor.Name, runId);
        }
        catch (Exception ex)
        {
            status = JobRunStatuses.Failed;
            message = $"執行時發生未預期的錯誤（{ex.GetType().Name}），請以錯誤追蹤碼 {traceId} 到「系統例外紀錄」查看詳細內容。";
            logger.LogError(ex, "Scheduled job failed. JobName={JobName}, RunId={RunId}", descriptor.Name, runId);
        }
        finally
        {
            var elapsedMs = (long)timeProvider.GetElapsedTime(startTimestamp).TotalMilliseconds;
            if (runId > 0)
            {
                try
                {
                    await runService.FinishRunAsync(runId, status, message, UtcNow(), elapsedMs, traceId);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to record scheduled job result. JobName={JobName}, RunId={RunId}", descriptor.Name, runId);
                }
            }

            if (status == JobRunStatuses.Failed)
            {
                await NotifyFailureAsync(descriptor, message, triggeredByUserId);
            }

            await PruneAsync();
            contextAccessor.Clear();
        }
    }

    /// <summary>
    /// 作業失敗（不含中斷與略過）時通知所有管理員與手動觸發者，並同時寄信（0.9.100 起）。
    /// 用新的 scope：作業自己的 scope 已經結束。通知失敗只記錯誤，不影響這次執行的結果。
    /// </summary>
    private async Task NotifyFailureAsync(ScheduledJobDescriptor descriptor, string? message, int? triggeredByUserId)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var notifications = scope.ServiceProvider.GetRequiredService<INotificationSender>();
            var target = triggeredByUserId is { } userId
                ? NotificationTarget.Union(NotificationTarget.AllAdmins(), NotificationTarget.Users(userId))
                : NotificationTarget.AllAdmins();
            await notifications.SendAsync(new NotificationRequest(
                NotificationCategories.JobFailed, $"排程作業失敗：{descriptor.DisplayName}", message, "/scheduled-jobs", target, AlsoEmail: true));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send the scheduled job failure notification. JobName={JobName}", descriptor.Name);
        }
    }

    private async Task PruneAsync()
    {
        try
        {
            var days = options.CurrentValue.JobRunRetentionDays;
            if (days > 0)
            {
                await runService.PruneAsync(RetentionCutoffs.JobRun(days, timeProvider));
            }
        }
        catch (Exception ex)
        {
            // 設定改壞（OptionsValidationException）或資料庫暫時忙碌：只是舊紀錄晚點清，不影響這次的結果。
            logger.LogWarning(ex, "Failed to prune scheduled job runs.");
        }
    }

    private DateTime UtcNow() => timeProvider.GetUtcNow().UtcDateTime;
}

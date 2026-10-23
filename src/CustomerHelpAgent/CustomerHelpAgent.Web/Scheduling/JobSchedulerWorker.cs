using Cronos;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Web.Configuration;

namespace CustomerHelpAgent.Web.Scheduling;

/// <summary>
/// 排程器（0.9.96 起）：依 cron 在時段到了時執行作業、啟動時補跑錯過的時段、處理管理頁的「立即執行」。
///
/// <list type="bullet">
/// <item><b>啟動</b>：建立缺少的狀態列 → 回收殘留的 Running → 暖機 <see cref="WarmUp"/>（不和啟動搶資源）→ 補跑。
/// 補跑的起點是「上次搶到的時段」與「建立或最後一次切換啟用」取晚的，所以新安裝與重新啟用都不會補跑；
/// 只有真的錯過的時段（例如 03:00 時網站閒置停止）才補跑一次。</item>
/// <item><b>迴圈</b>：最多睡一分鐘（改設定、切換啟用在一分鐘內生效）。時段到了就以<b>時段本身</b>（不是現在）執行，
/// 跨行程只跑一次由 <see cref="ScheduledJobRunner"/> 的時段搶占保證，IIS 回收不會再觸發。</item>
/// <item><b>設定改壞</b>：執行中修改 appsettings 讓驗證失敗時，<c>CurrentValue</c> 會丟例外 —— 沿用上一份合法設定並記錯誤，排程不會停。</item>
/// </list>
/// 必須註冊在 <c>ExceptionLogWriter</c> 之後：主機以相反順序停止，作業在關機時記的錯誤才還有人寫進系統例外紀錄。
/// </summary>
public sealed class JobSchedulerWorker : BackgroundService
{
    internal static readonly TimeSpan WarmUp = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxSleep = TimeSpan.FromMinutes(1);

    private readonly IReadOnlyList<ScheduledJobDescriptor> descriptors;
    private readonly ScheduledJobRunner runner;
    private readonly ScheduledJobRunService runService;
    private readonly ScheduledJobTriggerQueue triggerQueue;
    private readonly IOptionsMonitor<ScheduledJobSettings> options;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<JobSchedulerWorker> logger;

    private readonly Dictionary<string, PendingSlot> pendingSlots = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Task> manualRuns = [];
    private readonly object manualRunsLock = new();
    private ScheduledJobSettings lastGoodSettings;
    private bool settingsErrorLogged;

    public JobSchedulerWorker(
        IEnumerable<ScheduledJobDescriptor> descriptors,
        ScheduledJobRunner runner,
        ScheduledJobRunService runService,
        ScheduledJobTriggerQueue triggerQueue,
        IOptionsMonitor<ScheduledJobSettings> options,
        TimeProvider timeProvider,
        ILogger<JobSchedulerWorker> logger)
    {
        this.descriptors = descriptors.ToList();
        this.runner = runner;
        this.runService = runService;
        this.triggerQueue = triggerQueue;
        this.options = options;
        this.timeProvider = timeProvider;
        this.logger = logger;
        lastGoodSettings = options.CurrentValue;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 不要佔住主機的啟動流程：StartAsync 要等 ExecuteAsync 第一次 await 才返回。
        await Task.Yield();
        var manualLoop = ProcessManualRequestsAsync(stoppingToken);

        try
        {
            await StartupAsync(UtcNow(), stoppingToken);
            await Task.Delay(WarmUp, timeProvider, stoppingToken);
            await CatchUpAsync(UtcNow(), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                await TickAsync(UtcNow(), stoppingToken);
                await Task.Delay(ComputeDelay(UtcNow()), timeProvider, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 服務關閉。
        }

        await manualLoop;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        Task[] running;
        lock (manualRunsLock)
        {
            running = [.. manualRuns];
        }

        // 手動執行收到同一個停止訊號；等它們把「中斷」寫進執行紀錄（主機的關機時限到了就不再等）。
        try
        {
            await Task.WhenAll(running).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 關機時限到了。
        }
    }

    /// <summary>建立缺少的狀態列、回收殘留的 Running。</summary>
    internal async Task StartupAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        try
        {
            await runService.EnsureStatesAsync(descriptors.Select(x => x.Name), nowUtc, cancellationToken);
            foreach (var descriptor in descriptors)
            {
                await runner.RecoverStaleRunsAsync(descriptor);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Scheduled job startup preparation failed.");
        }
    }

    /// <summary>補跑錯過的時段（每個作業最多一次），並設定各作業的下一個時段。</summary>
    internal async Task CatchUpAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        var settings = CurrentSettings();
        if (settings.Enabled)
        {
            List<Models.AdapterModel.ScheduledJobStateAdapterModel> states;
            try
            {
                states = await runService.GetStatesAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Failed to load scheduled job states for catch-up.");
                states = [];
            }

            foreach (var descriptor in descriptors)
            {
                var state = states.FirstOrDefault(x => string.Equals(x.JobName, descriptor.Name, StringComparison.OrdinalIgnoreCase));
                if (state is not { IsEnabled: true } || !TryGetCron(descriptor, settings, out var cron))
                {
                    continue;
                }

                var anchor = ScheduleCalculator.Anchor(state.LastScheduledForUtc, state.UpdatedAtUtc);
                if (ScheduleCalculator.CatchUpSlotUtc(cron, anchor, nowUtc, timeProvider.LocalTimeZone) is { } slot)
                {
                    logger.LogInformation("Scheduled job missed a slot; catching up once. JobName={JobName}", descriptor.Name);
                    await RunSafelyAsync(descriptor, JobRunTriggers.CatchUp, slot, cancellationToken);
                }
            }
        }

        foreach (var descriptor in descriptors)
        {
            Reschedule(descriptor, settings, nowUtc);
        }
    }

    /// <summary>執行時段已到的作業，並排定下一個時段。時段本身（不是現在）是這次執行的 ScheduledFor。</summary>
    internal async Task TickAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        var settings = CurrentSettings();
        foreach (var descriptor in descriptors)
        {
            var cronText = ScheduleCalculator.GetEffectiveCron(descriptor, settings);
            if (!pendingSlots.TryGetValue(descriptor.Name, out var pending) || pending.Cron != cronText)
            {
                // 第一次、或設定改了執行時間：從現在重新排，不追溯。
                Reschedule(descriptor, settings, nowUtc);
                continue;
            }

            if (pending.SlotUtc is not { } slot || nowUtc < slot)
            {
                continue;
            }

            if (settings.Enabled)
            {
                await RunSafelyAsync(descriptor, JobRunTriggers.Schedule, slot, cancellationToken);
            }

            // 總開關關閉時也要往前推，重新開啟後不回頭補這段期間的時段。
            Reschedule(descriptor, settings, nowUtc > slot ? nowUtc : slot);
        }
    }

    private async Task ProcessManualRequestsAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var request in triggerQueue.Reader.ReadAllAsync(stoppingToken))
            {
                var descriptor = descriptors.FirstOrDefault(x => string.Equals(x.Name, request.JobName, StringComparison.OrdinalIgnoreCase));
                if (descriptor is null)
                {
                    triggerQueue.Complete(request.JobName);
                    continue;
                }

                // 由排程器自己的執行環境啟動並追蹤，關機時 StopAsync 會等它。
                var task = RunManualAsync(descriptor, request.Account, request.UserId, stoppingToken);
                lock (manualRunsLock)
                {
                    manualRuns.RemoveAll(t => t.IsCompleted);
                    manualRuns.Add(task);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 服務關閉。
        }
    }

    private async Task RunManualAsync(ScheduledJobDescriptor descriptor, string? account, int? userId, CancellationToken stoppingToken)
    {
        try
        {
            await RunSafelyAsync(descriptor, JobRunTriggers.Manual, null, stoppingToken, account, userId);
        }
        finally
        {
            triggerQueue.Complete(descriptor.Name);
        }
    }

    private async Task RunSafelyAsync(ScheduledJobDescriptor descriptor, string trigger, DateTime? slotUtc, CancellationToken cancellationToken, string? account = null, int? userId = null)
    {
        try
        {
            await runner.RunAsync(descriptor, trigger, slotUtc, account, cancellationToken, userId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 服務關閉。
        }
        catch (Exception ex)
        {
            // 執行器自己的錯誤（例如資料庫暫時無法寫入）不可以讓排程停掉；下一個時段再試。
            logger.LogError(ex, "Scheduled job dispatch failed. JobName={JobName}, Trigger={Trigger}", descriptor.Name, trigger);
        }
    }

    private void Reschedule(ScheduledJobDescriptor descriptor, ScheduledJobSettings settings, DateTime fromUtc)
    {
        var cronText = ScheduleCalculator.GetEffectiveCron(descriptor, settings);
        DateTime? next = TryGetCron(descriptor, settings, out var cron)
            ? ScheduleCalculator.NextOccurrenceUtc(cron, fromUtc, timeProvider.LocalTimeZone)
            : null;
        pendingSlots[descriptor.Name] = new PendingSlot(cronText, next);
    }

    private bool TryGetCron(ScheduledJobDescriptor descriptor, ScheduledJobSettings settings, out CronExpression cron)
    {
        if (ScheduleCalculator.TryParse(ScheduleCalculator.GetEffectiveCron(descriptor, settings), out var parsed))
        {
            cron = parsed!;
            return true;
        }

        cron = null!;
        logger.LogError("Scheduled job has an invalid cron and will not run on schedule. JobName={JobName}", descriptor.Name);
        return false;
    }

    private TimeSpan ComputeDelay(DateTime nowUtc)
    {
        var delay = MaxSleep;
        foreach (var pending in pendingSlots.Values)
        {
            if (pending.SlotUtc is { } slot)
            {
                var untilSlot = slot - nowUtc;
                if (untilSlot < delay)
                {
                    delay = untilSlot;
                }
            }
        }

        return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
    }

    /// <summary>目前的設定；驗證失敗（執行中改壞）時沿用上一份合法的設定。</summary>
    private ScheduledJobSettings CurrentSettings()
    {
        try
        {
            lastGoodSettings = options.CurrentValue;
            settingsErrorLogged = false;
        }
        catch (OptionsValidationException ex)
        {
            if (!settingsErrorLogged)
            {
                settingsErrorLogged = true;
                logger.LogError(ex, "Scheduled job settings are invalid; keeping the last valid settings.");
            }
        }

        return lastGoodSettings;
    }

    private DateTime UtcNow() => timeProvider.GetUtcNow().UtcDateTime;

    private sealed record PendingSlot(string Cron, DateTime? SlotUtc);
}

using Microsoft.Extensions.Options;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Models.AdapterModel;
using CustomerHelpAgent.Web.Configuration;

namespace CustomerHelpAgent.Web.Scheduling;

/// <summary>
/// 「排程作業」頁與系統健康監控讀的總覽（singleton，0.9.96 起）：每個作業的執行時間、下次執行、最近一次結果，
/// 以及啟用切換與「立即執行」。時間轉成伺服器本地時間給畫面。
/// </summary>
public sealed class ScheduledJobOverviewService
{
    private readonly IReadOnlyList<ScheduledJobDescriptor> descriptors;
    private readonly ScheduledJobRunService runService;
    private readonly ScheduledJobTriggerQueue triggerQueue;
    private readonly IOptionsMonitor<ScheduledJobSettings> options;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<ScheduledJobOverviewService> logger;

    public ScheduledJobOverviewService(
        IEnumerable<ScheduledJobDescriptor> descriptors,
        ScheduledJobRunService runService,
        ScheduledJobTriggerQueue triggerQueue,
        IOptionsMonitor<ScheduledJobSettings> options,
        TimeProvider timeProvider,
        ILogger<ScheduledJobOverviewService> logger)
    {
        this.descriptors = descriptors.ToList();
        this.runService = runService;
        this.triggerQueue = triggerQueue;
        this.options = options;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    public IReadOnlyList<ScheduledJobDescriptor> Descriptors => descriptors;

    public async Task<ScheduledJobOverview> GetOverviewAsync()
    {
        ScheduledJobSettings settings;
        string? settingsError = null;
        try
        {
            settings = options.CurrentValue;
        }
        catch (OptionsValidationException ex)
        {
            // 執行中把設定改壞：頁面照樣顯示（用預設時間），並提醒管理員去修正。
            logger.LogWarning(ex, "Scheduled job settings are invalid; showing default schedules.");
            settings = new ScheduledJobSettings();
            settingsError = string.Join("；", ex.Failures);
        }

        var states = (await runService.GetStatesAsync()).ToDictionary(x => x.JobName, StringComparer.OrdinalIgnoreCase);
        var latestRuns = await runService.GetLatestRunsAsync();
        var latestFinished = await runService.GetLatestFinishedRunsAsync();
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var zone = timeProvider.LocalTimeZone;

        var items = descriptors.Select(descriptor =>
        {
            var cron = ScheduleCalculator.GetEffectiveCron(descriptor, settings);
            var state = states.GetValueOrDefault(descriptor.Name);
            var isEnabled = state?.IsEnabled ?? true;
            DateTime? nextRunLocal = null;
            DateTime? overdueSlotUtc = null;
            if (ScheduleCalculator.TryParse(cron, out var expression))
            {
                if (settings.Enabled && isEnabled && ScheduleCalculator.NextOccurrenceUtc(expression!, nowUtc, zone) is { } next)
                {
                    nextRunLocal = TimeZoneInfo.ConvertTimeFromUtc(next, zone);
                }

                if (state is not null)
                {
                    overdueSlotUtc = ScheduleCalculator.CatchUpSlotUtc(
                        expression!, ScheduleCalculator.Anchor(state.LastScheduledForUtc, state.UpdatedAtUtc), nowUtc.Add(-OverdueGrace), zone);
                }
            }

            return new ScheduledJobOverviewItem(
                descriptor.Name,
                descriptor.DisplayName,
                descriptor.Description,
                cron,
                isEnabled,
                nextRunLocal,
                latestRuns.GetValueOrDefault(descriptor.Name),
                latestFinished.GetValueOrDefault(descriptor.Name),
                triggerQueue.IsPending(descriptor.Name),
                isEnabled ? overdueSlotUtc : null);
        }).ToList();

        return new ScheduledJobOverview(settings.Enabled, settingsError, items);
    }

    /// <summary>逾期的寬限：時段過了這麼久還沒有被任何行程搶占，才算「逾期未執行」。</summary>
    public static readonly TimeSpan OverdueGrace = TimeSpan.FromHours(1);

    public Task<List<JobRunAdapterModel>> GetRecentRunsAsync(string jobName, int take = 50)
        => runService.GetRecentRunsAsync(jobName, take);

    public Task SetEnabledAsync(string jobName, bool enabled, string? account)
        => runService.SetEnabledAsync(RequireDescriptor(jobName).Name, enabled, account, timeProvider.GetUtcNow().UtcDateTime);

    /// <summary>放入「立即執行」請求；同一個作業已在佇列或執行中時回 false。</summary>
    public bool TryTrigger(string jobName, string? account, int? userId = null)
        => triggerQueue.TryEnqueue(RequireDescriptor(jobName).Name, account, userId);

    public DateTime ToLocal(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(ScheduleCalculator.AsUtc(utc), timeProvider.LocalTimeZone);

    private ScheduledJobDescriptor RequireDescriptor(string jobName)
        => descriptors.FirstOrDefault(x => string.Equals(x.Name, jobName, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"沒有名為「{jobName}」的排程作業。", nameof(jobName));
}

/// <summary>排程作業總覽。</summary>
/// <param name="SchedulingEnabled">總開關（<c>ScheduledJobSettings:Enabled</c>）。</param>
/// <param name="SettingsError">執行中設定被改壞時的驗證訊息；正常為 null。</param>
public sealed record ScheduledJobOverview(bool SchedulingEnabled, string? SettingsError, IReadOnlyList<ScheduledJobOverviewItem> Items);

/// <summary>單一作業的總覽。</summary>
/// <param name="NextRunLocal">下次執行（本地時間）；停用或總開關關閉時為 null。</param>
/// <param name="LastRun">最近一次執行紀錄（可能還在執行中）。</param>
/// <param name="LastFinishedRun">最近一次「已有結果」的執行紀錄（不含執行中與略過）。</param>
/// <param name="IsQueued">「立即執行」已在佇列或執行中（本行程）。</param>
/// <param name="OverdueSlotUtc">已過寬限時間卻沒有被任何行程執行的時段；沒有逾期時為 null。</param>
public sealed record ScheduledJobOverviewItem(
    string Name,
    string DisplayName,
    string Description,
    string Cron,
    bool IsEnabled,
    DateTime? NextRunLocal,
    JobRunAdapterModel? LastRun,
    JobRunAdapterModel? LastFinishedRun,
    bool IsQueued,
    DateTime? OverdueSlotUtc);

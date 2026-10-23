using System.Collections.Concurrent;
using AutoMapper;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.AdapterModel;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Web.Configuration;
using CustomerHelpAgent.Web.Configuration.Validation;
using CustomerHelpAgent.Web.Diagnostics;
using CustomerHelpAgent.Web.Health;
using CustomerHelpAgent.Web.Scheduling;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 排程作業框架（0.9.96 起）。
///
/// 跨行程的測試用暫存**檔案**資料庫，每個 <see cref="SimulatedProcess"/> 有自己的 DI 容器、自己的連線與鎖控制代碼，
/// 模擬 IIS 重疊回收時並存的兩個行程（同一個行程內的第二個檔案控制代碼一樣會被 FileShare.None 擋下）。
/// 排程時間的計算用 <see cref="ManualTimeProvider"/>（本地時區 UTC+8）。
/// </summary>
public sealed class ScheduledJobFrameworkTests : IDisposable
{
    // UTC 2026-10-01 01:00 ＝ 本地（UTC+8）2026-10-01 09:00。
    private static readonly DateTime NowUtc = new(2026, 10, 1, 1, 0, 0, DateTimeKind.Utc);
    private static readonly TimeZoneInfo Zone = new ManualTimeProvider(DateTimeOffset.UnixEpoch).LocalTimeZone;
    private static readonly ScheduledJobDescriptor Probe = new("Probe", "探測作業", "測試用", "0 3 * * *", typeof(ProbeJob));

    private readonly string directory = Path.Combine(Path.GetTempPath(), "CustomerHelpAgentScheduledJobs", Guid.NewGuid().ToString("N"));
    private readonly string databaseFile;
    private readonly string connectionString;
    private readonly ProbeState probe = new();

    public ScheduledJobFrameworkTests()
    {
        Directory.CreateDirectory(directory);
        databaseFile = Path.Combine(directory, "BackendDB.db");
        connectionString = new SqliteConnectionStringBuilder { DataSource = databaseFile, ForeignKeys = true }.ToString();
        using var context = new FileDbContextFactory(connectionString).CreateDbContext();
        context.Database.EnsureCreated();
    }

    // ================================================================== 排程計算

    [Fact]
    public void NextOccurrence_ShouldUseTheProviderLocalZone()
    {
        // 本地 10-01 09:00 之後的 03:00 是本地 10-02 03:00 = UTC 10-01 19:00。改用 UTC 解讀會得到 10-02 03:00Z。
        var next = ScheduleCalculator.NextOccurrenceUtc(ScheduleCalculator.Parse("0 3 * * *"), NowUtc, Zone);

        Assert.Equal(new DateTime(2026, 10, 1, 19, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void CatchUp_WithUnspecifiedKindFromTheDatabase_ShouldNotThrow()
    {
        // EF 從 SQLite 讀回的 DateTime 是 Unspecified，Cronos 會丟 ArgumentException。
        var anchorFromDb = new DateTime(2026, 9, 29, 0, 0, 0, DateTimeKind.Unspecified);

        var slot = ScheduleCalculator.CatchUpSlotUtc(ScheduleCalculator.Parse("0 3 * * *"), anchorFromDb, NowUtc, Zone);

        Assert.NotNull(slot);
    }

    [Fact]
    public void CatchUp_SeveralMissedSlots_ShouldReturnOnlyTheLatest()
    {
        var anchor = new DateTime(2026, 9, 25, 0, 0, 0, DateTimeKind.Utc);

        var slot = ScheduleCalculator.CatchUpSlotUtc(ScheduleCalculator.Parse("0 3 * * *"), anchor, NowUtc, Zone);

        // 本地 10-01 03:00 = UTC 09-30 19:00。
        Assert.Equal(new DateTime(2026, 9, 30, 19, 0, 0, DateTimeKind.Utc), slot);
    }

    [Fact]
    public void CatchUp_FreshState_ShouldReturnNull()
    {
        // 今天本地 08:00 才建立狀態（新安裝或新作業），之後還沒有任何 03:00 時段。
        var createdAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

        var slot = ScheduleCalculator.CatchUpSlotUtc(ScheduleCalculator.Parse("0 3 * * *"), ScheduleCalculator.Anchor(null, createdAt), NowUtc, Zone);

        Assert.Null(slot);
    }

    [Fact]
    public void CatchUp_NowExactlyOnTheSlot_ShouldBeIncluded()
    {
        var slotUtc = new DateTime(2026, 9, 30, 19, 0, 0, DateTimeKind.Utc);

        var slot = ScheduleCalculator.CatchUpSlotUtc(ScheduleCalculator.Parse("0 3 * * *"), slotUtc.AddHours(-1), slotUtc, Zone);

        Assert.Equal(slotUtc, slot);
    }

    [Fact]
    public void CatchUp_ReEnabledAfterMissedSlots_ShouldNotReplayThem()
    {
        // 上次跑到 09-20；停用期間錯過很多天；今天本地 08:00 重新啟用（UpdatedAt）。
        var lastScheduled = new DateTime(2026, 9, 19, 19, 0, 0, DateTimeKind.Utc);
        var reEnabledAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

        var slot = ScheduleCalculator.CatchUpSlotUtc(ScheduleCalculator.Parse("0 3 * * *"), ScheduleCalculator.Anchor(lastScheduled, reEnabledAt), NowUtc, Zone);

        Assert.Null(slot);
    }

    // ================================================================== 設定驗證

    [Theory]
    [InlineData("not a cron")]
    [InlineData("0 0 3 * * *")]   // 6 欄位（含秒）不接受
    [InlineData("0 0 30 2 *")]    // 解析得過但永遠不會觸發
    public void Validator_ShouldRejectBadCron(string cron)
    {
        var result = Validate(new ScheduledJobSettings { Jobs = { ["Probe"] = new ScheduledJobOverride { Cron = cron } } });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("ScheduledJobSettings:Jobs:Probe:Cron", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_ShouldRejectUnknownJob_ButAcceptAnyCase()
    {
        Assert.True(Validate(new ScheduledJobSettings { Jobs = { ["Typo"] = new ScheduledJobOverride { Cron = "0 3 * * *" } } }).Failed);
        Assert.True(Validate(new ScheduledJobSettings { Jobs = { ["probe"] = new ScheduledJobOverride { Cron = "0 3 * * *" } } }).Succeeded);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(40000)]
    public void Validator_ShouldRejectRetentionOutOfRange(int days)
    {
        var result = Validate(new ScheduledJobSettings { JobRunRetentionDays = days });

        Assert.Contains(result.Failures!, f => f.Contains("ScheduledJobSettings:JobRunRetentionDays", StringComparison.Ordinal));
    }

    // ================================================================== 跨行程只跑一次

    [Fact]
    public async Task SequentialProcesses_SameSlot_SecondShouldNotExecute()
    {
        // 最常見的重疊回收情境：A 跑完釋放鎖，B 一毫秒後才拿到 —— 只靠鎖擋不住，要靠時段搶占。
        await using var a = await StartProcessAsync();
        await using var b = await StartProcessAsync();
        var slot = Slot(1);

        Assert.Equal(ScheduledJobRunOutcome.Executed, await a.Runner.RunAsync(Probe, JobRunTriggers.Schedule, slot, null, default));
        Assert.Equal(ScheduledJobRunOutcome.ClaimLost, await b.Runner.RunAsync(Probe, JobRunTriggers.Schedule, slot, null, default));

        Assert.Equal(1, probe.Executions);
        Assert.Equal(1, await CountRunsAsync());
    }

    [Fact]
    public async Task ConcurrentProcesses_SameSlot_ShouldExecuteOnce()
    {
        await using var a = await StartProcessAsync();
        await using var b = await StartProcessAsync();
        probe.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slot = Slot(1);

        var first = a.Runner.RunAsync(Probe, JobRunTriggers.Schedule, slot, null, default);
        var second = b.Runner.RunAsync(Probe, JobRunTriggers.Schedule, slot, null, default);
        await Task.Delay(200);
        probe.Gate.SetResult();
        var outcomes = await Task.WhenAll(first, second);

        Assert.Equal(1, probe.Executions);
        Assert.Single(outcomes, ScheduledJobRunOutcome.Executed);
    }

    [Fact]
    public async Task OlderSlotAfterANewerClaim_ShouldNotExecute()
    {
        // A 的迴圈晚醒、還在處理較舊的時段時，B 已經補跑了較新的時段 —— A 不可以再跑一次。
        await using var a = await StartProcessAsync();
        await using var b = await StartProcessAsync();

        await b.Runner.RunAsync(Probe, JobRunTriggers.CatchUp, Slot(2), null, default);
        var outcome = await a.Runner.RunAsync(Probe, JobRunTriggers.Schedule, Slot(1), null, default);

        Assert.Equal(ScheduledJobRunOutcome.ClaimLost, outcome);
        Assert.Equal(1, probe.Executions);
    }

    [Fact]
    public async Task ManualRun_ShouldNotConsumeTheScheduledSlot()
    {
        await using var a = await StartProcessAsync();

        await a.Runner.RunAsync(Probe, JobRunTriggers.Manual, null, "alice", default);
        var scheduled = await a.Runner.RunAsync(Probe, JobRunTriggers.Schedule, Slot(1), null, default);

        Assert.Equal(ScheduledJobRunOutcome.Executed, scheduled);
        Assert.Equal(2, probe.Executions);
        var manual = (await a.RunService.GetRecentRunsAsync("Probe", 10)).Single(x => x.Trigger == JobRunTriggers.Manual);
        Assert.Equal("alice", manual.TriggeredByAccount);
        Assert.Null(manual.ScheduledForUtc);
    }

    [Fact]
    public async Task LockHeldByAnotherRun_ShouldRecordSkipped_AndNotExecute()
    {
        await using var a = await StartProcessAsync();
        using var heldElsewhere = CrossProcessFileLock.TryAcquire($"{databaseFile}.job-Probe.lock");
        Assert.NotNull(heldElsewhere);

        var outcome = await a.Runner.RunAsync(Probe, JobRunTriggers.Schedule, Slot(1), null, default);

        Assert.Equal(ScheduledJobRunOutcome.Skipped, outcome);
        Assert.Equal(0, probe.Executions);
        Assert.Equal(JobRunStatuses.Skipped, (await a.RunService.GetRecentRunsAsync("Probe", 1)).Single().Status);
    }

    [Fact]
    public async Task DisabledJob_ShouldNotRunOnSchedule_ButManualStillRuns()
    {
        await using var a = await StartProcessAsync();
        await a.RunService.SetEnabledAsync("Probe", false, "alice", NowUtc);

        Assert.Equal(ScheduledJobRunOutcome.ClaimLost, await a.Runner.RunAsync(Probe, JobRunTriggers.Schedule, Slot(1), null, default));
        Assert.Equal(ScheduledJobRunOutcome.Executed, await a.Runner.RunAsync(Probe, JobRunTriggers.Manual, null, "alice", default));
        Assert.Equal(1, probe.Executions);
    }

    [Fact]
    public async Task StaleRunning_ShouldBeInterruptedOnlyWhenTheLockIsFree()
    {
        await using var a = await StartProcessAsync();
        await a.RunService.InsertRunAsync(new JobRun { JobName = "Probe", Trigger = JobRunTriggers.Schedule, StartedAtUtc = NowUtc.AddHours(-1), Status = JobRunStatuses.Running }, default);

        using (CrossProcessFileLock.TryAcquire($"{databaseFile}.job-Probe.lock"))
        {
            // 另一個行程正在跑：它的 Running 是真的在跑，不可以改。
            await a.Runner.RecoverStaleRunsAsync(Probe);
            Assert.Equal(JobRunStatuses.Running, (await a.RunService.GetRecentRunsAsync("Probe", 1)).Single().Status);
        }

        await a.Runner.RecoverStaleRunsAsync(Probe);
        Assert.Equal(JobRunStatuses.Interrupted, (await a.RunService.GetRecentRunsAsync("Probe", 1)).Single().Status);
    }

    [Fact]
    public async Task JobThrows_ShouldRecordFailedWithTraceId_AndLogTheError()
    {
        await using var a = await StartProcessAsync();
        probe.Throw = true;

        await a.Runner.RunAsync(Probe, JobRunTriggers.Manual, null, null, default);

        var run = (await a.RunService.GetRecentRunsAsync("Probe", 1)).Single();
        Assert.Equal(JobRunStatuses.Failed, run.Status);
        Assert.False(string.IsNullOrEmpty(run.TraceId));
        Assert.Contains(run.TraceId!, run.Message!, StringComparison.Ordinal);
        Assert.Contains(a.RunnerLog.Entries, e => e.Level == LogLevel.Error && e.Exception is InvalidOperationException);
    }

    [Fact]
    public async Task JobReportsFailure_ShouldRecordFailed_WithoutLoggingAnErrorAgain()
    {
        // 服務層已經記過錯誤；再 LogError 會讓系統例外紀錄出現兩筆。
        await using var a = await StartProcessAsync();
        probe.Fail = true;

        await a.Runner.RunAsync(Probe, JobRunTriggers.Manual, null, null, default);

        Assert.Equal(JobRunStatuses.Failed, (await a.RunService.GetRecentRunsAsync("Probe", 1)).Single().Status);
        Assert.DoesNotContain(a.RunnerLog.Entries, e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task FailedRun_ShouldNotifyAdminsAndTheTriggeringUser_WithEmail()
    {
        await using var a = await StartProcessAsync();
        probe.Fail = true;

        await a.Runner.RunAsync(Probe, JobRunTriggers.Manual, null, "alice", default, triggeredByUserId: 42);

        var request = Assert.Single(a.Notifications.Requests);
        Assert.Equal(NotificationCategories.JobFailed, request.Category);
        Assert.True(request.Target.Admins);
        Assert.Equal([42], request.Target.UserIds);
        Assert.True(request.AlsoEmail);
        Assert.Equal("/scheduled-jobs", request.Link);
    }

    [Fact]
    public async Task ScheduledFailure_ShouldNotifyOnlyAdmins_AndSuccessShouldNotNotify()
    {
        await using var a = await StartProcessAsync();
        await a.Runner.RunAsync(Probe, JobRunTriggers.Manual, null, null, default);
        Assert.Empty(a.Notifications.Requests);

        probe.Throw = true;
        await a.Runner.RunAsync(Probe, JobRunTriggers.Manual, null, null, default);

        var request = Assert.Single(a.Notifications.Requests);
        Assert.True(request.Target.Admins);
        Assert.Empty(request.Target.UserIds);
    }

    [Fact]
    public async Task InterruptedRun_ShouldNotNotify()
    {
        await using var a = await StartProcessAsync();
        probe.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var shutdown = new CancellationTokenSource();

        var running = a.Runner.RunAsync(Probe, JobRunTriggers.Manual, null, null, shutdown.Token);
        await Task.Delay(200);
        await shutdown.CancelAsync();
        await running;

        Assert.Empty(a.Notifications.Requests);
    }

    [Fact]
    public async Task CancelledRun_ShouldRecordInterrupted_AndReleaseTheLock()
    {
        await using var a = await StartProcessAsync();
        probe.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var shutdown = new CancellationTokenSource();

        var running = a.Runner.RunAsync(Probe, JobRunTriggers.Manual, null, null, shutdown.Token);
        await Task.Delay(200);
        shutdown.Cancel();
        await running;

        Assert.Equal(JobRunStatuses.Interrupted, (await a.RunService.GetRecentRunsAsync("Probe", 1)).Single().Status);
        using var reacquired = CrossProcessFileLock.TryAcquire($"{databaseFile}.job-Probe.lock");
        Assert.NotNull(reacquired);
    }

    [Fact]
    public async Task DuringARun_ExceptionContextShouldBeTheBackgroundJob()
    {
        // 作業記的錯誤要歸在「背景作業 / 作業名稱」，不是觸發它的畫面。
        // （執行後呼叫端看不到這個 context 是 AsyncLocal 的語意 —— 被 await 的方法裡設定的值不會流回呼叫端 ——
        // 所以不另外斷言「結束後清除」，那樣的斷言永遠成立、測不到任何東西。）
        await using var a = await StartProcessAsync();

        await a.Runner.RunAsync(Probe, JobRunTriggers.Manual, null, "alice", default);

        var seen = Assert.Single(probe.Contexts);
        Assert.Equal(ExceptionSources.Background, seen!.Source);
        Assert.Equal("Probe", seen.Page);
        Assert.Equal("alice", seen.Account);
    }

    [Fact]
    public async Task EachRun_ShouldHaveAFreshTraceCode_NotTheCallers()
    {
        await using var a = await StartProcessAsync();

        using (TraceCode.Begin("AAAAAAAA"))
        {
            await a.Runner.RunAsync(Probe, JobRunTriggers.Manual, null, null, default);
        }

        var seen = Assert.Single(probe.TraceCodes);
        Assert.NotEqual("AAAAAAAA", seen);
        Assert.Equal(seen, (await a.RunService.GetRecentRunsAsync("Probe", 1)).Single().TraceId);
    }

    [Fact]
    public async Task EachRun_ShouldResolveTheJobInAFreshScope()
    {
        await using var a = await StartProcessAsync();

        await a.Runner.RunAsync(Probe, JobRunTriggers.Manual, null, null, default);
        await a.Runner.RunAsync(Probe, JobRunTriggers.Manual, null, null, default);

        Assert.Equal(2, probe.ScopeMarkers.Distinct().Count());
    }

    [Fact]
    public async Task Prune_ShouldRemoveOldRuns_KeepRunningOnes_AndNotAffectCatchUp()
    {
        await using var a = await StartProcessAsync(new ScheduledJobSettings { JobRunRetentionDays = 1 });
        await a.Runner.RunAsync(Probe, JobRunTriggers.Schedule, Slot(1), null, default);
        await a.RunService.InsertRunAsync(new JobRun { JobName = "Probe", Trigger = JobRunTriggers.Manual, StartedAtUtc = NowUtc.AddDays(-10), Status = JobRunStatuses.Succeeded }, default);
        await a.RunService.InsertRunAsync(new JobRun { JobName = "Other", Trigger = JobRunTriggers.Manual, StartedAtUtc = NowUtc.AddDays(-10), Status = JobRunStatuses.Running }, default);

        await a.Runner.RunAsync(Probe, JobRunTriggers.Manual, null, null, default);

        await using var context = new FileDbContextFactory(connectionString).CreateDbContext();
        Assert.False(await context.JobRun.AnyAsync(x => x.JobName == "Probe" && x.StartedAtUtc < NowUtc.AddDays(-1)));
        Assert.True(await context.JobRun.AnyAsync(x => x.JobName == "Other" && x.Status == JobRunStatuses.Running));
        Assert.Equal(Slot(1), ScheduleCalculator.AsUtc((await context.ScheduledJobState.SingleAsync(x => x.JobName == "Probe")).LastScheduledForUtc!.Value));
    }

    [Fact]
    public async Task RetentionStats_ShouldCountWhatPruneRemoves_AndSkipRunningOnes()
    {
        // 資料保留與清理頁（0.9.133 起）：執行中的紀錄不算進「下次會清掉」，與 PruneAsync 相同。
        await using var a = await StartProcessAsync();
        await a.RunService.InsertRunAsync(new JobRun { JobName = "Probe", Trigger = JobRunTriggers.Manual, StartedAtUtc = NowUtc.AddDays(-10), Status = JobRunStatuses.Succeeded }, default);
        await a.RunService.InsertRunAsync(new JobRun { JobName = "Other", Trigger = JobRunTriggers.Manual, StartedAtUtc = NowUtc.AddDays(-10), Status = JobRunStatuses.Running }, default);
        await a.RunService.InsertRunAsync(new JobRun { JobName = "Probe", Trigger = JobRunTriggers.Manual, StartedAtUtc = NowUtc, Status = JobRunStatuses.Succeeded }, default);
        var cutoff = NowUtc.AddDays(-1);

        var stats = await a.RunService.GetRetentionStatsAsync(cutoff);

        Assert.Equal(3, stats.Total);
        Assert.Equal(1, stats.Expired);
        Assert.Equal(stats.Expired, await a.RunService.PruneAsync(cutoff));
        Assert.Null((await a.RunService.GetRetentionStatsAsync(null)).Expired);
    }

    // ================================================================== 排程器

    [Fact]
    public async Task Tick_ShouldRunWithTheSlotAsScheduledFor_AndNotRepeatItNextMinute()
    {
        await using var a = await StartProcessAsync();
        var worker = a.CreateWorker();
        await worker.CatchUpAsync(NowUtc, default);

        var slot = new DateTime(2026, 10, 1, 19, 0, 0, DateTimeKind.Utc);
        await worker.TickAsync(slot.AddSeconds(5), default);
        await worker.TickAsync(slot.AddMinutes(1), default);

        Assert.Equal(1, probe.Executions);
        Assert.Equal(slot, ScheduleCalculator.AsUtc((await a.RunService.GetRecentRunsAsync("Probe", 1)).Single().ScheduledForUtc!.Value));
    }

    [Fact]
    public async Task Tick_WithSchedulingSwitchedOff_ShouldNotRun()
    {
        await using var a = await StartProcessAsync(new ScheduledJobSettings { Enabled = false });
        var worker = a.CreateWorker();
        await worker.CatchUpAsync(NowUtc, default);

        await worker.TickAsync(new DateTime(2026, 10, 1, 19, 0, 5, DateTimeKind.Utc), default);

        Assert.Equal(0, probe.Executions);
    }

    [Fact]
    public async Task Tick_WhenSettingsBecomeInvalid_ShouldKeepTheLastValidOnes()
    {
        var monitor = new FlakyMonitor(new ScheduledJobSettings());
        await using var a = await StartProcessAsync(monitor: monitor);
        var worker = a.CreateWorker();
        await worker.CatchUpAsync(NowUtc, default);
        monitor.Broken = true;

        await worker.TickAsync(new DateTime(2026, 10, 1, 19, 0, 5, DateTimeKind.Utc), default);

        Assert.Equal(1, probe.Executions);
    }

    [Fact]
    public async Task Startup_OnAFreshDatabase_ShouldNotCatchUp()
    {
        await using var a = await StartProcessAsync(ensureStates: false);
        var worker = a.CreateWorker();

        await worker.StartupAsync(NowUtc, default);
        await worker.CatchUpAsync(NowUtc.AddMinutes(1), default);

        Assert.Equal(0, probe.Executions);
    }

    [Fact]
    public async Task Startup_AfterAMissedSlot_ShouldCatchUpOnce()
    {
        await using var a = await StartProcessAsync(ensureStates: false);
        await a.RunService.EnsureStatesAsync(["Probe"], NowUtc.AddDays(-5), default);
        var worker = a.CreateWorker();

        await worker.CatchUpAsync(NowUtc, default);

        Assert.Equal(1, probe.Executions);
        Assert.Equal(JobRunTriggers.CatchUp, (await a.RunService.GetRecentRunsAsync("Probe", 1)).Single().Trigger);
    }

    [Fact]
    public void TriggerQueue_ShouldRefuseADuplicateWhileOneIsPending()
    {
        var queue = new ScheduledJobTriggerQueue();

        Assert.True(queue.TryEnqueue("Probe", "alice"));
        Assert.False(queue.TryEnqueue("probe", "bob"));
        queue.Complete("Probe");
        Assert.True(queue.TryEnqueue("Probe", "bob"));
    }

    // ================================================================== 跨行程檔案鎖

    [Fact]
    public void CrossProcessFileLock_ShouldTreatOnlySharingViolationsAsHeld()
    {
        var path = Path.Combine(directory, "probe.lock");
        using (var first = CrossProcessFileLock.TryAcquire(path))
        {
            Assert.NotNull(first);
            Assert.Null(CrossProcessFileLock.TryAcquire(path));
        }

        using (var again = CrossProcessFileLock.TryAcquire(path))
        {
            Assert.NotNull(again);
        }

        // 其他 IO 錯誤（這裡是不合法的路徑）必須丟出，不可以被當成「有人佔著」而讓作業永遠默默不執行。
        Assert.ThrowsAny<Exception>(() => CrossProcessFileLock.TryAcquire(Path.Combine(directory, "bad\0name.lock")));
    }

    // ================================================================== 系統健康監控

    [Fact]
    public void Health_ShouldFlagFailedAndOverdueJobs()
    {
        var failed = new JobRunAdapterModel { Status = JobRunStatuses.Failed };
        var ok = new JobRunAdapterModel { Status = JobRunStatuses.Succeeded };

        Assert.Equal(SystemHealthStatus.Healthy, Evaluate(true, Item(ok)).Status);
        Assert.Equal(SystemHealthStatus.Unhealthy, Evaluate(true, Item(failed)).Status);
        Assert.Equal(SystemHealthStatus.Degraded, Evaluate(true, Item(ok, overdue: Slot(1))).Status);
        Assert.Equal(SystemHealthStatus.Degraded, Evaluate(false, Item(ok)).Status);

        // 停用的作業最後一次失敗不算（管理員知道它停了）。
        Assert.Equal(SystemHealthStatus.Healthy, Evaluate(true, Item(failed, enabled: false)).Status);
    }

    // ================================================================== 測試基礎

    private static DateTime Slot(int day) => new(2026, 9, day, 19, 0, 0, DateTimeKind.Utc);

    private static ValidateOptionsResult Validate(ScheduledJobSettings settings)
        => new ScheduledJobSettingsValidator([Probe]).Validate(null, settings);

    private static SystemHealthItem Evaluate(bool schedulingEnabled, ScheduledJobOverviewItem item)
        => SystemHealthService.EvaluateScheduledJobs(new ScheduledJobOverview(schedulingEnabled, null, [item]));

    private static ScheduledJobOverviewItem Item(JobRunAdapterModel lastFinished, bool enabled = true, DateTime? overdue = null)
        => new("Probe", "探測作業", "測試用", "0 3 * * *", enabled, null, lastFinished, lastFinished, false, overdue);

    private async Task<int> CountRunsAsync()
    {
        await using var context = new FileDbContextFactory(connectionString).CreateDbContext();
        return await context.JobRun.CountAsync();
    }

    private async Task<SimulatedProcess> StartProcessAsync(ScheduledJobSettings? settings = null, IOptionsMonitor<ScheduledJobSettings>? monitor = null, bool ensureStates = true)
    {
        var process = new SimulatedProcess(connectionString, databaseFile, probe, monitor ?? new StaticOptionsMonitor<ScheduledJobSettings>(settings ?? new ScheduledJobSettings()));
        if (ensureStates)
        {
            // 狀態建立於「現在」：等同全新安裝，不會補跑（要測補跑的測試自己建立較早的狀態）。
            await process.RunService.EnsureStatesAsync(["Probe"], NowUtc, default);
        }

        return process;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // 暫存檔清不掉不影響測試結果。
        }
    }

    /// <summary>一個「行程」：自己的 DI 容器、連線與鎖。</summary>
    private sealed class SimulatedProcess : IAsyncDisposable
    {
        private readonly ServiceProvider provider;
        private readonly IOptionsMonitor<ScheduledJobSettings> options;
        private readonly ManualTimeProvider clock = new(new DateTimeOffset(NowUtc));

        public SimulatedProcess(string connectionString, string databaseFile, ProbeState probe, IOptionsMonitor<ScheduledJobSettings> options)
        {
            this.options = options;
            var services = new ServiceCollection();
            services.AddSingleton<IDbContextFactory<BackendDBContext>>(new FileDbContextFactory(connectionString));
            services.AddSingleton<IMapper>(new MapperConfiguration(c => c.AddProfile<AutoMapping>(), NullLoggerFactory.Instance).CreateMapper());
            services.AddSingleton(probe);
            services.AddSingleton<ExceptionContextAccessor>();
            services.AddScoped<ScopeMarker>();
            services.AddScoped<ProbeJob>();
            services.AddSingleton<INotificationSender>(Notifications);
            provider = services.BuildServiceProvider();

            Accessor = provider.GetRequiredService<ExceptionContextAccessor>();
            RunService = new ScheduledJobRunService(provider.GetRequiredService<IDbContextFactory<BackendDBContext>>(), provider.GetRequiredService<IMapper>(), NullLogger<ScheduledJobRunService>.Instance);
            Runner = new ScheduledJobRunner(
                provider.GetRequiredService<IServiceScopeFactory>(),
                RunService,
                new JobLockProvider(databaseFile, NullLogger<JobLockProvider>.Instance),
                Accessor,
                options,
                clock,
                RunnerLog);
        }

        public ExceptionContextAccessor Accessor { get; }

        public ScheduledJobRunService RunService { get; }

        public ScheduledJobRunner Runner { get; }

        public CapturingLogger<ScheduledJobRunner> RunnerLog { get; } = new();

        /// <summary>作業失敗時發出的通知（0.9.100 起）。</summary>
        public RecordingNotificationSender Notifications { get; } = new();

        public JobSchedulerWorker CreateWorker()
            => new([Probe], Runner, RunService, new ScheduledJobTriggerQueue(), options, clock, NullLogger<JobSchedulerWorker>.Instance);

        public ValueTask DisposeAsync() => provider.DisposeAsync();
    }

    private sealed class FileDbContextFactory(string connectionString) : IDbContextFactory<BackendDBContext>
    {
        public BackendDBContext CreateDbContext()
            => new(new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connectionString).Options);
    }

    /// <summary>探測作業記下它看到的一切；可以讓它等待、回報失敗或丟例外。</summary>
    private sealed class ProbeState
    {
        private int executions;

        public int Executions => executions;

        public TaskCompletionSource? Gate { get; set; }

        public bool Throw { get; set; }

        public bool Fail { get; set; }

        public ConcurrentBag<ExceptionContext?> Contexts { get; } = [];

        public ConcurrentBag<string?> TraceCodes { get; } = [];

        public ConcurrentBag<Guid> ScopeMarkers { get; } = [];

        public void Executed() => Interlocked.Increment(ref executions);
    }

    private sealed class ScopeMarker
    {
        public Guid Id { get; } = Guid.NewGuid();
    }

    private sealed class ProbeJob(ProbeState state, ScopeMarker marker, ExceptionContextAccessor accessor) : IScheduledJob
    {
        public async Task<ScheduledJobResult> ExecuteAsync(ScheduledJobContext context, CancellationToken cancellationToken)
        {
            state.Executed();
            state.Contexts.Add(accessor.Current);
            state.TraceCodes.Add(TraceCode.Current);
            state.ScopeMarkers.Add(marker.Id);

            if (state.Gate is { } gate)
            {
                await gate.Task.WaitAsync(cancellationToken);
            }

            if (state.Throw)
            {
                throw new InvalidOperationException("probe failure");
            }

            return state.Fail ? ScheduledJobResult.Failure("reported failure") : ScheduledJobResult.Success("ok");
        }
    }

    /// <summary>模擬執行中把 appsettings 改壞：之後讀 CurrentValue 會丟驗證例外。</summary>
    private sealed class FlakyMonitor(ScheduledJobSettings value) : IOptionsMonitor<ScheduledJobSettings>
    {
        public bool Broken { get; set; }

        public ScheduledJobSettings CurrentValue
            => Broken ? throw new OptionsValidationException(ScheduledJobSettings.SectionName, typeof(ScheduledJobSettings), ["broken"]) : value;

        public ScheduledJobSettings Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<ScheduledJobSettings, string?> listener) => null;
    }
}

/// <summary>記下所有日誌項目的 logger（驗證「有沒有記錯誤」）。</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public ConcurrentQueue<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Entries.Enqueue((logLevel, formatter(state, exception), exception));
}

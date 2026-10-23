using System.Linq.Expressions;
using AutoMapper;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Models.AdapterModel;
using CustomerHelpAgent.Models.Systems;

namespace CustomerHelpAgent.Business.Services.DataAccess;

/// <summary>
/// 排程作業的狀態與執行紀錄（0.9.96 起）。註冊為 singleton：只注入 <see cref="IDbContextFactory{TContext}"/>（singleton），
/// 每個方法用完即棄，排程執行器與管理頁都直接使用。
///
/// ⚠️ <see cref="TryClaimSlotAsync"/> 是「跨行程同一時段只跑一次」的權威：一條 UPDATE 在 SQLite 的單寫者鎖內完成，
/// 兩個行程同一時段只有一個影響到資料列；而且單調 —— 較新的時段被搶走之後，較舊的時段也搶不到。
/// </summary>
public class ScheduledJobRunService
{
    private const int SqliteConstraintErrorCode = 19;
    private const int MaxMessageLength = 1000;

    private readonly IDbContextFactory<BackendDBContext> contextFactory;
    private readonly IMapper mapper;
    private readonly ILogger<ScheduledJobRunService> logger;

    public ScheduledJobRunService(
        IDbContextFactory<BackendDBContext> contextFactory,
        IMapper mapper,
        ILogger<ScheduledJobRunService> logger)
    {
        this.contextFactory = contextFactory;
        this.mapper = mapper;
        this.logger = logger;
    }

    /// <summary>
    /// 替還沒有狀態列的作業建立一列（啟用、建立時間 = 現在）。另一個行程同時建立時以主鍵衝突收場，視為成功。
    /// 建立時間就是補跑的起點：新安裝或新加入的作業不會一啟動就補跑。
    /// </summary>
    public async Task EnsureStatesAsync(IEnumerable<string> jobNames, DateTime nowUtc, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var existing = (await context.ScheduledJobState.Select(x => x.JobName).ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var name in jobNames.Where(n => !existing.Contains(n)))
        {
            context.ScheduledJobState.Add(new ScheduledJobState { JobName = name, IsEnabled = true, CreatedAtUtc = nowUtc, UpdatedAtUtc = nowUtc });
            try
            {
                await context.SaveChangesAsync(cancellationToken);
                logger.LogInformation("Scheduled job state created. JobName={JobName}", name);
            }
            catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: SqliteConstraintErrorCode })
            {
                // 另一個行程剛好也在建立同一列。
                context.ChangeTracker.Clear();
            }
        }
    }

    /// <summary>搶占排程時段：啟用中、而且這個時段比上次搶到的還新，才會影響 1 列並回 true。</summary>
    public async Task<bool> TryClaimSlotAsync(string jobName, DateTime slotUtc, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var affected = await context.ScheduledJobState
            .Where(x => x.JobName == jobName && x.IsEnabled && (x.LastScheduledForUtc == null || x.LastScheduledForUtc < slotUtc))
            .ExecuteUpdateAsync(x => x.SetProperty(s => s.LastScheduledForUtc, slotUtc), cancellationToken);
        return affected == 1;
    }

    /// <summary>寫入一筆執行紀錄並立即提交，回傳 Id。</summary>
    public async Task<int> InsertRunAsync(JobRun run, CancellationToken cancellationToken)
    {
        run.Message = Truncate(run.Message);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        context.JobRun.Add(run);
        await context.SaveChangesAsync(cancellationToken);
        return run.Id;
    }

    /// <summary>寫入執行結果。刻意不收 CancellationToken：關機中斷時也必須把「中斷」寫進去。</summary>
    public async Task FinishRunAsync(int runId, string status, string? message, DateTime finishedAtUtc, long durationMs, string? traceId)
    {
        var truncated = Truncate(message);
        await using var context = await contextFactory.CreateDbContextAsync();
        await context.JobRun
            .Where(x => x.Id == runId)
            .ExecuteUpdateAsync(x => x
                .SetProperty(r => r.Status, status)
                .SetProperty(r => r.Message, truncated)
                .SetProperty(r => r.FinishedAtUtc, finishedAtUtc)
                .SetProperty(r => r.DurationMs, durationMs)
                .SetProperty(r => r.TraceId, traceId));
    }

    /// <summary>
    /// 把這個作業殘留的「執行中」改成「中斷」。只能在持有這個作業的鎖時呼叫 —— 拿得到鎖代表沒有人正在執行它，
    /// 殘留的 Running 一定是上一個行程執行到一半被結束。
    /// </summary>
    public async Task<int> MarkStaleRunningInterruptedAsync(string jobName, DateTime nowUtc)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        return await context.JobRun
            .Where(x => x.JobName == jobName && x.Status == JobRunStatuses.Running)
            .ExecuteUpdateAsync(x => x
                .SetProperty(r => r.Status, JobRunStatuses.Interrupted)
                .SetProperty(r => r.FinishedAtUtc, nowUtc)
                .SetProperty(r => r.Message, "執行到一半時網站關閉或行程結束。"));
    }

    /// <summary>刪除早於門檻的執行紀錄（執行中的不刪）。補跑的錨點不在這張表，清掉不影響排程。</summary>
    public async Task<int> PruneAsync(DateTime cutoffUtc)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        return await context.JobRun
            .Where(Prunable(cutoffUtc))
            .ExecuteDeleteAsync();
    }

    /// <summary>清理條件：<see cref="PruneAsync"/> 與 <see cref="GetRetentionStatsAsync"/> 共用（「資料保留與清理」頁）。</summary>
    private static Expression<Func<JobRun, bool>> Prunable(DateTime cutoffUtc)
        => x => x.StartedAtUtc < cutoffUtc && x.Status != JobRunStatuses.Running;

    /// <summary>
    /// 筆數、最舊一筆與超過 <paramref name="cutoffUtc"/>（UTC；null＝不自動清理）的筆數。
    /// ⚠️ <see cref="RetentionStats.Oldest"/> 是 UTC。
    /// </summary>
    public async Task<RetentionStats> GetRetentionStatsAsync(DateTime? cutoffUtc)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var total = await context.JobRun.CountAsync();
        var oldest = await context.JobRun.MinAsync(x => (DateTime?)x.StartedAtUtc);
        int? expired = cutoffUtc is { } c ? await context.JobRun.CountAsync(Prunable(c)) : null;
        return new RetentionStats(total, oldest, expired);
    }

    /// <summary>
    /// 管理頁的啟用開關。同時更新 <c>UpdatedAtUtc</c>：重新啟用時不補跑停用期間錯過的時段。
    /// </summary>
    public async Task SetEnabledAsync(string jobName, bool enabled, string? account, DateTime nowUtc)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var state = await context.ScheduledJobState.FirstOrDefaultAsync(x => x.JobName == jobName);
        if (state is null)
        {
            state = new ScheduledJobState { JobName = jobName, CreatedAtUtc = nowUtc };
            context.ScheduledJobState.Add(state);
        }

        state.IsEnabled = enabled;
        state.UpdatedAtUtc = nowUtc;
        state.UpdatedBy = account;
        await context.SaveChangesAsync();
        logger.LogInformation("Scheduled job enabled state changed. JobName={JobName}, Enabled={Enabled}", jobName, enabled);
    }

    public async Task<List<ScheduledJobStateAdapterModel>> GetStatesAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var states = await context.ScheduledJobState.AsNoTracking().ToListAsync(cancellationToken);
        return mapper.Map<List<ScheduledJobStateAdapterModel>>(states);
    }

    /// <summary>每個作業最近一次的執行紀錄（以 Id 判斷先後：Id 依寫入順序遞增）。</summary>
    public async Task<Dictionary<string, JobRunAdapterModel>> GetLatestRunsAsync()
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var latestIds = await context.JobRun.GroupBy(x => x.JobName).Select(g => g.Max(x => x.Id)).ToListAsync();
        return await LoadByIdsAsync(context, latestIds);
    }

    /// <summary>每個作業最近一次「已有結果」的執行紀錄（健康監控用：不看還在跑的與略過的）。</summary>
    public async Task<Dictionary<string, JobRunAdapterModel>> GetLatestFinishedRunsAsync()
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var latestIds = await context.JobRun
            .Where(x => x.Status != JobRunStatuses.Running && x.Status != JobRunStatuses.Skipped)
            .GroupBy(x => x.JobName)
            .Select(g => g.Max(x => x.Id))
            .ToListAsync();
        return await LoadByIdsAsync(context, latestIds);
    }

    public async Task<List<JobRunAdapterModel>> GetRecentRunsAsync(string jobName, int take)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var runs = await context.JobRun
            .AsNoTracking()
            .Where(x => x.JobName == jobName)
            .OrderByDescending(x => x.Id)
            .Take(take)
            .ToListAsync();
        return mapper.Map<List<JobRunAdapterModel>>(runs);
    }

    private async Task<Dictionary<string, JobRunAdapterModel>> LoadByIdsAsync(BackendDBContext context, List<int> ids)
    {
        var runs = await context.JobRun.AsNoTracking().Where(x => ids.Contains(x.Id)).ToListAsync();
        return mapper.Map<List<JobRunAdapterModel>>(runs).ToDictionary(x => x.JobName, StringComparer.OrdinalIgnoreCase);
    }

    private static string? Truncate(string? message)
        => message is { Length: > MaxMessageLength } ? message[..MaxMessageLength] : message;
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Startup;

namespace CustomerHelpAgent.Business.Services.DataAccess;

/// <summary>
/// 劇本執行（M6）：展示者「準備此劇本」時建立一筆，並由 <c>DemoControlService.ActiveRunId</c> 讓後續流程埋點蓋上編號。
/// 這裡只負責建立與查詢；不刪除（重置示範資料也不會刪）。
/// </summary>
public class DemoScenarioRunService
{
    private readonly IDbContextFactory<BackendDBContext> contextFactory;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<DemoScenarioRunService> logger;

    public DemoScenarioRunService(
        IDbContextFactory<BackendDBContext> contextFactory,
        TimeProvider timeProvider,
        ILogger<DemoScenarioRunService> logger)
    {
        this.contextFactory = contextFactory;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    /// <summary>為劇本建立一筆執行；<paramref name="isReplay"/> 是建立當下是否為回放模式。</summary>
    public async Task<DemoScenarioRun> CreateAsync(DemoScenario scenario, bool isReplay)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var run = new DemoScenarioRun
        {
            ScenarioCode = scenario.Code,
            Family = scenario.Family,
            Flow = scenario.Flow,
            Account = scenario.Account,
            IsReplay = isReplay,
            StartedAt = timeProvider.GetLocalNow().DateTime,
        };
        context.DemoScenarioRun.Add(run);
        await context.SaveChangesAsync();
        logger.LogInformation(
            "Demo scenario run created. RunId={RunId}, ScenarioCode={ScenarioCode}, Flow={Flow}, IsReplay={IsReplay}",
            run.Id, run.ScenarioCode, run.Flow, run.IsReplay);
        return run;
    }

    public async Task<DemoScenarioRun?> GetByIdAsync(int id)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        return await context.DemoScenarioRun.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
    }

    /// <summary>開始時間落在 [<paramref name="from"/>, <paramref name="to"/>) 的執行，依開始時間由舊到新。</summary>
    public async Task<List<DemoScenarioRun>> GetByRangeAsync(DateTime from, DateTime to)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        return await context.DemoScenarioRun.AsNoTracking()
            .Where(x => x.StartedAt >= from && x.StartedAt < to)
            .OrderBy(x => x.StartedAt).ThenBy(x => x.Id)
            .ToListAsync();
    }
}

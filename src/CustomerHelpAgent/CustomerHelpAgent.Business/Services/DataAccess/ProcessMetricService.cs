using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Shop;
using CustomerHelpAgent.Share.Shop;

namespace CustomerHelpAgent.Business.Services.DataAccess;

/// <summary>流程埋點：記錄傳統與 Agent 管道各自的操作步驟。埋點失敗只記警告，絕不中斷主要流程。</summary>
public class ProcessMetricService
{
    private const int MaxActionLength = 64;

    private readonly IDbContextFactory<BackendDBContext> contextFactory;
    private readonly DemoControlService demoControlService;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<ProcessMetricService> logger;

    public ProcessMetricService(
        IDbContextFactory<BackendDBContext> contextFactory,
        DemoControlService demoControlService,
        TimeProvider timeProvider,
        ILogger<ProcessMetricService> logger)
    {
        this.contextFactory = contextFactory;
        this.demoControlService = demoControlService;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    /// <param name="action">值見 <c>ProcessMetricActions</c>。</param>
    public async Task RecordAsync(Guid caseKey, AfterSalesChannel channel, ProcessActor actor, string action, int? userId, int? requestId = null)
    {
        if (string.IsNullOrWhiteSpace(action) || action.Length > MaxActionLength)
        {
            logger.LogWarning("Process metric skipped because the action is empty or too long. ActionLength={ActionLength}", action?.Length ?? 0);
            return;
        }

        try
        {
            await using var context = await contextFactory.CreateDbContextAsync();
            context.ProcessMetric.Add(new ProcessMetric
            {
                CaseKey = caseKey,
                Channel = channel,
                Actor = actor,
                Action = action,
                UserId = userId,
                AfterSalesRequestId = requestId,
                OccurredAt = timeProvider.GetLocalNow().DateTime,
                // 示範模式且有目前的劇本執行（M6）時蓋上編號；DemoControlService 在示範模式關閉時回 null。
                DemoRunId = demoControlService.ActiveRunId,
            });
            await context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to record process metric. CaseKey={CaseKey}, ProcessAction={ProcessAction}", caseKey, action);
        }
    }

    /// <summary>
    /// Agent 確認卡確實建立申請後記一筆 <c>AgentRequestCreated</c>（M6，Actor＝顧客）：以申請編號在<b>這位顧客自己的</b>申請裡查出 Id 一併記下；
    /// 查不到時 Id 為 null，仍照記（申請筆數不因查詢失敗而少算）。
    /// </summary>
    public async Task RecordAgentRequestCreatedAsync(Guid caseKey, AfterSalesChannel channel, string requestNo, int? userId)
    {
        int? requestId = null;
        try
        {
            await using var context = await contextFactory.CreateDbContextAsync();
            requestId = await context.AfterSalesRequest.AsNoTracking()
                .Where(x => x.RequestNo == requestNo && x.CustomerUserId == userId)
                .Select(x => (int?)x.Id)
                .FirstOrDefaultAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to look up the created request for the process metric. CaseKey={CaseKey}", caseKey);
        }

        await RecordAsync(caseKey, channel, ProcessActor.Customer, ProcessMetricActions.AgentRequestCreated, userId, requestId);
    }
}

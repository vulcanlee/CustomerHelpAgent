using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Share.Shop;

namespace CustomerHelpAgent.Business.Services.DataAccess;

/// <summary>流程埋點：記錄傳統與 Agent 管道各自的操作步驟。埋點失敗只記警告，絕不中斷主要流程。</summary>
public class ProcessMetricService
{
    private const int MaxActionLength = 64;

    private readonly IDbContextFactory<BackendDBContext> contextFactory;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<ProcessMetricService> logger;

    public ProcessMetricService(
        IDbContextFactory<BackendDBContext> contextFactory,
        TimeProvider timeProvider,
        ILogger<ProcessMetricService> logger)
    {
        this.contextFactory = contextFactory;
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
            });
            await context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to record process metric. CaseKey={CaseKey}, ProcessAction={ProcessAction}", caseKey, action);
        }
    }
}

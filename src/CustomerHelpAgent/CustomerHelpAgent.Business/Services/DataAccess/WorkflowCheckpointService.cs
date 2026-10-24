using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Share.Agents;

namespace CustomerHelpAgent.Business.Services.DataAccess;

/// <summary>
/// MAF Workflow checkpoint 的資料存取（M4）。Business 不參考 MAF，所以這裡只處理字串；
/// 由 Web 的 <c>SqliteWorkflowCheckpointStore</c> 轉接成 <c>ICheckpointStore&lt;JsonElement&gt;</c>。
/// </summary>
public class WorkflowCheckpointService
{
    private readonly IDbContextFactory<BackendDBContext> contextFactory;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<WorkflowCheckpointService> logger;

    public WorkflowCheckpointService(
        IDbContextFactory<BackendDBContext> contextFactory,
        TimeProvider timeProvider,
        ILogger<WorkflowCheckpointService> logger)
    {
        this.contextFactory = contextFactory;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    public async Task AddAsync(string sessionId, string checkpointId, string? parentCheckpointId, string payloadJson)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        context.WorkflowCheckpoint.Add(new WorkflowCheckpoint
        {
            SessionId = sessionId,
            CheckpointId = checkpointId,
            ParentCheckpointId = parentCheckpointId,
            PayloadJson = payloadJson,
            CreatedAt = timeProvider.GetLocalNow().DateTime,
        });
        await context.SaveChangesAsync();
        logger.LogDebug("Workflow checkpoint saved. SessionId={SessionId}, CheckpointId={CheckpointId}", sessionId, checkpointId);
    }

    public async Task<string?> GetPayloadAsync(string sessionId, string checkpointId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        return await context.WorkflowCheckpoint.AsNoTracking()
            .Where(x => x.SessionId == sessionId && x.CheckpointId == checkpointId)
            .Select(x => x.PayloadJson)
            .FirstOrDefaultAsync();
    }

    /// <summary>某會話的 checkpoint Id，<b>由舊到新</b>（依 Id）；<paramref name="parentCheckpointId"/> 不為 null 時只取其子節點。</summary>
    public async Task<List<string>> ListCheckpointIdsAsync(string sessionId, string? parentCheckpointId = null)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var query = context.WorkflowCheckpoint.AsNoTracking().Where(x => x.SessionId == sessionId);
        if (parentCheckpointId is not null)
        {
            query = query.Where(x => x.ParentCheckpointId == parentCheckpointId);
        }

        return await query.OrderBy(x => x.Id).Select(x => x.CheckpointId).ToListAsync();
    }

    /// <summary>
    /// 刪掉這筆申請「已結束」（<see cref="PreReviewStatus.Completed"/>／<see cref="PreReviewStatus.Superseded"/>）各輪的 checkpoint，回傳刪除筆數：
    /// 結束的一輪不會再續跑，checkpoint 每個 superstep 一筆（含整張圖的定義），不清會一直累積。
    /// <see cref="PreReviewStatus.Failed"/> 的一輪保留（診斷用）；尚未結束的輪次當然保留（續跑要用）。
    /// ⚠️ 這是清理：失敗只記 Warning、不往外拋 —— 呼叫時決定通常已經生效，不可因為清不掉 checkpoint 而回報失敗。
    /// </summary>
    public async Task<int> DeleteForEndedRunsAsync(int requestId)
    {
        try
        {
            await using var context = await contextFactory.CreateDbContextAsync();
            var deleted = await context.WorkflowCheckpoint
                .Where(c => context.PreReviewRun.Any(r => r.AfterSalesRequestId == requestId
                    && r.WorkflowSessionId == c.SessionId
                    && (r.Status == PreReviewStatus.Completed || r.Status == PreReviewStatus.Superseded)))
                .ExecuteDeleteAsync();
            if (deleted > 0)
            {
                logger.LogInformation("Workflow checkpoints of ended pre-review runs deleted. RequestId={RequestId}, Count={Count}", requestId, deleted);
            }

            return deleted;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete workflow checkpoints of ended pre-review runs. RequestId={RequestId}", requestId);
            return 0;
        }
    }
}

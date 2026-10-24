using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Models.Agents;
using CustomerHelpAgent.Share.Agents;
using CustomerHelpAgent.Share.Shop;

namespace CustomerHelpAgent.Business.Services.DataAccess;

/// <summary>
/// AI 初審（M4）一輪執行的持久化：建立、節點進度、結論 JSON、狀態轉換。
/// ⚠️ 狀態轉換都以 <c>ExecuteUpdateAsync</c> 條件更新：兩位客服同時認領決策，只有一個成功。
/// </summary>
public class PreReviewRunService
{
    private readonly IDbContextFactory<BackendDBContext> contextFactory;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<PreReviewRunService> logger;

    public PreReviewRunService(
        IDbContextFactory<BackendDBContext> contextFactory,
        TimeProvider timeProvider,
        ILogger<PreReviewRunService> logger)
    {
        this.contextFactory = contextFactory;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    private DateTime Now => timeProvider.GetLocalNow().DateTime;

    /// <summary>Workflow 會話識別：<c>prereview-{requestId}-{attempt}</c>。</summary>
    public static string BuildSessionId(int requestId, int attempt) => $"prereview-{requestId}-{attempt}";

    private const int CreateMaxAttempts = 5;

    /// <summary>
    /// 為申請建立新的一輪（<c>Attempt</c> 為既有最大值加 1，狀態 <see cref="PreReviewStatus.Pending"/>）；申請不存在回 null。
    /// 兩個呼叫同時進來會算出同一個 Attempt，輸的一方撞唯一索引：換新的 context 重算 Attempt 再試（最多 <see cref="CreateMaxAttempts"/> 次）。
    /// </summary>
    public async Task<PreReviewRun?> CreateAsync(int requestId)
    {
        for (var tryNo = 1; ; tryNo++)
        {
            await using var context = await contextFactory.CreateDbContextAsync();
            var caseKey = await context.AfterSalesRequest.AsNoTracking()
                .Where(x => x.Id == requestId)
                .Select(x => (Guid?)x.CaseKey)
                .FirstOrDefaultAsync();
            if (caseKey is null)
            {
                return null;
            }

            var maxAttempt = await context.PreReviewRun
                .Where(x => x.AfterSalesRequestId == requestId)
                .MaxAsync(x => (int?)x.Attempt) ?? 0;
            var attempt = maxAttempt + 1;
            var now = Now;
            var run = new PreReviewRun
            {
                AfterSalesRequestId = requestId,
                Attempt = attempt,
                CaseKey = caseKey.Value,
                WorkflowSessionId = BuildSessionId(requestId, attempt),
                Status = PreReviewStatus.Pending,
                CreatedAt = now,
                UpdatedAt = now,
            };
            context.PreReviewRun.Add(run);
            try
            {
                await context.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (tryNo < CreateMaxAttempts && IsUniqueViolation(ex))
            {
                logger.LogDebug(ex, "Pre-review attempt number collided; retrying. RequestId={RequestId}, Attempt={Attempt}", requestId, attempt);
                continue;
            }

            logger.LogInformation("Pre-review run created. RunId={RunId}, RequestId={RequestId}, Attempt={Attempt}", run.Id, requestId, attempt);
            return run;
        }
    }

    /// <summary>唯一索引衝突（SQLITE_CONSTRAINT_UNIQUE，擴充碼 2067）；外鍵、鎖定逾時等其他失敗不重試。</summary>
    private static bool IsUniqueViolation(DbUpdateException ex)
        => ex.InnerException is SqliteException { SqliteErrorCode: 19, SqliteExtendedErrorCode: 2067 };

    public async Task<PreReviewRun?> GetAsync(int runId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        return await context.PreReviewRun.AsNoTracking().FirstOrDefaultAsync(x => x.Id == runId);
    }

    /// <summary>這筆申請最新一輪（含節點，依節點順序）；沒有回 null。</summary>
    public async Task<PreReviewRun?> GetLatestForRequestAsync(int requestId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var run = await context.PreReviewRun.AsNoTracking()
            .Where(x => x.AfterSalesRequestId == requestId)
            .OrderByDescending(x => x.Attempt).ThenByDescending(x => x.Id)
            .FirstOrDefaultAsync();
        if (run is not null)
        {
            run.Nodes = await context.PreReviewNodeState.AsNoTracking()
                .Where(x => x.PreReviewRunId == run.Id)
                .OrderBy(x => x.Node).ThenBy(x => x.Id)
                .ToListAsync();
        }

        return run;
    }

    /// <summary>
    /// 多筆申請各自的最新一輪（佇列頁用）：只投影狀態、建議與警示旗標，不載入節點結論 JSON。以申請 Id 為鍵，沒有初審的申請不在結果內。
    /// </summary>
    public async Task<Dictionary<int, PreReviewQueueStatus>> GetLatestByRequestIdsAsync(IReadOnlyCollection<int> requestIds)
    {
        if (requestIds.Count == 0)
        {
            return [];
        }

        await using var context = await contextFactory.CreateDbContextAsync();
        var runs = await context.PreReviewRun.AsNoTracking()
            .Where(x => requestIds.Contains(x.AfterSalesRequestId))
            .OrderBy(x => x.AfterSalesRequestId).ThenBy(x => x.Attempt).ThenBy(x => x.Id)
            .Select(x => new { x.AfterSalesRequestId, View = new PreReviewQueueStatus(x.Status, x.Recommendation, x.HasWarning) })
            .ToListAsync();
        // 依 Attempt 遞增排序，後者覆蓋前者即為最新一輪。
        var result = new Dictionary<int, PreReviewQueueStatus>();
        foreach (var run in runs)
        {
            result[run.AfterSalesRequestId] = run.View;
        }

        return result;
    }

    /// <summary>
    /// 設定節點狀態（不存在就建立）。進入 <see cref="NodeStatus.Running"/> 記開始時間；
    /// <see cref="NodeStatus.Done"/>／<see cref="NodeStatus.Failed"/>／<see cref="NodeStatus.Skipped"/> 記結束時間。
    /// </summary>
    public async Task SetNodeAsync(int runId, PreReviewNode node, NodeStatus status, string? note = null)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var now = Now;
        DateTime? startedAt = status == NodeStatus.Running ? now : null;
        DateTime? endedAt = status is NodeStatus.Done or NodeStatus.Failed or NodeStatus.Skipped ? now : null;
        var nodeValue = (int)node;
        var statusValue = (int)status;
        var running = (int)NodeStatus.Running;
        // 原子 upsert：同一 (RunId, Node) 同時寫入不會撞唯一索引。重新進入 Running 清掉結束時間；Waiting 不動時間。
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "PreReviewNodeState" ("PreReviewRunId", "Node", "Status", "StartedAt", "EndedAt", "Note")
            VALUES ({runId}, {nodeValue}, {statusValue}, {startedAt}, {endedAt}, {note})
            ON CONFLICT ("PreReviewRunId", "Node") DO UPDATE SET
                "Status" = excluded."Status",
                "Note" = excluded."Note",
                "StartedAt" = COALESCE(excluded."StartedAt", "StartedAt"),
                "EndedAt" = CASE WHEN excluded."Status" = {running} THEN NULL ELSE COALESCE(excluded."EndedAt", "EndedAt") END
            """);
        await context.PreReviewRun.Where(x => x.Id == runId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.UpdatedAt, now));
    }

    /// <summary>儲存節點的結論 JSON（只有 PhotoReview／RuleCheck／HistoryReview／Summary 有欄位；其他節點忽略並記警告）。</summary>
    public async Task SaveFindingAsync(int runId, PreReviewNode node, string? json)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var now = Now;
        var query = context.PreReviewRun.Where(x => x.Id == runId);
        switch (node)
        {
            case PreReviewNode.PhotoReview:
                await query.ExecuteUpdateAsync(s => s.SetProperty(x => x.PhotoJson, json).SetProperty(x => x.UpdatedAt, now));
                break;
            case PreReviewNode.RuleCheck:
                await query.ExecuteUpdateAsync(s => s.SetProperty(x => x.RuleJson, json).SetProperty(x => x.UpdatedAt, now));
                break;
            case PreReviewNode.HistoryReview:
                await query.ExecuteUpdateAsync(s => s.SetProperty(x => x.HistoryJson, json).SetProperty(x => x.UpdatedAt, now));
                break;
            case PreReviewNode.Summary:
                await query.ExecuteUpdateAsync(s => s.SetProperty(x => x.SummaryJson, json).SetProperty(x => x.UpdatedAt, now));
                break;
            default:
                logger.LogWarning("Pre-review node has no finding column. RunId={RunId}, Node={Node}", runId, node);
                break;
        }
    }

    /// <summary>設定摘要欄位（建議、警示旗標），供佇列頁顯示。</summary>
    public async Task SaveSummaryAsync(int runId, string? recommendation, bool hasWarning)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var now = Now;
        await context.PreReviewRun.Where(x => x.Id == runId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Recommendation, recommendation)
                .SetProperty(x => x.HasWarning, hasWarning)
                .SetProperty(x => x.UpdatedAt, now));
    }

    /// <summary>設定這一輪回放所用的錄製。</summary>
    public async Task SetReplayRecordingAsync(int runId, int? recordingId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var now = Now;
        await context.PreReviewRun.Where(x => x.Id == runId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ReplayRecordingId, recordingId).SetProperty(x => x.UpdatedAt, now));
    }

    /// <summary>
    /// 設定狀態；<see cref="PreReviewStatus.Completed"/>／<see cref="PreReviewStatus.Failed"/> 同時記完成時間。沒有套用時回 false。
    /// 狀態守門（條件更新，不怕併發）：
    /// <list type="bullet">
    /// <item>已是 <see cref="PreReviewStatus.Completed"/> 或 <see cref="PreReviewStatus.Superseded"/> 的一輪不再變動。</item>
    /// <item>要設為 <see cref="PreReviewStatus.Completed"/>／<see cref="PreReviewStatus.Superseded"/>，目前必須是 <see cref="PreReviewStatus.Executing"/>
    /// （只有正在執行決定的那一輪能收尾；人工直接決定另走 <see cref="SupersedeAsync"/>）。</item>
    /// </list>
    /// </summary>
    public async Task<bool> SetStatusAsync(int runId, PreReviewStatus status, string? errorMessage = null)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var now = Now;
        DateTime? completedAt = status is PreReviewStatus.Completed or PreReviewStatus.Failed ? now : null;
        var fromExecutingOnly = status is PreReviewStatus.Completed or PreReviewStatus.Superseded;
        var affected = await context.PreReviewRun
            .Where(x => x.Id == runId
                && x.Status != PreReviewStatus.Superseded
                && x.Status != PreReviewStatus.Completed
                && (!fromExecutingOnly || x.Status == PreReviewStatus.Executing))
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, status)
                .SetProperty(x => x.ErrorMessage, errorMessage)
                .SetProperty(x => x.CompletedAt, completedAt)
                .SetProperty(x => x.UpdatedAt, now));
        if (affected == 0)
        {
            logger.LogInformation("Pre-review status change skipped by the state guard. RunId={RunId}, TargetStatus={TargetStatus}", runId, status);
        }

        return affected > 0;
    }

    /// <summary>認領決策：<see cref="PreReviewStatus.AwaitingDecision"/> → <see cref="PreReviewStatus.Executing"/> 條件更新，只有一個呼叫者回 true。</summary>
    public async Task<bool> TryClaimDecisionAsync(int runId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var now = Now;
        var affected = await context.PreReviewRun
            .Where(x => x.Id == runId && x.Status == PreReviewStatus.AwaitingDecision)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, PreReviewStatus.Executing)
                .SetProperty(x => x.UpdatedAt, now));
        return affected == 1;
    }

    /// <summary>
    /// 這筆申請所有尚未結束的初審（Pending／Running／AwaitingDecision／Failed）標為已由人工處理（<see cref="PreReviewStatus.Superseded"/>），
    /// 回傳受影響的輪數。已完成的不動；<see cref="PreReviewStatus.Executing"/>（決策已被認領、正由 Workflow 執行）也不動，執行的狀態守門已保護申請。
    /// </summary>
    public async Task<int> SupersedeAsync(int requestId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var now = Now;
        var affected = await context.PreReviewRun
            .Where(x => x.AfterSalesRequestId == requestId
                && (x.Status == PreReviewStatus.Pending
                    || x.Status == PreReviewStatus.Running
                    || x.Status == PreReviewStatus.AwaitingDecision
                    || x.Status == PreReviewStatus.Failed))
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, PreReviewStatus.Superseded)
                .SetProperty(x => x.CompletedAt, now)
                .SetProperty(x => x.UpdatedAt, now));
        if (affected > 0)
        {
            logger.LogInformation("Pre-review runs superseded by staff decision. RequestId={RequestId}, Count={Count}", requestId, affected);
        }

        return affected;
    }

    /// <summary>
    /// 決策已被認領、正在執行（<see cref="PreReviewStatus.Executing"/>）的初審；依 Id 由舊到新。
    /// ⚠️ 只在應用程式啟動時用來找「執行到一半當機」的一輪：執行中途的狀態，平常一定有人正在處理。
    /// </summary>
    public async Task<List<PreReviewRun>> GetExecutingAsync()
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        return await context.PreReviewRun.AsNoTracking()
            .Where(x => x.Status == PreReviewStatus.Executing)
            .OrderBy(x => x.Id)
            .ToListAsync();
    }

    /// <summary>
    /// Agent 管道、已提交、卻沒有任何一輪初審的申請 Id（送件後、建立初審前當機，或掛勾失敗）；依 Id 由舊到新。啟動時補建用。
    /// </summary>
    public async Task<List<int>> GetAgentRequestIdsWithoutRunAsync()
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        return await context.AfterSalesRequest.AsNoTracking()
            .Where(x => x.Channel == AfterSalesChannel.Agent
                && x.Status == AfterSalesStatus.Submitted
                && !context.PreReviewRun.Any(r => r.AfterSalesRequestId == x.Id))
            .OrderBy(x => x.Id)
            .Select(x => x.Id)
            .ToListAsync();
    }

    /// <summary>尚未跑完的初審（<see cref="PreReviewStatus.Pending"/>／<see cref="PreReviewStatus.Running"/>），啟動時續跑用；依 Id 由舊到新。</summary>
    public async Task<List<PreReviewRun>> GetUnfinishedAsync()
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        return await context.PreReviewRun.AsNoTracking()
            .Where(x => x.Status == PreReviewStatus.Pending || x.Status == PreReviewStatus.Running)
            .OrderBy(x => x.Id)
            .ToListAsync();
    }
}

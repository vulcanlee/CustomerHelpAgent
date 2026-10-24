using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Models.Agents;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Agents;

namespace CustomerHelpAgent.Business.Services.DataAccess;

/// <summary>
/// 售後 Agent 執行軌跡的持久化與查詢（幕後面板與管理端軌跡頁共用）。
/// 併發假設：同一個 run 只有單一寫入者（Runner 依序呼叫），因此 Sequence 以「同一個 context 內取 max + 1」計算，
/// 不加鎖也不靠唯一索引重試。
/// </summary>
public class AgentTraceService
{
    private readonly IDbContextFactory<BackendDBContext> contextFactory;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<AgentTraceService> logger;

    public AgentTraceService(
        IDbContextFactory<BackendDBContext> contextFactory,
        TimeProvider timeProvider,
        ILogger<AgentTraceService> logger)
    {
        this.contextFactory = contextFactory;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    private DateTime Now => timeProvider.GetLocalNow().DateTime;

    /// <summary>
    /// 開始一輪執行，回傳 RunId。<paramref name="replayRecordingId"/>：回放中這一輪用的錄製（即時執行不傳）。
    /// 資料庫失敗時照常拋出（沒有 run 就無從記錄）。
    /// </summary>
    public async Task<int> StartRunAsync(Guid sessionKey, int customerUserId, int? replayRecordingId = null)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var run = new AgentRunTrace
        {
            SessionKey = sessionKey,
            CustomerUserId = customerUserId,
            StartedAt = Now,
            ReplayRecordingId = replayRecordingId,
        };
        context.AgentRunTrace.Add(run);
        await context.SaveChangesAsync();
        logger.LogDebug("Agent run started. RunId={RunId}", run.Id);
        return run.Id;
    }

    /// <summary>
    /// 追加一個步驟。軌跡是旁路資訊，任何失敗都只記 Warning（僅 RunId、Kind，不含內容）而不拋出，
    /// 以免中斷顧客的對話流程。
    /// </summary>
    public async Task AppendStepAsync(int runId, AgentStepKind kind, AgentDataSource source, string plainText, string? rawJson)
    {
        try
        {
            await using var context = await contextFactory.CreateDbContextAsync();
            var last = await context.AgentRunStep
                .Where(x => x.AgentRunTraceId == runId)
                .MaxAsync(x => (int?)x.Sequence) ?? 0;
            context.AgentRunStep.Add(new AgentRunStep
            {
                AgentRunTraceId = runId,
                Sequence = last + 1,
                Kind = kind,
                Source = source,
                PlainText = plainText,
                RawJson = rawJson,
                OccurredAt = Now,
            });
            await context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to append agent step. RunId={RunId}, Kind={Kind}", runId, kind);
        }
    }

    /// <summary>結束一輪執行並寫入停止原因與呼叫次數。資料庫失敗時照常拋出。</summary>
    public async Task EndRunAsync(int runId, AgentStopReason reason, int modelCalls, int toolCalls)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var run = await context.AgentRunTrace.SingleAsync(x => x.Id == runId);
        run.EndedAt = Now;
        run.StopReason = reason;
        run.ModelCallCount = modelCalls;
        run.ToolCallCount = toolCalls;
        await context.SaveChangesAsync();
        logger.LogDebug("Agent run ended. RunId={RunId}, StopReason={StopReason}", runId, reason);
    }

    public async Task<DataRequestResult<AgentRunSummary>> GetRunsAsync(DataRequest dataRequest)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        // 顧客姓名不經導覽屬性：MyUser 有軟刪除過濾器，導覽會讓已刪除顧客的紀錄消失（速查表 §7）。
        var users = context.MyUser.IgnoreQueryFilters([ISoftDeletable.FilterName]);

        // Skip/Take 一定要搭配 OrderBy。
        var ordered = context.AgentRunTrace.AsNoTracking()
            .OrderByDescending(x => x.StartedAt).ThenByDescending(x => x.Id);

        DataRequestResult<AgentRunSummary> result = new() { Count = await ordered.CountAsync() };

        var paged = ordered.Skip((dataRequest.CurrentPage - 1) * dataRequest.PageSize);
        if (dataRequest.Take != 0)
        {
            paged = paged.Take(dataRequest.PageSize);
        }

        result.Result = await paged
            .Select(x => new AgentRunSummary(
                x.Id,
                x.SessionKey,
                users.Where(u => u.Id == x.CustomerUserId).Select(u => u.Name).FirstOrDefault() ?? string.Empty,
                x.StartedAt,
                x.EndedAt,
                x.StopReason,
                x.ModelCallCount,
                x.ToolCallCount,
                x.ReplayRecordingId))
            .ToListAsync();
        return result;
    }

    public async Task<IReadOnlyList<AgentRunStepView>> GetStepsAsync(int runId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        return await context.AgentRunStep.AsNoTracking()
            .Where(x => x.AgentRunTraceId == runId)
            .OrderBy(x => x.Sequence).ThenBy(x => x.Id)
            .Select(x => new AgentRunStepView(x.Sequence, x.Kind, x.Source, x.PlainText, x.RawJson, x.OccurredAt))
            .ToListAsync();
    }

    /// <summary>
    /// 這個會話（＝售後申請的 CaseKey）最近一次回放所用的錄製 Id；會話沒有回放過（即時執行）回 null。
    /// AI 初審（M4）據此判斷要不要以同一份錄製重播模型節點，而不呼叫真實模型。
    /// </summary>
    public async Task<int?> GetReplayRecordingIdAsync(Guid sessionKey)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        return await context.AgentRunTrace.AsNoTracking()
            .Where(x => x.SessionKey == sessionKey && x.ReplayRecordingId != null)
            .OrderByDescending(x => x.StartedAt).ThenByDescending(x => x.Id)
            .Select(x => x.ReplayRecordingId)
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// 一個會話所有執行的步驟（依 run 的 <c>StartedAt</c>、Id，再依步驟 <c>Sequence</c>、Id），供重新整理頁面時還原對話與幕後面板。
    /// 只取這位顧客的執行：別人的會話視同沒有紀錄。
    /// </summary>
    public async Task<IReadOnlyList<AgentRunStepView>> GetSessionStepsAsync(Guid sessionKey, int customerUserId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        return await context.AgentRunStep.AsNoTracking()
            .Where(x => x.AgentRunTrace!.SessionKey == sessionKey && x.AgentRunTrace.CustomerUserId == customerUserId)
            .OrderBy(x => x.AgentRunTrace!.StartedAt).ThenBy(x => x.AgentRunTraceId)
            .ThenBy(x => x.Sequence).ThenBy(x => x.Id)
            .Select(x => new AgentRunStepView(x.Sequence, x.Kind, x.Source, x.PlainText, x.RawJson, x.OccurredAt))
            .ToListAsync();
    }
}

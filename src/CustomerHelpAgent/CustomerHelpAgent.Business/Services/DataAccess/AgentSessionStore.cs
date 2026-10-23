using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Share.Agents;

namespace CustomerHelpAgent.Business.Services.DataAccess;

/// <summary>
/// 售後 Agent 的會話（MAF <c>AgentSession</c> 序列化結果）與確認關卡紀錄（1.0.4 起）。
/// 查詢一律帶 <c>customerUserId</c>：別人的會話視同不存在。
/// </summary>
public class AgentSessionStore
{
    private readonly IDbContextFactory<BackendDBContext> contextFactory;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<AgentSessionStore> logger;

    public AgentSessionStore(
        IDbContextFactory<BackendDBContext> contextFactory,
        TimeProvider timeProvider,
        ILogger<AgentSessionStore> logger)
    {
        this.contextFactory = contextFactory;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    private DateTime Now => timeProvider.GetLocalNow().DateTime;

    /// <summary>這位顧客最近更新、尚未關閉的會話。</summary>
    public async Task<AgentSessionRecord?> GetLatestOpenAsync(int customerUserId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        return await context.AgentSessionRecord.AsNoTracking()
            .Where(x => x.CustomerUserId == customerUserId && !x.IsClosed)
            .OrderByDescending(x => x.UpdatedAt).ThenByDescending(x => x.Id)
            .FirstOrDefaultAsync();
    }

    public async Task<AgentSessionRecord?> GetAsync(Guid sessionKey, int customerUserId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        return await context.AgentSessionRecord.AsNoTracking()
            .FirstOrDefaultAsync(x => x.SessionKey == sessionKey && x.CustomerUserId == customerUserId);
    }

    /// <summary>建立一個空白（尚無對話）的新會話，回傳 SessionKey。</summary>
    public async Task<Guid> CreateAsync(int customerUserId)
    {
        var sessionKey = Guid.NewGuid();
        await SaveStateAsync(sessionKey, customerUserId, string.Empty, null);
        logger.LogInformation("Agent session created. SessionKey={SessionKey}", sessionKey);
        return sessionKey;
    }

    /// <summary>
    /// 存入序列化後的會話與本輪停止原因；不存在則建立。
    /// SessionKey 已屬於別的顧客時拋 <see cref="InvalidOperationException"/>（不覆寫別人的會話）。
    /// </summary>
    public Task SaveStateAsync(Guid sessionKey, int customerUserId, string stateJson, AgentStopReason stopReason)
        => SaveStateAsync(sessionKey, customerUserId, stateJson, (AgentStopReason?)stopReason);

    public async Task CloseAsync(Guid sessionKey, int customerUserId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var record = await context.AgentSessionRecord
            .FirstOrDefaultAsync(x => x.SessionKey == sessionKey && x.CustomerUserId == customerUserId);
        if (record is null || record.IsClosed)
        {
            return;
        }

        record.IsClosed = true;
        record.UpdatedAt = Now;
        await context.SaveChangesAsync();
        logger.LogInformation("Agent session closed. SessionKey={SessionKey}", sessionKey);
    }

    /// <summary>記錄一張顯示給顧客的確認卡（尚未決定）。</summary>
    public async Task<AgentConfirmation> AddConfirmationAsync(
        Guid sessionKey, int customerUserId, string approvalRequestId, string toolName, string argumentsJson, string cardJson)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var confirmation = new AgentConfirmation
        {
            SessionKey = sessionKey,
            CustomerUserId = customerUserId,
            ApprovalRequestId = approvalRequestId,
            ToolName = toolName,
            ArgumentsJson = argumentsJson,
            CardJson = cardJson,
            CreatedAt = Now,
        };
        context.AgentConfirmation.Add(confirmation);
        await context.SaveChangesAsync();
        logger.LogInformation(
            "Agent confirmation shown. ConfirmationId={ConfirmationId}, SessionKey={SessionKey}, ToolName={ToolName}",
            confirmation.Id, sessionKey, toolName);
        return confirmation;
    }

    /// <summary>這個會話最後一張尚未決定的確認卡。</summary>
    public async Task<AgentConfirmation?> GetPendingConfirmationAsync(Guid sessionKey, int customerUserId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        return await context.AgentConfirmation.AsNoTracking()
            .Where(x => x.SessionKey == sessionKey && x.CustomerUserId == customerUserId && x.Approved == null)
            .OrderByDescending(x => x.Id)
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// 以單一條件式更新（<c>Approved == null</c>）寫入顧客的決定：只有第一個呼叫者會成功，回傳 true。
    /// 兩個分頁同時按下同一張確認卡時，另一個拿到 false，不可再把核准送給模型。
    /// </summary>
    public async Task<bool> TryClaimConfirmationAsync(int id, int customerUserId, bool approved)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var now = Now;
        var claimed = await context.AgentConfirmation
            .Where(x => x.Id == id && x.CustomerUserId == customerUserId && x.Approved == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Approved, approved)
                .SetProperty(x => x.DecidedAt, now)) == 1;
        logger.LogInformation(
            "Agent confirmation claim attempted. ConfirmationId={ConfirmationId}, Approved={Approved}, Claimed={Claimed}",
            id, approved, claimed);
        return claimed;
    }

    /// <summary>
    /// 記錄顧客的決定。決定只寫第一次（之後再呼叫不會改變同意／拒絕與決定時間）；
    /// <paramref name="requestId"/> 有值時回填建立的售後申請 Id（同意後申請建立成功才會有）。
    /// </summary>
    public async Task MarkConfirmationAsync(int id, bool approved, int? requestId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var confirmation = await context.AgentConfirmation.FirstOrDefaultAsync(x => x.Id == id);
        if (confirmation is null)
        {
            logger.LogWarning("Agent confirmation not found when recording the decision. ConfirmationId={ConfirmationId}", id);
            return;
        }

        if (confirmation.Approved is null)
        {
            confirmation.Approved = approved;
            confirmation.DecidedAt = Now;
        }

        if (requestId is not null)
        {
            confirmation.AfterSalesRequestId = requestId;
        }

        await context.SaveChangesAsync();
        logger.LogInformation(
            "Agent confirmation decided. ConfirmationId={ConfirmationId}, Approved={Approved}, RequestId={RequestId}",
            id, confirmation.Approved, confirmation.AfterSalesRequestId);
    }

    private async Task SaveStateAsync(Guid sessionKey, int customerUserId, string stateJson, AgentStopReason? stopReason)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var record = await context.AgentSessionRecord.FirstOrDefaultAsync(x => x.SessionKey == sessionKey);
        if (record is null)
        {
            record = new AgentSessionRecord
            {
                SessionKey = sessionKey,
                CustomerUserId = customerUserId,
                CreatedAt = Now,
            };
            context.AgentSessionRecord.Add(record);
        }
        else if (record.CustomerUserId != customerUserId)
        {
            logger.LogWarning("Agent session state rejected because the session belongs to another customer. SessionKey={SessionKey}", sessionKey);
            throw new InvalidOperationException("這個對話不屬於目前登入的顧客。");
        }

        record.StateJson = stateJson;
        record.LastStopReason = stopReason;
        record.UpdatedAt = Now;
        await context.SaveChangesAsync();
    }
}

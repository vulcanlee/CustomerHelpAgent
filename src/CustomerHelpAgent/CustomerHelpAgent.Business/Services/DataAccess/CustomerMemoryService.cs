using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Agents;
using CustomerHelpAgent.Share.Agents;

namespace CustomerHelpAgent.Business.Services.DataAccess;

/// <summary>
/// 顧客長期記憶（M5）的唯一讀寫入口：每位顧客每種偏好只存一筆（新值覆蓋舊值），內容一律先過 <see cref="MemoryContentPolicy"/>。
/// 查詢與刪除都帶 <c>customerUserId</c>：別人的記憶視同不存在。每次都直接查資料庫、不做快取，
/// 顧客刪除後下一輪對話就不會再被注入。稽核（<see cref="AuditActions.Memory"/>）只記偏好種類與筆數，不記記憶內容；
/// 稽核寫入失敗不影響已生效的資料變更。
/// </summary>
public class CustomerMemoryService
{
    public const string NotFoundMessage = "找不到這筆記憶。";
    public const string FailedMessage = "儲存記憶失敗，請稍後再試。";

    private readonly IDbContextFactory<BackendDBContext> contextFactory;
    private readonly IAuditLogService auditLogService;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<CustomerMemoryService> logger;

    public CustomerMemoryService(
        IDbContextFactory<BackendDBContext> contextFactory,
        IAuditLogService auditLogService,
        TimeProvider timeProvider,
        ILogger<CustomerMemoryService> logger)
    {
        this.contextFactory = contextFactory;
        this.auditLogService = auditLogService;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    /// <summary>該顧客的全部記憶（依種類排序）。</summary>
    public async Task<IReadOnlyList<CustomerMemoryView>> GetAsync(int customerUserId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        return await context.CustomerMemory
            .AsNoTracking()
            .Where(x => x.CustomerUserId == customerUserId)
            .OrderBy(x => x.Kind)
            .Select(x => new CustomerMemoryView(x.Id, x.Kind, x.Value, x.UpdatedAt))
            .ToListAsync();
    }

    /// <summary>
    /// 只重核、不寫入（M5 記憶確認卡出卡前用）：與 <see cref="UpsertAsync"/> 同一套規則（含該顧客的姓名、帳號）。
    /// 找不到顧客時回拒絕（<see cref="FailedMessage"/>）。
    /// </summary>
    public async Task<(bool Ok, string? Normalized, string? Reason)> ValidateAsync(int customerUserId, MemoryKind kind, string? value)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var customer = await context.MyUser.AsNoTracking()
            .Where(x => x.Id == customerUserId)
            .Select(x => new { x.Name, x.Account })
            .FirstOrDefaultAsync();
        return customer is null
            ? (false, null, FailedMessage)
            : MemoryContentPolicy.Validate(kind, value, [customer.Name, customer.Account]);
    }

    /// <summary>寫入（或覆蓋）一種偏好。內容由程式重核：不合格式、含個資、姓名或帳號都拒絕且不寫入。</summary>
    public async Task<MemoryResult> UpsertAsync(int customerUserId, MemoryKind kind, string value, Guid? sessionKey)
    {
        var now = timeProvider.GetLocalNow().DateTime;
        string account;
        CustomerMemoryView view;
        try
        {
            await using var context = await contextFactory.CreateDbContextAsync();
            var customer = await context.MyUser.AsNoTracking()
                .Where(x => x.Id == customerUserId)
                .Select(x => new { x.Name, x.Account })
                .FirstOrDefaultAsync();
            if (customer is null)
            {
                logger.LogWarning("Memory save rejected because the customer was not found.");
                return Fail(FailedMessage);
            }

            var (ok, normalized, reason) = MemoryContentPolicy.Validate(kind, value, [customer.Name, customer.Account]);
            if (!ok)
            {
                logger.LogInformation("Memory save rejected by content policy. Kind={Kind}", kind);
                return Fail(reason!);
            }

            account = customer.Account;
            view = await SaveAsync(context, customerUserId, kind, normalized!, sessionKey, now);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save customer memory. Kind={Kind}", kind);
            return Fail(FailedMessage);
        }

        logger.LogInformation("Customer memory saved. MemoryId={MemoryId}, Kind={Kind}", view.Id, kind);
        await WriteAuditAsync(AuditActions.Memory.Saved, customerUserId, account, view.Id, $"kind={kind}");
        return new MemoryResult(true, string.Empty, view);
    }

    /// <summary>刪除自己的一筆記憶；別人的或不存在的一律回「找不到」。</summary>
    public async Task<MemoryResult> DeleteAsync(int customerUserId, int memoryId)
    {
        MemoryKind kind;
        try
        {
            await using var context = await contextFactory.CreateDbContextAsync();
            var row = await context.CustomerMemory.AsNoTracking()
                .Where(x => x.Id == memoryId && x.CustomerUserId == customerUserId)
                .Select(x => new { x.Kind })
                .FirstOrDefaultAsync();
            if (row is null)
            {
                logger.LogInformation("Memory delete denied because the row was not found for the customer. MemoryId={MemoryId}", memoryId);
                return Fail(NotFoundMessage);
            }

            kind = row.Kind;
            var affected = await context.CustomerMemory
                .Where(x => x.Id == memoryId && x.CustomerUserId == customerUserId)
                .ExecuteDeleteAsync();
            if (affected == 0)
            {
                return Fail(NotFoundMessage);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete customer memory. MemoryId={MemoryId}", memoryId);
            return Fail(FailedMessage);
        }

        logger.LogInformation("Customer memory deleted. MemoryId={MemoryId}, Kind={Kind}", memoryId, kind);
        await WriteAuditAsync(AuditActions.Memory.Deleted, customerUserId, null, memoryId, $"kind={kind}");
        return new MemoryResult(true, string.Empty, null);
    }

    /// <summary>刪除該顧客的全部記憶，回傳刪除筆數。</summary>
    public async Task<int> DeleteAllAsync(int customerUserId)
    {
        int count;
        await using (var context = await contextFactory.CreateDbContextAsync())
        {
            count = await context.CustomerMemory.Where(x => x.CustomerUserId == customerUserId).ExecuteDeleteAsync();
        }

        logger.LogInformation("Customer memories deleted. Rows={Rows}", count);
        if (count > 0)
        {
            await WriteAuditAsync(AuditActions.Memory.Deleted, customerUserId, null, null, $"all; rows={count}");
        }

        return count;
    }

    private static async Task<CustomerMemoryView> SaveAsync(
        BackendDBContext context, int customerUserId, MemoryKind kind, string value, Guid? sessionKey, DateTime now)
    {
        // 先條件更新、沒有才新增；兩個分頁同時新增同一種偏好時，輸的一方撞到唯一索引，改走更新。
        if (await UpdateAsync(context, customerUserId, kind, value, sessionKey, now) == 0)
        {
            context.CustomerMemory.Add(new CustomerMemory
            {
                CustomerUserId = customerUserId,
                Kind = kind,
                Value = value,
                SourceSessionKey = sessionKey,
                CreatedAt = now,
                UpdatedAt = now,
            });
            try
            {
                await context.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                context.ChangeTracker.Clear();
                if (await UpdateAsync(context, customerUserId, kind, value, sessionKey, now) == 0)
                {
                    throw;
                }
            }
        }

        return await context.CustomerMemory.AsNoTracking()
            .Where(x => x.CustomerUserId == customerUserId && x.Kind == kind)
            .Select(x => new CustomerMemoryView(x.Id, x.Kind, x.Value, x.UpdatedAt))
            .SingleAsync();
    }

    private static Task<int> UpdateAsync(BackendDBContext context, int customerUserId, MemoryKind kind, string value, Guid? sessionKey, DateTime now)
        => context.CustomerMemory
            .Where(x => x.CustomerUserId == customerUserId && x.Kind == kind)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Value, value)
                .SetProperty(x => x.SourceSessionKey, sessionKey)
                .SetProperty(x => x.UpdatedAt, now));

    private async Task WriteAuditAsync(string action, int customerUserId, string? account, int? memoryId, string detail)
    {
        try
        {
            await auditLogService.WriteAsync(
                action,
                success: true,
                actorUserId: customerUserId,
                actorAccount: account,
                targetType: nameof(CustomerMemory),
                targetId: memoryId?.ToString(CultureInfo.InvariantCulture),
                detail: detail);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to write audit log for customer memory. Action={Action}", action);
        }
    }

    private static MemoryResult Fail(string message) => new(false, message, null);
}

using System.Linq.Expressions;
using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Factories;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.AdapterModel;
using CustomerHelpAgent.Models.Systems;

namespace CustomerHelpAgent.Business.Services.DataAccess;

/// <summary>
/// 稽核紀錄的讀取端。
///
/// 寫入端是另一支 <c>AuditLogService</c>（<c>Services/Other</c>）。兩者都注入 <see cref="IDbContextFactory{TContext}"/>、
/// 每個方法用完即棄 —— Blazor 的 DI scope 等同整條 SignalR circuit，共用 scoped DbContext 會累積追蹤實體。
///
/// 詳細內容檔（0.9.131 起）的刪除也在這裡：清除與清空都要連同 <see cref="AuditContentFileStore"/> 的檔案一起處理。
///
/// ⚠️ <b>時區是本服務唯一的職責重點。</b>資料表的 <c>OccurredAt</c> 存的是 UTC
/// （與帳號鎖定的 <c>LockoutEndUtc</c> 一致），而畫面的 DatePicker 與顯示格式都是本地時間。
/// 換算<b>全部在這裡做完</b>：查詢條件本地→UTC、回傳結果 UTC→本地。呼叫端不要再轉第二次。
/// 這與「系統例外紀錄」不同 —— 那張表存的是 <c>DateTime.Now</c>（本地），所以它整條路徑都不換算。
/// </summary>
public class AuditLogQueryService
{
    /// <summary>
    /// 匯出時的取用上限。匯出的是「目前查詢條件下的全部資料」而非當頁，
    /// 但仍需要一個上限，否則稽核表長到數十萬列時會把整個 circuit 拖垮。
    /// </summary>
    public const int MaxExportRows = 10000;

    private readonly IDbContextFactory<BackendDBContext> contextFactory;
    private readonly AuditContentFileStore contentFileStore;

    public IMapper Mapper { get; }
    public ILogger<AuditLogQueryService> Logger { get; }

    public AuditLogQueryService(
        IDbContextFactory<BackendDBContext> contextFactory,
        IMapper mapper,
        ILogger<AuditLogQueryService> logger,
        AuditContentFileStore contentFileStore)
    {
        this.contextFactory = contextFactory;
        Mapper = mapper;
        Logger = logger;
        this.contentFileStore = contentFileStore;
    }

    /// <summary>
    /// 讀取一筆稽核的詳細內容（修改差異、查詢條件）。沒有內容檔、或檔案已不存在時回 null。
    /// </summary>
    public async Task<string?> GetContentAsync(int id)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var relativePath = await context.AuditLog
            .Where(x => x.Id == id)
            .Select(x => x.ContentFile)
            .FirstOrDefaultAsync();

        return await contentFileStore.ReadAsync(relativePath);
    }

    /// <summary>本地時間 → UTC。DatePicker 給的 Kind 多半是 Unspecified，先釘成 Local 再換算。</summary>
    public static DateTime ToUtc(DateTime local)
        => DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime();

    /// <summary>UTC → 本地時間。SQLite 讀回來的 Kind 是 Unspecified，先釘成 Utc 再換算。</summary>
    public static DateTime ToLocal(DateTime utc)
        => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();

    public async Task<DataRequestResult<AuditLogAdapterModel>> GetAsync(AuditLogQuery query)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogDebug(
            "Loading audit logs. Action={Action}, CurrentPage={CurrentPage}, PageSize={PageSize}",
            query.Action,
            query.CurrentPage,
            query.PageSize);

        DataRequestResult<AuditLogAdapterModel> result = new();
        IQueryable<AuditLog> dataSource = context.AuditLog.AsNoTracking();

        // 條件端的時間是本地時間，資料表存的是 UTC —— 比對前一定要先換算，
        // 否則查詢區間會整整偏掉一個時區（台灣是 8 小時）。
        if (query.StartTime.HasValue)
        {
            var start = ToUtc(query.StartTime.Value);
            dataSource = dataSource.Where(x => x.OccurredAt >= start);
        }

        if (query.EndTime.HasValue)
        {
            var end = ToUtc(query.EndTime.Value);
            dataSource = dataSource.Where(x => x.OccurredAt <= end);
        }

        if (string.IsNullOrWhiteSpace(query.Account) == false)
        {
            dataSource = dataSource.Where(x => x.ActorAccount != null && x.ActorAccount.Contains(query.Account));
        }

        if (string.IsNullOrWhiteSpace(query.Action) == false)
        {
            dataSource = dataSource.Where(x => x.Action == query.Action);
        }

        if (string.IsNullOrWhiteSpace(query.ClientIp) == false)
        {
            var ip = query.ClientIp.Trim();
            dataSource = dataSource.Where(x => x.ClientIp != null && x.ClientIp.StartsWith(ip));
        }

        if (query.Success.HasValue)
        {
            var success = query.Success.Value;
            dataSource = dataSource.Where(x => x.Success == success);
        }

        if (string.IsNullOrWhiteSpace(query.Keyword) == false)
        {
            dataSource = dataSource.Where(x =>
                x.Action.Contains(query.Keyword) ||
                (x.ActorAccount != null && x.ActorAccount.Contains(query.Keyword)) ||
                (x.TargetType != null && x.TargetType.Contains(query.Keyword)) ||
                (x.TargetId != null && x.TargetId.Contains(query.Keyword)) ||
                (x.Detail != null && x.Detail.Contains(query.Keyword)) ||
                (x.ClientIp != null && x.ClientIp.Contains(query.Keyword)));
        }

        IOrderedQueryable<AuditLog>? sorted = null;

        if (query.SortField == nameof(AuditLogAdapterModel.OccurredAt))
        {
            sorted = query.SortDescending == true
                ? dataSource.OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.Id)
                : query.SortDescending == false
                    ? dataSource.OrderBy(x => x.OccurredAt).ThenBy(x => x.Id)
                    : null;
        }

        // Skip/Take 之前一定要有 OrderBy，否則 SQLite 不保證回傳順序，分頁會重複或漏資料。
        // 未指定欄位、欄位不認得、方向為 null —— 三種情況一律退回預設排序。
        dataSource = sorted ?? dataSource.OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.Id);

        result.Count = await dataSource.CountAsync();
        dataSource = dataSource
            .Skip((query.CurrentPage - 1) * query.PageSize)
            .Take(query.PageSize);

        List<AuditLog> records = await dataSource.ToListAsync();
        result.Result = Mapper.Map<List<AuditLogAdapterModel>>(records);

        // UTC → 本地，統一在這裡做完。AutoMapper 只會原封不動搬過去，不會幫忙換算。
        foreach (var item in result.Result)
        {
            item.OccurredAt = ToLocal(item.OccurredAt);
        }

        return result;
    }

    /// <summary>
    /// 篩選下拉用的動作代碼清單，直接取自資料。
    ///
    /// 刻意不寫死清單：動作代碼目前散落在 15 個呼叫點的字串字面值裡，沒有集中的常數來源，
    /// 任何手寫清單都會在下一次有人新增稽核事件時默默過期。
    /// </summary>
    public async Task<List<string>> GetDistinctActionsAsync()
    {
        await using var context = await contextFactory.CreateDbContextAsync();

        return await context.AuditLog
            .AsNoTracking()
            .Select(x => x.Action)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync();
    }

    /// <summary>
    /// 刪除早於 N 天前的紀錄。
    ///
    /// ⚠️ 門檻必須用 <see cref="DateTime.UtcNow"/> 算 —— 資料表存的是 UTC。
    /// 「系統例外紀錄」的同名方法用的是 <c>DateTime.Now</c>，因為那張表存本地時間；
    /// 照抄過來會讓清除範圍整整偏掉一個時區。
    /// </summary>
    public async Task<VerifyRecordResult> PurgeAsync(int days)
    {
        Logger.LogInformation("Purging audit logs older than {Days} days.", days);

        try
        {
            var removed = await PurgeBeforeAsync(DateTime.UtcNow.AddDays(-days));

            if (removed == 0)
            {
                return VerifyRecordResultFactory.Build(true, "沒有符合條件的紀錄。");
            }

            Logger.LogInformation("Purged audit logs. Rows={Rows}", removed);
            return VerifyRecordResultFactory.Build(true, $"已清除 {removed} 筆紀錄。");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to purge audit logs.");
            return VerifyRecordResultFactory.Build(false, "清除稽核紀錄失敗。", ex);
        }
    }

    /// <summary>
    /// 刪除早於 <paramref name="thresholdUtc"/> 的稽核紀錄，回傳刪除筆數。
    /// ⚠️ 門檻必須是 UTC（資料表存 UTC）。供頁面手動清除與自動保存期限（LOG-13）共用；失敗時拋出。
    /// </summary>
    /// <summary>
    /// 清理條件：<see cref="PurgeBeforeAsync"/> 與 <see cref="GetRetentionStatsAsync"/> 共用，
    /// 「資料保留與清理」頁的「下次會清掉幾筆」才會等於實際刪掉的筆數。
    /// </summary>
    private static Expression<Func<AuditLog, bool>> OlderThan(DateTime thresholdUtc)
        => x => x.OccurredAt < thresholdUtc;

    /// <summary>
    /// 筆數、最舊一筆與超過 <paramref name="thresholdUtc"/>（UTC；null＝不自動清理）的筆數。
    /// ⚠️ <see cref="RetentionStats.Oldest"/> 是 UTC，顯示前由呼叫端轉本地時間。
    /// </summary>
    public async Task<RetentionStats> GetRetentionStatsAsync(DateTime? thresholdUtc)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var total = await context.AuditLog.CountAsync();
        var oldest = await context.AuditLog.MinAsync(x => (DateTime?)x.OccurredAt);
        int? expired = thresholdUtc is { } t ? await context.AuditLog.CountAsync(OlderThan(t)) : null;
        return new RetentionStats(total, oldest, expired);
    }

    public async Task<int> PurgeBeforeAsync(DateTime thresholdUtc)
    {
        await using var context = await contextFactory.CreateDbContextAsync();

        // 先記下要刪的內容檔（只取有檔的列：稽核表可能很大），刪完資料列再刪檔 ——
        // 反過來的話，資料列刪失敗會留下指向不存在檔案的紀錄。
        var files = await context.AuditLog
            .Where(OlderThan(thresholdUtc))
            .Where(x => x.ContentFile != null)
            .Select(x => x.ContentFile!)
            .ToListAsync();

        var removed = await context.AuditLog
            .Where(OlderThan(thresholdUtc))
            .ExecuteDeleteAsync();

        foreach (var file in files)
        {
            contentFileStore.Delete(file);
        }

        // 收尾：空的月份目錄，以及資料列沒建起來時留下的孤兒檔。
        contentFileStore.DeleteMonthsBefore(thresholdUtc);
        return removed;
    }

    /// <summary>清空全部紀錄。</summary>
    public async Task<VerifyRecordResult> ClearAllAsync()
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogInformation("Clearing all audit logs.");

        try
        {
            var removed = await context.AuditLog.ExecuteDeleteAsync();
            contentFileStore.DeleteAll();

            Logger.LogInformation("Cleared all audit logs. Rows={Rows}", removed);
            return VerifyRecordResultFactory.Build(true, $"已清空 {removed} 筆紀錄。");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to clear audit logs.");
            return VerifyRecordResultFactory.Build(false, "清空稽核紀錄失敗。", ex);
        }
    }
}

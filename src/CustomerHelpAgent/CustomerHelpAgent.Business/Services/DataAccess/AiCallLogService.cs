using System.Linq.Expressions;
using System.Text.Encodings.Web;
using System.Text.Json;
using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Factories;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.AdapterModel;
using CustomerHelpAgent.Models.Systems;

namespace CustomerHelpAgent.Business.Services.DataAccess;

/// <summary>
/// AI 對話紀錄的資料存取（0.9.72 起）：記錄、查詢、明細、清除與自動過期。
///
/// 資料表只放可篩選的中繼資料，完整請求與回應一律寫進 <see cref="AiCallLogFileStore"/>。
/// ⚠️ 內容只供系統管理員查閱；日誌訊息只記作業與 CallId，絕不記內容。
/// </summary>
public class AiCallLogService : IAiCallLogRecorder
{
    /// <summary>CSV 匯出的筆數上限。</summary>
    public const int MaxExportRows = 10000;

    /// <summary>關聯說明的長度上限（超過截斷）。</summary>
    public const int MaxRelatedInfoLength = 500;

    /// <summary>
    /// 內容檔的序列化設定。
    /// ⚠️ 必須用 <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/>：預設編碼器會把中文跳脫成 \uXXXX，
    /// 檔案膨脹數倍且無法直接閱讀。內容檔不會以 HTML 輸出，所以放寬跳脫是安全的。
    /// </summary>
    public static readonly JsonSerializerOptions FileJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IDbContextFactory<BackendDBContext> contextFactory;
    private readonly AiCallLogFileStore fileStore;
    private readonly IOptionsMonitor<AiCallLogSettings> settings;

    public IMapper Mapper { get; }
    public ILogger<AiCallLogService> Logger { get; }

    // ⚠️ 只能有一個建構式：DataAccessServiceLifetimeTests 用 GetConstructors().Single()。
    public AiCallLogService(
        IDbContextFactory<BackendDBContext> contextFactory,
        IMapper mapper,
        ILogger<AiCallLogService> logger,
        AiCallLogFileStore fileStore,
        IOptionsMonitor<AiCallLogSettings> settings)
    {
        this.contextFactory = contextFactory;
        Mapper = mapper;
        Logger = logger;
        this.fileStore = fileStore;
        this.settings = settings;
    }

    public bool IsEnabled => settings.CurrentValue.Enabled;

    public int RetentionDays => settings.CurrentValue.RetentionDays;

    /// <summary>
    /// 記錄一次已送出的 AI 呼叫：先寫內容檔，再建資料列；資料列建不起來就把檔刪掉。
    /// ⚠️ 全程吞例外。
    /// </summary>
    public async Task RecordAsync(AiCallLogEntry entry)
    {
        if (IsEnabled == false)
        {
            return;
        }

        try
        {
            var json = JsonSerializer.Serialize(entry, FileJsonOptions);
            var contentFile = await fileStore.WriteAsync(entry.OccurredAt, entry.CallId, json);

            var item = new AiCallLog
            {
                CallId = entry.CallId,
                OccurredAt = entry.OccurredAt,
                Operation = entry.Operation,
                Provider = entry.Provider,
                Model = entry.Model,
                Account = entry.Account,
                UserId = entry.UserId,
                Success = entry.Success,
                FailureReason = entry.FailureReason,
                HttpStatus = entry.HttpStatus,
                FinishReason = string.IsNullOrEmpty(entry.FinishReason) ? null : entry.FinishReason,
                ElapsedMilliseconds = entry.ElapsedMilliseconds,
                RequestCharacters = entry.RequestBody.Length,
                ResponseCharacters = entry.ResponseText.Length,
                RelatedInfo = TrimRelatedInfo(entry.RelatedInfo),
                ConversationId = entry.ConversationId,
                ContentFile = contentFile,
            };

            try
            {
                await using var context = await contextFactory.CreateDbContextAsync();
                await context.AiCallLog.AddAsync(item);
                await context.SaveChangesAsync();
            }
            catch (Exception)
            {
                // 資料列沒建起來，剛才那個檔就是孤兒 —— 一律清掉。
                fileStore.Delete(contentFile);
                throw;
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to record AI call log. Operation={Operation}, CallId={CallId}", entry.Operation, entry.CallId);
        }
    }

    public async Task<DataRequestResult<AiCallLogAdapterModel>> GetAsync(AiCallLogQuery query)
    {
        await using var context = await contextFactory.CreateDbContextAsync();

        DataRequestResult<AiCallLogAdapterModel> result = new();
        var dataSource = ApplyFilters(context.AiCallLog.AsNoTracking(), query);

        IOrderedQueryable<AiCallLog>? sorted = null;

        if (string.IsNullOrWhiteSpace(query.SortField) == false)
        {
            if (query.SortField == nameof(AiCallLogAdapterModel.OccurredAt))
            {
                sorted = query.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.Id)
                    : query.SortDescending == false
                        ? dataSource.OrderBy(x => x.OccurredAt).ThenBy(x => x.Id)
                        : null;
            }
            else if (query.SortField == nameof(AiCallLogAdapterModel.ElapsedMilliseconds))
            {
                sorted = query.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.ElapsedMilliseconds).ThenByDescending(x => x.Id)
                    : query.SortDescending == false
                        ? dataSource.OrderBy(x => x.ElapsedMilliseconds).ThenBy(x => x.Id)
                        : null;
            }
            else if (query.SortField == nameof(AiCallLogAdapterModel.RequestCharacters))
            {
                sorted = query.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.RequestCharacters).ThenByDescending(x => x.Id)
                    : query.SortDescending == false
                        ? dataSource.OrderBy(x => x.RequestCharacters).ThenBy(x => x.Id)
                        : null;
            }
        }

        // Skip/Take 之前一定要有 OrderBy，否則 SQLite 不保證回傳順序，分頁會重複或漏資料。
        dataSource = sorted ?? dataSource.OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.Id);

        result.Count = await dataSource.CountAsync();
        var records = await dataSource
            .Skip((query.CurrentPage - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync();

        result.Result = Mapper.Map<List<AiCallLogAdapterModel>>(records);
        return result;
    }

    /// <summary>兩個下拉的可選值，只列出資料庫中實際出現過的。</summary>
    public async Task<AiCallLogFilterOptions> GetFilterOptionsAsync()
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var dataSource = context.AiCallLog.AsNoTracking();

        return new AiCallLogFilterOptions
        {
            Operations = await dataSource.Select(x => x.Operation).Distinct().OrderBy(x => x).ToListAsync(),
            Models = await dataSource.Select(x => x.Model).Distinct().OrderBy(x => x).ToListAsync(),
        };
    }

    /// <summary>以呼叫識別碼找紀錄 Id（Token 用量頁「查看對話」的深連結用）。</summary>
    public async Task<int?> FindIdByCallIdAsync(Guid callId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        return await context.AiCallLog.AsNoTracking()
            .Where(x => x.CallId == callId)
            .Select(x => (int?)x.Id)
            .FirstOrDefaultAsync();
    }

    public async Task<bool> ExistsAsync(Guid callId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        return await context.AiCallLog.AsNoTracking().AnyAsync(x => x.CallId == callId);
    }

    /// <summary>明細：資料列、內容檔、解析後的訊息、對應用量與同一對話的其他紀錄。找不到回 null。</summary>
    public async Task<AiCallLogDetail?> GetDetailAsync(int id)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var item = await context.AiCallLog.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (item is null)
        {
            return null;
        }

        var content = Deserialize(await fileStore.ReadAsync(item.ContentFile), item.CallId);

        var usage = await context.TokenUsageLog.AsNoTracking()
            .Where(x => x.CallId == item.CallId)
            .OrderBy(x => x.Id)
            .FirstOrDefaultAsync();

        List<AiCallLog> conversation = [];
        if (item.ConversationId is { } conversationId)
        {
            conversation = await context.AiCallLog.AsNoTracking()
                .Where(x => x.ConversationId == conversationId)
                .OrderBy(x => x.OccurredAt)
                .ThenBy(x => x.Id)
                .ToListAsync();
        }

        return new AiCallLogDetail
        {
            Item = Mapper.Map<AiCallLogAdapterModel>(item),
            Content = content,
            Messages = AiCallLogMessages.Parse(content?.RequestBody),
            Usage = usage is null ? null : Mapper.Map<TokenUsageLogAdapterModel>(usage),
            Conversation = Mapper.Map<List<AiCallLogAdapterModel>>(conversation),
        };
    }

    /// <summary>內容檔原文（下載 JSON 用）。檔案不存在時回 null。</summary>
    public async Task<string?> ReadContentFileAsync(int id)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var contentFile = await context.AiCallLog.AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => x.ContentFile)
            .FirstOrDefaultAsync();
        return await fileStore.ReadAsync(contentFile);
    }

    public async Task<VerifyRecordResult> DeleteAsync(int id)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogInformation("Deleting AI call log. CallLogId={CallLogId}", id);

        try
        {
            var item = await context.AiCallLog.FirstOrDefaultAsync(x => x.Id == id);
            if (item is null)
            {
                return VerifyRecordResultFactory.Build(false, "找不到要刪除的對話紀錄。");
            }

            var contentFile = item.ContentFile;
            context.AiCallLog.Remove(item);
            await context.SaveChangesAsync();

            fileStore.Delete(contentFile);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to delete AI call log. CallLogId={CallLogId}", id);
            return VerifyRecordResultFactory.Build(false, "刪除對話紀錄失敗。", ex);
        }
    }

    public async Task<VerifyRecordResult> ClearAllAsync()
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogInformation("Clearing all AI call logs.");

        try
        {
            var removed = await context.AiCallLog.ExecuteDeleteAsync();
            fileStore.DeleteAll();

            Logger.LogInformation("Cleared all AI call logs. Rows={Rows}", removed);
            return VerifyRecordResultFactory.Build(true, $"已清空 {removed} 筆紀錄。");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to clear AI call logs.");
            return VerifyRecordResultFactory.Build(false, "清空對話紀錄失敗。", ex);
        }
    }

    /// <summary>刪除指定日期（不含當日）之前的紀錄，連同內容檔一起。</summary>
    public async Task<VerifyRecordResult> PurgeBeforeAsync(DateTime beforeDate)
    {
        var (success, removed) = await PurgeCoreAsync(beforeDate.Date);
        if (success == false)
        {
            return VerifyRecordResultFactory.Build(false, "清除對話紀錄失敗。");
        }

        return removed == 0
            ? VerifyRecordResultFactory.Build(true, "沒有符合條件的紀錄。")
            : VerifyRecordResultFactory.Build(true, $"已清除 {removed} 筆紀錄。");
    }

    /// <summary>
    /// 自動過期：刪除超過 <c>RetentionDays</c> 的紀錄與內容檔，並清掉門檻月份之前的整個月份目錄（孤兒檔）。
    /// 停用記錄時照樣執行 —— 停用的意思是「不再記」，已存在的內容仍必須過期。
    /// </summary>
    /// <returns>刪除的資料列數；失敗回 null。</returns>
    public Task<int?> PurgeExpiredAsync() => PurgeExpiredAsync(DateTime.Now);

    /// <summary>同 <see cref="PurgeExpiredAsync()"/>，由呼叫端提供「現在」（本地時間）—— 排程作業傳入 TimeProvider 的時間，測試才控制得了。</summary>
    public Task<int?> PurgeExpiredAsync(DateTime nowLocal) => PurgeOlderThanAsync(ExpiryThreshold(nowLocal));

    /// <summary>
    /// 刪除早於 <paramref name="threshold"/>（本地時間）的紀錄與內容檔，並清掉門檻月份之前的月份目錄：
    /// 自動過期與「資料保留與清理」頁的「自訂清除」共用（0.9.134 起）。
    /// </summary>
    /// <returns>刪除的資料列數；失敗回 null。</returns>
    public async Task<int?> PurgeOlderThanAsync(DateTime threshold)
    {
        var (success, removed) = await PurgeCoreAsync(threshold);
        if (success)
        {
            fileStore.DeleteMonthsBefore(threshold);
        }

        return success ? removed : null;
    }

    /// <summary>自動過期的門檻：今天往前 <c>RetentionDays</c> 天的 0 點（本地時間）。</summary>
    public DateTime ExpiryThreshold(DateTime nowLocal) => nowLocal.Date.AddDays(-RetentionDays);

    /// <summary>
    /// 清理條件：手動清除、自動過期與 <see cref="GetRetentionStatsAsync"/> 共用，
    /// 「資料保留與清理」頁的「下次會清掉幾筆」才會等於實際刪掉的筆數。
    /// </summary>
    private static Expression<Func<AiCallLog, bool>> OlderThan(DateTime threshold)
        => x => x.OccurredAt < threshold;

    /// <summary>筆數、最舊一筆與自動過期會刪掉的筆數（保留天數至少 1 天，一定會自動清理）。</summary>
    public Task<RetentionStats> GetRetentionStatsAsync(DateTime nowLocal)
        => GetRetentionStatsBeforeAsync(ExpiryThreshold(nowLocal));

    /// <summary>筆數、最舊一筆與早於 <paramref name="threshold"/>（本地時間）的筆數 —— 「自訂清除」的預覽也用它。</summary>
    public async Task<RetentionStats> GetRetentionStatsBeforeAsync(DateTime threshold)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var total = await context.AiCallLog.CountAsync();
        var oldest = await context.AiCallLog.MinAsync(x => (DateTime?)x.OccurredAt);
        var expired = await context.AiCallLog.CountAsync(OlderThan(threshold));
        return new RetentionStats(total, oldest, expired);
    }

    private async Task<(bool Success, int Removed)> PurgeCoreAsync(DateTime threshold)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogInformation("Purging AI call logs before {Threshold}.", threshold);

        try
        {
            var stale = context.AiCallLog.Where(OlderThan(threshold));
            var contentFiles = await stale.Select(x => x.ContentFile).ToListAsync();
            if (contentFiles.Count == 0)
            {
                return (true, 0);
            }

            // 先刪資料列再刪檔：資料列刪失敗就整批不動，不會留下「有列卻沒檔」的狀態。
            // 新寫入的紀錄不可能早於門檻，所以兩次查詢之間不會有漏網之魚。
            var removed = await stale.ExecuteDeleteAsync();
            foreach (var contentFile in contentFiles)
            {
                fileStore.Delete(contentFile);
            }

            Logger.LogInformation("Purged AI call logs. Rows={Rows}", removed);
            return (true, removed);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to purge AI call logs.");
            return (false, 0);
        }
    }

    private AiCallLogEntry? Deserialize(string? json, Guid callId)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AiCallLogEntry>(json, FileJsonOptions);
        }
        catch (JsonException ex)
        {
            Logger.LogWarning(ex, "Failed to parse AI call log file. CallId={CallId}", callId);
            return null;
        }
    }

    private static string? TrimRelatedInfo(string? relatedInfo)
    {
        if (string.IsNullOrWhiteSpace(relatedInfo))
        {
            return null;
        }

        var trimmed = relatedInfo.Trim();
        return trimmed.Length <= MaxRelatedInfoLength ? trimmed : trimmed[..MaxRelatedInfoLength];
    }

    private static IQueryable<AiCallLog> ApplyFilters(IQueryable<AiCallLog> source, AiCallLogQuery query)
    {
        if (query.StartDate.HasValue)
        {
            var start = query.StartDate.Value.Date;
            source = source.Where(x => x.OccurredAt >= start);
        }

        if (query.EndDate.HasValue)
        {
            // 結束日以「當日整天」為準，否則使用者選了今天卻查不到今天的資料。
            var endExclusive = query.EndDate.Value.Date.AddDays(1);
            source = source.Where(x => x.OccurredAt < endExclusive);
        }

        if (string.IsNullOrWhiteSpace(query.Operation) == false)
        {
            source = source.Where(x => x.Operation == query.Operation);
        }

        if (string.IsNullOrWhiteSpace(query.Account) == false)
        {
            source = source.Where(x => x.Account != null && x.Account.Contains(query.Account));
        }

        if (query.Success.HasValue)
        {
            var success = query.Success.Value;
            source = source.Where(x => x.Success == success);
        }

        if (string.IsNullOrWhiteSpace(query.Model) == false)
        {
            source = source.Where(x => x.Model == query.Model);
        }

        if (string.IsNullOrWhiteSpace(query.Keyword) == false)
        {
            source = source.Where(x => x.RelatedInfo != null && x.RelatedInfo.Contains(query.Keyword));
        }

        return source;
    }
}

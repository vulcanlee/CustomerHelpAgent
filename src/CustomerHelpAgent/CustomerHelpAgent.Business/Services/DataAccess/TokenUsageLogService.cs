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
/// LLM token 用量的資料存取。
///
/// <b>新增 LLM 呼叫點的擴充方式</b>：注入本服務，在拿到 API 回應之後呼叫一次
/// <see cref="RecordAsync"/>，該次呼叫就會自動出現在「Token 用量」頁與所有統計頁籤上。
/// 記錄點請放在**發出 HTTP 的那一層**，不要放畫面層 —— 原始 usage、模型名稱與耗時
/// 在回到畫面之前就已經丟失了。
/// </summary>
public class TokenUsageLogService : ITokenUsageRecorder
{
    private readonly IDbContextFactory<BackendDBContext> contextFactory;
    private readonly TokenUsageRawStore rawStore;
    private readonly IAiUsageCostCalculator costCalculator;
    private readonly IAiQuotaService quotaService;

    public IMapper Mapper { get; }
    public ILogger<TokenUsageLogService> Logger { get; }

    // ⚠️ 只能有一個建構式：DataAccessServiceLifetimeTests 用 GetConstructors().Single()。
    public TokenUsageLogService(
        IDbContextFactory<BackendDBContext> contextFactory,
        IMapper mapper,
        ILogger<TokenUsageLogService> logger,
        TokenUsageRawStore rawStore,
        IAiUsageCostCalculator costCalculator,
        IAiQuotaService quotaService)
    {
        this.contextFactory = contextFactory;
        Mapper = mapper;
        Logger = logger;
        this.rawStore = rawStore;
        this.costCalculator = costCalculator;
        this.quotaService = quotaService;
    }

    /// <summary>
    /// 記錄一次 LLM 呼叫。成功與失敗都要記 —— 「回應成功但內容為空」那種情況
    /// 付了錢卻沒拿到東西，而且 API 有回傳用量，正是最值得被看見的。
    ///
    /// ⚠️ 全程吞例外：記錄用量絕不可以讓呼叫端的主流程失敗。
    /// </summary>
    public async Task RecordAsync(TokenUsageEntry entry)
    {
        try
        {
            await using var context = await contextFactory.CreateDbContextAsync();

            var rawFile = await rawStore.WriteAsync(entry.OccurredAt, entry.RawUsageJson);

            // ⚠️ 這層 try/catch 不能省。外層那個雖然也會吞例外，但它會在 AddAsync 之前就中止 ——
            // 計算器有 bug 會讓每一列用量都靜默消失，那比丟掉費用嚴重得多。用量比費用重要。
            AiUsageCost? cost = null;
            try
            {
                cost = costCalculator.Calculate(entry);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to estimate call cost. Operation={Operation}", entry.Operation);
            }

            var item = new TokenUsageLog
            {
                OccurredAt = entry.OccurredAt,
                Operation = entry.Operation,
                CallKind = entry.CallKind,
                Provider = entry.Provider,
                Model = entry.Model,
                Account = entry.Account,
                UserId = entry.UserId,
                InputCount = entry.InputCount,
                OutputCount = entry.OutputCount,
                TotalCount = entry.TotalCount,
                CachedInputCount = entry.CachedInputCount,
                ReasoningCount = entry.ReasoningCount,
                ImageInputCount = entry.ImageInputCount,
                ImageCachedInputCount = entry.ImageCachedInputCount,
                ImageOutputCount = entry.ImageOutputCount,
                DurationSeconds = entry.DurationSeconds,
                CharacterCount = entry.CharacterCount,
                ElapsedMilliseconds = entry.ElapsedMilliseconds,
                Success = entry.Success,
                FailureReason = entry.FailureReason,
                CostUsd = cost?.CostUsd,
                CostTwd = cost?.CostTwd,
                CostExchangeRate = cost?.ExchangeRate,
                CostPriceKey = cost?.PriceKey,
                CostLongContext = cost?.LongContext ?? false,
                CostRateSnapshot = cost?.RateSnapshot,
                RawUsageFile = rawFile,
                CallId = entry.CallId,
            };

            try
            {
                await context.TokenUsageLog.AddAsync(item);
                await context.SaveChangesAsync();
            }
            catch (Exception)
            {
                // 資料列沒建起來，剛才那個檔就是孤兒 —— 一律清掉。
                rawStore.Delete(rawFile);
                throw;
            }

            // AI 用量上限的 80%／100% 提醒（0.9.109 起；健康檢測也計入）。沒有費用的呼叫不會改變已用金額，不必評估。
            // 方法本身不丟例外，提醒失敗不影響這筆用量。
            if (item.CostTwd is > 0)
            {
                await quotaService.NotifyIfReachedAsync(entry.UserId);
            }
        }
        catch (Exception ex)
        {
            // ⚠️ 訊息刻意不含 "token" 這個字：LoggingConventionTests 禁止日誌佔位符與
            // 訊息含敏感字樣，這也是既有欄位叫 InputCount 而非 InputTokens 的原因。
            Logger.LogWarning(ex, "Failed to record LLM usage. Operation={Operation}", entry.Operation);
        }
    }

    public async Task<DataRequestResult<TokenUsageLogAdapterModel>> GetAsync(TokenUsageQuery query)
    {
        await using var context = await contextFactory.CreateDbContextAsync();

        DataRequestResult<TokenUsageLogAdapterModel> result = new();
        var dataSource = ApplyFilters(context.TokenUsageLog.AsNoTracking(), query);

        IOrderedQueryable<TokenUsageLog>? sorted = null;

        if (string.IsNullOrWhiteSpace(query.SortField) == false)
        {
            if (query.SortField == nameof(TokenUsageLogAdapterModel.TotalCount))
            {
                sorted = query.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.TotalCount).ThenByDescending(x => x.Id)
                    : query.SortDescending == false
                        ? dataSource.OrderBy(x => x.TotalCount).ThenBy(x => x.Id)
                        : null;
            }
            else if (query.SortField == nameof(TokenUsageLogAdapterModel.OccurredAt))
            {
                sorted = query.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.Id)
                    : query.SortDescending == false
                        ? dataSource.OrderBy(x => x.OccurredAt).ThenBy(x => x.Id)
                        : null;
            }
            else if (query.SortField == nameof(TokenUsageLogAdapterModel.CostTwd))
            {
                // 排序鍵用台幣而非美金，與畫面上顯示的欄位一致。
                // 未定價（NULL）在 SQLite 升冪時排最前面，效果上等於把它們聚在一起。
                sorted = query.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.CostTwd).ThenByDescending(x => x.Id)
                    : query.SortDescending == false
                        ? dataSource.OrderBy(x => x.CostTwd).ThenBy(x => x.Id)
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

        result.Result = Mapper.Map<List<TokenUsageLogAdapterModel>>(records);
        return result;
    }

    /// <summary>
    /// 篩選範圍的合計。
    ///
    /// ⚠️ 加總語意：<b>快取是輸入的折扣子集、推理計入輸出</b>，兩者都只回報「其中多少」，
    /// 不再加進合計，否則會重複計算。依時長計費（無 token 數）的呼叫不計入。
    /// </summary>
    public async Task<TokenUsageSummary> GetSummaryAsync(TokenUsageQuery query)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var dataSource = ApplyFilters(context.TokenUsageLog.AsNoTracking(), query);

        return new TokenUsageSummary
        {
            InputCount = await dataSource.SumAsync(x => (long?)x.InputCount) ?? 0,
            OutputCount = await dataSource.SumAsync(x => (long?)x.OutputCount) ?? 0,
            CachedInputCount = await dataSource.SumAsync(x => (long?)x.CachedInputCount) ?? 0,
            ReasoningCount = await dataSource.SumAsync(x => (long?)x.ReasoningCount) ?? 0,
            TotalCount = await dataSource.SumAsync(x => (long?)x.TotalCount) ?? 0,
            // 費用逐列加總。台幣不能由美金總額乘上「目前匯率」換算 —— 區間橫跨匯率調整時，
            // 每一列的匯率快照都不同，只有逐列相加才是正確的台幣帳。
            CostUsd = await dataSource.SumAsync(x => x.CostUsd) ?? 0,
            CostTwd = await dataSource.SumAsync(x => x.CostTwd) ?? 0,
            UnpricedCount = await dataSource.CountAsync(x => x.CostUsd == null),
            CallCount = await dataSource.CountAsync(),
        };
    }

    /// <summary>依指定維度分組統計，合計由大到小。</summary>
    public async Task<List<TokenUsageGroupRow>> GetGroupedAsync(TokenUsageQuery query, TokenUsageGroupBy groupBy)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var dataSource = ApplyFilters(context.TokenUsageLog.AsNoTracking(), query);

        var keyed = groupBy switch
        {
            TokenUsageGroupBy.Operation => dataSource.Select(x => new { Key = x.Operation, Row = x }),
            TokenUsageGroupBy.Model => dataSource.Select(x => new { Key = x.Model, Row = x }),
            TokenUsageGroupBy.CallKind => dataSource.Select(x => new { Key = x.CallKind, Row = x }),
            // 系統自動觸發時沒有帳號，分組鍵給一個看得懂的字。
            _ => dataSource.Select(x => new { Key = x.Account ?? "（系統自動）", Row = x }),
        };

        var rows = await keyed
            .GroupBy(x => x.Key)
            .Select(g => new TokenUsageGroupRow
            {
                Key = g.Key,
                InputCount = g.Sum(x => (long?)x.Row.InputCount) ?? 0,
                OutputCount = g.Sum(x => (long?)x.Row.OutputCount) ?? 0,
                CachedInputCount = g.Sum(x => (long?)x.Row.CachedInputCount) ?? 0,
                ReasoningCount = g.Sum(x => (long?)x.Row.ReasoningCount) ?? 0,
                TotalCount = g.Sum(x => (long?)x.Row.TotalCount) ?? 0,
                CostUsd = g.Sum(x => x.Row.CostUsd) ?? 0,
                CostTwd = g.Sum(x => x.Row.CostTwd) ?? 0,
                UnpricedCount = g.Count(x => x.Row.CostUsd == null),
                CallCount = g.Count(),
            })
            .ToListAsync();

        return rows
            .OrderByDescending(x => x.TotalCount)
            .ThenBy(x => x.Key, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// 依日期分組統計，由舊到新。供「最近 N 天」摘要卡與「每日趨勢」使用。
    ///
    /// ⚠️ 分組鍵是 <c>OccurredAt.Date</c>，SQLite 譯為 <c>date(OccurredAt)</c>，聚合在資料庫端完成。
    /// 沒有資料的日期<b>不會</b>出現在結果裡 —— 補零是畫面層的事（畫面才知道有效區間到哪）。
    ///
    /// ⚠️ 摘要卡是拿這裡的日列切片相加得出的，與直接 SUM 全部資料列相比，
    /// double 的最末幾位可能不同。顯示精度是台幣 N2／美金 N4，沒有實質差異，<b>不是 bug</b>。
    /// </summary>
    public async Task<List<TokenUsageDailyRow>> GetDailyAsync(TokenUsageQuery query)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var dataSource = ApplyFilters(context.TokenUsageLog.AsNoTracking(), query);

        var rows = await dataSource
            .GroupBy(x => x.OccurredAt.Date)
            .Select(g => new TokenUsageDailyRow
            {
                Date = g.Key,
                TotalCount = g.Sum(x => (long?)x.TotalCount) ?? 0,
                CostUsd = g.Sum(x => x.CostUsd) ?? 0,
                CostTwd = g.Sum(x => x.CostTwd) ?? 0,
                UnpricedCount = g.Count(x => x.CostUsd == null),
                CallCount = g.Count(),
            })
            .ToListAsync();

        // 時間軸一律由舊到新。排序放在記憶體做，與 GetGroupedAsync 同風格；
        // 沒有 Skip/Take，不會觸發 RowLimitingOperationWithoutOrderByWarning。
        return rows.OrderBy(x => x.Date).ToList();
    }

    /// <summary>三個下拉的可選值，只列出資料庫中實際出現過的。</summary>
    public async Task<TokenUsageFilterOptions> GetFilterOptionsAsync()
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var dataSource = context.TokenUsageLog.AsNoTracking();

        return new TokenUsageFilterOptions
        {
            Operations = await dataSource.Select(x => x.Operation).Distinct().OrderBy(x => x).ToListAsync(),
            CallKinds = await dataSource.Select(x => x.CallKind).Distinct().OrderBy(x => x).ToListAsync(),
            Models = await dataSource.Select(x => x.Model).Distinct().OrderBy(x => x).ToListAsync(),
        };
    }

    /// <summary>讀取原始 usage JSON。檔案不存在時回 null，由畫面顯示友善訊息。</summary>
    public async Task<string?> GetRawUsageAsync(int id)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var item = await context.TokenUsageLog.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        return item is null ? null : await rawStore.ReadAsync(item.RawUsageFile);
    }

    public async Task<VerifyRecordResult> DeleteAsync(int id)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogInformation("Deleting LLM usage record. UsageId={UsageId}", id);

        try
        {
            var item = await context.TokenUsageLog.FirstOrDefaultAsync(x => x.Id == id);
            if (item is null)
            {
                return VerifyRecordResultFactory.Build(false, "找不到要刪除的用量紀錄。");
            }

            var rawFile = item.RawUsageFile;
            context.TokenUsageLog.Remove(item);
            await context.SaveChangesAsync();

            rawStore.Delete(rawFile);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to delete LLM usage record. UsageId={UsageId}", id);
            return VerifyRecordResultFactory.Build(false, "刪除用量紀錄失敗。", ex);
        }
    }

    public async Task<VerifyRecordResult> ClearAllAsync()
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogInformation("Clearing all LLM usage records.");

        try
        {
            var removed = await context.TokenUsageLog.ExecuteDeleteAsync();
            rawStore.DeleteAll();

            Logger.LogInformation("Cleared all LLM usage records. Rows={Rows}", removed);
            return VerifyRecordResultFactory.Build(true, $"已清空 {removed} 筆紀錄。");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to clear LLM usage records.");
            return VerifyRecordResultFactory.Build(false, "清空用量紀錄失敗。", ex);
        }
    }

    /// <summary>自動過期的門檻：今天往前 <paramref name="days"/> 天的 0 點（本地時間）。</summary>
    public static DateTime ExpiryThreshold(int days, DateTime nowLocal) => nowLocal.Date.AddDays(-days);

    /// <summary>
    /// 清理條件：手動清除、自動過期與 <see cref="GetRetentionStatsAsync"/> 共用，
    /// 「資料保留與清理」頁的「下次會清掉幾筆」才會等於實際刪掉的筆數。
    /// </summary>
    private static Expression<Func<TokenUsageLog, bool>> OlderThan(DateTime threshold)
        => x => x.OccurredAt < threshold;

    /// <summary>筆數、最舊一筆與自動過期會刪掉的筆數（<paramref name="days"/> 為 0 以下＝不自動清除，回 null）。</summary>
    public Task<RetentionStats> GetRetentionStatsAsync(int days, DateTime nowLocal)
        => GetRetentionStatsAsync(days > 0 ? ExpiryThreshold(days, nowLocal) : null);

    /// <summary>筆數、最舊一筆與早於 <paramref name="threshold"/>（本地時間；null＝不算）的筆數 —— 「自訂清除」的預覽也用它。</summary>
    public async Task<RetentionStats> GetRetentionStatsAsync(DateTime? threshold)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var total = await context.TokenUsageLog.CountAsync();
        var oldest = await context.TokenUsageLog.MinAsync(x => (DateTime?)x.OccurredAt);
        int? expired = threshold is { } t ? await context.TokenUsageLog.CountAsync(OlderThan(t)) : null;
        return new RetentionStats(total, oldest, expired);
    }

    /// <summary>刪除指定日期（不含當日）之前的紀錄，連同原始檔一起。</summary>
    public async Task<VerifyRecordResult> PurgeBeforeAsync(DateTime beforeDate)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var threshold = beforeDate.Date;
        Logger.LogInformation("Purging LLM usage records before {Threshold}.", threshold);

        try
        {
            var stale = await context.TokenUsageLog
                .Where(OlderThan(threshold))
                .ToListAsync();

            if (stale.Count == 0)
            {
                return VerifyRecordResultFactory.Build(true, "沒有符合條件的紀錄。");
            }

            // 先刪資料列再刪檔：資料列刪失敗就整批不動，不會留下「有列卻沒檔」的狀態。
            var rawFiles = stale.Select(x => x.RawUsageFile).ToList();
            context.TokenUsageLog.RemoveRange(stale);
            await context.SaveChangesAsync();

            foreach (var rawFile in rawFiles)
            {
                rawStore.Delete(rawFile);
            }

            Logger.LogInformation("Purged LLM usage records. Rows={Rows}", stale.Count);
            return VerifyRecordResultFactory.Build(true, $"已清除 {stale.Count} 筆紀錄。");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to purge LLM usage records.");
            return VerifyRecordResultFactory.Build(false, "清除用量紀錄失敗。", ex);
        }
    }

    /// <summary>
    /// 自動過期（0.9.96 起由排程作業呼叫）：刪除早於「今天往前 <paramref name="days"/> 天」的紀錄與原始檔，
    /// 最後清掉門檻月份之前的整個月份目錄（中斷時留下的孤兒檔）。
    ///
    /// ⚠️ 分批刪：<see cref="PurgeBeforeAsync"/> 一次載入全部過期資料並在一個交易裡刪，第一次清兩年份的資料會長時間佔住
    /// SQLite 的寫入鎖，所有使用者的存檔都得排隊。每批先刪資料列再經 <see cref="TokenUsageRawStore"/> 刪檔（速查表 §6.7 紅線），
    /// 批次之間檢查取消 —— 關機時停在批次邊界，不會留下「列刪了、檔還在」以外的狀態，而那種孤兒檔下一次會被月份清理收掉。
    /// </summary>
    /// <returns>刪除的資料列數；失敗回 null（已記錄錯誤）。</returns>
    public async Task<int?> PurgeExpiredAsync(int days, DateTime nowLocal, CancellationToken cancellationToken, int batchSize = 500)
    {
        var removed = await PurgeBeforeBatchedAsync(ExpiryThreshold(days, nowLocal), cancellationToken, batchSize);
        if (removed is not null)
        {
            Logger.LogInformation("Expired LLM usage records purged. Rows={Rows}, RetentionDays={RetentionDays}", removed, days);
        }

        return removed;
    }

    /// <summary>
    /// 分批刪除早於 <paramref name="threshold"/>（本地時間）的紀錄與原始檔：自動過期與「資料保留與清理」頁的「自訂清除」共用（0.9.134 起）。
    /// 做法與注意事項見 <see cref="PurgeExpiredAsync"/>。
    /// </summary>
    /// <returns>刪除的資料列數；失敗回 null（已記錄錯誤）。</returns>
    public async Task<int?> PurgeBeforeBatchedAsync(DateTime threshold, CancellationToken cancellationToken, int batchSize = 500)
    {
        var removed = 0;

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
                var batch = await context.TokenUsageLog
                    .Where(OlderThan(threshold))
                    .OrderBy(x => x.Id)
                    .Select(x => new { x.Id, x.RawUsageFile })
                    .Take(batchSize)
                    .ToListAsync(cancellationToken);
                if (batch.Count == 0)
                {
                    break;
                }

                var ids = batch.Select(x => x.Id).ToList();
                removed += await context.TokenUsageLog.Where(x => ids.Contains(x.Id)).ExecuteDeleteAsync(cancellationToken);
                foreach (var item in batch)
                {
                    rawStore.Delete(item.RawUsageFile);
                }
            }

            rawStore.DeleteMonthsBefore(threshold);
            return removed;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to purge expired LLM usage records. RowsBeforeFailure={Rows}", removed);
            return null;
        }
    }

    private static IQueryable<TokenUsageLog> ApplyFilters(IQueryable<TokenUsageLog> source, TokenUsageQuery query)
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

        if (string.IsNullOrWhiteSpace(query.Account) == false)
        {
            source = source.Where(x => x.Account != null && x.Account.Contains(query.Account));
        }

        if (string.IsNullOrWhiteSpace(query.Operation) == false)
        {
            source = source.Where(x => x.Operation == query.Operation);
        }

        if (string.IsNullOrWhiteSpace(query.CallKind) == false)
        {
            source = source.Where(x => x.CallKind == query.CallKind);
        }

        if (string.IsNullOrWhiteSpace(query.Model) == false)
        {
            source = source.Where(x => x.Model == query.Model);
        }

        return source;
    }
}

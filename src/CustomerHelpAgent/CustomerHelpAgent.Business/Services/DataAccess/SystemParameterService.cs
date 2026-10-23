using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Factories;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Models.AdapterModel;
using CustomerHelpAgent.Models.Systems;

namespace CustomerHelpAgent.Business.Services.DataAccess;

/// <summary>
/// 系統參數覆寫值的存取（0.9.98 起）。只負責資料列，不認識參數目錄：鍵是否可編輯、值是否合法，
/// 由 Web 的 <c>SystemParameterManager</c> 在呼叫這裡之前判斷。
///
/// 註冊為 singleton（只注入 singleton 的工廠），設定覆寫層的定期刷新與管理頁都直接使用。
///
/// 並行控制：開窗時記下的版本號放進 UPDATE／DELETE 的條件，影響 0 列＝別人先改過或刪掉了 → 回衝突；
/// 新增時（開窗時還沒有覆寫）主鍵衝突＝別人先新增了 → 也回衝突。
/// </summary>
public class SystemParameterService
{
    private const int SqliteConstraintErrorCode = 19;

    private readonly IDbContextFactory<BackendDBContext> contextFactory;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<SystemParameterService> logger;

    public SystemParameterService(
        IDbContextFactory<BackendDBContext> contextFactory,
        TimeProvider timeProvider,
        ILogger<SystemParameterService> logger)
    {
        this.contextFactory = contextFactory;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    public async Task<List<SystemParameterAdapterModel>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.SystemParameter
            .AsNoTracking()
            .OrderBy(x => x.ParameterKey)
            .Select(x => new SystemParameterAdapterModel
            {
                ParameterKey = x.ParameterKey,
                Value = x.Value,
                ConcurrencyStamp = x.ConcurrencyStamp,
                UpdatedAtUtc = x.UpdatedAtUtc,
                UpdatedBy = x.UpdatedBy,
            })
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// 新增或更新一個覆寫值。<paramref name="expectedStamp"/> 為 null 表示開窗時還沒有覆寫（新增）。
    /// </summary>
    public async Task<VerifyRecordResult> UpsertAsync(string key, string value, string? expectedStamp, string? account, CancellationToken cancellationToken = default)
    {
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        if (expectedStamp is null)
        {
            context.SystemParameter.Add(new SystemParameter
            {
                ParameterKey = key,
                Value = value,
                ConcurrencyStamp = ConcurrencyStampHelper.New(),
                CreatedAtUtc = nowUtc,
                UpdatedAtUtc = nowUtc,
                UpdatedBy = account,
            });
            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: SqliteConstraintErrorCode })
            {
                logger.LogInformation("System parameter was created by someone else first. ParameterKey={ParameterKey}", key);
                return VerifyRecordResultFactory.Build(false, ConcurrencyStampHelper.ConflictMessage);
            }

            return VerifyRecordResultFactory.Build(true);
        }

        var newStamp = ConcurrencyStampHelper.New();
        var affected = await context.SystemParameter
            .Where(x => x.ParameterKey == key && x.ConcurrencyStamp == expectedStamp)
            .ExecuteUpdateAsync(x => x
                .SetProperty(p => p.Value, value)
                .SetProperty(p => p.ConcurrencyStamp, newStamp)
                .SetProperty(p => p.UpdatedAtUtc, nowUtc)
                .SetProperty(p => p.UpdatedBy, account), cancellationToken);
        if (affected == 0)
        {
            logger.LogInformation("System parameter changed by someone else before saving. ParameterKey={ParameterKey}", key);
            return VerifyRecordResultFactory.Build(false, ConcurrencyStampHelper.ConflictMessage);
        }

        return VerifyRecordResultFactory.Build(true);
    }

    /// <summary>刪除覆寫值（回到設定檔的值）。</summary>
    public async Task<VerifyRecordResult> DeleteAsync(string key, string expectedStamp, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var affected = await context.SystemParameter
            .Where(x => x.ParameterKey == key && x.ConcurrencyStamp == expectedStamp)
            .ExecuteDeleteAsync(cancellationToken);
        if (affected == 0)
        {
            logger.LogInformation("System parameter changed by someone else before resetting. ParameterKey={ParameterKey}", key);
            return VerifyRecordResultFactory.Build(false, ConcurrencyStampHelper.ConflictMessage);
        }

        return VerifyRecordResultFactory.Build(true);
    }
}

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.Business.Helpers;

namespace CustomerHelpAgent.Web.Scheduling;

/// <summary>
/// 排程作業的跨行程鎖（0.9.96 起）：每個作業一個鎖檔 <c>&lt;資料庫檔&gt;.job-&lt;名稱&gt;.lock</c>，保證同一個作業不會重疊執行
/// （例如手動執行還沒結束時排程時間到了、或 IIS 重疊回收時兩個行程同時想跑）。
///
/// 鎖檔放在 EF 實際開啟的資料庫檔旁邊（與資料庫初始化鎖相同的理由）。記憶體資料庫沒有檔案，不上鎖 ——
/// 此時只靠資料庫的時段搶占保證「同一時段只跑一次」。
/// </summary>
public sealed class JobLockProvider
{
    private readonly Lazy<string?> databaseFile;
    private readonly ILogger<JobLockProvider> logger;

    public JobLockProvider(IDbContextFactory<BackendDBContext> contextFactory, ILogger<JobLockProvider> logger)
    {
        this.logger = logger;
        databaseFile = new Lazy<string?>(() =>
        {
            using var context = contextFactory.CreateDbContext();
            var dataSource = new SqliteConnectionStringBuilder(context.Database.GetConnectionString()).DataSource;
            return CrossProcessFileLock.ResolveDatabaseFile(dataSource);
        });
    }

    /// <summary>測試用：直接指定資料庫檔（null 代表不上鎖）。</summary>
    internal JobLockProvider(string? databaseFile, ILogger<JobLockProvider> logger)
    {
        this.logger = logger;
        this.databaseFile = new Lazy<string?>(() => databaseFile);
    }

    /// <summary>試著立即取得作業的鎖；被佔著時回 null。不需要鎖（記憶體資料庫）時回一個空操作的控制代碼。</summary>
    public IDisposable? TryAcquire(string jobName)
    {
        if (databaseFile.Value is not { } file)
        {
            return NoopLock.Instance;
        }

        var handle = CrossProcessFileLock.TryAcquire($"{file}.job-{jobName}.lock");
        if (handle is null)
        {
            logger.LogDebug("Scheduled job lock is held by another run. JobName={JobName}", jobName);
        }

        return handle;
    }

    private sealed class NoopLock : IDisposable
    {
        public static readonly NoopLock Instance = new();

        public void Dispose()
        {
        }
    }
}

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.AccessDatas;

namespace CustomerHelpAgent.Business.Startup;

/// <summary>
/// 啟動時的資料庫準備流程（0.9.91 起，取代 Program.cs 原本約 130 行的 migrate 與 seed）：
/// 取得跨行程鎖 → 套用 migration → 設定 WAL → 依序執行 <see cref="IDatabaseSeeder"/> → 釋放鎖。
///
/// 任何一步丟出例外都會中止啟動（與原本相同）；只有 RBAC 回填自己吞掉例外（見 <see cref="RbacBackfillSeeder"/>）。
/// </summary>
public sealed class DatabaseInitializer : IDatabaseInitializer
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly DatabaseInitializerOptions options;
    private readonly ILogger<DatabaseInitializer> logger;

    public DatabaseInitializer(
        IServiceScopeFactory scopeFactory,
        IOptions<DatabaseInitializerOptions> options,
        ILogger<DatabaseInitializer> logger)
    {
        this.scopeFactory = scopeFactory;
        this.options = options.Value;
        this.logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<BackendDBContext>();

        // 鎖檔放在 EF 實際開啟的那個檔案旁邊，而不是另外從設定組路徑 —— 兩者不一致時，鎖就鎖錯地方了。
        var databasePath = new SqliteConnectionStringBuilder(dbContext.Database.GetConnectionString()).DataSource;

        using var initializationLock = await DatabaseInitializationLock.AcquireAsync(
            databasePath, options.LockTimeout, logger, cancellationToken);

        logger.LogInformation("Ensuring database is ready.");
        await dbContext.Database.MigrateAsync(cancellationToken);
        logger.LogInformation("Database migrations applied successfully.");

        EnableWriteAheadLogging(dbContext, databasePath);

        var seeders = scope.ServiceProvider.GetServices<IDatabaseSeeder>()
            .OrderBy(x => x.Order)
            .ToList();
        EnsureUniqueOrder(seeders);

        foreach (var seeder in seeders)
        {
            logger.LogDebug("Running database seeder. Seeder={Seeder} Order={Order}", seeder.Name, seeder.Order);
            await seeder.SeedAsync(cancellationToken);

            // 所有 seeder 共用同一個 DbContext；清掉追蹤狀態，避免某個 seeder 失敗後殘留的變更
            // 被下一個 seeder 的 SaveChanges 一併寫進資料庫。
            dbContext.ChangeTracker.Clear();
        }
    }

    /// <summary>
    /// WAL 讓讀取不會被寫入擋住（Blazor 的使用情境是一寫多讀）。設定存在資料庫檔內，之後每條連線自動生效。
    ///
    /// 失敗只記警告不中止：WAL 是效能設定，不是正確性的前提；下一次啟動會再試。
    /// 刻意用 ADO 而不是 ExecuteSqlRaw —— 這個 PRAGMA 會回傳實際生效的模式，要檢查它。
    /// </summary>
    private void EnableWriteAheadLogging(BackendDBContext dbContext, string? databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath)
            || databasePath.Contains(":memory:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            dbContext.Database.OpenConnection();
            using var command = dbContext.Database.GetDbConnection().CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL;";
            var mode = command.ExecuteScalar() as string;

            if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("SQLite did not switch to WAL journal mode. JournalMode={JournalMode}", mode);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to enable SQLite WAL journal mode; continuing with the current mode.");
        }
        finally
        {
            dbContext.Database.CloseConnection();
        }
    }

    private static void EnsureUniqueOrder(IReadOnlyList<IDatabaseSeeder> seeders)
    {
        var duplicates = seeders
            .GroupBy(x => x.Order)
            .Where(x => x.Count() > 1)
            .Select(x => $"{x.Key}: {string.Join(", ", x.Select(s => s.Name))}")
            .ToList();

        if (duplicates.Count > 0)
        {
            throw new InvalidOperationException(
                "IDatabaseSeeder 的 Order 不可重複，否則執行順序不確定：" + string.Join("; ", duplicates));
        }
    }
}

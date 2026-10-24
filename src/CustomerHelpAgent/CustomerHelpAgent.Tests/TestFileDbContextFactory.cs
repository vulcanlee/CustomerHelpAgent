using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using CustomerHelpAgent.AccessDatas;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 測試用的 <see cref="IDbContextFactory{TContext}"/>：暫存檔的 SQLite 資料庫，<b>每個 context 各開一條連線</b>（與正式環境相同）。
///
/// 給會在背景執行緒存取資料庫的測試用 —— 例如多 Agent 模式（M5）的 Handoff Workflow 以 <c>InProcessExecution.OffThread</c> 執行，
/// checkpoint、工具與執行軌跡會在兩條執行緒同時寫入。<see cref="TestDbContextFactory"/> 讓所有 context 共用同一條 in-memory 連線，
/// 並行使用會出現「SQLite Error 5: unable to delete/modify user-function due to active statements」。
/// 檔案以 WAL 模式開啟，寫入互斥由 SQLite 的忙碌等待處理；<see cref="Dispose"/> 刪除檔案。
/// </summary>
public sealed class TestFileDbContextFactory : IDbContextFactory<BackendDBContext>, IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), "cha-test-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly string connectionString;

    public TestFileDbContextFactory()
    {
        connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL;";
        command.ExecuteNonQuery();
    }

    public BackendDBContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<BackendDBContext>()
            .UseSqlite(connectionString)
            .ConfigureWarnings(w => w.Throw(CoreEventId.RowLimitingOperationWithoutOrderByWarning))
            .Options;

        return new BackendDBContext(options);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { path, path + "-wal", path + "-shm" })
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // 暫存檔刪不掉不影響測試結果（下次清理暫存資料夾時處理）。
            }
        }
    }
}

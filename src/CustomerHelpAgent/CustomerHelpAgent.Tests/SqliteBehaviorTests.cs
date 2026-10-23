using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 釘住三個「連線參數該怎麼設」所依賴的 SQLite／Microsoft.Data.Sqlite 行為（0.9.91）。
///
/// 這些不是本專案的程式碼，而是驅動程式與原生程式庫的預設行為。之所以寫成測試：
/// 連線字串只寫了必要的參數、刻意沒加 <c>busy_timeout</c>，理由全建立在這些行為上；
/// 哪天升級套件讓行為改變，這裡會先紅，而不是等到正式環境出現 <c>database is locked</c>。
/// </summary>
public sealed class SqliteBehaviorTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "CustomerHelpAgentSqliteBehavior", Guid.NewGuid().ToString("N"));
    private readonly string connectionString;

    public SqliteBehaviorTests()
    {
        Directory.CreateDirectory(directory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "test.db") }.ToString();
    }

    [Fact]
    public void ForeignKeys_ShouldBeEnabledEvenWithoutConnectionStringKeyword()
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        Assert.Equal(1L, ExecuteScalar(connection, "PRAGMA foreign_keys;"));
    }

    [Fact]
    public void JournalModeWal_ShouldBeAcceptedAndPersistInTheFile()
    {
        using (var connection = new SqliteConnection(connectionString))
        {
            connection.Open();
            Assert.Equal("wal", ExecuteScalar(connection, "PRAGMA journal_mode=WAL;"));
        }

        SqliteConnection.ClearAllPools();

        using var reopened = new SqliteConnection(connectionString);
        reopened.Open();
        Assert.Equal("wal", ExecuteScalar(reopened, "PRAGMA journal_mode;"));
    }

    [Fact]
    public async Task BusyDatabase_ShouldBeRetriedUntilTheLockIsReleased()
    {
        CreateTable();

        using var holder = new SqliteConnection(connectionString);
        holder.Open();
        using var transaction = holder.BeginTransaction();
        Execute(holder, "INSERT INTO Item (Name) VALUES ('holder');", transaction);

        var releaseAfter = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            transaction.Commit();
        });

        using var writer = new SqliteConnection(connectionString);
        writer.Open();

        // 寫入者沒有設定任何 busy_timeout，卻不會立刻丟出 SQLITE_BUSY：驅動會自己重試，直到持有者提交。
        Execute(writer, "INSERT INTO Item (Name) VALUES ('writer');");
        await releaseAfter;

        Assert.Equal(2L, ExecuteScalar(writer, "SELECT COUNT(*) FROM Item;"));
    }

    [Fact]
    public void BusyDatabase_ShouldFailOnceTheCommandTimeoutIsReached()
    {
        CreateTable();

        using var holder = new SqliteConnection(connectionString);
        holder.Open();
        using var transaction = holder.BeginTransaction();
        Execute(holder, "INSERT INTO Item (Name) VALUES ('holder');", transaction);

        using var writer = new SqliteConnection(connectionString);
        writer.Open();
        using var command = writer.CreateCommand();
        command.CommandText = "INSERT INTO Item (Name) VALUES ('writer');";
        command.CommandTimeout = 1;

        var watch = Stopwatch.StartNew();
        var exception = Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
        watch.Stop();

        // 重試的上限就是命令逾時（Microsoft.Data.Sqlite 預設 30 秒），不是無限等待。
        Assert.Equal(5, exception.SqliteErrorCode); // SQLITE_BUSY
        Assert.InRange(watch.Elapsed, TimeSpan.FromMilliseconds(800), TimeSpan.FromSeconds(10));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // 暫存目錄清不掉不影響測試結果；系統的暫存清理會處理。
        }
    }

    private void CreateTable()
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        Execute(connection, "CREATE TABLE Item (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL);");
    }

    private static object? ExecuteScalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        command.ExecuteNonQuery();
    }
}

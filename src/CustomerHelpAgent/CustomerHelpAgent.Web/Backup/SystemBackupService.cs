using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Web.Configuration;

namespace CustomerHelpAgent.Web.Backup;

/// <summary>一次備份的結果。</summary>
public sealed record BackupRunResult(bool Success, string Message, string? FileName = null, long SizeBytes = 0, int MissingFileCount = 0);

/// <summary>
/// 建立一份系統備份 zip（0.9.99 起）：資料庫快照＋專案附件＋售後照片（1.0.3 起）＋例外堆疊檔＋Token 原始檔＋稽核內容檔（0.9.131 起）＋Data Protection 金鑰環（AI 對話內容可選）。
///
/// ⚠️ 設計重點（都有測試釘住）：
/// <list type="bullet">
/// <item>資料庫用 <c>VACUUM INTO</c> 做快照：WAL 模式下讀到一致的快照、不擋寫入、產出單一檔。<b>絕不直接複製 <c>BackendDB.db</c></b>
/// （最近的寫入可能還在 <c>-wal</c> 裡，複製出來的檔是殘缺的）。用不經連線池、不設逾時的獨立連線，不走 EF 與慢查詢攔截器。</item>
/// <item>快照後跑 <c>PRAGMA quick_check</c>，不是 ok 就不產出備份。</item>
/// <item>先資料庫、後檔案：之後才出現的檔案只是孤兒；反過來則會有資料列指向不在備份裡的檔。打包途中消失或被獨佔而打不開的檔案只計數，不中止。</item>
/// <item>一律先寫 <c>.work/</c> 與 <c>*.partial</c>，完成才改名成 <c>.zip</c>；失敗或取消都清掉（被強制結束時由下一次清）。</item>
/// <item>呼叫端（排程作業）持有作業鎖，同一時間只有一份備份在跑。</item>
/// </list>
/// </summary>
public sealed class SystemBackupService
{
    private const int MaxListedMissingFiles = 50;
    private const double DiskSpaceMargin = 1.1;

    private readonly IOptions<SystemSettings> systemSettings;
    private readonly IOptionsMonitor<BackupSettings> backupSettings;
    private readonly IDbContextFactory<BackendDBContext> contextFactory;
    private readonly ISystemIdentity systemIdentity;
    private readonly BackupStore store;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<SystemBackupService> logger;

    public SystemBackupService(
        IOptions<SystemSettings> systemSettings,
        IOptionsMonitor<BackupSettings> backupSettings,
        IDbContextFactory<BackendDBContext> contextFactory,
        ISystemIdentity systemIdentity,
        BackupStore store,
        TimeProvider timeProvider,
        ILogger<SystemBackupService> logger)
    {
        this.systemSettings = systemSettings;
        this.backupSettings = backupSettings;
        this.contextFactory = contextFactory;
        this.systemIdentity = systemIdentity;
        this.store = store;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    /// <summary>
    /// 測試用：可用空間的判斷（預設讀磁碟）。回 null 表示查不到（例如 UNC 路徑），此時不檢查。
    /// </summary>
    internal Func<string, long?> FreeSpaceProbe { get; set; } = DefaultFreeSpace;

    /// <summary>測試用：每加入一個 zip entry 之後呼叫（用來在打包途中取消）。</summary>
    internal Action<string>? EntryAdded { get; set; }

    public async Task<BackupRunResult> CreateAsync(string trigger, CancellationToken cancellationToken)
    {
        var paths = systemSettings.Value.ExternalFileSystem;
        var root = paths.BackupPath;
        Directory.CreateDirectory(root);
        store.CleanLeftovers();

        var databaseFile = await ResolveDatabaseFileAsync();
        if (databaseFile is null || !File.Exists(databaseFile))
        {
            return new(false, "找不到資料庫檔（記憶體資料庫無法備份）。");
        }

        var folders = SelectFolders(paths);
        var estimate = EstimateBytes(databaseFile, folders);
        if (FreeSpaceProbe(root) is { } free && free < estimate * DiskSpaceMargin)
        {
            return new(false, $"備份目錄的可用空間不足：約需 {estimate / 1024 / 1024:N0} MB，剩 {free / 1024 / 1024:N0} MB。");
        }

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var fileName = store.NewFileName(timeProvider.GetLocalNow().DateTime);
        var finalPath = Path.Combine(root, fileName);
        var partialPath = finalPath + BackupStore.PartialExtension;
        var workDirectory = Path.Combine(root, BackupStore.WorkDirectoryName, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDirectory);

        try
        {
            var snapshot = Path.Combine(workDirectory, "BackendDB.db");
            await SnapshotDatabaseAsync(databaseFile, snapshot, cancellationToken);

            var check = await QuickCheckAsync(snapshot);
            if (!string.Equals(check, "ok", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogError("Backup snapshot failed the integrity check. Result={Result}", check);
                return new(false, $"資料庫快照沒有通過完整性檢查（{check}），沒有產生備份。");
            }

            var manifest = new BackupManifest
            {
                CreatedAtUtc = nowUtc,
                SystemVersion = systemIdentity.Version,
                DatabaseSha256 = await HashFileAsync(snapshot, cancellationToken),
                Trigger = trigger,
            };
            (manifest.LastMigration, manifest.MigrationCount) = await ReadMigrationsAsync(snapshot);

            await using (var output = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create))
            {
                await AddFileAsync(zip, snapshot, manifest.DatabaseEntry, cancellationToken);
                EntryAdded?.Invoke(manifest.DatabaseEntry);
                foreach (var folder in folders)
                {
                    var count = await AddFolderAsync(zip, folder, manifest, cancellationToken);
                    manifest.Folders.Add(new BackupManifestFolder { Entry = folder.Entry, SettingKey = folder.SettingKey, FileCount = count });
                }

                var manifestEntry = zip.CreateEntry(BackupStore.ManifestEntryName, CompressionLevel.Optimal);
                await using var manifestStream = manifestEntry.Open();
                await JsonSerializer.SerializeAsync(manifestStream, manifest, BackupManifest.JsonOptions, cancellationToken);
            }

            File.Move(partialPath, finalPath);
            var size = new FileInfo(finalPath).Length;
            logger.LogInformation(
                "System backup created. FileName={FileName}, SizeBytes={SizeBytes}, MissingFiles={MissingFiles}, Trigger={Trigger}",
                fileName, size, manifest.MissingFileCount, trigger);
            return new(true, $"已建立 {fileName}（{size / 1024d / 1024d:N1} MB）。", fileName, size, manifest.MissingFileCount);
        }
        finally
        {
            TryDeleteDirectory(workDirectory);
            TryDeleteFile(partialPath);
        }
    }

    internal sealed record BackupFolder(string Entry, string SettingKey, string Path);

    private List<BackupFolder> SelectFolders(ExternalFileSystem paths)
    {
        var folders = new List<BackupFolder>
        {
            new("files/ProjectFile", nameof(paths.ProjectFilePath), paths.ProjectFilePath),
            new("files/AfterSalesFile", nameof(paths.AfterSalesFilePath), paths.AfterSalesFilePath),
            new("files/Exception", nameof(paths.ExceptionPath), paths.ExceptionPath),
            new("files/TokenUsage", nameof(paths.TokenUsagePath), paths.TokenUsagePath),
            new("files/AuditContent", nameof(paths.AuditContentPath), paths.AuditContentPath),
            new("files/Keys", nameof(paths.DataProtectionKeyPath), paths.DataProtectionKeyPath),
        };

        if (backupSettings.CurrentValue.IncludeAiCallLogs)
        {
            folders.Add(new("files/AiCallLog", nameof(paths.AiCallLogPath), paths.AiCallLogPath));
        }

        return folders;
    }

    private async Task<string?> ResolveDatabaseFileAsync()
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var dataSource = new SqliteConnectionStringBuilder(context.Database.GetConnectionString()).DataSource;
        return CrossProcessFileLock.ResolveDatabaseFile(dataSource);
    }

    /// <summary>不經連線池（快照檔不能被池子裡的連線鎖著）、不設逾時（大資料庫超過預設 30 秒）。</summary>
    private static SqliteConnection OpenRawConnection(string file, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = file,
            Mode = mode,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static async Task SnapshotDatabaseAsync(string databaseFile, string snapshot, CancellationToken cancellationToken)
    {
        await using var connection = OpenRawConnection(databaseFile, SqliteOpenMode.ReadOnly);
        await using var command = connection.CreateCommand();
        command.CommandText = "VACUUM INTO $target";
        command.Parameters.AddWithValue("$target", snapshot);
        command.CommandTimeout = 0;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<string> QuickCheckAsync(string snapshot)
    {
        await using var connection = OpenRawConnection(snapshot, SqliteOpenMode.ReadOnly);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check";
        command.CommandTimeout = 0;
        return Convert.ToString(await command.ExecuteScalarAsync()) ?? string.Empty;
    }

    private static async Task<(string? Last, int Count)> ReadMigrationsAsync(string snapshot)
    {
        await using var connection = OpenRawConnection(snapshot, SqliteOpenMode.ReadOnly);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(MigrationId), COUNT(*) FROM __EFMigrationsHistory";
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.IsDBNull(0) ? null : reader.GetString(0), reader.GetInt32(1));
    }

    private async Task<int> AddFolderAsync(ZipArchive zip, BackupFolder folder, BackupManifest manifest, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(folder.Path) || !Directory.Exists(folder.Path))
        {
            return 0;
        }

        var count = 0;
        foreach (var file in Directory.EnumerateFiles(folder.Path, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(folder.Path, file).Replace('\\', '/');
            var source = TryOpenSource(file);
            if (source is null)
            {
                // 打包途中被清理作業刪掉、或正被別的程式獨佔：只計數，不讓整份備份失敗。
                manifest.MissingFileCount++;
                if (manifest.MissingFiles.Count < MaxListedMissingFiles)
                {
                    manifest.MissingFiles.Add($"{folder.Entry}/{relative}");
                }

                continue;
            }

            await using (source)
            {
                await AddStreamAsync(zip, source, File.GetLastWriteTime(file), $"{folder.Entry}/{relative}", cancellationToken);
            }

            count++;
            EntryAdded?.Invoke($"{folder.Entry}/{relative}");
        }

        return count;
    }

    private static async Task AddFileAsync(ZipArchive zip, string sourceFile, string entryName, CancellationToken cancellationToken)
    {
        await using var source = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        await AddStreamAsync(zip, source, File.GetLastWriteTime(sourceFile), entryName, cancellationToken);
    }

    private static async Task AddStreamAsync(ZipArchive zip, Stream source, DateTime lastWriteTime, string entryName, CancellationToken cancellationToken)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        entry.LastWriteTime = lastWriteTime;
        await using var target = entry.Open();
        await source.CopyToAsync(target, cancellationToken);
    }

    /// <summary>
    /// 先開來源檔再建 zip entry（打不開時不留下空的 entry）。允許別人同時寫入或刪除。
    /// 只有「開啟來源」的失敗當成略過；寫進 zip 的失敗（例如磁碟滿）照常丟出，讓這次備份失敗。
    /// </summary>
    private static FileStream? TryOpenSource(string file)
    {
        try
        {
            return new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task<string> HashFileAsync(string file, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(file);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static long EstimateBytes(string databaseFile, IEnumerable<BackupFolder> folders)
    {
        long total = new FileInfo(databaseFile).Length;
        var wal = databaseFile + "-wal";
        if (File.Exists(wal))
        {
            total += new FileInfo(wal).Length;
        }

        foreach (var folder in folders.Where(x => !string.IsNullOrWhiteSpace(x.Path) && Directory.Exists(x.Path)))
        {
            total += Directory.EnumerateFiles(folder.Path, "*", SearchOption.AllDirectories).Sum(x => new FileInfo(x).Length);
        }

        // 快照（未壓縮）與 zip 同時存在，抓兩倍。
        return total * 2;
    }

    private static long? DefaultFreeSpace(string path)
    {
        try
        {
            var drive = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(drive) || drive.StartsWith(@"\\", StringComparison.Ordinal) ? null : new DriveInfo(drive).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 下一次備份開始時會再清一次。
            logger.LogWarning(ex, "Backup work directory could not be deleted; it will be removed by the next backup.");
        }
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Partial backup file could not be deleted; it will be removed by the next backup.");
        }
    }
}

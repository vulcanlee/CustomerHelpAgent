using System.IO.Compression;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Web.Auth;
using CustomerHelpAgent.Web.Backup;
using CustomerHelpAgent.Web.Configuration;
using CustomerHelpAgent.Web.Configuration.Validation;
using CustomerHelpAgent.Web.Controllers;
using CustomerHelpAgent.Web.Scheduling;
using CustomerHelpAgent.Web.Scheduling.Jobs;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 系統備份（0.9.99 起）：快照一致、內容正確、失敗與取消不留殘檔、成功後才刪舊備份、備份目錄不可與資料目錄重疊、下載只限管理員。
/// </summary>
public sealed class SystemBackupTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "CustomerHelpAgentBackupTests", Guid.NewGuid().ToString("N"));
    private readonly SystemSettings settings;
    private readonly string databaseFile;

    public SystemBackupTests()
    {
        settings = new SystemSettings();
        settings.SystemInformation.SystemVersion = "0.9.99 (2026/10/04)";
        var paths = settings.ExternalFileSystem;
        paths.DatabasePath = Dir("DB");
        paths.ProjectFilePath = Dir("ProjectFile");
        paths.AfterSalesFilePath = Dir("AfterSalesFile");
        paths.ExceptionPath = Dir("Exception");
        paths.TokenUsagePath = Dir("TokenUsage");
        paths.AiCallLogPath = Dir("AiCallLog");
        paths.AuditContentPath = Dir("AuditContent");
        paths.DataProtectionKeyPath = Dir("Keys");
        paths.BackupPath = Path.Combine(root, "Backup");
        databaseFile = Path.Combine(paths.DatabasePath, "BackendDB.db");

        using (var context = NewContext())
        {
            context.Database.Migrate();
            context.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
        }

        File.WriteAllText(Path.Combine(paths.ProjectFilePath, "a.txt"), "attachment");
        Directory.CreateDirectory(Path.Combine(paths.ProjectFilePath, "2026", "10"));
        File.WriteAllText(Path.Combine(paths.ProjectFilePath, "2026", "10", "b.pdf"), "nested attachment");
        Directory.CreateDirectory(Path.Combine(paths.AfterSalesFilePath, "2026", "10"));
        File.WriteAllText(Path.Combine(paths.AfterSalesFilePath, "2026", "10", "photo.jpg"), "after-sales photo");
        File.WriteAllText(Path.Combine(paths.ExceptionPath, "stack.txt"), "stack");
        File.WriteAllText(Path.Combine(paths.TokenUsagePath, "usage.json"), "{}");
        File.WriteAllText(Path.Combine(paths.DataProtectionKeyPath, "key-1.xml"), "<key/>");
        File.WriteAllText(Path.Combine(paths.AiCallLogPath, "call.json"), "secret prompt");
        Directory.CreateDirectory(Path.Combine(paths.AuditContentPath, "202610"));
        File.WriteAllText(Path.Combine(paths.AuditContentPath, "202610", "diff.txt"), "Name: A → B");
    }

    // ---------- 備份內容 ----------

    [Fact]
    public async Task Backup_ShouldSnapshotTheDatabaseWhileItIsBeingWritten_AndPackTheSelectedFolders()
    {
        InsertAuditRows(50);
        using var stop = new CancellationTokenSource();
        var writer = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                InsertAuditRows(1);
                await Task.Delay(5);
            }
        });

        var result = await NewService().CreateAsync(JobRunTriggers.Manual, CancellationToken.None);
        await stop.CancelAsync();
        await writer;

        Assert.True(result.Success, result.Message);
        var zipPath = Path.Combine(settings.ExternalFileSystem.BackupPath, result.FileName!);
        var extracted = Extract(zipPath);
        var manifest = ReadManifest(extracted);

        Assert.Equal(BackupManifest.CurrentFormatVersion, manifest.FormatVersion);
        Assert.Equal("0.9.99 (2026/10/04)", manifest.SystemVersion);
        Assert.Equal(JobRunTriggers.Manual, manifest.Trigger);
        Assert.False(string.IsNullOrEmpty(manifest.LastMigration));
        var snapshot = Path.Combine(extracted, "db", "BackendDB.db");
        Assert.Equal(manifest.DatabaseSha256, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(snapshot))));
        Assert.Equal("ok", Scalar(snapshot, "PRAGMA quick_check"));
        Assert.True(Convert.ToInt32(Scalar(snapshot, "SELECT COUNT(*) FROM AuditLog")) >= 50);

        Assert.Equal(
            new[] { "AfterSalesFilePath", "AuditContentPath", "DataProtectionKeyPath", "ExceptionPath", "ProjectFilePath", "TokenUsagePath" },
            manifest.Folders.Select(x => x.SettingKey).Order().ToArray());
        Assert.True(File.Exists(Path.Combine(extracted, "files", "ProjectFile", "2026", "10", "b.pdf")));
        Assert.True(File.Exists(Path.Combine(extracted, "files", "AfterSalesFile", "2026", "10", "photo.jpg")));
        var afterSalesFolder = manifest.Folders.Single(x => x.SettingKey == "AfterSalesFilePath");
        Assert.Equal("files/AfterSalesFile", afterSalesFolder.Entry);
        Assert.Equal(1, afterSalesFolder.FileCount);
        Assert.True(File.Exists(Path.Combine(extracted, "files", "Keys", "key-1.xml")));
        Assert.True(File.Exists(Path.Combine(extracted, "files", "AuditContent", "202610", "diff.txt")));
        Assert.Equal(2, manifest.Folders.Single(x => x.SettingKey == "ProjectFilePath").FileCount);
        Assert.False(Directory.Exists(Path.Combine(extracted, "files", "AiCallLog")), "AI 對話內容預設不備份。");
        AssertNoLeftovers();
    }

    [Fact]
    public async Task AiCallLogs_ShouldOnlyBeIncludedWhenEnabled()
    {
        var result = await NewService(new BackupSettings { IncludeAiCallLogs = true }).CreateAsync(JobRunTriggers.Schedule, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        var extracted = Extract(Path.Combine(settings.ExternalFileSystem.BackupPath, result.FileName!));
        Assert.Equal("secret prompt", File.ReadAllText(Path.Combine(extracted, "files", "AiCallLog", "call.json")));
    }

    [Fact]
    public async Task FileThatCannotBeOpened_ShouldBeCountedAsMissing_NotFailTheBackup()
    {
        var locked = Path.Combine(settings.ExternalFileSystem.ExceptionPath, "locked.txt");
        File.WriteAllText(locked, "in use");
        using var holder = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = await NewService().CreateAsync(JobRunTriggers.Manual, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Equal(1, result.MissingFileCount);
        var manifest = ReadManifest(Extract(Path.Combine(settings.ExternalFileSystem.BackupPath, result.FileName!)));
        Assert.Contains("files/Exception/locked.txt", manifest.MissingFiles);
    }

    [Fact]
    public async Task Cancellation_ShouldLeaveNoPartialFiles()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NewService().CreateAsync(JobRunTriggers.Manual, cancelled.Token));

        Assert.Empty(new BackupStore(Options.Create(settings), NullLogger<BackupStore>.Instance).List());
        AssertNoLeftovers();
    }

    [Fact]
    public async Task CancellationWhilePacking_ShouldDeleteThePartialZip()
    {
        using var cancel = new CancellationTokenSource();
        var service = NewService();
        service.EntryAdded = _ => cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CreateAsync(JobRunTriggers.Manual, cancel.Token));

        Assert.Empty(Directory.EnumerateFiles(settings.ExternalFileSystem.BackupPath, "backup-*"));
        AssertNoLeftovers();
    }

    [Fact]
    public async Task NotEnoughDiskSpace_ShouldFailBeforeWritingAnything()
    {
        var service = NewService();
        service.FreeSpaceProbe = _ => 1;

        var result = await service.CreateAsync(JobRunTriggers.Manual, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("可用空間不足", result.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(settings.ExternalFileSystem.BackupPath, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task LeftoversFromAnInterruptedRun_ShouldBeCleanedByTheNextBackup()
    {
        var backupRoot = settings.ExternalFileSystem.BackupPath;
        Directory.CreateDirectory(Path.Combine(backupRoot, BackupStore.WorkDirectoryName, "old-run"));
        File.WriteAllText(Path.Combine(backupRoot, BackupStore.WorkDirectoryName, "old-run", "BackendDB.db"), "half");
        File.WriteAllText(Path.Combine(backupRoot, "backup-20260101-000000.zip" + BackupStore.PartialExtension), "half");

        var result = await NewService().CreateAsync(JobRunTriggers.Manual, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        AssertNoLeftovers();
    }

    // ---------- 檔案清單 ----------

    [Theory]
    [InlineData("backup-20261004-020000.zip", true)]
    [InlineData("backup-20261004-020000-2.zip", true)]
    [InlineData("../backup-20261004-020000.zip", false)]
    [InlineData("..\\backup-20261004-020000.zip", false)]
    [InlineData("sub/backup-20261004-020000.zip", false)]
    [InlineData("backup-20261004-020000.zip.partial", false)]
    [InlineData("backup-20261004.zip", false)]
    [InlineData("BackendDB.db", false)]
    [InlineData("", false)]
    public void TryResolveFullPath_ShouldOnlyAcceptBackupFileNamesInTheRoot(string fileName, bool accepted)
    {
        var store = new BackupStore(Options.Create(settings), NullLogger<BackupStore>.Instance);
        var path = store.TryResolveFullPath(fileName);

        Assert.Equal(accepted, path is not null);
        if (path is not null)
        {
            Assert.Equal(Path.GetFullPath(settings.ExternalFileSystem.BackupPath), Path.GetDirectoryName(path));
        }
    }

    [Fact]
    public void Prune_ShouldKeepTheNewestBackups_IncludingSameSecondSequences()
    {
        var store = new BackupStore(Options.Create(settings), NullLogger<BackupStore>.Instance);
        var names = new[] { "backup-20261001-020000.zip", "backup-20261002-020000.zip", "backup-20261002-020000-2.zip", "backup-20261002-020000-10.zip", "backup-20261003-020000.zip" };
        Directory.CreateDirectory(settings.ExternalFileSystem.BackupPath);
        foreach (var name in names)
        {
            File.WriteAllText(Path.Combine(settings.ExternalFileSystem.BackupPath, name), name);
        }

        Assert.Empty(store.Prune(0));
        var deleted = store.Prune(3);

        // 同一秒的序號要以數字比較：-10 比 -2 新，-2 比沒有序號的新。
        Assert.Equal(new[] { "backup-20261002-020000.zip", "backup-20261001-020000.zip" }, deleted);
        Assert.Equal(new[] { "backup-20261003-020000.zip", "backup-20261002-020000-10.zip", "backup-20261002-020000-2.zip" }, store.List().Select(x => x.FileName));
    }

    [Fact]
    public void NewFileName_ShouldNotCollideWithinTheSameSecond()
    {
        var store = new BackupStore(Options.Create(settings), NullLogger<BackupStore>.Instance);
        Directory.CreateDirectory(settings.ExternalFileSystem.BackupPath);
        var at = new DateTime(2026, 10, 4, 2, 0, 0);
        File.WriteAllText(Path.Combine(settings.ExternalFileSystem.BackupPath, store.NewFileName(at)), "1");

        Assert.Equal("backup-20261004-020000-2.zip", store.NewFileName(at));
    }

    // ---------- 作業 ----------

    [Fact]
    public async Task Job_ShouldOnlyPruneAfterASuccessfulBackup()
    {
        var backupRoot = settings.ExternalFileSystem.BackupPath;
        Directory.CreateDirectory(backupRoot);
        foreach (var day in new[] { "01", "02", "03" })
        {
            File.WriteAllText(Path.Combine(backupRoot, $"backup-202610{day}-020000.zip"), "old");
        }

        var audit = new RecordingAudit();
        var failing = NewService(new BackupSettings { KeepCount = 1 });
        failing.FreeSpaceProbe = _ => 1;
        var failed = await NewJob(failing, new BackupSettings { KeepCount = 1 }, audit).ExecuteAsync(Context(), CancellationToken.None);

        Assert.False(failed.Succeeded);
        Assert.Equal(3, Directory.EnumerateFiles(backupRoot, "backup-*.zip").Count());
        Assert.Empty(audit.Actions);

        var succeeded = await NewJob(NewService(new BackupSettings { KeepCount = 1 }), new BackupSettings { KeepCount = 1 }, audit).ExecuteAsync(Context(), CancellationToken.None);

        Assert.True(succeeded.Succeeded, succeeded.Message);
        Assert.Single(Directory.EnumerateFiles(backupRoot, "backup-*.zip"));
        Assert.Equal(new[] { AuditActions.Backup.Create, AuditActions.Backup.AutoPurge }, audit.Actions);
    }

    // ---------- 備份目錄的位置 ----------

    public static TheoryData<string> OtherPathKeys =>
    [
        nameof(ExternalFileSystem.DatabasePath), nameof(ExternalFileSystem.DownloadPath), nameof(ExternalFileSystem.UploadPath),
        nameof(ExternalFileSystem.ProjectFilePath), nameof(ExternalFileSystem.ExceptionPath), nameof(ExternalFileSystem.TokenUsagePath),
        nameof(ExternalFileSystem.AiCallLogPath), nameof(ExternalFileSystem.AuditContentPath), nameof(ExternalFileSystem.DataProtectionKeyPath),
        nameof(ExternalFileSystem.AfterSalesFilePath),
    ];

    [Theory]
    [MemberData(nameof(OtherPathKeys))]
    public void BackupPath_InsideAnotherDataPath_ShouldFailValidation(string key)
    {
        var paths = ValidPaths();
        paths.BackupPath = Path.Combine(GetPath(paths, key).ToUpperInvariant(), "backup");
        AssertBackupPathInvalid(paths);
    }

    [Theory]
    [MemberData(nameof(OtherPathKeys))]
    public void BackupPath_ContainingOrEqualToAnotherDataPath_ShouldFailValidation(string key)
    {
        var paths = ValidPaths();
        paths.BackupPath = GetPath(paths, key) + Path.DirectorySeparatorChar;
        AssertBackupPathInvalid(paths);

        paths.BackupPath = Path.GetDirectoryName(GetPath(paths, key))!;
        AssertBackupPathInvalid(paths);
    }

    [Fact]
    public void BackupPath_NextToADataPathWithASharedPrefix_ShouldPass()
    {
        var paths = ValidPaths();
        paths.BackupPath = paths.DownloadPath + "Backup";
        var result = new SystemSettingsValidator().Validate(null, WithName(paths));
        Assert.True(result.Succeeded, string.Join(" | ", result.Failures ?? []));
    }

    [Fact]
    public void BackupPath_InsideTheSiteDirectory_ShouldFailValidation()
    {
        var paths = ValidPaths();
        paths.BackupPath = Path.Combine(AppContext.BaseDirectory, "backups");
        AssertBackupPathInvalid(paths);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("bytes=0-", true)]
    [InlineData("bytes=0-1023", true)]
    [InlineData("bytes=1024-", false)]
    public void Download_ShouldOnlyAuditTheFirstRange(string? range, bool expected)
        => Assert.Equal(expected, BackupController.IsFirstRange(range));

    // ---------- 測試工具 ----------

    private string Dir(string name)
    {
        var path = Path.Combine(root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private BackendDBContext NewContext()
        => new(new DbContextOptionsBuilder<BackendDBContext>().UseSqlite($"Data Source={databaseFile}").Options);

    private void InsertAuditRows(int count)
    {
        using var context = NewContext();
        for (var i = 0; i < count; i++)
        {
            context.AuditLog.Add(new AuditLog { Action = "Test.Row", OccurredAt = DateTime.UtcNow, Success = true });
        }

        context.SaveChanges();
    }

    private SystemBackupService NewService(BackupSettings? backup = null)
        => new(
            Options.Create(settings),
            new StaticOptionsMonitor<BackupSettings>(backup ?? new BackupSettings()),
            new FileDbContextFactory(databaseFile),
            new SystemIdentity(new StaticOptionsMonitor<SystemSettings>(settings)),
            new BackupStore(Options.Create(settings), NullLogger<BackupStore>.Instance),
            TimeProvider.System,
            NullLogger<SystemBackupService>.Instance);

    private SystemBackupJob NewJob(SystemBackupService service, BackupSettings backup, RecordingAudit audit)
        => new(service, new BackupStore(Options.Create(settings), NullLogger<BackupStore>.Instance), audit,
            new StaticOptionsMonitor<BackupSettings>(backup), NullLogger<SystemBackupJob>.Instance);

    private static ScheduledJobContext Context() => new(1, JobRunTriggers.Schedule, DateTime.UtcNow, null);

    private string Extract(string zipPath)
    {
        var target = Path.Combine(root, "extract-" + Guid.NewGuid().ToString("N"));
        ZipFile.ExtractToDirectory(zipPath, target);
        return target;
    }

    private static BackupManifest ReadManifest(string extracted)
        => JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(Path.Combine(extracted, "manifest.json")), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private static string? Scalar(string file, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={file};Pooling=False;Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar());
    }

    private void AssertNoLeftovers()
    {
        var backupRoot = settings.ExternalFileSystem.BackupPath;
        Assert.Empty(Directory.EnumerateFiles(backupRoot, "*" + BackupStore.PartialExtension));
        var work = Path.Combine(backupRoot, BackupStore.WorkDirectoryName);
        Assert.True(!Directory.Exists(work) || !Directory.EnumerateFileSystemEntries(work).Any(), "暫存目錄沒有清乾淨。");
    }

    private static ExternalFileSystem ValidPaths() => new()
    {
        DatabasePath = @"C:\data\app\DB",
        DownloadPath = @"C:\data\app\Download",
        UploadPath = @"C:\data\app\Upload",
        ProjectFilePath = @"C:\data\app\ProjectFile",
        AfterSalesFilePath = @"C:\data\app\AfterSalesFile",
        ExceptionPath = @"C:\data\app\Exception",
        TokenUsagePath = @"C:\data\app\TokenUsage",
        AiCallLogPath = @"C:\data\app\AiCallLog",
        AuditContentPath = @"C:\data\app\AuditContent",
        DataProtectionKeyPath = @"C:\data\app\Keys",
        BackupPath = @"D:\backup\app",
    };

    private static string GetPath(ExternalFileSystem paths, string key)
        => (string)typeof(ExternalFileSystem).GetProperty(key)!.GetValue(paths)!;

    private static SystemSettings WithName(ExternalFileSystem paths)
    {
        var value = new SystemSettings { ExternalFileSystem = paths };
        value.SystemInformation.SystemName = "測試";
        return value;
    }

    private static void AssertBackupPathInvalid(ExternalFileSystem paths)
    {
        var result = new SystemSettingsValidator().Validate(null, WithName(paths));
        Assert.True(result.Failed, $"BackupPath={paths.BackupPath} 應該被擋下。");
        Assert.Contains(result.Failures!, x => x.StartsWith("SystemSettings:ExternalFileSystem:BackupPath", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // 檔案控制代碼偶爾晚一點才釋放；暫存目錄留著無害。
        }
    }

    private sealed class FileDbContextFactory(string file) : IDbContextFactory<BackendDBContext>
    {
        public BackendDBContext CreateDbContext()
            => new(new DbContextOptionsBuilder<BackendDBContext>().UseSqlite($"Data Source={file}").Options);
    }

    private sealed class RecordingAudit : IAuditLogService
    {
        public List<string> Actions { get; } = [];

        public Task WriteAsync(string action, bool success = true, int? actorUserId = null, string? actorAccount = null,
            string? targetType = null, string? targetId = null, string? detail = null, string? content = null)
        {
            Actions.Add(action);
            return Task.CompletedTask;
        }
    }
}

/// <summary>備份下載端點：只收 Cookie、只限管理員（從資料庫確認）、支援續傳、稽核只記一筆、檔名不可穿越目錄。</summary>
[Collection(nameof(IntegrationHostCollection))]
public sealed class BackupDownloadTests : IClassFixture<ApiTestApplicationFactory>
{
    private readonly ApiTestApplicationFactory factory;

    public BackupDownloadTests(ApiTestApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task Download_ShouldRequireAnAdministrator_SupportRanges_AndAuditOnce()
    {
        var fileName = $"backup-20261004-{Random.Shared.Next(100000, 999999)}.zip";
        var backupRoot = factory.Services.GetRequiredService<IOptions<SystemSettings>>().Value.ExternalFileSystem.BackupPath;
        Directory.CreateDirectory(backupRoot);
        await File.WriteAllBytesAsync(Path.Combine(backupRoot, fileName), Enumerable.Range(0, 4096).Select(x => (byte)x).ToArray());
        var url = $"/api/backups/{fileName}/download";

        using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.NotEqual(HttpStatusCode.OK, (await anonymous.GetAsync(url)).StatusCode);

        var userCookie = await CookieForAsync(isAdmin: false);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(url, userCookie)).StatusCode);

        var adminCookie = await CookieForAsync(isAdmin: true);
        var full = await SendAsync(url, adminCookie);
        Assert.Equal(HttpStatusCode.OK, full.StatusCode);
        Assert.Equal("bytes", full.Headers.AcceptRanges.Single());
        Assert.Equal(4096, (await full.Content.ReadAsByteArrayAsync()).Length);

        var partial = await SendAsync(url, adminCookie, "bytes=1024-");
        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync("/api/backups/..%2Fappsettings.json/download", adminCookie)).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        Assert.Equal(1, await db.AuditLog.CountAsync(x => x.Action == AuditActions.Backup.Download && x.TargetId == fileName));
    }

    private async Task<HttpResponseMessage> SendAsync(string url, string cookie, string? range = null)
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", cookie);
        if (range is not null)
        {
            request.Headers.Add("Range", range);
        }

        return await client.SendAsync(request);
    }

    private async Task<string> CookieForAsync(bool isAdmin)
    {
        var account = $"backup-{Guid.NewGuid():N}";
        MyUser user;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
            user = new MyUser { Account = account, Name = account, Password = "x", Status = true, IsAdmin = isAdmin };
            db.MyUser.Add(user);
            await db.SaveChangesAsync();
        }

        var options = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(MagicObjectHelper.CookieScheme);
        // 與真正登入相同的 claims（含工作階段版本，0.9.103 起 Cookie 驗證器會比對）。
        var ticket = options.TicketDataFormat.Protect(new AuthenticationTicket(CookieClaims.Create(user), MagicObjectHelper.CookieScheme));
        return $"{options.Cookie.Name}={ticket}";
    }
}

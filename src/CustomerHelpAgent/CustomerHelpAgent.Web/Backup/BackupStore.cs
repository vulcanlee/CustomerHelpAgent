using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.Models.Systems;

namespace CustomerHelpAgent.Web.Backup;

/// <summary>一份備份檔的摘要（管理頁清單用）。</summary>
public sealed record BackupFileInfo(string FileName, long SizeBytes, DateTime CreatedAtLocal, BackupManifest? Manifest);

/// <summary>
/// 備份目錄裡的 zip 檔（0.9.99 起）：列出、解析路徑、讀 manifest、刪除、依份數清除。
///
/// ⚠️ 只認 <see cref="ExternalFileSystem.BackupPath"/> 根目錄下、檔名符合 <c>backup-yyyyMMdd-HHmmss[-n].zip</c> 的檔案；
/// 子目錄（<c>.work</c>）與 <c>*.partial</c> 一律不認。下載與刪除都先經 <see cref="TryResolveFullPath"/>，
/// 檔名帶 <c>..</c>、路徑分隔字元或其他副檔名一律拒絕（比照 <c>ProjectFileStore</c> 的根目錄檢查）。
/// 備份清單只存在檔案系統：放在資料庫的話，還原時清單會跟著倒回去。
/// </summary>
public sealed partial class BackupStore
{
    public const string WorkDirectoryName = ".work";
    public const string PartialExtension = ".partial";
    public const string ManifestEntryName = "manifest.json";

    private readonly IOptions<SystemSettings> systemSettings;
    private readonly ILogger<BackupStore> logger;

    public BackupStore(IOptions<SystemSettings> systemSettings, ILogger<BackupStore> logger)
    {
        this.systemSettings = systemSettings;
        this.logger = logger;
    }

    public string Root => systemSettings.Value.ExternalFileSystem.BackupPath;

    [GeneratedRegex(@"^backup-(?<stamp>\d{8}-\d{6})(?:-(?<seq>\d{1,4}))?\.zip$", RegexOptions.CultureInvariant)]
    private static partial Regex FileNamePattern();

    /// <summary>依本地時間產生新的檔名；同一秒已有檔案時加上 <c>-2</c>、<c>-3</c>…。</summary>
    public string NewFileName(DateTime createdAtLocal)
    {
        var stamp = createdAtLocal.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var name = $"backup-{stamp}.zip";
        for (var seq = 2; File.Exists(Path.Combine(Root, name)) || File.Exists(Path.Combine(Root, name + PartialExtension)); seq++)
        {
            name = $"backup-{stamp}-{seq}.zip";
        }

        return name;
    }

    /// <summary>檔名合法且在備份根目錄之下時回傳完整路徑（不檢查檔案是否存在）；否則回 null。</summary>
    public string? TryResolveFullPath(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(Root) || !FileNamePattern().IsMatch(fileName))
        {
            return null;
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Root));
        var fullPath = Path.GetFullPath(Path.Combine(root, fileName));
        return string.Equals(Path.GetDirectoryName(fullPath), root, StringComparison.OrdinalIgnoreCase) ? fullPath : null;
    }

    /// <summary>新到舊。</summary>
    public IReadOnlyList<BackupFileInfo> List()
    {
        if (string.IsNullOrWhiteSpace(Root) || !Directory.Exists(Root))
        {
            return [];
        }

        return Directory.EnumerateFiles(Root, "backup-*.zip", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(x => x is not null && FileNamePattern().IsMatch(x))
            .Select(x => x!)
            .OrderByDescending(SortKey)
            .Select(name =>
            {
                var info = new FileInfo(Path.Combine(Root, name));
                return new BackupFileInfo(name, info.Length, ParseCreatedAt(name) ?? info.LastWriteTime, ReadManifest(name));
            })
            .ToList();
    }

    public BackupManifest? ReadManifest(string fileName)
    {
        var path = TryResolveFullPath(fileName);
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        try
        {
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.GetEntry(ManifestEntryName);
            if (entry is null)
            {
                return null;
            }

            using var stream = entry.Open();
            return JsonSerializer.Deserialize<BackupManifest>(stream, BackupManifest.JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Backup manifest could not be read. FileName={FileName}", fileName);
            return null;
        }
    }

    /// <summary>刪除一份備份；檔名不合法或檔案不存在回 false。</summary>
    public bool Delete(string fileName)
    {
        var path = TryResolveFullPath(fileName);
        if (path is null || !File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    /// <summary>只保留最新 <paramref name="keepCount"/> 份，回傳被刪的檔名；0 表示不刪。</summary>
    public IReadOnlyList<string> Prune(int keepCount)
    {
        if (keepCount <= 0)
        {
            return [];
        }

        var deleted = new List<string>();
        foreach (var old in List().Skip(keepCount))
        {
            try
            {
                if (Delete(old.FileName))
                {
                    deleted.Add(old.FileName);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 下載中被鎖住之類：留到下一次。
                logger.LogWarning(ex, "Old backup could not be deleted. FileName={FileName}", old.FileName);
            }
        }

        return deleted;
    }

    /// <summary>清掉上一次中斷留下的暫存（<c>.work</c> 與 <c>*.partial</c>）。只能在持有備份作業的鎖時呼叫。</summary>
    public void CleanLeftovers()
    {
        if (string.IsNullOrWhiteSpace(Root) || !Directory.Exists(Root))
        {
            return;
        }

        var work = Path.Combine(Root, WorkDirectoryName);
        if (Directory.Exists(work))
        {
            Directory.Delete(work, recursive: true);
        }

        foreach (var partial in Directory.EnumerateFiles(Root, "*" + PartialExtension, SearchOption.TopDirectoryOnly))
        {
            File.Delete(partial);
        }
    }

    /// <summary>
    /// 排序鍵：時間戳＋同一秒的序號。不能直接比字串：「<c>-2.zip</c>」的 <c>-</c> 排在「<c>.zip</c>」的 <c>.</c> 前面，較新的反而排到後面。
    /// </summary>
    internal static (string Stamp, int Seq) SortKey(string fileName)
    {
        var match = FileNamePattern().Match(fileName);
        return (match.Groups["stamp"].Value, match.Groups["seq"].Success ? int.Parse(match.Groups["seq"].Value, CultureInfo.InvariantCulture) : 1);
    }

    private static DateTime? ParseCreatedAt(string fileName)
        => DateTime.TryParseExact(SortKey(fileName).Stamp, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            ? value
            : null;
}

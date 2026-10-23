using System.Text.Json;

namespace CustomerHelpAgent.Web.Backup;

/// <summary>
/// 備份 zip 裡的 <c>manifest.json</c>（0.9.99 起）。還原腳本 <c>scripts/Restore-Backup.ps1</c> 讀它決定要放回哪些資料夾、
/// 核對資料庫雜湊與版本；欄位名稱改了要同步改腳本。
/// </summary>
public sealed class BackupManifest
{
    public const int CurrentFormatVersion = 1;

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public int FormatVersion { get; set; } = CurrentFormatVersion;

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>備份當時的 <c>SystemVersion</c>（例如 <c>0.9.99 (2026/10/04)</c>）。</summary>
    public string SystemVersion { get; set; } = string.Empty;

    /// <summary>資料庫最後一筆 migration；還原到較舊的程式版本時，網站不認得較新的 migration。</summary>
    public string? LastMigration { get; set; }

    public int MigrationCount { get; set; }

    /// <summary>zip 裡的資料庫檔路徑。</summary>
    public string DatabaseEntry { get; set; } = "db/BackendDB.db";

    public string DatabaseSha256 { get; set; } = string.Empty;

    /// <summary>備份了哪些資料夾：zip 裡的資料夾 → <c>ExternalFileSystem</c> 的設定鍵名（不記機器上的實際路徑）。</summary>
    public List<BackupManifestFolder> Folders { get; set; } = [];

    /// <summary>打包途中消失的檔案（例如同時被清理作業刪掉），最多記 50 筆。</summary>
    public List<string> MissingFiles { get; set; } = [];

    public int MissingFileCount { get; set; }

    /// <summary><c>Schedule</c>／<c>CatchUp</c>／<c>Manual</c>。</summary>
    public string Trigger { get; set; } = string.Empty;
}

public sealed class BackupManifestFolder
{
    /// <summary>zip 裡的資料夾，例如 <c>files/ProjectFile</c>。</summary>
    public string Entry { get; set; } = string.Empty;

    /// <summary><c>ExternalFileSystem</c> 的設定鍵名，例如 <c>ProjectFilePath</c>。</summary>
    public string SettingKey { get; set; } = string.Empty;

    public int FileCount { get; set; }
}

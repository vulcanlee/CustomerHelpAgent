using Microsoft.Extensions.Options;
using CustomerHelpAgent.Models.Systems;

namespace CustomerHelpAgent.Web.Configuration.Validation;

/// <summary>
/// <c>SystemSettings</c>：外部目錄的路徑與系統名稱。
///
/// 路徑留空時沒有任何錯誤，而是靜默地用錯地方：<c>DatabasePath</c> 空白 → 資料庫建在工作目錄；
/// <c>ExceptionPath</c>／<c>TokenUsagePath</c>／<c>AiCallLogPath</c> 空白 → 檔案存放器直接跳過寫入。
/// 相對路徑則會隨工作目錄漂移（IIS 的工作目錄不是網站目錄），所以一律要求完整路徑。
/// </summary>
public sealed class SystemSettingsValidator : IValidateOptions<SystemSettings>
{
    public ValidateOptionsResult Validate(string? name, SystemSettings options)
    {
        var errors = new OptionsErrors();
        var paths = options.ExternalFileSystem;

        RequirePath(errors, nameof(paths.DatabasePath), paths.DatabasePath);
        RequirePath(errors, nameof(paths.DownloadPath), paths.DownloadPath);
        RequirePath(errors, nameof(paths.UploadPath), paths.UploadPath);
        RequirePath(errors, nameof(paths.ProjectFilePath), paths.ProjectFilePath);
        RequirePath(errors, nameof(paths.ExceptionPath), paths.ExceptionPath);
        RequirePath(errors, nameof(paths.TokenUsagePath), paths.TokenUsagePath);
        RequirePath(errors, nameof(paths.AiCallLogPath), paths.AiCallLogPath);
        RequirePath(errors, nameof(paths.AuditContentPath), paths.AuditContentPath);
        RequirePath(errors, nameof(paths.DataProtectionKeyPath), paths.DataProtectionKeyPath);
        RequirePath(errors, nameof(paths.BackupPath), paths.BackupPath);
        RequireBackupPathIsolated(errors, paths);

        errors.RequireText(
            "SystemSettings:SystemInformation:SystemName",
            options.SystemInformation.SystemName,
            "它是瀏覽器分頁標題與側邊欄的系統名稱。");

        return errors.ToResult();
    }

    /// <summary>
    /// 備份目錄不可與其他資料目錄重疊（相等、位於其下、或包含它們）：備份含全部資料與金鑰環，放進下載目錄就等於公開，
    /// 包含資料目錄則會把備份本身也打包進下一份備份。也不可在網站目錄底下（重新部署會被覆蓋或刪除）。
    /// </summary>
    private static void RequireBackupPathIsolated(OptionsErrors errors, ExternalFileSystem paths)
    {
        const string key = "SystemSettings:ExternalFileSystem:BackupPath";
        if (!Path.IsPathFullyQualified(paths.BackupPath))
        {
            return;
        }

        var backup = Normalize(paths.BackupPath);
        var others = new (string Name, string Value)[]
        {
            (nameof(paths.DatabasePath), paths.DatabasePath),
            (nameof(paths.DownloadPath), paths.DownloadPath),
            (nameof(paths.UploadPath), paths.UploadPath),
            (nameof(paths.ProjectFilePath), paths.ProjectFilePath),
            (nameof(paths.ExceptionPath), paths.ExceptionPath),
            (nameof(paths.TokenUsagePath), paths.TokenUsagePath),
            (nameof(paths.AiCallLogPath), paths.AiCallLogPath),
            (nameof(paths.AuditContentPath), paths.AuditContentPath),
            (nameof(paths.DataProtectionKeyPath), paths.DataProtectionKeyPath),
        };

        foreach (var (name, value) in others.Where(x => Path.IsPathFullyQualified(x.Value)))
        {
            var other = Normalize(value);
            if (IsSameOrInside(backup, other) || IsSameOrInside(other, backup))
            {
                errors.Add(key, $"不可與 {name} 重疊（相同、位於其下或包含它）。備份含全部資料與金鑰環，請放在獨立的目錄。");
            }
        }

        if (IsSameOrInside(backup, Normalize(AppContext.BaseDirectory)))
        {
            errors.Add(key, "不可放在網站目錄底下（重新部署會覆蓋或刪除，也可能被當成網站內容提供）。");
        }
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary><paramref name="path"/> 等於 <paramref name="root"/> 或位於其下（以分隔字元判斷，<c>C:\a\Backup2</c> 不算在 <c>C:\a\Backup</c> 底下）。</summary>
    private static bool IsSameOrInside(string path, string root)
        => string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void RequirePath(OptionsErrors errors, string field, string value)
    {
        var key = $"SystemSettings:ExternalFileSystem:{field}";
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add(key, "不可留空。請填寫完整路徑，例如 C:\\temp\\CustomerHelpAgent\\DB。");
        }
        else if (!Path.IsPathFullyQualified(value))
        {
            errors.Add(key, $"必須是完整路徑（含磁碟機代號或 UNC），目前是「{value}」。相對路徑會隨工作目錄改變，IIS 下尤其容易寫錯地方。");
        }
    }
}

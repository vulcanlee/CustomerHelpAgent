using System.ComponentModel.DataAnnotations;

namespace CustomerHelpAgent.Web.Configuration;

/// <summary>
/// 系統備份（0.9.99 起）。備份目錄在 <c>SystemSettings:ExternalFileSystem:BackupPath</c>，執行時間在 <c>ScheduledJobSettings:Jobs:SystemBackup</c>。
/// 兩個欄位都可在「系統參數」頁覆寫。
/// </summary>
public class BackupSettings
{
    public const string SectionName = "BackupSettings";

    /// <summary>保留最新幾份備份；0＝不自動刪除舊備份。只在備份成功之後才刪（失敗不可輪掉好的備份）。</summary>
    [Range(0, 365)]
    public int KeepCount { get; set; } = 7;

    /// <summary>
    /// 是否一併備份 AI 對話紀錄的內容檔。預設不備份：內容含日誌與例外堆疊、本來就只保留 90 天、檔案又大。
    /// </summary>
    public bool IncludeAiCallLogs { get; set; }
}

using CustomerHelpAgent.Models.Systems;

namespace CustomerHelpAgent.Business.Services.DataAccess;

/// <summary>
/// AI 對話紀錄的記錄端點（0.9.72 起）。
///
/// 新增 AI 呼叫點時與 <see cref="ITokenUsageRecorder"/> 一起使用：記錄點放在發出 HTTP 的那一層，
/// 以同一個 CallId 寫兩邊。刻意與 <see cref="AiCallLogService"/> 的查詢／刪除方法分開，
/// 呼叫端只需要「記一筆」，測試也只要假造這一個介面。
/// </summary>
public interface IAiCallLogRecorder
{
    /// <summary>
    /// 目前是否記錄（<c>AiCallLogSettings:Enabled</c>，支援設定重載）。
    /// 頁面上方的說明橫幅依此顯示「保留 N 天」或「已停用記錄」。
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// 記錄一次已送出的 AI 呼叫。未啟用時什麼都不做。
    /// 實作保證<b>絕不拋出</b>：記錄對話不可以讓呼叫端的主流程失敗。
    /// </summary>
    Task RecordAsync(AiCallLogEntry entry);
}

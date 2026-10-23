using CustomerHelpAgent.Web.Diagnostics;

namespace CustomerHelpAgent.Web.Ai;

/// <summary>把日誌查詢結果送去 AI 整理。</summary>
public interface IAiLogAnalysisService
{
    /// <summary>設定是否完整可用。日誌檢視頁的 AI 分析按鈕依此決定要不要停用。</summary>
    bool IsAvailable { get; }

    /// <summary>設定不完整時的中文原因（可直接當 Tooltip）；可用時為空字串。</summary>
    string UnavailableReason { get; }

    /// <summary>
    /// 分析日誌。
    /// ⚠️ 這個方法<b>不丟例外</b>，所有失敗都以 <see cref="AiAnalysisResult"/> 回報，
    /// 呼叫端不需要為了上游錯誤包 try-catch（仍應為非預期錯誤保留外層防護）。
    /// </summary>
    /// <param name="entriesAscending">查詢結果，時間正序。</param>
    Task<AiAnalysisResult> AnalyzeAsync(
        IReadOnlyList<LogEntry> entriesAscending,
        CancellationToken cancellationToken = default);
}

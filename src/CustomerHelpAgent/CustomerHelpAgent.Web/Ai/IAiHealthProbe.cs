namespace CustomerHelpAgent.Web.Ai;

/// <summary>
/// 系統健康監控頁用的 LLM 連線探測：送一句 hello，確認 API 真的有回應。
///
/// 刻意不重用 <see cref="IAiLogAnalysisService"/>：那支需要 <c>IReadOnlyList&lt;LogEntry&gt;</c>，
/// 空清單會直接回 NoData，硬餵假日誌既繞路又會把帳記成「AI 日誌分析」，
/// 汙染 Token 用量頁的作業分類。
/// </summary>
public interface IAiHealthProbe
{
    Task<AiHealthProbeResult> ProbeAsync(CancellationToken cancellationToken = default);
}

/// <summary>探測結果。失敗一律用此型別表達，不丟例外 —— 健康檢查不能被單一項目炸掉。</summary>
public sealed record AiHealthProbeResult
{
    /// <summary>AI 設定是否完整。false 時代表沒有發出任何請求。</summary>
    public bool IsConfigured { get; init; }

    public bool Success { get; init; }

    /// <summary>設定不完整的中文原因，或呼叫失敗的簡述。成功時為空字串。</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>實際使用的模型名稱（優先取回應的 model，空的才退回設定值）。</summary>
    public string ModelName { get; init; } = string.Empty;

    /// <summary>端點主機名（不含路徑與查詢字串）。未設定時為空字串。</summary>
    public string EndpointHost { get; init; } = string.Empty;

    public long ElapsedMilliseconds { get; init; }

    public static AiHealthProbeResult NotConfigured(string message)
        => new() { IsConfigured = false, Success = false, Message = message };
}

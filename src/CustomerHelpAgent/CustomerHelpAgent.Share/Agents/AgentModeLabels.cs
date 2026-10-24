namespace CustomerHelpAgent.Share.Agents;

/// <summary>
/// 售後 Agent 會話模式（M5）的中文標籤與線上客服網址參數（<c>/shop/assistant?mode=handoff</c>；展示控制台產生、線上客服頁解析，
/// 回放提示與錄製清單共用標籤），避免各寫一份而漂移。
/// </summary>
public static class AgentModeLabels
{
    /// <summary>網址參數名稱：<c>?mode=</c>。</summary>
    public const string QueryName = "mode";

    /// <summary>模式名稱：「單一 Agent」或「多 Agent」。</summary>
    public static string Text(AgentMode mode) => mode switch
    {
        AgentMode.Single => "單一 Agent",
        AgentMode.Handoff => "多 Agent",
        _ => mode.ToString(),
    };

    /// <summary>網址參數值：<c>single</c> 或 <c>handoff</c>。</summary>
    public static string QueryValue(AgentMode mode) => mode == AgentMode.Handoff ? "handoff" : "single";

    /// <summary>解析網址參數值（不分大小寫）；空白或不認得的值回 null（頁面照原本的行為）。</summary>
    public static AgentMode? FromQuery(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "handoff" => AgentMode.Handoff,
        "single" => AgentMode.Single,
        _ => null,
    };
}

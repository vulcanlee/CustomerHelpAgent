namespace CustomerHelpAgent.Web.Configuration;

/// <summary>
/// 售後 Agent 的迴圈出口（<c>AfterSalesAgentSettings</c> 區段，1.0.4 起）。範圍由 <c>AfterSalesAgentSettingsValidator</c> 在啟動時檢查。
/// </summary>
public class AfterSalesAgentSettings
{
    public const string SectionName = "AfterSalesAgentSettings";

    /// <summary>一輪最多幾次「模型回應 → 執行工具」往返（<c>FunctionInvokingChatClient.MaximumIterationsPerRequest</c>，1–20）。</summary>
    public int MaxSteps { get; set; } = 8;

    /// <summary>工具連續失敗（查無、無規則、衝突、不合法或拋例外）幾次就停下（1–5）。</summary>
    public int MaxConsecutiveToolFailures { get; set; } = 2;

    /// <summary>每次工具呼叫（含身分與權限檢查）的逾時秒數（1–120）；送出申請本身不受此限。</summary>
    public int ToolTimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// 呼叫模型的 API 形式：<c>Responses</c>（預設，<c>/v1/responses</c>）或 <c>ChatCompletions</c>（<c>/v1/chat/completions</c>）。
    ///
    /// ⚠️ 預設 Responses 是實測結果：推論模型（例如 gpt-6.1-sol）在 Chat Completions 上「帶函式工具」時只接受
    /// <c>reasoning_effort = none</c>，而這個模型又不支援 none —— 工具呼叫在 Chat Completions 上完全走不通。
    /// Responses 一律送 <c>store=false</c>：不使用服務端儲存的對話，會話歷史仍由 MAF <c>AgentSession</c> 保存。
    /// </summary>
    public string ApiStyle { get; set; } = nameof(AgentApiStyle.Responses);

    /// <summary>
    /// 推理強度（Responses 送 <c>reasoning.effort</c>、Chat Completions 送 <c>reasoning_effort</c>）。
    /// 空字串（預設）＝不送，由模型決定；可填 <c>none</c>、<c>minimal</c>、<c>low</c>、<c>medium</c>、<c>high</c>。
    /// 各模型支援的值不同（送了不支援的值會回 400），請依部署的模型填。
    /// </summary>
    public string ReasoningEffort { get; set; } = string.Empty;

    /// <summary>
    /// 取樣溫度（0–2）。<c>null</c>（預設）＝不送：推論模型不接受 temperature，送了會回 400。
    /// 非推論模型要更穩定的回覆時再填（例如 0.2）。
    /// </summary>
    public double? Temperature { get; set; }

    /// <summary>
    /// AI 初審照片節點（M4）用的模型，必須能看圖（Azure 為部署名稱）。空字串（預設）＝沿用 <c>AiSettings:Model</c>。
    /// 端點與金鑰沿用 <c>AiSettings</c>，所以只能填同一個資源下的部署；其他初審節點一律用 <c>AiSettings:Model</c>。
    /// </summary>
    public string VisionModel { get; set; } = string.Empty;

    /// <summary>
    /// 照片節點要改用的模型（唯一的解析點）：<see cref="VisionModel"/> 有填回它，空字串回 null（＝沿用 <c>AiSettings:Model</c>）。
    /// 前後空白已由 <c>AfterSalesAgentSettingsValidator</c> 在啟動時擋下，這裡不再修剪。
    /// </summary>
    public string? VisionModelOverride => string.IsNullOrEmpty(VisionModel) ? null : VisionModel;

    /// <summary>把 <see cref="ApiStyle"/> 解析成列舉（值已由 <c>AfterSalesAgentSettingsValidator</c> 在啟動時檢查）。</summary>
    public AgentApiStyle GetApiStyle() => Enum.Parse<AgentApiStyle>(ApiStyle, ignoreCase: true);
}

/// <summary>Agent 呼叫模型的 API 形式（見 <see cref="AfterSalesAgentSettings.ApiStyle"/>）。</summary>
public enum AgentApiStyle
{
    ChatCompletions,
    Responses,
}

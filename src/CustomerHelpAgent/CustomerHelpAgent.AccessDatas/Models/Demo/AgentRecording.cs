using CustomerHelpAgent.Share.Agents;

namespace CustomerHelpAgent.AccessDatas.Models;

/// <summary>
/// 一段售後 Agent 會話的錄製（1.0.5 起）：示範模式開啟且為即時模式時自動建立，供回放（不呼叫真實模型）重播。
/// 內容只有顧客動作（<see cref="AgentRecordedTurn"/>）與模型請求摘要／回應（<see cref="AgentRecordedCall"/>），
/// 不存系統指示、收件人、地址、手機。一鍵重置示範資料<b>不刪</b>錄製。
/// </summary>
public class AgentRecording
{
    public int Id { get; set; }

    /// <summary>錄製的會話（<see cref="AgentSessionRecord.SessionKey"/>，唯一）；刻意不設外鍵，重置刪掉會話後錄製仍在。</summary>
    public Guid SessionKey { get; set; }

    /// <summary>錄製時的顧客 Id；刻意不設外鍵。</summary>
    public int CustomerUserId { get; set; }

    /// <summary>
    /// 錄製時會話的模式（M5）：建立錄製時從 <see cref="AgentSessionRecord.Mode"/> 記下，之後不變。回放必須以同一模式開新對話，
    /// Runner 才會重建相同的 Agent 結構（單一 Agent 或多 Agent 交接）。M5 之前的錄製一律是 <see cref="AgentMode.Single"/>。
    /// </summary>
    public AgentMode Mode { get; set; } = AgentMode.Single;

    /// <summary>第一句顧客訊息的前 20 字。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>是否為 <see cref="ScenarioCode"/> 劇本的金劇本（同一劇本只能有一個，部分唯一索引守門）。</summary>
    public bool IsGolden { get; set; }

    /// <summary>設為金劇本時指定的劇本代碼；被取代後保留代碼、只清掉 <see cref="IsGolden"/>。</summary>
    public string? ScenarioCode { get; set; }

    /// <summary>模型名稱：第一筆回應帶的模型，沒有就用設定的模型（<c>AiSettings:Model</c>）。</summary>
    public string ModelName { get; set; } = string.Empty;

    /// <summary>
    /// 這段會話的估算費用（台幣）：每記一筆模型呼叫就以 AI 對話紀錄（<c>ConversationId</c>＝<see cref="SessionKey"/>）
    /// 對到 Token 用量的 <c>CostTwd</c> 重新加總後存下（AI 對話紀錄過期後仍保留）。沒有任何已定價的用量時為 null。
    /// </summary>
    public decimal? TotalCostTwd { get; set; }

    public DateTime CreatedAt { get; set; }

    public List<AgentRecordedTurn> Turns { get; set; } = [];

    public List<AgentRecordedCall> Calls { get; set; } = [];
}

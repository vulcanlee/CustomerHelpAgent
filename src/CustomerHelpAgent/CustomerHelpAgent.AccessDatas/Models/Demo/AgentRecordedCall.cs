namespace CustomerHelpAgent.AccessDatas.Models;

/// <summary>
/// 錄製中的一次模型呼叫：請求只存摘要（<see cref="RequestDigest"/>，不存請求原文），回應存完整的 <c>ChatResponse</c> JSON。
/// 回應是模型輸出（文字與函式呼叫），不含工具結果，也就不含收件人、地址、手機。
/// </summary>
public class AgentRecordedCall
{
    public int Id { get; set; }
    public int AgentRecordingId { get; set; }
    public AgentRecording? AgentRecording { get; set; }

    /// <summary>同一段錄製內的順序（由 1 起遞增），回放依此順序取用。</summary>
    public int Sequence { get; set; }

    /// <summary>請求摘要（SHA-256 小寫十六進位，見 <c>ModelRequestDigest</c>）：只涵蓋顧客文字與工具結果，日期與申請編號已正規化。</summary>
    public string RequestDigest { get; set; } = string.Empty;

    /// <summary>以 <c>AIJsonUtilities.DefaultOptions</c> 序列化的 <c>ChatResponse</c>（串流呼叫為完成後合併的結果）。</summary>
    public string ResponseJson { get; set; } = string.Empty;

    /// <summary>回應的用量（<c>UsageDetails</c> JSON）；沒有回報時為 null。</summary>
    public string? UsageJson { get; set; }

    public DateTime CreatedAt { get; set; }
}

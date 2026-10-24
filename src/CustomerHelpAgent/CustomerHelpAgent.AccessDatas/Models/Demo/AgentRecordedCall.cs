using CustomerHelpAgent.Share.Demo;

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

    /// <summary>
    /// 呼叫來源（M4 起，<see cref="AgentRecordingStreams"/>）：顧客端 Agent 為 <c>customer</c>（M4 以前的錄製一律是 customer），
    /// AI 初審的模型節點為 <c>prereview:photo</c>／<c>prereview:history</c>／<c>prereview:summary</c>。
    /// 併行的初審節點各自一條 stream、各自編號，回放時各取各的，不會互相搶順序。
    /// </summary>
    public string Stream { get; set; } = AgentRecordingStreams.Customer;

    /// <summary>同一段錄製、同一條 <see cref="Stream"/> 內的順序（由 1 起遞增），回放依此順序取用。</summary>
    public int Sequence { get; set; }

    /// <summary>請求摘要（SHA-256 小寫十六進位，見 <c>ModelRequestDigest</c>）：只涵蓋顧客文字與工具結果，日期與申請編號已正規化。</summary>
    public string RequestDigest { get; set; } = string.Empty;

    /// <summary>以 <c>AIJsonUtilities.DefaultOptions</c> 序列化的 <c>ChatResponse</c>（串流呼叫為完成後合併的結果）。</summary>
    public string ResponseJson { get; set; } = string.Empty;

    /// <summary>回應的用量（<c>UsageDetails</c> JSON）；沒有回報時為 null。</summary>
    public string? UsageJson { get; set; }

    public DateTime CreatedAt { get; set; }
}

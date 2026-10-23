using CustomerHelpAgent.Share.Agents;

namespace CustomerHelpAgent.AccessDatas.Models;

/// <summary>售後 Agent 的一輪執行（顧客送出一句話、確認或上傳照片後到停下來為止）。</summary>
public class AgentRunTrace
{
    public int Id { get; set; }

    /// <summary>所屬會話（<see cref="AgentSessionRecord.SessionKey"/>）；刻意不設外鍵，會話關閉或清除不影響執行紀錄。</summary>
    public Guid SessionKey { get; set; }

    /// <summary>顧客 Id；刻意不設外鍵，顧客姓名以關掉軟刪除過濾器的明確查詢取得。</summary>
    public int CustomerUserId { get; set; }

    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public AgentStopReason? StopReason { get; set; }
    public int ModelCallCount { get; set; }
    public int ToolCallCount { get; set; }

    /// <summary>
    /// 回放（1.0.5 起）：這一輪用哪一份錄製（<see cref="AgentRecording.Id"/>）重播模型回應、沒有呼叫模型；即時執行為 null。
    /// 刻意不設外鍵：錄製刪除後仍看得出這一輪是回放。
    /// </summary>
    public int? ReplayRecordingId { get; set; }

    public List<AgentRunStep> Steps { get; set; } = [];
}

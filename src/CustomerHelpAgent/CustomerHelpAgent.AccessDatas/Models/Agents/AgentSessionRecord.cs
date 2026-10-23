using CustomerHelpAgent.Share.Agents;

namespace CustomerHelpAgent.AccessDatas.Models;

/// <summary>售後 Agent 的會話（MAF <c>AgentSession</c> 序列化後存在 <see cref="StateJson"/>，換頁或重新整理後可續聊）。</summary>
public class AgentSessionRecord
{
    public int Id { get; set; }

    /// <summary>會話識別碼（唯一）；同時作為售後申請與流程埋點的 <c>CaseKey</c>。</summary>
    public Guid SessionKey { get; set; }

    /// <summary>會話擁有者。刻意不設外鍵：只用來比對「是不是本人的會話」，不經導覽屬性查詢。</summary>
    public int CustomerUserId { get; set; }

    public string StateJson { get; set; } = string.Empty;
    public bool IsClosed { get; set; }
    public AgentStopReason? LastStopReason { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

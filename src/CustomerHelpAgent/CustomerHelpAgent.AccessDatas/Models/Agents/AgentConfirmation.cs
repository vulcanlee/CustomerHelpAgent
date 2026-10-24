using System.ComponentModel.DataAnnotations;

namespace CustomerHelpAgent.AccessDatas.Models;

/// <summary>
/// 寫入動作的確認關卡（MAF 的核准請求）：顯示給顧客的確認卡與顧客的決定。
/// <see cref="SessionKey"/>、<see cref="CustomerUserId"/>、<see cref="AfterSalesRequestId"/> 刻意不設外鍵，只作比對與回查。
/// </summary>
public class AgentConfirmation
{
    public int Id { get; set; }
    public Guid SessionKey { get; set; }
    public int CustomerUserId { get; set; }

    /// <summary>MAF <c>ToolApprovalRequestContent.RequestId</c>。</summary>
    [MaxLength(128)]
    public string ApprovalRequestId { get; set; } = string.Empty;

    [MaxLength(64)]
    public string ToolName { get; set; } = string.Empty;

    public string ArgumentsJson { get; set; } = string.Empty;
    public string CardJson { get; set; } = string.Empty;

    /// <summary>
    /// 多 Agent 模式（M5）的外部核准請求原文（<c>ToolApprovalRequestContent</c> 以 <c>AIJsonUtilities.DefaultOptions</c> 序列化，含原始呼叫的 CallId）：
    /// 顧客按下確認或取消時（含應用程式重啟後）以它 <c>CreateResponse</c>。單一模式為 null（原始請求從還原的會話歷史找）。
    /// </summary>
    public string? ApprovalRequestJson { get; set; }

    /// <summary>null＝尚未決定。</summary>
    public bool? Approved { get; set; }

    public DateTime? DecidedAt { get; set; }
    public int? AfterSalesRequestId { get; set; }
    public DateTime CreatedAt { get; set; }
}

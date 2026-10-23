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

    /// <summary>null＝尚未決定。</summary>
    public bool? Approved { get; set; }

    public DateTime? DecidedAt { get; set; }
    public int? AfterSalesRequestId { get; set; }
    public DateTime CreatedAt { get; set; }
}

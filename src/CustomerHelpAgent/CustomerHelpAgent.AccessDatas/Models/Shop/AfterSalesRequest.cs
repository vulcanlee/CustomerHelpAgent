using CustomerHelpAgent.Share.Shop;

namespace CustomerHelpAgent.AccessDatas.Models;

/// <summary>售後申請。</summary>
public class AfterSalesRequest
{
    public int Id { get; set; }

    /// <summary>申請編號，格式 <c>AS{yyyyMMdd}{Id:D4}</c>。</summary>
    public string RequestNo { get; set; } = string.Empty;

    public Guid CaseKey { get; set; }
    public int CustomerUserId { get; set; }
    public MyUser? CustomerUser { get; set; }
    public int SalesOrderItemId { get; set; }
    public SalesOrderItem? SalesOrderItem { get; set; }
    public AfterSalesType Type { get; set; }
    public AfterSalesStatus Status { get; set; }
    public AfterSalesChannel Channel { get; set; }
    public string? TargetSize { get; set; }
    public string? TargetColor { get; set; }
    public bool? IsWorn { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? StaffNote { get; set; }
    public int? AppliedPolicyId { get; set; }
    public AfterSalesPolicy? AppliedPolicy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DecidedAt { get; set; }
    public int? DecidedByUserId { get; set; }

    public List<AfterSalesAttachment> Attachments { get; set; } = [];
}

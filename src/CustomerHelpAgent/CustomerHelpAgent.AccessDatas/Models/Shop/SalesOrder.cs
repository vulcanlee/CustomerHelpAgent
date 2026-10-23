namespace CustomerHelpAgent.AccessDatas.Models;

/// <summary>銷售訂單。</summary>
public class SalesOrder
{
    public int Id { get; set; }
    public string OrderNo { get; set; } = string.Empty;
    public int CustomerUserId { get; set; }
    public MyUser? CustomerUser { get; set; }
    public DateTime OrderedAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public string RecipientName { get; set; } = string.Empty;
    public string ShippingAddress { get; set; } = string.Empty;
    public string RecipientPhone { get; set; } = string.Empty;

    public List<SalesOrderItem> Items { get; set; } = [];
}

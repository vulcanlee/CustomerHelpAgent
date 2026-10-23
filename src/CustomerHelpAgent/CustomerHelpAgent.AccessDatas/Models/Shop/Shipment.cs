namespace CustomerHelpAgent.AccessDatas.Models;

/// <summary>出貨單。</summary>
public class Shipment
{
    public int Id { get; set; }
    public int SalesOrderId { get; set; }
    public SalesOrder? SalesOrder { get; set; }
    public string Carrier { get; set; } = string.Empty;
    public string TrackingNo { get; set; } = string.Empty;
    public string StatusText { get; set; } = string.Empty;
    public DateTime? ShippedAt { get; set; }

    public List<ShipmentEvent> Events { get; set; } = [];
}

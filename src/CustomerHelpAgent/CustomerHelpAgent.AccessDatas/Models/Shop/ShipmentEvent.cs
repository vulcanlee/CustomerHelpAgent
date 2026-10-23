namespace CustomerHelpAgent.AccessDatas.Models;

/// <summary>物流事件。</summary>
public class ShipmentEvent
{
    public int Id { get; set; }
    public int ShipmentId { get; set; }
    public Shipment? Shipment { get; set; }
    public DateTime OccurredAt { get; set; }
    public string Location { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

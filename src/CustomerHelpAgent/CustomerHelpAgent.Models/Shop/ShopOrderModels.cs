namespace CustomerHelpAgent.Models.Shop;

/// <summary>顧客訂單明細項目（不含任何手機資訊）。</summary>
public sealed record ShopOrderItemView(
    int OrderItemId,
    string ProductName,
    string ProductCategory,
    string Color,
    string Size,
    int Quantity,
    decimal UnitPrice);

/// <summary>同款商品某一規格的庫存（換尺碼／換顏色前查看是否有貨）。</summary>
public sealed record ShopVariantStock(string Color, string Size, int Stock);

/// <summary>顧客訂單摘要。</summary>
public sealed record ShopOrderSummary(
    int OrderId,
    string OrderNo,
    DateTime OrderedAt,
    DateTime? DeliveredAt,
    string StatusText,
    IReadOnlyList<ShopOrderItemView> Items);

/// <summary>物流事件。</summary>
public sealed record ShopShipmentEventView(DateTime OccurredAt, string Location, string Description);

/// <summary>出貨與物流追蹤。</summary>
public sealed record ShopShipmentView(
    string Carrier,
    string TrackingNo,
    string StatusText,
    DateTime? ShippedAt,
    IReadOnlyList<ShopShipmentEventView> Events);

/// <summary>顧客訂單詳情。</summary>
public sealed record ShopOrderDetail(
    ShopOrderSummary Summary,
    string RecipientName,
    string ShippingAddress,
    ShopShipmentView? Shipment);

namespace CustomerHelpAgent.Web.Agents.AfterSales;

// 給模型看的投影：只放模型推理需要的欄位。收件人、地址、手機、價格、庫存數量、使用者 Id 一律不在這裡。

/// <summary>一個訂單商品。<see cref="DeliveryStatus"/> 為「已簽收／配送中／處理中」。</summary>
public sealed record AgentOrderItem(
    int OrderItemId,
    string OrderNo,
    string ProductName,
    string Color,
    string Size,
    string DeliveryStatus);

/// <summary>
/// 規則卡：規則條文原文＋程式計算的資格結果（簽收天數、不符原因、尚缺資訊）。
/// 顧客看到的條文由這張卡渲染，不採模型轉述。
/// </summary>
public sealed record AgentPolicyCard(
    int PolicyId,
    string Code,
    string Title,
    string Content,
    string Source,
    int Version,
    DateTime EffectiveFrom,
    int? DaysSinceDelivery,
    IReadOnlyList<string> UnmetReasons,
    IReadOnlyList<string> MissingInfo);

/// <summary>同款商品某一規格是否有貨（不給數量）。</summary>
public sealed record AgentStockOption(string Color, string Size, bool InStock);

/// <summary>物流追蹤。<see cref="CanUrge"/> 由程式依出貨時間與是否簽收判定。</summary>
public sealed record AgentShipment(
    string Carrier,
    string StatusText,
    DateTime? ShippedAt,
    IReadOnlyList<string> Events,
    bool CanUrge);

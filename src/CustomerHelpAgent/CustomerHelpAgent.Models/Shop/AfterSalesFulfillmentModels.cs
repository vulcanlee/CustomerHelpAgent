using CustomerHelpAgent.Share.Shop;

namespace CustomerHelpAgent.Models.Shop;

/// <summary>客服決定的執行結果；<see cref="FulfillmentNo"/> 只在核准成功時有值。</summary>
public sealed record DecisionResult(bool Success, string Message, AfterSalesStatus? NewStatus, string? FulfillmentNo);

/// <summary>售後處理單（審核頁與顧客頁共用）。</summary>
public sealed record AfterSalesFulfillmentView(
    int Id,
    int AfterSalesRequestId,
    FulfillmentKind Kind,
    string ReferenceNo,
    int? ProductVariantId,
    string? VariantText,
    int Quantity,
    decimal? RefundAmount,
    string Note,
    DateTime CreatedAt);

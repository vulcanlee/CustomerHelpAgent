using CustomerHelpAgent.Share.Shop;

namespace CustomerHelpAgent.AccessDatas.Models;

/// <summary>
/// 售後處理單（模擬）：申請核准時在同一交易內建立，一筆申請最多一張（<see cref="AfterSalesRequestId"/> 唯一）。
/// </summary>
public class AfterSalesFulfillment
{
    public int Id { get; set; }
    public int AfterSalesRequestId { get; set; }
    public AfterSalesRequest? AfterSalesRequest { get; set; }
    public FulfillmentKind Kind { get; set; }

    /// <summary>處理單號，格式 <c>{EX|RF|RP|SF}{yyyyMMdd}{Id:D4}</c>。</summary>
    public string ReferenceNo { get; set; } = string.Empty;

    /// <summary>換貨／補寄的出貨規格；退款與催件為原規格。</summary>
    public int? ProductVariantId { get; set; }
    public ProductVariant? ProductVariant { get; set; }
    public int Quantity { get; set; }
    public decimal? RefundAmount { get; set; }
    public string Note { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

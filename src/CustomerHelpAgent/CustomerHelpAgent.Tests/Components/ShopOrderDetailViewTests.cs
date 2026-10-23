using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Models.Shop;

namespace CustomerHelpAgent.Tests.Components;

/// <summary>訂單明細頁「申請催件」的顯示條件：未簽收，且已出貨超過 3 天。</summary>
public sealed class ShopOrderDetailViewTests
{
    private static readonly DateTime Now = new(2026, 10, 23, 12, 0, 0);

    [Theory]
    [InlineData(3 * 24 * 60 + 1, false, true)]   // 出貨 3 天又 1 分鐘、未簽收
    [InlineData(3 * 24 * 60, false, false)]      // 剛好 3 天，不算「超過」
    [InlineData(10 * 24 * 60, true, false)]      // 已簽收
    public void CanRequestUrge_ShouldFollowShippedDaysAndDelivery(int minutesSinceShipped, bool delivered, bool expected)
    {
        var detail = Detail(Now.AddMinutes(-minutesSinceShipped), delivered ? Now.AddDays(-1) : null);

        Assert.Equal(expected, Urge(detail));
    }

    [Fact]
    public void CanRequestUrge_WithoutShipment_ShouldBeFalse()
    {
        var detail = Detail(shippedAt: null, deliveredAt: null) with { Shipment = null };

        Assert.False(Urge(detail));
    }

    [Fact]
    public void CanRequestUrge_NotYetShipped_ShouldBeFalse()
    {
        Assert.False(Urge(Detail(shippedAt: null, deliveredAt: null)));
    }

    private static bool Urge(ShopOrderDetail detail)
        => AfterSalesEligibility.CanRequestUrge(detail.Summary.DeliveredAt, detail.Shipment?.ShippedAt, Now);

    private static ShopOrderDetail Detail(DateTime? shippedAt, DateTime? deliveredAt) => new(
        new ShopOrderSummary(1, "SO-TEST-0001", Now.AddDays(-12), deliveredAt, "配送中",
            [new ShopOrderItemView(7, "示範球鞋", "鞋類", "黑", "40", 1, 1990m)]),
        "測試收件人",
        "測試地址",
        new ShopShipmentView("示範物流", "TRK-0001", "配送中", shippedAt, []));
}

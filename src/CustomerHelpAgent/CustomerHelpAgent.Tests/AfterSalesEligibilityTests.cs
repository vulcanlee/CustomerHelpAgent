using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Models.Shop;
using CustomerHelpAgent.Share.Shop;

namespace CustomerHelpAgent.Tests;

public sealed class AfterSalesEligibilityTests
{
    private static readonly DateTime Now = new(2026, 10, 23, 10, 0, 0);

    private static AfterSalesPolicyModel Policy(int? windowDays = 7, bool unworn = true, bool photo = false) => new()
    {
        WindowDays = windowDays,
        RequiresUnworn = unworn,
        RequiresPhoto = photo,
    };

    [Fact]
    public void DescribeUnmet_JoinsReasonsThenMissingInfo()
    {
        Assert.Equal(
            "已超過 7 天申請期限（簽收後第 10 天）；商品已穿過，不符合未穿著條件；尚缺：是否穿過、照片",
            AfterSalesEligibility.DescribeUnmet(new EligibilityResult(
                false, ["已超過 7 天申請期限（簽收後第 10 天）", "商品已穿過，不符合未穿著條件"], ["是否穿過", "照片"])));
        Assert.Equal("訂單尚未出貨", AfterSalesEligibility.DescribeUnmet(new EligibilityResult(false, ["訂單尚未出貨"], [])));
        Assert.Equal("尚缺：照片", AfterSalesEligibility.DescribeUnmet(new EligibilityResult(false, [], ["照片"])));
        Assert.Equal(string.Empty, AfterSalesEligibility.DescribeUnmet(new EligibilityResult(true, [], [])));
    }

    [Fact]
    public void Evaluate_Day3Unworn_SizeExchange_ShouldBeEligible()
    {
        var result = AfterSalesEligibility.Evaluate(Policy(), Now.AddDays(-3), false, false, Now);

        Assert.True(result.IsEligible);
        Assert.Empty(result.UnmetReasons);
        Assert.Empty(result.MissingInfo);
    }

    [Fact]
    public void Evaluate_Day10_ShouldFailWindow()
    {
        var result = AfterSalesEligibility.Evaluate(Policy(), Now.AddDays(-10), false, false, Now);

        Assert.False(result.IsEligible);
        Assert.Single(result.UnmetReasons);
    }

    [Fact]
    public void Evaluate_NotDelivered_ShouldFail()
    {
        var result = AfterSalesEligibility.Evaluate(Policy(), null, false, false, Now);

        Assert.False(result.IsEligible);
        Assert.Single(result.UnmetReasons);
    }

    [Fact]
    public void Evaluate_Worn_ShouldFail()
    {
        var result = AfterSalesEligibility.Evaluate(Policy(), Now.AddDays(-1), true, false, Now);

        Assert.False(result.IsEligible);
        Assert.Single(result.UnmetReasons);
    }

    [Fact]
    public void Evaluate_WornUnknown_ShouldAskWhetherWorn()
    {
        var result = AfterSalesEligibility.Evaluate(Policy(), Now.AddDays(-1), null, false, Now);

        Assert.False(result.IsEligible);
        Assert.Empty(result.UnmetReasons);
        Assert.Equal(["是否穿過"], result.MissingInfo);
    }

    [Fact]
    public void Evaluate_QualityWithoutPhoto_ShouldAskPhoto()
    {
        var result = AfterSalesEligibility.Evaluate(Policy(unworn: false, photo: true), Now.AddDays(-1), null, false, Now);

        Assert.False(result.IsEligible);
        Assert.Equal(["照片"], result.MissingInfo);
    }

    [Fact]
    public void DaysSinceDelivery_CrossMidnight_ShouldUseLocalDate()
    {
        var delivered = new DateTime(2026, 10, 22, 23, 59, 0);
        var now = new DateTime(2026, 10, 23, 0, 1, 0);

        Assert.Equal(1, AfterSalesEligibility.DaysSinceDelivery(delivered, now));
        Assert.Null(AfterSalesEligibility.DaysSinceDelivery(null, now));
    }

    [Fact]
    public void Evaluate_Day7_IsEligible()
    {
        var result = AfterSalesEligibility.Evaluate(Policy(), Now.AddDays(-7), false, false, Now);

        Assert.True(result.IsEligible);
    }

    [Fact]
    public void Evaluate_Day8_FailsWindow()
    {
        var result = AfterSalesEligibility.Evaluate(Policy(), Now.AddDays(-8), false, false, Now);

        Assert.False(result.IsEligible);
        Assert.Single(result.UnmetReasons);
    }

    [Fact]
    public void Evaluate_FutureDelivery_IsUnmet()
    {
        var result = AfterSalesEligibility.Evaluate(Policy(), Now.AddDays(1), false, false, Now);

        Assert.False(result.IsEligible);
        Assert.Equal(["簽收日期異常，請洽客服"], result.UnmetReasons);
    }

    [Fact]
    public void Evaluate_Urge_ShippedFourDaysUndelivered_IsEligible()
    {
        var result = AfterSalesEligibility.Evaluate(UrgePolicy(), null, null, false, Now, Now.AddDays(-4));

        Assert.True(result.IsEligible);
        Assert.Empty(result.UnmetReasons);
    }

    [Fact]
    public void Evaluate_Urge_Delivered_IsUnmet()
    {
        var result = AfterSalesEligibility.Evaluate(UrgePolicy(), Now.AddDays(-1), null, false, Now, Now.AddDays(-4));

        Assert.False(result.IsEligible);
        Assert.Equal(["商品已簽收，不需催件"], result.UnmetReasons);
    }

    [Fact]
    public void Evaluate_Urge_ExactlyThreeDays_IsUnmet()
    {
        var result = AfterSalesEligibility.Evaluate(UrgePolicy(), null, null, false, Now, Now.AddDays(-3));

        Assert.False(result.IsEligible);
        Assert.Equal(["出貨未滿 3 天，請再等候"], result.UnmetReasons);
    }

    [Fact]
    public void Evaluate_Urge_NotShipped_IsUnmet()
    {
        var result = AfterSalesEligibility.Evaluate(UrgePolicy(), null, null, false, Now);

        Assert.Equal(["訂單尚未出貨"], result.UnmetReasons);
    }

    [Fact]
    public void CanRequestUrge_NotShipped_IsFalse()
    {
        Assert.False(AfterSalesEligibility.CanRequestUrge(null, null, Now));
    }

    private static AfterSalesPolicyModel UrgePolicy() => new() { Type = AfterSalesType.ShipmentUrge };
}

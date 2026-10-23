using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Models.Shop;

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
}

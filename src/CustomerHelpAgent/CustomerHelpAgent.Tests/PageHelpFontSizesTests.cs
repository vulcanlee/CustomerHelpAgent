using CustomerHelpAgent.Web.Components.Commons;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 說明窗字體大小（0.9.121 起）：存值與 CSS class 的對照。localStorage 的值可能沒存過、被竄改或讀不到，一律回「中」。
/// </summary>
public sealed class PageHelpFontSizesTests
{
    [Theory]
    [InlineData(PageHelpFontSize.Small, "small", "page-help-font-small")]
    [InlineData(PageHelpFontSize.Medium, "medium", "page-help-font-medium")]
    [InlineData(PageHelpFontSize.Large, "large", "page-help-font-large")]
    public void Size_ShouldRoundTripThroughStorage_AndMapToItsCssClass(PageHelpFontSize size, string stored, string cssClass)
    {
        Assert.Equal(stored, PageHelpFontSizes.ToStored(size));
        Assert.Equal(size, PageHelpFontSizes.Parse(stored));
        Assert.Equal(cssClass, PageHelpFontSizes.CssClass(size));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("LARGE")]
    [InlineData("huge")]
    public void Parse_WithMissingOrUnknownValue_ShouldFallBackToMedium(string? stored)
    {
        Assert.Equal(PageHelpFontSize.Medium, PageHelpFontSizes.Parse(stored));
    }

    [Fact]
    public void Options_ShouldBeSmallMediumLarge_InThatOrder()
    {
        Assert.Equal(["小", "中", "大"], PageHelpFontSizes.Options.Select(x => x.Label));
        Assert.Equal([PageHelpFontSize.Small, PageHelpFontSize.Medium, PageHelpFontSize.Large], PageHelpFontSizes.Options.Select(x => x.Size));
    }
}

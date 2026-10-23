using Bunit;
using CustomerHelpAgent.Web.Components.Commons;

namespace CustomerHelpAgent.Tests.Components;

/// <summary>
/// 條件渲染與 <c>ChildContent</c>（RenderFragment）的樣板。
///
/// FormSection 本身不帶樣式，只負責產生固定的 class 名稱讓 OverlayStyles.razor 的全域樣式命中；
/// class 名稱一旦改掉，表單對話窗的 2 欄排版就會靜默失效 —— 這正是元件測試要擋的東西。
/// </summary>
public sealed class FormSectionTests : ComponentTestBase
{
    [Fact]
    public void WithTitleAndHint_ShouldRenderHeading()
    {
        var cut = Render<FormSection>(parameters => parameters
            .Add(p => p.Title, "基本資料")
            .Add(p => p.Hint, "必填"));

        var title = cut.Find("h3.form-section-title");
        Assert.Contains("基本資料", title.TextContent, StringComparison.Ordinal);
        Assert.Equal("必填", title.QuerySelector("span.form-section-hint")?.TextContent);
    }

    [Fact]
    public void WithoutTitle_ShouldNotRenderHeading()
    {
        var cut = Render<FormSection>(parameters => parameters.Add(p => p.Hint, "沒有標題時不顯示"));

        Assert.Empty(cut.FindAll("h3.form-section-title"));
        Assert.Empty(cut.FindAll("span.form-section-hint"));
    }

    [Fact]
    public void ChildContent_ShouldRenderInsideTwoColumnGrid()
    {
        var cut = Render<FormSection>(parameters => parameters
            .Add(p => p.Title, "基本資料")
            .AddChildContent("<input id=\"field\" />"));

        var grid = cut.Find("section.form-section > div.form-grid");
        Assert.False(grid.ClassList.Contains("form-grid-single"));
        Assert.NotNull(grid.QuerySelector("#field"));
    }

    [Fact]
    public void SingleColumn_ShouldAddSingleColumnClass()
    {
        var cut = Render<FormSection>(parameters => parameters.Add(p => p.SingleColumn, true));

        Assert.True(cut.Find("div.form-grid").ClassList.Contains("form-grid-single"));
    }
}

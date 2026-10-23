using Bunit;
using CustomerHelpAgent.Web.Components.Commons;

namespace CustomerHelpAgent.Tests.Components;

/// <summary>
/// 含 AntDesign 元件（Tooltip、Button）與點擊事件的樣板。
///
/// AntDesign 元件內部的 JS 呼叫由 <see cref="ComponentTestBase"/> 的 Loose 模式吸收；
/// 這裡只關心本專案元件自己的行為：渲染成連結還是按鈕、class、事件有沒有觸發。
/// </summary>
public sealed class CrudActionButtonTests : ComponentTestBase
{
    [Fact]
    public void WithHref_ShouldRenderLinkInsteadOfButton()
    {
        var cut = Render<CrudActionButton>(parameters => parameters
            .Add(p => p.Title, "下載")
            .Add(p => p.Icon, "download")
            .Add(p => p.Href, "/api/project-files/1/download"));

        var link = cut.Find("a.crud-action-button");
        Assert.Equal("/api/project-files/1/download", link.GetAttribute("href"));
        Assert.Equal("_blank", link.GetAttribute("target"));
        Assert.Equal("下載", link.GetAttribute("aria-label"));
        Assert.Empty(cut.FindAll("button"));
    }

    [Fact]
    public void WithoutHref_ShouldRenderButtonWithTitle()
    {
        var cut = Render<CrudActionButton>(parameters => parameters
            .Add(p => p.Title, "編輯")
            .Add(p => p.Icon, "edit"));

        var button = cut.Find("button.crud-action-button");
        Assert.False(button.ClassList.Contains("crud-action-button-danger"));
        Assert.Equal("編輯", button.QuerySelector("span.crud-action-button-label")?.TextContent);
    }

    [Fact]
    public void Danger_ShouldAddDangerClass()
    {
        var cut = Render<CrudActionButton>(parameters => parameters
            .Add(p => p.Title, "刪除")
            .Add(p => p.Icon, "delete")
            .Add(p => p.Danger, true));

        Assert.True(cut.Find("button.crud-action-button").ClassList.Contains("crud-action-button-danger"));
    }

    [Fact]
    public void Click_ShouldInvokeOnClick()
    {
        var clicked = 0;
        var cut = Render<CrudActionButton>(parameters => parameters
            .Add(p => p.Title, "編輯")
            .Add(p => p.Icon, "edit")
            .Add(p => p.OnClick, () => clicked++));

        cut.Find("button.crud-action-button").Click();

        Assert.Equal(1, clicked);
    }

    [Fact]
    public void Disabled_ShouldRenderDisabledButton()
    {
        var cut = Render<CrudActionButton>(parameters => parameters
            .Add(p => p.Title, "編輯")
            .Add(p => p.Icon, "edit")
            .Add(p => p.Disabled, true));

        Assert.True(cut.Find("button.crud-action-button").HasAttribute("disabled"));
    }
}

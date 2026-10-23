using Bunit;
using CustomerHelpAgent.Web.Components.Commons;

namespace CustomerHelpAgent.Tests.Components;

/// <summary>
/// 最單純的元件測試樣板：傳參數、渲染、檢查輸出的標記。
///
/// 斷言 class 與文字，而不是比對整段 HTML —— 整段比對會因為無關的標記調整（多一個屬性、換行）而失敗，
/// 測試就變成「改什麼都要跟著改」的負擔。
/// </summary>
public sealed class StatusPillTests : ComponentTestBase
{
    [Theory]
    [InlineData(StatusTone.Neutral, "status-pill-neutral")]
    [InlineData(StatusTone.Positive, "status-pill-positive")]
    [InlineData(StatusTone.Muted, "status-pill-muted")]
    [InlineData(StatusTone.Warning, "status-pill-warning")]
    [InlineData(StatusTone.Accent, "status-pill-accent")]
    public void Tone_ShouldMapToItsCssClass(StatusTone tone, string expectedClass)
    {
        var cut = Render<StatusPill>(parameters => parameters
            .Add(p => p.Text, "啟用")
            .Add(p => p.Tone, tone));

        var pill = cut.Find("span.status-pill");
        Assert.True(pill.ClassList.Contains(expectedClass), $"預期 class 含 {expectedClass}，實際為「{pill.ClassName}」。");
        Assert.Equal("啟用", pill.TextContent);
    }

    [Fact]
    public void DefaultTone_ShouldBeNeutral()
    {
        var cut = Render<StatusPill>(parameters => parameters.Add(p => p.Text, "等待"));

        Assert.True(cut.Find("span.status-pill").ClassList.Contains("status-pill-neutral"));
    }

    [Fact]
    public void WithOnClick_ShouldRenderAButton_WithHint_AndInvokeTheCallback()
    {
        // 0.9.120 起：清單上點「啟用／停用」直接切換。
        var clicks = 0;
        var cut = Render<StatusPill>(parameters => parameters
            .Add(p => p.Text, "啟用")
            .Add(p => p.Tone, StatusTone.Positive)
            .Add(p => p.Hint, "點一下切換為停用")
            .Add(p => p.OnClick, () => clicks++));

        Assert.Empty(cut.FindAll("span.status-pill"));
        var button = cut.Find("button.status-pill.status-pill-clickable");
        Assert.True(button.ClassList.Contains("status-pill-positive"));
        Assert.Equal("button", button.GetAttribute("type"));
        Assert.Equal("點一下切換為停用", button.GetAttribute("title"));
        Assert.Equal("啟用", button.TextContent);

        button.Click();

        Assert.Equal(1, clicks);
    }

    [Fact]
    public void WithoutOnClick_ShouldStayANonInteractiveSpan()
    {
        var cut = Render<StatusPill>(parameters => parameters
            .Add(p => p.Text, "停用")
            .Add(p => p.Hint, "點一下切換為啟用"));

        Assert.Empty(cut.FindAll("button"));
        Assert.Null(cut.Find("span.status-pill").GetAttribute("title"));
    }
}

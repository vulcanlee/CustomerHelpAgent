using CustomerHelpAgent.Web.Components.Commons;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 頁面操作說明的 Markdown 管線。輸出會進 MarkupString，且說明檔大量使用表格。
/// </summary>
public sealed class HelpMarkdownRendererTests
{
    [Fact]
    public void RawHtml_IsEscaped()
    {
        var html = HelpMarkdownRenderer.ToHtml("<script>alert(1)</script>\n\n文字 <b onclick=\"x\">粗</b>");

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<b onclick", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PipeTable_RendersHtmlTable()
    {
        var html = HelpMarkdownRenderer.ToHtml("| 名詞 | 白話解釋 |\n|---|---|\n| 分類 | 專案的類別 |");

        Assert.Contains("<table>", html, StringComparison.Ordinal);
        Assert.Contains("<td>分類</td>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void DollarSign_IsNotTreatedAsMath()
        => Assert.Contains("$100 與 $200", HelpMarkdownRenderer.ToHtml("費用 $100 與 $200"), StringComparison.Ordinal);

    [Fact]
    public void NullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, HelpMarkdownRenderer.ToHtml(null));
        Assert.Equal(string.Empty, HelpMarkdownRenderer.ToHtml(string.Empty));
    }
}

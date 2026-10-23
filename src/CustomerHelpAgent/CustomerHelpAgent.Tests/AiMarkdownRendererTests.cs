using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using CustomerHelpAgent.Web.Ai;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// Markdown 安全管線的守門測試。
///
/// AI 的輸出受日誌內容影響（有人把字串送進 log 就能間接影響輸出），屬於不可信輸入，
/// 而結果會經 <c>MarkupString</c> 直接注入頁面。這組測試釘住三件事：
/// raw HTML 被轉義、危險 scheme 的連結被清掉、圖片被移除。
///
/// ⚠️ 若有人在 <see cref="AiMarkdownRenderer"/> 的管線加上 UseAdvancedExtensions()、
/// UseGenericAttributes() 或 UseAutoLinks()，這裡會失敗。那不是測試壞了。
/// </summary>
public sealed class AiMarkdownRendererTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ToSafeHtml_ShouldReturnEmpty_WhenInputBlank(string? markdown)
    {
        Assert.Equal(string.Empty, AiMarkdownRenderer.ToSafeHtml(markdown));
    }

    [Fact]
    public void ToSafeHtml_ShouldEscapeRawHtmlBlock()
    {
        var html = AiMarkdownRenderer.ToSafeHtml("<script>alert(1)</script>");

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToSafeHtml_ShouldEscapeInlineHtml()
    {
        var html = AiMarkdownRenderer.ToSafeHtml("這段文字後面有 <img src=x onerror=alert(1)> 標籤。");

        // 轉義之後 onerror 仍會以「顯示文字」出現，那是無害的；
        // 要斷言的是它沒有變成真正的標籤與事件處理屬性。
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;img", html, StringComparison.OrdinalIgnoreCase);
        AssertNoEventHandlerAttribute(html);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JaVaScRiPt:alert(1)")]
    [InlineData("  javascript:alert(1)")]
    [InlineData("data:text/html;base64,PHNjcmlwdD4=")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("file:///etc/passwd")]
    public void ToSafeHtml_ShouldStripDangerousLinkScheme(string url)
    {
        var html = AiMarkdownRenderer.ToSafeHtml($"[點我]({url})");

        Assert.DoesNotContain("javascript", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("vbscript", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data:text/html", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("file:///", html, StringComparison.OrdinalIgnoreCase);

        // 連結文字本身仍要保留，不可整段吞掉。
        Assert.Contains("點我", html);
    }

    /// <summary>
    /// 圖片一律移除：外部圖片等於一個由攻擊者指定網址、且會被管理員瀏覽器自動觸發的
    /// 請求（追蹤像素或內網探測），連點都不需要。
    /// </summary>
    [Fact]
    public void ToSafeHtml_ShouldRemoveImages()
    {
        var html = AiMarkdownRenderer.ToSafeHtml("![追蹤像素](https://evil.example/pixel.png)");

        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("evil.example", html);
    }

    [Fact]
    public void ParseSanitized_ShouldClearImageFlag()
    {
        var document = AiMarkdownRenderer.ParseSanitized("![圖](https://evil.example/x.png)");
        var links = document.Descendants<LinkInline>().ToList();

        Assert.All(links, link => Assert.False(link.IsImage));
        Assert.All(links, link => Assert.True(string.IsNullOrEmpty(link.Url)));
    }

    [Fact]
    public void ToSafeHtml_ShouldKeepHttpsLink_WithNoopenerRel()
    {
        var html = AiMarkdownRenderer.ToSafeHtml("[文件](https://learn.microsoft.com/aspnet/core)");

        Assert.Contains("https://learn.microsoft.com/aspnet/core", html);
        Assert.Contains("rel=\"noopener noreferrer nofollow\"", html);
        Assert.Contains("target=\"_blank\"", html);
    }

    [Fact]
    public void ToSafeHtml_ShouldKeepMailtoLink()
    {
        var html = AiMarkdownRenderer.ToSafeHtml("[聯絡](mailto:support@example.com)");

        Assert.Contains("mailto:support@example.com", html);
    }

    /// <summary>
    /// generic attributes 擴充會讓這種語法變成 &lt;h2 onclick="alert(1)"&gt;。沒開的話它只是
    /// 標題裡的普通文字（引號被轉義），所以斷言的是「沒有變成標籤上的事件處理屬性」。
    /// </summary>
    [Fact]
    public void ToSafeHtml_ShouldNotEmitGenericAttributes()
    {
        var html = AiMarkdownRenderer.ToSafeHtml("## 標題 {onclick=\"alert(1)\"}");

        Assert.Contains("<h2>", html);
        AssertNoEventHandlerAttribute(html);
    }

    /// <summary>
    /// 輸出中不得有任何 HTML 標籤帶著 on* 事件處理屬性。
    /// 這比單純比對字串可靠：被轉義成顯示文字的 "onclick" 是無害的，真正危險的是
    /// 它出現在標籤的屬性位置。
    /// </summary>
    private static void AssertNoEventHandlerAttribute(string html)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            html,
            @"<[a-zA-Z][^>]*\son[a-zA-Z]+\s*=",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        Assert.False(match.Success, $"輸出含事件處理屬性：{match.Value}");
    }

    /// <summary>autolink 擴充會把日誌裡的裸 URL 變成可點連結，必須確認沒開。</summary>
    [Fact]
    public void ToSafeHtml_ShouldNotAutoLinkBareUrl()
    {
        var html = AiMarkdownRenderer.ToSafeHtml("請求目標是 https://evil.example/collect 這個網址。");

        Assert.DoesNotContain("<a ", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("href", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToSafeHtml_ShouldRenderSupportedMarkdown()
    {
        const string markdown = """
            ## 總結
            這是 **重點** 與 `code`。

            - 項目一
            - 項目二

            1. 第一步
            2. 第二步

            ```
            var x = 1;
            ```
            """;

        var html = AiMarkdownRenderer.ToSafeHtml(markdown);

        Assert.Contains("<h2", html);
        Assert.Contains("<strong>重點</strong>", html);
        Assert.Contains("<code>code</code>", html);
        Assert.Contains("<ul>", html);
        Assert.Contains("<ol>", html);
        Assert.Contains("<pre><code>", html);
    }

    /// <summary>HtmlEntityInline 在 DisableHtml() 之後仍會產生，不可掉字。</summary>
    [Fact]
    public void ToSafeHtml_ShouldDecodeHtmlEntities()
    {
        var html = AiMarkdownRenderer.ToSafeHtml("A &amp; B");

        Assert.Contains("A &amp; B", html);
    }
}

using Markdig;

namespace CustomerHelpAgent.Web.Components.Commons;

/// <summary>
/// 頁面操作說明（<c>Datas/Help/*.md</c>）專用的 Markdown 轉 HTML。
/// </summary>
/// <remarks>
/// <para>
/// <b>刻意與 <c>Ai/AiMarkdownRenderer</c> 分開</b>：那條管線處理的是模型輸出（不可信輸入），
/// 註解明文禁止擴充；說明檔則是隨原始碼進版控的可信內容，而且大量使用表格，必須開 <c>UsePipeTables()</c>。
/// 兩者的威脅模型不同，共用一條管線只會讓其中一邊被迫妥協。
/// </para>
/// <list type="bullet">
/// <item><c>DisableHtml()</c>：輸出會進 <c>MarkupString</c>，仍不讓內容夾帶 raw HTML。</item>
/// <item><c>UsePipeTables()</c>：CommonMark 本身沒有表格；少了它，表格會渲染成字面的 <c>| 欄 | 欄 |</c>。</item>
/// <item><b>不可</b>加 <c>UseAdvancedExtensions()</c>：其中的數學擴充會把 <c>$</c> 當公式分隔符。</item>
/// </list>
/// 由 HelpMarkdownRendererTests 與 PageHelpCatalogTests 守門。
/// </remarks>
public static class HelpMarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UsePipeTables()
        .UseSoftlineBreakAsHardlineBreak()
        .Build();

    public static string ToHtml(string? markdown)
        => string.IsNullOrEmpty(markdown) ? string.Empty : Markdown.ToHtml(markdown, Pipeline);
}

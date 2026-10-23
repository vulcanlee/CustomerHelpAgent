namespace CustomerHelpAgent.Web.Components.Commons;

/// <summary>頁面說明的一個章節。<see cref="Markdown"/> 是原始 Markdown，轉 HTML 留在 Razor 端。</summary>
/// <param name="Heading">章節標題（不含 <c>## </c>），例如「一、功能摘要」。</param>
/// <param name="Markdown">章節本文的原始 Markdown（不含標題那一行）。</param>
public sealed record PageHelpSection(string Heading, string Markdown);

/// <summary>「六、相關頁面」段解析出的一列。</summary>
/// <param name="Title">顯示文字。</param>
/// <param name="Route">目標路由（以 <c>/</c> 開頭）。</param>
/// <param name="Description">為什麼相關的一句話。</param>
public sealed record PageHelpRelatedPage(string Title, string Route, string Description);

/// <summary>
/// 一份頁面說明的解析結果。只存 Markdown、不存 HTML：轉換交給 <see cref="HelpMarkdownRenderer"/>，
/// 讓 <see cref="PageHelpMarkdownParser"/> 的測試不牽動 Markdig 的行為。
/// </summary>
public sealed class PageHelpDocument
{
    /// <summary>第一個 <c>## </c> 之前的內容：H1、一句話摘要、callout 與「一分鐘看懂這一頁」。</summary>
    public string Preamble { get; init; } = string.Empty;

    /// <summary>各章節，依檔案中的出現順序。</summary>
    public IReadOnlyList<PageHelpSection> Sections { get; init; } = [];

    /// <summary>「六、相關頁面」段解析出的清單。</summary>
    public IReadOnlyList<PageHelpRelatedPage> RelatedPages { get; init; } = [];

    /// <summary>
    /// 檔案讀不到時的佔位文件。內容寫死在程式裡、不從檔案讀，避免「連佔位檔都缺」的第二層失敗。
    /// 正常情況不會出現：PageHelpCatalogTests 要求每個登記的路由都有檔案。
    /// </summary>
    public static PageHelpDocument Placeholder() => new()
    {
        Preamble = """
                   這一頁的操作說明暫時無法載入。

                   - 畫面上的按鈕把滑鼠停在上面，會出現提示文字。
                   - 仍有疑問，請洽系統管理員。
                   """,
    };
}

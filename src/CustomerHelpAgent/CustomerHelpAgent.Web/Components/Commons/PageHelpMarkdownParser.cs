using System.Text;
using System.Text.RegularExpressions;

namespace CustomerHelpAgent.Web.Components.Commons;

/// <summary>
/// 把頁面說明的 Markdown 切成章節，並解析「相關頁面」清單。純函式、不碰 IO，行為由 PageHelpParsingTests 覆蓋。
/// </summary>
public static class PageHelpMarkdownParser
{
    /// <summary>
    /// 七個<b>必要</b>章節標題（逐字、有序、各一次，也是唯一允許的 H2）。對話窗的章節導覽與 PageHelpCatalogTests 共用這一份定義。
    /// </summary>
    public static readonly string[] RequiredHeadings =
    [
        "一、功能摘要",
        "二、這個頁面在做什麼",
        "三、畫面上有哪些按鈕、各自做什麼",
        "四、建議這樣操作，會得到什麼",
        "五、名詞解釋",
        "六、相關頁面",
        "七、常見問題",
    ];

    /// <summary>「相關頁面」那一段的標題，UI 據此改用結構化卡片渲染。</summary>
    public const string RelatedPagesHeading = "六、相關頁面";

    /// <summary>「常見問題」那一段的標題。</summary>
    public const string FaqHeading = "七、常見問題";

    /// <summary>常見問題固定的最後一題，引導使用者帶著系統版本回報問題。</summary>
    public const string FaqClosingQuestion = "還是解決不了怎麼辦？";

    /// <summary>
    /// 前言中「一分鐘看懂」區塊的小標題。<b>刻意是 H3</b>：寫成 H2 會被切成章節，章節導覽就多出一項。
    /// </summary>
    public const string QuickLookHeading = "一分鐘看懂這一頁";

    /// <summary>「一分鐘看懂這一頁」的六個固定標籤，順序固定，每條寫成 <c>- **標籤**：內容</c>。</summary>
    public static readonly string[] QuickLookLabels =
    [
        "解決什麼問題",
        "誰會用到",
        "它在系統的哪個位置",
        "開始前要準備",
        "做完會得到",
        "它不做什麼",
    ];

    /// <summary>第三段欄位表的固定表頭。</summary>
    public static readonly string[] FieldTableHeaders = ["欄位", "意思", "範例", "怎麼填或怎麼用"];

    /// <summary>第三段按鈕表的固定表頭。</summary>
    public static readonly string[] ButtonTableHeaders = ["按鈕", "按下後會做什麼", "右下角會看到的提示", "注意事項"];

    /// <summary>相關頁面的唯一合法行格式：<c>- [顯示標題](/route)：說明</c>。全形與半形冒號都接受。</summary>
    private static readonly Regex RelatedPagePattern = new(
        @"^\s*[-*]\s*\[(?<title>[^\]]+)\]\((?<route>/[^)\s]*)\)\s*[:：]\s*(?<desc>.+?)\s*$",
        RegexOptions.Compiled);

    /// <summary>切出前言、章節與相關頁面清單。輸入為空白時回傳佔位文件。</summary>
    public static PageHelpDocument Parse(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return PageHelpDocument.Placeholder();
        }

        var sections = new List<PageHelpSection>();
        var preamble = new StringBuilder();
        string? currentHeading = null;
        var currentBody = new StringBuilder();

        void Flush()
        {
            if (currentHeading is null)
            {
                return;
            }

            sections.Add(new PageHelpSection(currentHeading, currentBody.ToString().Trim('\r', '\n')));
            currentBody.Clear();
        }

        foreach (var line in SplitLines(markdown))
        {
            if (IsSectionHeading(line, out var heading))
            {
                Flush();
                currentHeading = heading;
                continue;
            }

            (currentHeading is null ? preamble : currentBody).AppendLine(line);
        }

        Flush();

        var relatedSection = sections.FirstOrDefault(x => x.Heading == RelatedPagesHeading);

        return new PageHelpDocument
        {
            Preamble = preamble.ToString().Trim('\r', '\n'),
            Sections = sections,
            RelatedPages = relatedSection is null ? [] : ParseRelatedPages(relatedSection.Markdown),
        };
    }

    /// <summary>解析「相關頁面」段的清單。無法解析的行不入清單（守門測試會要求每一行都可解析）。</summary>
    public static List<PageHelpRelatedPage> ParseRelatedPages(string? sectionMarkdown)
    {
        var result = new List<PageHelpRelatedPage>();
        if (string.IsNullOrWhiteSpace(sectionMarkdown))
        {
            return result;
        }

        foreach (var line in SplitLines(sectionMarkdown))
        {
            var match = RelatedPagePattern.Match(line);
            if (!match.Success)
            {
                continue;
            }

            result.Add(new PageHelpRelatedPage(
                match.Groups["title"].Value.Trim(),
                match.Groups["route"].Value.Trim(),
                match.Groups["desc"].Value.Trim()));
        }

        return result;
    }

    /// <summary>
    /// 取「相關頁面」段中清單項<b>之前</b>的前導文字，讓它照常以 Markdown 渲染；清單項改用結構化卡片。
    /// </summary>
    public static string RelatedPagesIntro(string? sectionMarkdown)
    {
        if (string.IsNullOrWhiteSpace(sectionMarkdown))
        {
            return string.Empty;
        }

        var lines = SplitLines(sectionMarkdown)
            .TakeWhile(x => !x.TrimStart().StartsWith('-') && !x.TrimStart().StartsWith('*'));

        return string.Join('\n', lines).Trim();
    }

    /// <summary>取出所有 <c>## </c> 章節標題（依出現順序，含重複）。供內容完整性測試使用。</summary>
    public static List<string> ExtractSectionHeadings(string? markdown)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return result;
        }

        foreach (var line in SplitLines(markdown))
        {
            if (IsSectionHeading(line, out var heading))
            {
                result.Add(heading);
            }
        }

        return result;
    }

    private static bool IsSectionHeading(string line, out string heading)
    {
        heading = string.Empty;

        // 不用 TrimStart()：縮排四格以上的 "## x" 在 Markdown 裡是程式碼區塊，不是標題；
        // 也因為只認「行首 ## 加空白」，### 小節不會被誤判成新章節。
        if (!line.StartsWith("## ", StringComparison.Ordinal))
        {
            return false;
        }

        heading = line[3..].Trim();
        return heading.Length > 0;
    }

    private static string[] SplitLines(string text)
        => text.Split('\n').Select(x => x.TrimEnd('\r')).ToArray();
}

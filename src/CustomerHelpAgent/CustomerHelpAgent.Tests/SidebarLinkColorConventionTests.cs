using System.Text.RegularExpressions;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 守住「側邊欄葉節點的文字顏色」。
///
/// <para>AntDesign 的 <c>MenuItem</c> 只要有 <c>RouterLink</c>，就會在
/// <c>&lt;li class="ant-menu-item"&gt;</c> 裡面**再包一層 <c>&lt;a&gt;</c>**，而它出廠自帶
/// <c>.ant-menu-item a { color: rgba(0,0,0,.85) }</c> 與
/// <c>.ant-menu-item-selected a { color: #1890ff }</c>。CSS 的 <c>color</c> 在子元素被
/// 直接指定時不會繼承，所以只打在 <c>&lt;li&gt;</c> 上的顏色**到不了葉節點的文字**。</para>
///
/// <para>⚠️ 後果是深梅側邊欄上出現**黑字與藍字**，幾乎看不見 —— 而且
/// 建置、<c>dotnet format</c>、<c>TreatWarningsAsErrors</c> 三道關卡全都看不到，
/// 只有實際展開選單才會發現。0.9.40 之前正是這個狀態。</para>
///
/// <para>群組標題與「登出」之所以一直正常，是因為它們沒有那層 <c>&lt;a&gt;</c>
/// （前者是 <c>&lt;div&gt;</c>，後者走 <c>OnClick</c>）—— 這也是當初難以察覺的原因：
/// 看起來「大部分都對」。</para>
/// </summary>
public sealed class SidebarLinkColorConventionTests
{
    [Fact]
    public void SidebarMenuAnchors_ShouldHaveExplicitColorOverrides()
    {
        var css = File.ReadAllText(Path.Combine(FindWebRoot(), "Components", "Layout", "NavMenu.razor.css"));
        var body = StripBlockComments(css);

        var missing = new List<string>();

        // 一般葉節點、選中的葉節點，兩者都必須有覆寫，否則會分別變成黑字與藍字。
        if (HasColorRule(body, @"\.ant-menu-item a\b") == false)
        {
            missing.Add(".ant-menu-item a（沒有覆寫 → 葉節點會變成 AntDesign 的黑字）");
        }

        if (HasColorRule(body, @"\.ant-menu-item-selected a\b") == false)
        {
            missing.Add(".ant-menu-item-selected a（沒有覆寫 → 選中項會變成 AntDesign 的藍字 #1890ff）");
        }

        // 右緣的選中指示條，出廠是 3px #1890ff。
        if (Regex.IsMatch(body, @"\.ant-menu-item::after[^{]*\{[^}]*border-right-color:", RegexOptions.Singleline) == false)
        {
            missing.Add(".ant-menu-item::after（沒有覆寫 → 右緣會留一條 AntDesign 的藍色指示條）");
        }

        Assert.True(
            missing.Count == 0,
            "NavMenu.razor.css 缺少側邊欄選單連結的顏色覆寫："
            + string.Join("、", missing)
            + "。AntDesign 的 MenuItem 帶 RouterLink 時會多包一層 <a> 並自帶深色／藍色，"
            + "只打在 <li> 上的 color 不會繼承進去。少了這些覆寫，深梅側邊欄上會出現看不見的黑字，"
            + "而且建置與格式檢查都不會有任何徵兆。");
    }

    /// <summary>覆寫一律要帶 !important：scoped CSS 束在 App.razor 裡排在 AntDesign 之前。</summary>
    [Fact]
    public void SidebarMenuAnchorOverrides_ShouldUseImportant()
    {
        var body = StripBlockComments(
            File.ReadAllText(Path.Combine(FindWebRoot(), "Components", "Layout", "NavMenu.razor.css")));

        // a 必須是**獨立的元素選擇器**：寫成 [^{}]*a[^{}]* 會連 .material-icons
        // 這種含字母 a 的 class 都匹配到，把圖示那一組誤判成違規。
        foreach (Match block in Regex.Matches(body, @"[^{}]*\.ant-menu-item[^{}]*\ba\b[^{}]*\{([^}]*)\}"))
        {
            var declarations = block.Groups[1].Value;
            if (declarations.Contains("color:", StringComparison.Ordinal) == false)
            {
                continue;
            }

            Assert.True(
                declarations.Contains("!important", StringComparison.Ordinal),
                "側邊欄選單連結的顏色覆寫必須帶 !important —— scoped CSS 束（CustomerHelpAgent.Web.styles.css）"
                + "在 App.razor 裡排在 AntDesign 的 CSS **之前**，同分特異性會輸掉。"
                + "問題區塊：" + block.Value.Trim());
        }
    }

    private static bool HasColorRule(string cssBody, string selectorPattern)
        => Regex.IsMatch(cssBody, selectorPattern + @"[^{}]*\{[^}]*color:", RegexOptions.Singleline);

    private static string StripBlockComments(string text)
        => Regex.Replace(text, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);

    private static string FindWebRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "CustomerHelpAgent.Web");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            var srcCandidate = Path.Combine(dir.FullName, "src", "CustomerHelpAgent", "CustomerHelpAgent.Web");
            if (Directory.Exists(srcCandidate))
            {
                return srcCandidate;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("找不到 CustomerHelpAgent.Web 目錄。");
    }
}

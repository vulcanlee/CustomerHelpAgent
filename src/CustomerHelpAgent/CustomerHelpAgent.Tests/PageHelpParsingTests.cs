using CustomerHelpAgent.Web.Components.Commons;
using CustomerHelpAgent.Web.Components.Layout;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 頁面操作說明的純函式：檔名規則、路由比對、章節切分、相關頁面解析與權限過濾。
/// 內容層面的守門（每頁都有、六大段齊全、編碼）在 PageHelpCatalogTests。
/// </summary>
public sealed class PageHelpParsingTests
{
    [Theory]
    [InlineData("/projects", "projects.md")]
    [InlineData("/log-level-setting", "log-level-setting.md")]
    [InlineData("/ChangePassword", "changepassword.md")]
    [InlineData("/App", "app.md")]
    [InlineData("/Auths/Login", "auths-login.md")]
    [InlineData("/", "")]
    [InlineData(null, "")]
    public void ToSlugFileName_FollowsRule(string? route, string expected)
        => Assert.Equal(expected, PageHelpService.ToSlugFileName(route));

    [Theory]
    [InlineData("/projects", "/projects")]
    [InlineData("projects", "/projects")]
    [InlineData("/Projects/", "/projects")]
    [InlineData("/projects?page=2#top", "/projects")]
    [InlineData("changepassword", "/ChangePassword")]
    public void MatchTopic_MatchesExactPath_IgnoringCaseQueryAndSlashes(string path, string expectedRoute)
        => Assert.Equal(expectedRoute, PageHelpService.MatchTopic(Topics, path)?.Route);

    [Theory]
    [InlineData("/projects/5")]
    [InlineData("/project")]
    [InlineData("")]
    [InlineData("/")]
    public void MatchTopic_DoesNotPrefixMatch(string path)
        => Assert.Null(PageHelpService.MatchTopic(Topics, path));

    [Fact]
    public void Parse_SplitsPreambleAndSections_WithoutSplittingOnH3()
    {
        var document = PageHelpMarkdownParser.Parse(
            "# 標題\n\n摘要\n\n### 一分鐘看懂這一頁\n\n- 一\n\n## 一、功能摘要\n\n內容\n\n### 小節\n\n小節內容\n\n## 二、這個頁面在做什麼\n\n第二段");

        Assert.StartsWith("# 標題", document.Preamble);
        Assert.Contains("### 一分鐘看懂這一頁", document.Preamble);
        Assert.Equal(["一、功能摘要", "二、這個頁面在做什麼"], document.Sections.Select(x => x.Heading));
        Assert.Contains("### 小節", document.Sections[0].Markdown);
    }

    [Fact]
    public void Parse_IgnoresIndentedFakeHeading()
    {
        var headings = PageHelpMarkdownParser.ExtractSectionHeadings("## 一、功能摘要\n\n    ## 這是程式碼區塊\n");

        Assert.Equal(["一、功能摘要"], headings);
    }

    [Fact]
    public void Parse_HandlesCrLf()
    {
        var document = PageHelpMarkdownParser.Parse("# 標題\r\n\r\n## 一、功能摘要\r\n\r\n內容\r\n");

        Assert.Equal("# 標題", document.Preamble);
        Assert.Equal("內容", document.Sections.Single().Markdown);
    }

    [Fact]
    public void Parse_EmptyInput_ReturnsPlaceholder()
    {
        var document = PageHelpMarkdownParser.Parse("  ");

        Assert.Empty(document.Sections);
        Assert.False(string.IsNullOrWhiteSpace(document.Preamble));
    }

    [Fact]
    public void ParseRelatedPages_AcceptsFullAndHalfWidthColon_AndSkipsMalformedLines()
    {
        var related = PageHelpMarkdownParser.ParseRelatedPages(
            "前言文字。\n\n- [專案項目](/projects)：管理專案\n- [分類清單](/categories): 管理分類\n- 沒有連結的一行\n- [外部](https://example.com)：不是站內路由");

        Assert.Equal(
            [new PageHelpRelatedPage("專案項目", "/projects", "管理專案"), new PageHelpRelatedPage("分類清單", "/categories", "管理分類")],
            related);
    }

    [Fact]
    public void RelatedPagesIntro_ReturnsTextBeforeFirstBullet()
        => Assert.Equal(
            "以下頁面依你的權限顯示。",
            PageHelpMarkdownParser.RelatedPagesIntro("以下頁面依你的權限顯示。\n\n- [專案項目](/projects)：管理專案"));

    [Fact]
    public void FilterVisibleRelatedPages_HidesUnauthorizedMenuPages_KeepsNonMenuPages()
    {
        var related = new[]
        {
            new PageHelpRelatedPage("專案項目", "/projects", "a"),
            new PageHelpRelatedPage("使用者管理", "/myusers", "b"),
            new PageHelpRelatedPage("變更密碼", "/ChangePassword", "c"),
        };
        var allMenuUrls = new HashSet<string>(["projects", "myusers"], StringComparer.OrdinalIgnoreCase);
        var authorizedMenuUrls = new HashSet<string>(["Projects"], StringComparer.OrdinalIgnoreCase);

        var visible = PageHelpService.FilterVisibleRelatedPages(related, allMenuUrls, authorizedMenuUrls);

        Assert.Equal(["/projects", "/ChangePassword"], visible.Select(x => x.Route));
    }

    [Fact]
    public void CollectUrls_WalksNestedMenu_AndNormalizes()
    {
        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        SidebarMenuService.CollectUrls(
            [
                new SidebarMenuItemModel { Url = "/App" },
                new SidebarMenuItemModel
                {
                    SubMenu = [new SidebarMenuItemModel { SubMenu = [new SidebarMenuItemModel { Url = "/logs" }] }],
                },
            ],
            urls);

        Assert.Equal(["App", "logs"], urls.Order(StringComparer.Ordinal));
    }

    private static readonly List<PageHelpTopicModel> Topics =
    [
        new() { Route = "/projects", Title = "專案項目", File = "projects.md" },
        new() { Route = "/ChangePassword", Title = "變更密碼", File = "changepassword.md" },
    ];
}

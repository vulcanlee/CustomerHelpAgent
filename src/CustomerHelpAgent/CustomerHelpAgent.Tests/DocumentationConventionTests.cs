using System.Text.Json;
using System.Text.RegularExpressions;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 守住兩件「文件與程式不同步、但不會有任何人發現」的事。
///
/// 兩條都是靜默失效：建置不會紅、<c>dotnet format</c> 看不到 Markdown 與 XML、
/// <c>TreatWarningsAsErrors</c> 也管不到。只有實際去用那個功能才會發現。
/// </summary>
public sealed class DocumentationConventionTests
{
    /// <summary>
    /// <c>appsettings.json</c> 的每一個頂層區段，都必須同時出現在兩份設定文件裡。
    ///
    /// ⚠️ 這兩份文件**刻意重複**：`日誌與設定檔說明.md` 是維運視角的逐鍵參考，
    /// `腳手架開發指引.md` §4 是開發者從頭讀的入口。重複是產品決定，
    /// 代價就是「加了新區段只更新一份」會讓兩邊說法不一致 —— 這條測試就是擋這個。
    ///
    /// 為什麼比對「區段」而不是「逐鍵」：`Provider`、`Model`、`Enabled` 這種鍵名太通用，
    /// 逐鍵比對會大量誤報；區段名稱（`AiPricingSettings`、`BootstrapSettings`…）夠獨特，
    /// 而真正會發生的失誤就是漏掉一整個新區段。
    /// </summary>
    [Fact]
    public void AppSettingsSections_ShouldBeDocumentedInBothGuides()
    {
        var webRoot = FindWebRoot();
        var docsRoot = FindDocsRoot();

        var appSettingsPath = Path.Combine(webRoot, "appsettings.json");
        Assert.True(File.Exists(appSettingsPath), $"找不到 {appSettingsPath}。");

        using var document = JsonDocument.Parse(File.ReadAllText(appSettingsPath));
        var sections = document.RootElement
            .EnumerateObject()
            .Select(property => property.Name)
            .ToList();

        // 掃描失效時（例如檔案被清空）不要空跑綠燈。
        Assert.NotEmpty(sections);

        var guides = new Dictionary<string, string>
        {
            ["guides/腳手架開發指引.md"] = Path.Combine(docsRoot, "guides", "腳手架開發指引.md"),
            ["operations/日誌與設定檔說明.md"] = Path.Combine(docsRoot, "operations", "日誌與設定檔說明.md"),
        };

        var violations = new List<string>();

        foreach (var (label, path) in guides)
        {
            Assert.True(File.Exists(path), $"找不到 {path}。");
            var content = File.ReadAllText(path);

            violations.AddRange(sections
                .Where(section => content.Contains(section, StringComparison.Ordinal) == false)
                .Select(section => $"{label} 沒有提到區段「{section}」"));
        }

        Assert.True(
            violations.Count == 0,
            "appsettings.json 的每個頂層區段都必須同時出現在兩份設定文件裡。"
                + "兩份文件刻意重複（一份維運視角、一份開發入口），所以新增區段時兩邊都要寫。"
                + Environment.NewLine
                + string.Join(Environment.NewLine, violations));
    }

    /// <summary>
    /// <c>nlog.config</c> 的 layout 欄位數，必須與 <c>LogQueryService</c> 的
    /// <c>FieldCount</c> 相同。
    ///
    /// ⚠️ 擋的是一個非常安靜的地雷：日誌檢視頁（<c>/logs</c>）是逐行切 <c>|</c> 來解析日誌的，
    /// 欄位數寫死在程式裡。改了 nlog.config 的 layout 之後，那一頁會**查不到任何東西**，
    /// 而且不會有例外、不會有錯誤訊息 —— 看起來就像「最近沒有日誌」。
    ///
    /// 這條測試以正規表示式從原始碼讀 <c>FieldCount</c>，而不是把它改成 internal ——
    /// 為了一個測試放寬產品程式的可見範圍並不划算，而且這個常數本來就只該有一個讀者。
    /// </summary>
    [Fact]
    public void NLogLayout_ShouldMatchLogQueryServiceFieldCount()
    {
        var webRoot = FindWebRoot();

        var nlogPath = Path.Combine(webRoot, "nlog.config");
        Assert.True(File.Exists(nlogPath), $"找不到 {nlogPath}。");
        var nlogContent = File.ReadAllText(nlogPath);

        // 檔案裡有兩個 layout（file 與 console），只比對寫進檔案的那一個 ——
        // /logs 讀的是檔案，console 的欄位順序刻意不同。
        var fileTarget = Regex.Match(
            nlogContent,
            @"<target\s+name=""file""[^>]*?layout=""(?<layout>[^""]+)""",
            RegexOptions.Singleline);

        Assert.True(
            fileTarget.Success,
            "在 nlog.config 找不到 name=\"file\" 這個 target 的 layout 屬性。"
                + "target 名稱或屬性順序若有調整，請一併更新這條測試。");

        var layoutFieldCount = fileTarget.Groups["layout"].Value.Split('|').Length;

        var servicePath = Path.Combine(webRoot, "Diagnostics", "LogQueryService.cs");
        Assert.True(File.Exists(servicePath), $"找不到 {servicePath}。");

        var declared = Regex.Match(
            File.ReadAllText(servicePath),
            @"FieldCount\s*=\s*(?<count>\d+)");

        Assert.True(
            declared.Success,
            "在 LogQueryService.cs 找不到 FieldCount 的宣告。常數若被改名，請一併更新這條測試。");

        var expected = int.Parse(declared.Groups["count"].Value);

        Assert.True(
            layoutFieldCount == expected,
            $"nlog.config 的 file target layout 有 {layoutFieldCount} 個欄位，"
                + $"但 LogQueryService.FieldCount 是 {expected}。"
                + "日誌檢視頁（/logs）靠切 | 解析每一行，兩者不一致會讓它查不到任何東西，"
                + "而且不會有任何錯誤訊息。改 layout 時請同步 "
                + "LogQueryService 的 FieldCount／TimestampLength／TimestampFormat。");
    }

    private static string FindWebRoot() => FindUp("CustomerHelpAgent.Web");

    private static string FindDocsRoot() => FindUp("docs");

    private static string FindUp(string relativeName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relativeName);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            var srcCandidate = Path.Combine(dir.FullName, "src", "CustomerHelpAgent", relativeName);
            if (Directory.Exists(srcCandidate))
            {
                return srcCandidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException($"找不到 {relativeName}。");
    }
}

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 驗證 Components 下的 .razor 不再以 emoji 當作按鈕圖示。
///
/// 背景：專案的圖示慣例是 BlazorMaterialIcons（見 readme.md 技術堆疊表），
/// 按鈕圖示一律透過共用元件呈現 —— 工具列用 ToolbarIconButton、表格操作欄用
/// CrudActionButton。2026-06-22 的移植只把操作欄的 emoji 換掉，工具列漏掉，
/// 之後新頁面靠複製既有檢視建立，emoji 就一路擴散到六個檢視共 22 個按鈕。
///
/// 文件與 copilot-instructions 只是建議，唯有測試會擋下 PR，因此以此測試守門。
/// </summary>
public sealed class ButtonIconConventionTests
{
    /// <summary>
    /// 過去被當成按鈕圖示使用的 emoji。新增時請一併補進此清單。
    /// </summary>
    private static readonly (string Glyph, string Suggestion)[] ForbiddenGlyphs =
    [
        ("➕", "add"),                  // 新增
        ("\U0001F504", "refresh"),          // 重新整理
        ("❌", "close"),                // 清空搜尋
        ("\U0001F50D", "search"),           // 搜尋
        ("⬇", "file_download"),        // 匯出下載
        ("✏", "edit"),                 // 修改
        ("\U0001F5D1", "delete"),           // 刪除
    ];

    /// <summary>
    /// 已知且允許的例外：Blazor 錯誤 UI 的關閉連結由框架樣板產生，
    /// 是 &lt;a&gt; 而非按鈕，且不使用 Material Icons。
    /// </summary>
    private static readonly HashSet<string> ExemptFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "EmptyLayout.razor",
        "NoFooterLayout.razor",
    };

    [Fact]
    public void RazorComponents_ShouldNotUseEmojiAsButtonIcons()
    {
        var componentsRoot = FindComponentsRoot();
        var razorFiles = Directory.EnumerateFiles(componentsRoot, "*.razor", SearchOption.AllDirectories)
            .Where(path => !ExemptFiles.Contains(Path.GetFileName(path)))
            .ToList();

        Assert.NotEmpty(razorFiles);

        var violations = new List<string>();

        foreach (var file in razorFiles)
        {
            var lines = File.ReadAllLines(file);
            for (var index = 0; index < lines.Length; index++)
            {
                foreach (var (glyph, suggestion) in ForbiddenGlyphs)
                {
                    if (lines[index].Contains(glyph, StringComparison.Ordinal))
                    {
                        violations.Add(
                            $"{Path.GetFileName(file)}:{index + 1} 使用了 emoji 圖示 '{glyph}'，" +
                            $"請改用 <ToolbarIconButton Icon=\"{suggestion}\" />（工具列）" +
                            $"或 <CrudActionButton Icon=\"{suggestion}\" />（操作欄）。");
                    }
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "發現以 emoji 作為按鈕圖示，違反專案圖示慣例（BlazorMaterialIcons）："
                + Environment.NewLine
                + string.Join(Environment.NewLine, violations));
    }

    private static string FindComponentsRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "CustomerHelpAgent.Web", "Components");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            var srcCandidate = Path.Combine(dir.FullName, "src", "CustomerHelpAgent", "CustomerHelpAgent.Web", "Components");
            if (Directory.Exists(srcCandidate))
            {
                return srcCandidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("找不到 CustomerHelpAgent.Web/Components。");
    }
}

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 有單筆「刪除」的列表頁都要能勾選多筆後「刪除選取」（0.9.132 起）。
///
/// 新頁面多半是複製既有檢視或用 New-CrudModule.ps1 產生；漏了勾選欄或按鈕不會壞、不會紅，
/// 只會讓這一頁和其他頁不一致，因此以測試守門：操作欄有 <c>CrudActionButton Title="刪除"</c>，
/// 就必須同時有 <c>&lt;Selection</c> 與 <c>&lt;BatchDeleteButton</c>。
/// </summary>
public sealed class BatchDeleteConventionTests
{
    [Fact]
    public void ViewsWithRowDelete_ShouldOfferBatchDelete()
    {
        var componentsRoot = FindComponentsRoot();
        var views = Directory.EnumerateFiles(componentsRoot, "*.razor", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains("CrudActionButton Title=\"刪除\"", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(views);

        var violations = views
            .Select(path => (Name: Path.GetFileName(path), Text: File.ReadAllText(path)))
            .Where(x => !x.Text.Contains("<Selection", StringComparison.Ordinal)
                        || !x.Text.Contains("<BatchDeleteButton", StringComparison.Ordinal))
            .Select(x => $"{x.Name} 有單筆刪除，但缺少勾選欄 <Selection /> 或工具列的 <BatchDeleteButton />。")
            .ToList();

        Assert.True(violations.Count == 0,
            "列表頁的多筆刪除不一致（見 docs/architecture/開發慣例與限制速查.md「多筆刪除」）："
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

using System.Text.RegularExpressions;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 驗證所有 UI 上的登出入口都會先問過使用者。
///
/// 登出有三個入口（使用者選單、側邊欄展開、側邊欄收合），0.9.29 之前全都是直接連到
/// <c>/auths/logout</c> —— 按下去就登出，沒有任何確認。只改其中一個的話，
/// 從另外兩個登出仍然會無聲登出，而那種缺失只有實際點到才會發現。
///
/// 因此規則是：**razor 裡不得出現寫死的登出網址連結**，
/// 一律走 <c>Components/Commons/LogoutConfirm.cs</c>。
///
/// ⚠️ 這條規則只管 UI 入口。<c>AuthenticationStateHelper</c> 的六處程式化強制登出
/// （未登入、找不到使用者、角色遺失…）**刻意不確認** —— 那是系統行為不是使用者意圖，
/// 對 session 已經失效的人跳出「確定要登出嗎？」只會讓他卡住。
/// 那些程式碼在 CustomerHelpAgent.Business，依分層根本參照不到 Web 的 LogoutConfirm，
/// 構造上就不可能誤走確認流程。
/// </summary>
public sealed class LogoutConfirmationConventionTests
{
    /// <summary>登出網址的唯一真相來源是 <c>MagicObjectHelper.SignoutUrl</c>。</summary>
    private static readonly Regex HardCodedLogoutLink = new(
        @"(href|RouterLink)\s*=\s*""[^""]*auths/logout",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [Fact]
    public void RazorComponents_ShouldNotLinkDirectlyToLogout()
    {
        var componentsRoot = FindComponentsRoot();
        var razorFiles = Directory
            .EnumerateFiles(componentsRoot, "*.razor", SearchOption.AllDirectories)
            .ToList();

        // 掃描路徑若失效，測試會空跑綠燈，等於沒有守門。
        Assert.NotEmpty(razorFiles);

        var violations = new List<string>();

        foreach (var file in razorFiles)
        {
            var lines = File.ReadAllLines(file);
            for (var index = 0; index < lines.Length; index++)
            {
                if (HardCodedLogoutLink.IsMatch(lines[index]))
                {
                    violations.Add($"{Path.GetFileName(file)}:{index + 1} {lines[index].Trim()}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "UI 上的登出入口不得直接連到登出網址 —— 那樣按下去就登出，沒有二次確認。"
                + "請改用 Components/Commons/LogoutConfirm.RequestAsync，"
                + "並以 LogoutConfirm.IsLogoutUrl 判斷選單項是不是登出。"
                + Environment.NewLine
                + string.Join(Environment.NewLine, violations));
    }

    /// <summary>
    /// 三個登出入口的確認行為必須由同一個樣板提供，不可各自寫一份。
    /// </summary>
    [Fact]
    public void LogoutEntryPoints_ShouldUseTheSharedTemplate()
    {
        var componentsRoot = FindComponentsRoot();
        var users = Directory
            .EnumerateFiles(componentsRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => File.ReadAllText(file).Contains("LogoutConfirm.RequestAsync", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(
            users.Count >= 3,
            "登出有三個 UI 入口（使用者選單、側邊欄展開、側邊欄收合），都必須走 LogoutConfirm。"
                + $"目前只有 {users.Count} 處呼叫："
                + Environment.NewLine
                + (users.Count == 0 ? "（無）" : string.Join("、", users)));
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

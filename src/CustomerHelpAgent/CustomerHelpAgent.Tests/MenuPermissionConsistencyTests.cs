using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Web.Components.Layout;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 選單權限的宣告式對應共有「四方」，必須彼此一致：
///
///   1. Datas/Menu.json 的每個節點 id
///   2. SidebarMenuService.MenuPermissionMap 的 id → 權限鍵
///   3. MagicObjectHelper 的權限鍵常數
///   4. 各檢視 OnInitializedAsync 內實際呼叫的 CheckAccessPage(權限鍵)
///
/// 原本只有前三方被守住（id 一一對應、無錯位），第四方沒人管，結果是：
/// ProjectViewView 用的是「群組鍵」而非葉節點鍵，只被授予「專案項目」的角色
/// 看得到選單、點進去卻被踢出。本測試把第四方一併納管。
///
/// 另外守住權限鍵常數不得帶前後空白 —— 曾有兩個常數帶尾隨空白，
/// 讓 RBAC 種資料與 RoleList.Contains 的 Ordinal 比對都跟著帶進髒字串。
/// </summary>
public sealed class MenuPermissionConsistencyTests
{
    /// <summary>
    /// 檢視檔名 → 該檢視所屬選單節點 id。
    /// 新增受權限控管的檢視時，於此登錄一筆，測試便會驗證它用對權限鍵。
    /// </summary>
    private static readonly IReadOnlyDictionary<string, int> ViewToMenuId = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["HomeWelcomeView.razor.cs"] = 1,
        ["ProjectViewView.razor.cs"] = 21,
        ["CategoryViewView.razor.cs"] = 51,
        ["TeamViewView.razor.cs"] = 52,
        ["AfterSalesReviewView.razor.cs"] = 91,
        ["AfterSalesPolicyView.razor.cs"] = 92,
        ["AgentRunsView.razor.cs"] = 93,
        ["BenefitComparisonView.razor.cs"] = 94,
    };

    /// <summary>
    /// 管理員專屬檢視：刻意以 CheckIsAdmin() 守門，不使用 CheckAccessPage。
    /// 對應的權限鍵不會上架角色矩陣（見 AdminOnlyPermissionTests）。
    /// </summary>
    private static readonly IReadOnlySet<string> AdminOnlyViews = new HashSet<string>(StringComparer.Ordinal)
    {
        "MyUserView.razor.cs",
        "RoleViewView.razor.cs",
        "LogViewerView.razor.cs",
        "DatabaseUsageView.razor.cs",
        "LogLevelSettingView.razor.cs",
        "ExceptionLogView.razor.cs",
        "TokenUsageView.razor.cs",
        "AiCallLogView.razor.cs",
        "AuditLogView.razor.cs",
        "ScheduledJobView.razor.cs",
        "SystemParameterView.razor.cs",
        "BackupView.razor.cs",
        "DataRetentionView.razor.cs",
        "DemoConsoleView.razor.cs",
        "AnnouncementView.razor.cs",
        "PromptTemplateView.razor.cs",
    };

    [Fact]
    public void MenuJsonIds_ShouldExactlyMatch_MenuPermissionMapKeys()
    {
        var menuIds = ReadMenuJsonIds();
        var mapKeys = SidebarMenuService.MenuPermissionMap.Keys.ToHashSet();

        Assert.NotEmpty(menuIds);

        var missingInMap = menuIds.Except(mapKeys).OrderBy(x => x).ToList();
        var missingInMenu = mapKeys.Except(menuIds).OrderBy(x => x).ToList();

        Assert.True(
            missingInMap.Count == 0,
            $"Menu.json 有 id 未在 SidebarMenuService.MenuPermissionMap 登錄：{string.Join(", ", missingInMap)}。");
        Assert.True(
            missingInMenu.Count == 0,
            $"MenuPermissionMap 有 id 不存在於 Menu.json：{string.Join(", ", missingInMenu)}。");
    }

    [Fact]
    public void MenuPermissionMapValues_ShouldAllBe_MagicObjectHelperConstants()
    {
        var constants = PermissionKeyConstants().Values.ToHashSet(StringComparer.Ordinal);

        foreach (var (id, key) in SidebarMenuService.MenuPermissionMap)
        {
            Assert.True(
                constants.Contains(key),
                $"MenuPermissionMap[{id}] 的權限鍵 '{key}' 不是 MagicObjectHelper 的權限鍵常數。");
        }
    }

    [Fact]
    public void PermissionKeyConstants_ShouldNotHaveSurroundingWhitespace()
    {
        foreach (var (name, value) in PermissionKeyConstants())
        {
            Assert.True(
                value == value.Trim(),
                $"MagicObjectHelper.{name} 的值 '{value}' 帶有前後空白。" +
                "權限鍵會被寫進 RBAC 資料表並以 Ordinal 比對，空白會造成授權對不上。");
        }
    }

    [Fact]
    public void Views_CheckAccessPageKey_ShouldMatch_MenuPermissionMap()
    {
        foreach (var (fileName, menuId) in ViewToMenuId)
        {
            var source = File.ReadAllText(FindViewPath(fileName));
            var match = Regex.Match(source, @"CheckAccessPage\(\s*MagicObjectHelper\.(?<key>[^\s)]+)\s*\)");

            Assert.True(match.Success, $"{fileName} 找不到 CheckAccessPage(MagicObjectHelper.*) 的呼叫。");

            var actual = PermissionKeyConstants()[match.Groups["key"].Value];
            var expected = SidebarMenuService.MenuPermissionMap[menuId];

            Assert.True(
                string.Equals(actual, expected, StringComparison.Ordinal),
                $"{fileName} 檢查的權限鍵是 '{actual}'，但選單 id {menuId} 對應的是 '{expected}'。" +
                "兩者不一致會讓使用者看得到選單卻進不去頁面。");
        }
    }

    [Fact]
    public void AdminOnlyViews_ShouldNotUse_CheckAccessPage()
    {
        foreach (var fileName in AdminOnlyViews)
        {
            var source = File.ReadAllText(FindViewPath(fileName));

            Assert.DoesNotContain("CheckAccessPage(", source, StringComparison.Ordinal);
            Assert.Contains("CheckIsAdmin()", source, StringComparison.Ordinal);
        }
    }

    private static Dictionary<string, string> PermissionKeyConstants()
    {
        return typeof(MagicObjectHelper)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .Where(f => f.Name.StartsWith("角色_", StringComparison.Ordinal))
            .ToDictionary(f => f.Name, f => (string)f.GetRawConstantValue()!, StringComparer.Ordinal);
    }

    private static HashSet<int> ReadMenuJsonIds()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FindRepoFile(Path.Combine("CustomerHelpAgent.Web", "Datas", "Menu.json"))));

        var ids = new HashSet<int>();
        foreach (var node in document.RootElement.EnumerateArray())
        {
            CollectIds(node, ids);
        }

        return ids;
    }

    private static void CollectIds(JsonElement node, HashSet<int> ids)
    {
        if (node.TryGetProperty("id", out var idElement) && idElement.TryGetInt32(out var id))
        {
            ids.Add(id);
        }

        if (node.TryGetProperty("subMenu", out var subMenu) && subMenu.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in subMenu.EnumerateArray())
            {
                CollectIds(child, ids);
            }
        }
    }

    private static string FindViewPath(string fileName)
    {
        var viewsRoot = FindRepoDirectory(Path.Combine("CustomerHelpAgent.Web", "Components", "Views"));
        var matches = Directory.GetFiles(viewsRoot, fileName, SearchOption.AllDirectories);

        Assert.True(matches.Length == 1, $"預期在 Components/Views 下找到唯一的 {fileName}，實際找到 {matches.Length} 個。");

        return matches[0];
    }

    private static string FindRepoFile(string relativePath) => Locate(relativePath, File.Exists);

    private static string FindRepoDirectory(string relativePath) => Locate(relativePath, Directory.Exists);

    private static string Locate(string relativePath, Func<string, bool> exists)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (exists(candidate))
            {
                return candidate;
            }

            var srcCandidate = Path.Combine(dir.FullName, "src", "CustomerHelpAgent", relativePath);
            if (exists(srcCandidate))
            {
                return srcCandidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException($"找不到 {relativePath}。");
    }
}

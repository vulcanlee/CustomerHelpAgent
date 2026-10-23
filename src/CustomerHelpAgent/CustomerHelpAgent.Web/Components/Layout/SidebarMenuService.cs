using System.Text.Json;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Web.Caching;

namespace CustomerHelpAgent.Web.Components.Layout;

public sealed class SidebarMenuService
{
    private const string MenuCacheKey = "sidebar:menu:raw";

    /// <summary>
    /// 選單項目 Id → 權限鍵的宣告式對應（取代原本的「位置索引」耦合，重排選單不會錯位）。
    /// 新增受控頁面時，於此加入 Id→權限鍵即可。
    ///
    /// 宣告為 internal 是為了讓 CustomerHelpAgent.Tests 的 MenuPermissionConsistencyTests 能直接讀取，
    /// 驗證它與 Menu.json 的 id 集合、以及各檢視實際使用的權限鍵三者一致。
    /// </summary>
    internal static readonly IReadOnlyDictionary<int, string> MenuPermissionMap = new Dictionary<int, string>
    {
        [1] = MagicObjectHelper.角色_首頁,
        [2] = MagicObjectHelper.角色_專案管理,
        [21] = MagicObjectHelper.角色_專案項目,
        [9] = MagicObjectHelper.角色_售後服務,
        [91] = MagicObjectHelper.角色_售後審核,
        [92] = MagicObjectHelper.角色_售後規則,
        // 系統管理群組與其下四個子群組（帳號與權限、監控與診斷、AI 管理、系統設定與維運）的權限鍵，刻意不列入
        // RolePermissionService.GetRoleListPermissionAllName()，因此不會種出 Permission 資料列、
        // 任何角色都無法被授予，只有管理員短路能通過。詳見 MagicObjectHelper。
        // 系統管理底下只放子群組、不放單一頁面（0.9.113 起；New-CrudModule.ps1 也擋 -MenuGroupId 3）。
        [3] = MagicObjectHelper.角色_系統管理,
        [34] = MagicObjectHelper.角色_帳號與權限,
        [31] = MagicObjectHelper.角色_使用者管理,
        [32] = MagicObjectHelper.角色_角色管理,
        [35] = MagicObjectHelper.角色_稽核紀錄,
        [6] = MagicObjectHelper.角色_監控與診斷,
        [65] = MagicObjectHelper.角色_系統健康監控,
        [61] = MagicObjectHelper.角色_日誌檢視,
        [63] = MagicObjectHelper.角色_日誌等級設定,
        [33] = MagicObjectHelper.角色_系統例外紀錄,
        [62] = MagicObjectHelper.角色_資料庫用量,
        [7] = MagicObjectHelper.角色_AI管理,
        [39] = MagicObjectHelper.角色_AI提示詞,
        [66] = MagicObjectHelper.角色_AI對話紀錄,
        [64] = MagicObjectHelper.角色_Token用量,
        [8] = MagicObjectHelper.角色_系統設定與維運,
        [36] = MagicObjectHelper.角色_系統參數,
        [38] = MagicObjectHelper.角色_公告管理,
        [67] = MagicObjectHelper.角色_排程作業,
        [37] = MagicObjectHelper.角色_系統備份,
        [68] = MagicObjectHelper.角色_資料保留與清理,
        [5] = MagicObjectHelper.角色_資料定義,
        [51] = MagicObjectHelper.角色_分類清單,
        [52] = MagicObjectHelper.角色_團隊清單,
        [4] = MagicObjectHelper.角色_登出,
    };

    private readonly IWebHostEnvironment environment;
    private readonly ILogger<SidebarMenuService> logger;
    private readonly ICacheService cacheService;

    public SidebarMenuService(
        IWebHostEnvironment environment,
        ILogger<SidebarMenuService> logger,
        ICacheService cacheService)
    {
        this.environment = environment;
        this.logger = logger;
        this.cacheService = cacheService;
    }

    public async Task<IReadOnlyList<SidebarMenuItemModel>> LoadAuthorizedMenuItemsAsync(AuthenticationStateHelper authenticationStateHelper)
    {
        var items = await LoadMenuItemsAsync();
        var permissionMappedItems = ApplyPermissionStructure(items);
        var authorizedItems = FilterAuthorizedMenuItems(permissionMappedItems, authenticationStateHelper);

        logger.LogDebug("Loaded authorized sidebar menu successfully. ItemCount={ItemCount}", authorizedItems.Count);
        return authorizedItems;
    }

    /// <summary>
    /// Menu.json 內所有網址（不過濾權限）。頁面操作說明的「相關頁面」用它區分
    /// 「受選單權限控管、但此人無權」與「根本不在選單裡」（例如 /ChangePassword）兩種情況。
    /// </summary>
    public async Task<IReadOnlySet<string>> LoadAllMenuUrlsAsync()
    {
        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectUrls(await LoadMenuItemsAsync(), urls);
        return urls;
    }

    internal static void CollectUrls(IEnumerable<SidebarMenuItemModel> items, ISet<string> urls)
    {
        foreach (var item in items)
        {
            if (!string.IsNullOrWhiteSpace(item.Url))
            {
                urls.Add(PageHelpService.NormalizePath(item.Url));
            }

            CollectUrls(item.SubMenu, urls);
        }
    }

    private async Task<IReadOnlyList<SidebarMenuItemModel>> LoadMenuItemsAsync()
        => await cacheService.GetOrCreateAsync<List<SidebarMenuItemModel>>(
            MenuCacheKey,
            () => Task.FromResult(ReadMenuItemsFromDisk().ToList()));

    private IReadOnlyList<SidebarMenuItemModel> ReadMenuItemsFromDisk()
    {
        var menuFilePath = Path.Combine(environment.ContentRootPath, MagicObjectHelper.Menu結構定義);
        if (!File.Exists(menuFilePath))
        {
            logger.LogWarning("Sidebar menu file not found: {MenuFilePath}", menuFilePath);
            return [];
        }

        try
        {
            using var stream = File.OpenRead(menuFilePath);
            return JsonSerializer.Deserialize<List<SidebarMenuItemModel>>(stream, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? [];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load sidebar menu from {MenuFilePath}", menuFilePath);
            return [];
        }
    }

    private IReadOnlyList<SidebarMenuItemModel> ApplyPermissionStructure(IReadOnlyList<SidebarMenuItemModel> items)
    {
        var result = new List<SidebarMenuItemModel>(items.Count);

        foreach (var item in items)
        {
            var permissionName = MenuPermissionMap.TryGetValue(item.Id, out var mapped) ? mapped : item.Name;
            var subMenu = item.HasChildren
                ? ApplyPermissionStructure(item.SubMenu).ToList()
                : new List<SidebarMenuItemModel>();

            result.Add(item.CloneWith(subMenu, permissionName));
        }

        return result;
    }

    private List<SidebarMenuItemModel> FilterAuthorizedMenuItems(
        IReadOnlyList<SidebarMenuItemModel> items,
        AuthenticationStateHelper authenticationStateHelper)
    {
        var result = new List<SidebarMenuItemModel>(items.Count);

        foreach (var item in items)
        {
            var filteredChildren = item.HasChildren
                ? FilterAuthorizedMenuItems(item.SubMenu, authenticationStateHelper)
                : [];

            var permissionNames = GetPermissionNames(item);
            var hasPermission = permissionNames.Count == 0
                || permissionNames.Any(authenticationStateHelper.CheckAccessPage);

            if (!hasPermission && filteredChildren.Count == 0)
            {
                continue;
            }

            result.Add(item.CloneWith(filteredChildren));
        }

        return result;
    }

    private static List<string> GetPermissionNames(SidebarMenuItemModel item)
    {
        var result = new List<string>();

        if (!string.IsNullOrWhiteSpace(item.Name))
        {
            result.Add(item.Name);
        }

        if (!string.IsNullOrWhiteSpace(item.PermissionName)
            && result.Contains(item.PermissionName, StringComparer.Ordinal) == false)
        {
            result.Add(item.PermissionName);
        }

        return result;
    }
}

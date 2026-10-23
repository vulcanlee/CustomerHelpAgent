using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Web.Dashboard;

namespace CustomerHelpAgent.Web.Components.Views.Commons;

/// <summary>
/// 登入後首頁（/App）：精簡的歡迎列（品牌、系統名稱與說明、版本）、快速入口，以及依權限過濾的小工具區（0.9.106 起取代原本的能力介紹卡片）。
/// 小工具的權限過濾用登記時的權限鍵（<see cref="DashboardWidgetCatalog.VisibleTo"/>），不在這裡另外寫頁面權限檢查的常數呼叫 ——
/// <c>MenuPermissionConsistencyTests</c> 以第一個出現的那種呼叫比對選單權限（連註解裡的也算）。
/// </summary>
public partial class HomeWelcomeView
{
    /// <summary>快速入口。<paramref name="PermissionKey"/> 用於濾掉目前使用者沒有權限的項目。</summary>
    private sealed record QuickLink(string Url, string Icon, string Title, string PermissionKey);

    /// <summary>
    /// ⚠️ 圖示名稱必須是 <b>classic Material Icons</b>（App.razor 載入的是 Material Icons，
    /// 不是 Material Symbols）。用 Symbols 專有名稱會渲染失敗。見開發慣例速查 §6.1。
    /// </summary>
    private static readonly QuickLink[] AllQuickLinks =
    [
        new("/projects", "workspaces", "專案項目", MagicObjectHelper.角色_專案項目),
        new("/categories", "category", "分類清單", MagicObjectHelper.角色_分類清單),
        new("/teams", "groups", "團隊清單", MagicObjectHelper.角色_團隊清單),
    ];

    [Inject]
    public AuthenticationStateProvider authStateProvider { get; set; } = default!;

    [Inject]
    public AuthenticationStateHelper AuthenticationStateHelper { get; set; } = default!;

    [Inject]
    public NavigationManager NavigationManager { get; set; } = default!;

    [Inject]
    public ILogger<HomeWelcomeView> Logger { get; set; } = default!;

    [Inject]
    public ISystemIdentity SystemIdentity { get; set; } = default!;

    [Inject]
    public IWebHostEnvironment WebHostEnvironment { get; set; } = default!;

    [Inject]
    public DashboardWidgetCatalog WidgetCatalog { get; set; } = default!;

    [Inject]
    public CurrentUserService CurrentUserService { get; set; } = default!;

    /// <summary>系統名稱，取自 ISystemIdentity（可在「系統參數」頁修改，0.9.98 起）。</summary>
    private string SystemName => SystemIdentity.Name;

    /// <summary>系統簡短說明，取自 ISystemIdentity（可在「系統參數」頁修改，0.9.98 起）。</summary>
    private string SystemDescription => SystemIdentity.Description;

    /// <summary>系統版本，唯一來源同上（SystemVersion）。</summary>
    private string SystemVersion => SystemIdentity.Version;

    private string RoleMessage = string.Empty;
    private bool isAccessChecked;

    private IReadOnlyList<QuickLink> quickLinks = [];
    private IReadOnlyList<DashboardWidgetDescriptor> widgets = [];

    protected override async Task OnInitializedAsync()
    {
        Logger.LogDebug("Initializing home welcome view.");

        var checkResult = await AuthenticationStateHelper.Check(authStateProvider, NavigationManager);
        if (checkResult != AuthenticationCheckResult.Succeeded)
        {
            Logger.LogWarning("Home welcome view initialization stopped because authentication check failed.");
            return;
        }

        isAccessChecked = true;

        if (AuthenticationStateHelper.CheckAccessPage(MagicObjectHelper.角色_首頁) == false)
        {
            RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
            await AuthenticationStateHelper.RecordPageAccessDeniedAsync("/");
            Logger.LogWarning("Home welcome view denied because current user has not this role permission.");
            return;
        }

        quickLinks = [.. AllQuickLinks.Where(x => AuthenticationStateHelper.CheckAccessPage(x.PermissionKey))];
        widgets = WidgetCatalog.VisibleTo(CurrentUserService.CurrentUser.IsAdmin, AuthenticationStateHelper.CheckAccessPage);
    }
}

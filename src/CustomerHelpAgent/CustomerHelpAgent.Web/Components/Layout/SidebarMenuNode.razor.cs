using AntDesign;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using CustomerHelpAgent.Web.Components.Commons;
namespace CustomerHelpAgent.Web.Components.Layout;

public partial class SidebarMenuNode : ComponentBase
{
    [Parameter, EditorRequired]
    public SidebarMenuItemModel Item { get; set; } = default!;

    [Parameter]
    public int Level { get; set; }

    [Parameter, EditorRequired]
    public string ItemKey { get; set; } = default!;

    [Inject]
    private ModalService ModalService { get; set; } = default!;

    [Inject]
    private NavigationManager NavigationManager { get; set; } = default!;

    /// <summary>
    /// 使用者在確認窗按下「取消」時通知上層。
    ///
    /// ⚠️ 少了這個回報，登出項會留著選中樣式：AntDesign 的 Menu 預設 Selectable=true，
    /// 點下去就先把該項標成選中，而取消登出不會產生 LocationChanged，
    /// NavMenu 的 SyncMenuStateFromRoute() 也就不會被觸發去清掉它。
    /// </summary>
    [Parameter]
    public EventCallback OnLogoutCancelled { get; set; }

    /// <summary>側邊欄展開狀態的登出入口。行為與使用者選單、收合側邊欄一致。</summary>
    private async Task OnLogoutClickAsync()
    {
        if (await LogoutConfirm.RequestAsync(ModalService, NavigationManager) == false)
        {
            await OnLogoutCancelled.InvokeAsync();
        }
    }

    private string GetMenuKey()
    {
        return ItemKey;
    }

    private NavLinkMatch GetMatchMode()
    {
        return string.Equals(Item.Url, "/", StringComparison.Ordinal) ? NavLinkMatch.All : NavLinkMatch.Prefix;
    }

    private string GetMaterialIconKind()
    {
        var icon = Item.Icon?.Trim();

        if (string.IsNullOrWhiteSpace(icon))
        {
            return Item.HasChildren ? "folder_open" : "article";
        }

        return icon switch
        {
            "home" => "home",
            "dashboard" => "space_dashboard",
            "admin" => "admin_panel_settings",
            "users" => "group",
            "roles" => "security",
            "shield_person" => "security",
            "setting" => "settings",
            "ProjectFilled" => "workspaces",
            "CarryOutFilled" => "checklist",
            "EnterOutlined" => "event",
            _ => icon
        };
    }
}

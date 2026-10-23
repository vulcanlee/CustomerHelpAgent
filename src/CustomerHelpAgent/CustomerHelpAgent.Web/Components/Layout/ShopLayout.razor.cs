using AntDesign;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Web.Components.Commons;

namespace CustomerHelpAgent.Web.Components.Layout;

/// <summary>
/// 顧客商店版面（售後展示 M1）。登入閘門與 <see cref="MainLayout"/> 相同：檢查完成前不渲染；
/// 展示者若同時具後台首頁權限，另顯示「回到後台」。
/// </summary>
public partial class ShopLayout : LayoutComponentBase
{
    private const string DefaultUserDisplayName = "顧客";

    [Inject]
    private NavigationManager NavigationManager { get; set; } = default!;

    [Inject]
    private AuthenticationStateHelper AuthenticationStateHelper { get; set; } = default!;

    [Inject]
    private AuthenticationStateProvider AuthenticationStateProvider { get; set; } = default!;

    [Inject]
    private CurrentUserService CurrentUserService { get; set; } = default!;

    [Inject]
    private ModalService ModalService { get; set; } = default!;

    [Inject]
    private ILogger<ShopLayout> Logger { get; set; } = default!;

    private bool isAuthenticated;
    private bool canOpenBackOffice;
    private string userDisplayName = DefaultUserDisplayName;

    protected override async Task OnInitializedAsync()
    {
        Logger.LogDebug("Initializing shop layout.");

        var checkResult = await AuthenticationStateHelper.Check(AuthenticationStateProvider, NavigationManager);
        if (checkResult != AuthenticationCheckResult.Succeeded)
        {
            return;
        }

        var currentUser = CurrentUserService.CurrentUser;
        userDisplayName = !string.IsNullOrWhiteSpace(currentUser.Name)
            ? currentUser.Name
            : !string.IsNullOrWhiteSpace(currentUser.Account)
                ? currentUser.Account
                : DefaultUserDisplayName;
        canOpenBackOffice = AuthenticationStateHelper.CheckAccessPage(MagicObjectHelper.角色_首頁);
        isAuthenticated = true;
    }

    /// <summary>使用者主動登出：一律經 <see cref="LogoutConfirm"/> 二次確認（LogoutConfirmationConventionTests）。</summary>
    private async Task OnLogoutClickAsync()
    {
        await LogoutConfirm.RequestAsync(ModalService, NavigationManager);
    }
}

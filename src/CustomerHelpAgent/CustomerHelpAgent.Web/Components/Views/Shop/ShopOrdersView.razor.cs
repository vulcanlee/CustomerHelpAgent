using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Shop;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Share.Shop;

namespace CustomerHelpAgent.Web.Components.Views.Shop;

/// <summary>顧客「我的訂單」（/shop）：卡片列出本人的訂單與商品，每個商品可前往申請售後。</summary>
public partial class ShopOrdersView
{
    [Inject]
    public AuthenticationStateProvider authStateProvider { get; set; } = default!;

    [Inject]
    public AuthenticationStateHelper AuthenticationStateHelper { get; set; } = default!;

    [Inject]
    public NavigationManager NavigationManager { get; set; } = default!;

    [Inject]
    public CurrentUserService CurrentUserService { get; set; } = default!;

    [Inject]
    public ShopOrderService ShopOrderService { get; set; } = default!;

    [Inject]
    public ProcessMetricService ProcessMetricService { get; set; } = default!;

    [Inject]
    public ILogger<ShopOrdersView> Logger { get; set; } = default!;

    /// <summary>這個頁面實例的流程埋點識別碼。</summary>
    private readonly Guid caseKey = Guid.NewGuid();

    private string RoleMessage = string.Empty;
    private bool isAccessChecked;

    /// <summary>null 表示仍在載入。</summary>
    private IReadOnlyList<ShopOrderSummary>? orders;

    protected override async Task OnInitializedAsync()
    {
        Logger.LogDebug("Initializing shop orders view.");

        var checkResult = await AuthenticationStateHelper.Check(authStateProvider, NavigationManager);
        if (checkResult != AuthenticationCheckResult.Succeeded)
        {
            Logger.LogWarning("Shop orders view initialization stopped because authentication check failed.");
            return;
        }

        isAccessChecked = true;

        if (AuthenticationStateHelper.CheckAccessPage(MagicObjectHelper.角色_顧客商店) == false)
        {
            RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
            await AuthenticationStateHelper.RecordPageAccessDeniedAsync("/shop");
            Logger.LogWarning("Shop orders view denied because current user has not this role permission.");
            return;
        }

        var userId = CurrentUserService.CurrentUser.Id;
        orders = await ShopOrderService.GetMyOrdersAsync(userId);
        await ProcessMetricService.RecordAsync(caseKey, AfterSalesChannel.Traditional, ProcessActor.Customer, ProcessMetricActions.ViewOrders, userId);
    }

    private static string FormatDate(DateTime? value) => value?.ToString("yyyy/MM/dd") ?? "尚未簽收";
}

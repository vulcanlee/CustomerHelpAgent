using AntDesign;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Shop;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Share.Shop;
using CustomerHelpAgent.Web.Components.Commons;

namespace CustomerHelpAgent.Web.Components.Views.Shop;

/// <summary>
/// 顧客訂單明細與物流（/shop/orders/{OrderId}）。不是本人的訂單一律顯示「找不到這筆訂單」。
/// 未簽收且已出貨超過 3 天時可「申請催件」：以訂單第一個商品建立 <see cref="AfterSalesType.ShipmentUrge"/> 申請。
/// </summary>
public partial class ShopOrderDetailView
{
    private static readonly TimeSpan UrgeAfter = TimeSpan.FromDays(3);

    private const string UrgeDescription = "已出貨超過 3 天仍未收到包裹，請協助催件。";

    [Parameter]
    public int OrderId { get; set; }

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
    public AfterSalesRequestService AfterSalesRequestService { get; set; } = default!;

    [Inject]
    public ProcessMetricService ProcessMetricService { get; set; } = default!;

    [Inject]
    public NotificationService NotificationService { get; set; } = default!;

    [Inject]
    public TimeProvider TimeProvider { get; set; } = default!;

    [Inject]
    public ILogger<ShopOrderDetailView> Logger { get; set; } = default!;

    /// <summary>這個頁面實例的流程埋點識別碼；催件申請沿用同一個。</summary>
    private readonly Guid caseKey = Guid.NewGuid();

    private string RoleMessage = string.Empty;
    private bool isAccessChecked;
    private bool isLoading = true;
    private ShopOrderDetail? detail;
    private bool canUrge;
    private bool urging;
    private string? urgedRequestNo;

    protected override async Task OnInitializedAsync()
    {
        Logger.LogDebug("Initializing shop order detail view.");

        var checkResult = await AuthenticationStateHelper.Check(authStateProvider, NavigationManager);
        if (checkResult != AuthenticationCheckResult.Succeeded)
        {
            Logger.LogWarning("Shop order detail view initialization stopped because authentication check failed.");
            return;
        }

        isAccessChecked = true;

        if (AuthenticationStateHelper.CheckAccessPage(MagicObjectHelper.角色_顧客商店) == false)
        {
            RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
            await AuthenticationStateHelper.RecordPageAccessDeniedAsync($"/shop/orders/{OrderId}");
            Logger.LogWarning("Shop order detail view denied because current user has not this role permission.");
            return;
        }

        var userId = CurrentUserService.CurrentUser.Id;
        detail = await ShopOrderService.GetMyOrderDetailAsync(userId, OrderId);
        isLoading = false;
        if (detail is null)
        {
            return;
        }

        canUrge = CanRequestUrge(detail, TimeProvider.GetLocalNow().DateTime);
        await ProcessMetricService.RecordAsync(caseKey, AfterSalesChannel.Traditional, ProcessActor.Customer, ProcessMetricActions.ViewShipment, userId);
    }

    /// <summary>未簽收，且出貨時間距今超過 3 天（剛好 3 天不算）。</summary>
    internal static bool CanRequestUrge(ShopOrderDetail detail, DateTime now)
        => detail.Summary.DeliveredAt is null
            && detail.Shipment?.ShippedAt is { } shippedAt
            && now - shippedAt > UrgeAfter;

    private async Task OnUrgeClickAsync()
    {
        if (detail is null || urging || detail.Summary.Items.Count == 0)
        {
            return;
        }

        urging = true;
        try
        {
            var userId = CurrentUserService.CurrentUser.Id;
            var firstItem = detail.Summary.Items.MinBy(x => x.OrderItemId)!;
            var result = await AfterSalesRequestService.SubmitAsync(userId, new SubmitAfterSalesInput
            {
                CaseKey = caseKey,
                OrderItemId = firstItem.OrderItemId,
                Type = AfterSalesType.ShipmentUrge,
                Description = UrgeDescription,
                Channel = AfterSalesChannel.Traditional,
            }, []);

            if (result.Success == false)
            {
                Logger.LogInformation("Shipment urge was not accepted. OrderId={OrderId}", OrderId);
                ViewNotification.Caution(NotificationService, result.Message);
                return;
            }

            await ProcessMetricService.RecordAsync(caseKey, AfterSalesChannel.Traditional, ProcessActor.Customer, ProcessMetricActions.UrgeShipment, userId, result.RequestId);
            urgedRequestNo = result.RequestNo;
            Logger.LogInformation("Shipment urge submitted. OrderId={OrderId}, RequestId={RequestId}", OrderId, result.RequestId);
            ViewNotification.Warning(NotificationService, $"已送出催件申請，申請編號 {result.RequestNo}。");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Shipment urge failed. OrderId={OrderId}", OrderId);
            ViewNotification.UnexpectedError(NotificationService, "送出催件申請失敗，請稍後再試。", ex);
        }
        finally
        {
            urging = false;
        }
    }

    private static string FormatDate(DateTime? value, string fallback) => value?.ToString("yyyy/MM/dd") ?? fallback;
}

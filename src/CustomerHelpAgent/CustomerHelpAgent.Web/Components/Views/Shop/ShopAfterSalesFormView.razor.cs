using AntDesign;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Shop;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Share.Shop;
using CustomerHelpAgent.Web.Components.Commons;

namespace CustomerHelpAgent.Web.Components.Views.Shop;

/// <summary>
/// 顧客售後申請表單（/shop/after-sales/new）。**刻意保留傳統的多步驟**：
/// ①選訂單商品 ②選申請類型 ③依類型填細節 ④問題描述 ⑤送出，作為與 M2 Agent 對照的基準。
/// 表單**不顯示**規則條文與資格判定 —— 傳統做法是顧客自行判斷、由客服審核。
/// 每一步的變更各記一筆流程埋點（同一個 CaseKey），送出成功時再記一筆並帶申請 Id。
/// </summary>
public partial class ShopAfterSalesFormView
{
    /// <summary>從「我的訂單」帶入時預選的訂單商品；不是本人的商品則不預選。</summary>
    [Parameter]
    public int? OrderItemId { get; set; }

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
    public ModalService ModalService { get; set; } = default!;

    [Inject]
    public NotificationService NotificationService { get; set; } = default!;

    [Inject]
    public ILogger<ShopAfterSalesFormView> Logger { get; set; } = default!;

    private static readonly AfterSalesType[] TypeChoices =
    [
        AfterSalesType.SizeExchange,
        AfterSalesType.ColorExchange,
        AfterSalesType.ReturnRefund,
        AfterSalesType.QualityIssue,
        AfterSalesType.ShipmentUrge,
    ];

    /// <summary>這張表單的案件識別碼：埋點與送出的申請共用同一個。</summary>
    private readonly Guid caseKey = Guid.NewGuid();

    private string RoleMessage = string.Empty;
    private bool isAccessChecked;

    /// <summary>null 表示仍在載入。</summary>
    private IReadOnlyList<OrderItemOption>? orderItems;
    private int? selectedOrderItemId;
    private IReadOnlyList<(string Color, string Size)> variantOptions = [];
    private AfterSalesType? selectedType;
    private string? targetSize;
    private string? targetColor;
    private bool? isWorn;
    private string description = string.Empty;
    private readonly List<ShopPendingPhoto> pendingPhotos = [];
    private bool submitting;
    private string? submittedRequestNo;

    private sealed record OrderItemOption(int OrderItemId, string Label, string Color, string Size);

    private OrderItemOption? SelectedItem => orderItems?.FirstOrDefault(x => x.OrderItemId == selectedOrderItemId);

    private int CustomerId => CurrentUserService.CurrentUser.Id;

    protected override async Task OnInitializedAsync()
    {
        Logger.LogDebug("Initializing shop after-sales form view.");

        var checkResult = await AuthenticationStateHelper.Check(authStateProvider, NavigationManager);
        if (checkResult != AuthenticationCheckResult.Succeeded)
        {
            Logger.LogWarning("Shop after-sales form initialization stopped because authentication check failed.");
            return;
        }

        isAccessChecked = true;

        if (AuthenticationStateHelper.CheckAccessPage(MagicObjectHelper.角色_顧客商店) == false)
        {
            RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
            await AuthenticationStateHelper.RecordPageAccessDeniedAsync("/shop/after-sales/new");
            Logger.LogWarning("Shop after-sales form denied because current user has not this role permission.");
            return;
        }

        var orders = await ShopOrderService.GetMyOrdersAsync(CustomerId);
        orderItems = orders
            .SelectMany(order => order.Items.Select(item => new OrderItemOption(
                item.OrderItemId,
                $"{order.OrderNo}｜{item.ProductName}（{item.Color}／{item.Size}）",
                item.Color,
                item.Size)))
            .ToList();

        if (OrderItemId is { } preselected && orderItems.Any(x => x.OrderItemId == preselected))
        {
            selectedOrderItemId = preselected;
            await LoadVariantOptionsAsync();
        }

        await RecordAsync(ProcessMetricActions.OpenForm);
    }

    /// <summary>同顏色的其他尺碼（換尺碼的選項）。</summary>
    internal static IReadOnlyList<string> TargetSizes(IReadOnlyList<(string Color, string Size)> variants, string color, string size)
        => variants.Where(x => x.Color == color && x.Size != size).Select(x => x.Size).Distinct().ToList();

    /// <summary>同尺碼的其他顏色（換顏色的選項）。</summary>
    internal static IReadOnlyList<string> TargetColors(IReadOnlyList<(string Color, string Size)> variants, string color, string size)
        => variants.Where(x => x.Size == size && x.Color != color).Select(x => x.Color).Distinct().ToList();

    /// <summary>換尺碼、換顏色、退貨要問「是否穿過」。</summary>
    private static bool AsksIsWorn(AfterSalesType? type)
        => type is AfterSalesType.SizeExchange or AfterSalesType.ColorExchange or AfterSalesType.ReturnRefund;

    private async Task LoadVariantOptionsAsync()
        => variantOptions = selectedOrderItemId is { } id
            ? await ShopOrderService.GetVariantOptionsAsync(CustomerId, id)
            : [];

    private async Task OnOrderItemChangedAsync()
    {
        targetSize = null;
        targetColor = null;
        await LoadVariantOptionsAsync();
        await RecordAsync(ProcessMetricActions.SelectOrderItem);
    }

    private async Task OnTypeSelectedAsync(AfterSalesType type)
    {
        if (selectedType == type)
        {
            return;
        }

        selectedType = type;
        targetSize = null;
        targetColor = null;
        isWorn = null;
        if (type != AfterSalesType.QualityIssue)
        {
            pendingPhotos.Clear();
        }

        await RecordAsync(ProcessMetricActions.SelectType);
    }

    private async Task OnIsWornSelectedAsync(bool value)
    {
        if (isWorn == value)
        {
            return;
        }

        isWorn = value;
        await RecordAsync(ProcessMetricActions.FillDetail);
    }

    private Task OnDetailChangedAsync() => RecordAsync(ProcessMetricActions.FillDetail);

    private async Task OnPhotosSelectedAsync(InputFileChangeEventArgs args)
    {
        if (await ShopPhotoPicker.AddAsync(args, pendingPhotos, NotificationService, Logger) > 0)
        {
            await RecordAsync(ProcessMetricActions.UploadPhoto);
        }
    }

    private void RemovePhoto(Guid id) => pendingPhotos.RemoveAll(x => x.Id == id);

    /// <summary>送出前的必填檢查；回傳第一個錯誤訊息，全部通過回 null。</summary>
    private string? Validate()
    {
        if (SelectedItem is null)
        {
            return "請選擇訂單商品。";
        }

        if (selectedType is null)
        {
            return "請選擇申請類型。";
        }

        if (selectedType == AfterSalesType.SizeExchange && string.IsNullOrWhiteSpace(targetSize))
        {
            return "請選擇想換的尺碼。";
        }

        if (selectedType == AfterSalesType.ColorExchange && string.IsNullOrWhiteSpace(targetColor))
        {
            return "請選擇想換的顏色。";
        }

        if (AsksIsWorn(selectedType) && isWorn is null)
        {
            return "請選擇商品是否穿過。";
        }

        return string.IsNullOrWhiteSpace(description) ? "請填寫問題描述。" : null;
    }

    private async Task OnSubmitClickAsync()
    {
        if (submitting)
        {
            return;
        }

        var error = Validate();
        if (error is not null)
        {
            Logger.LogInformation("After-sales form validation failed. Error={Error}", error);
            ViewNotification.ValidationError(NotificationService, error);
            return;
        }

        if (await ConfirmDialog.AskAsync(ModalService, "確認送出", "確定要送出這筆售後申請嗎？送出後由客服審核。", "送出", "再檢查") == false)
        {
            return;
        }

        submitting = true;
        var uploads = selectedType == AfterSalesType.QualityIssue ? ShopPhotoPicker.ToUploads(pendingPhotos) : [];
        try
        {
            var result = await AfterSalesRequestService.SubmitAsync(CustomerId, new SubmitAfterSalesInput
            {
                CaseKey = caseKey,
                OrderItemId = SelectedItem!.OrderItemId,
                Type = selectedType!.Value,
                TargetSize = selectedType == AfterSalesType.SizeExchange ? targetSize : null,
                TargetColor = selectedType == AfterSalesType.ColorExchange ? targetColor : null,
                IsWorn = AsksIsWorn(selectedType) ? isWorn : null,
                Description = description,
                Channel = AfterSalesChannel.Traditional,
            }, uploads);

            if (result.Success == false)
            {
                Logger.LogInformation("After-sales form submission was not accepted. OrderItemId={OrderItemId}", SelectedItem.OrderItemId);
                ViewNotification.Caution(NotificationService, result.Message);
                return;
            }

            await RecordAsync(ProcessMetricActions.Submit, result.RequestId);
            submittedRequestNo = result.RequestNo;
            pendingPhotos.Clear();
            Logger.LogInformation("After-sales form submitted. RequestId={RequestId}", result.RequestId);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "After-sales form submission failed.");
            ViewNotification.UnexpectedError(NotificationService, "送出售後申請失敗，請稍後再試。", ex);
        }
        finally
        {
            ShopPhotoPicker.DisposeAll(uploads);
            submitting = false;
        }
    }

    private Task RecordAsync(string action, int? requestId = null)
        => ProcessMetricService.RecordAsync(caseKey, AfterSalesChannel.Traditional, ProcessActor.Customer, action, CustomerId, requestId);
}

using AntDesign;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Shop;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Share.Shop;
using CustomerHelpAgent.Web.Components.Commons;

namespace CustomerHelpAgent.Web.Components.Views.Shop;

/// <summary>
/// 顧客「我的申請」（/shop/after-sales）：列出本人的售後申請（編號、商品、類型、狀態、客服備註）。
/// 「待補件」的申請可以「補充資料」：表單對話窗（是否穿過、補充說明、照片）→ <see cref="AfterSalesRequestService.ResubmitAsync"/>。
/// 補件的埋點沿用該申請的 CaseKey，讓顧客與客服的步驟串在同一個案件上。
/// </summary>
public partial class ShopAfterSalesListView : IDisposable
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
    public AfterSalesRequestService AfterSalesRequestService { get; set; } = default!;

    [Inject]
    public AfterSalesDecisionService AfterSalesDecisionService { get; set; } = default!;

    [Inject]
    public INotificationSignal NotificationSignal { get; set; } = default!;

    [Inject]
    public ProcessMetricService ProcessMetricService { get; set; } = default!;

    [Inject]
    public ModalService ModalService { get; set; } = default!;

    [Inject]
    public NotificationService NotificationService { get; set; } = default!;

    [Inject]
    public ILogger<ShopAfterSalesListView> Logger { get; set; } = default!;

    /// <summary>補件對話窗的欄位（FormDirtyTracker 以 JSON 值比對，所以要是公開屬性）。</summary>
    public sealed class ResubmitFormModel
    {
        public bool? IsWorn { get; set; }

        public string Supplement { get; set; } = string.Empty;
    }

    /// <summary>「查看我的申請」這次瀏覽的埋點識別碼。</summary>
    private readonly Guid caseKey = Guid.NewGuid();

    private string RoleMessage = string.Empty;
    private bool isAccessChecked;

    /// <summary>null 表示仍在載入。</summary>
    private IReadOnlyList<AfterSalesRequestView>? requests;

    /// <summary>核准後的處理單（申請 Id → 處理單），與 <see cref="requests"/> 一起載入。</summary>
    private IReadOnlyDictionary<int, AfterSalesFulfillmentView> fulfillments = new Dictionary<int, AfterSalesFulfillmentView>();

    private IDisposable? subscription;
    private bool disposed;

    private bool modalVisible;
    private AfterSalesRequestView? resubmitTarget;
    private ResubmitFormModel resubmitForm = new();
    private readonly List<ShopPendingPhoto> resubmitPhotos = [];
    private readonly FormDirtyTracker dirtyTracker = new();

    private int CustomerId => CurrentUserService.CurrentUser.Id;

    protected override async Task OnInitializedAsync()
    {
        Logger.LogDebug("Initializing shop after-sales list view.");

        var checkResult = await AuthenticationStateHelper.Check(authStateProvider, NavigationManager);
        if (checkResult != AuthenticationCheckResult.Succeeded)
        {
            Logger.LogWarning("Shop after-sales list initialization stopped because authentication check failed.");
            return;
        }

        isAccessChecked = true;

        if (AuthenticationStateHelper.CheckAccessPage(MagicObjectHelper.角色_顧客商店) == false)
        {
            RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
            await AuthenticationStateHelper.RecordPageAccessDeniedAsync("/shop/after-sales");
            Logger.LogWarning("Shop after-sales list denied because current user has not this role permission.");
            return;
        }

        // 先訂閱再載入：載入途中被關閉時 Dispose 也收得到這個訂閱；關閉後的訊號一律略過。
        subscription = NotificationSignal.Subscribe(CustomerId, () => InvokeAsync(async () =>
        {
            if (disposed)
            {
                return;
            }

            await LoadAsync();
            StateHasChanged();
        }));
        await LoadAsync();
        await ProcessMetricService.RecordAsync(caseKey, AfterSalesChannel.Traditional, ProcessActor.Customer, ProcessMetricActions.ViewRequests, CustomerId);
    }

    /// <summary>重新載入申請與處理單（批次查詢，不逐筆）。</summary>
    private async Task LoadAsync()
    {
        var list = await AfterSalesRequestService.GetMyRequestsAsync(CustomerId);
        fulfillments = await AfterSalesDecisionService.GetFulfillmentsAsync(
            list.Where(x => x.Status == AfterSalesStatus.Approved).Select(x => x.Id).ToList());
        requests = list;
    }

    /// <summary>核准顯示處理單號；駁回／待補件顯示客服說明；其餘為審核中。</summary>
    private string ResultText(AfterSalesRequestView request) => request.Status switch
    {
        AfterSalesStatus.Approved when fulfillments.TryGetValue(request.Id, out var fulfillment) => fulfillment.Kind switch
        {
            FulfillmentKind.ExchangeOrder => $"換貨單 {fulfillment.ReferenceNo} 已建立",
            FulfillmentKind.RefundRecord => $"退款紀錄 {fulfillment.ReferenceNo} 已建立",
            _ => $"處理單 {fulfillment.ReferenceNo} 已建立",
        },
        AfterSalesStatus.Approved => "已核准",
        AfterSalesStatus.Rejected or AfterSalesStatus.NeedMoreInfo => string.IsNullOrWhiteSpace(request.StaffNote) ? "—" : request.StaffNote,
        _ => "客服審核中",
    };

    private static StatusTone ToneOf(AfterSalesStatus status) => status switch
    {
        AfterSalesStatus.NeedMoreInfo => StatusTone.Warning,
        AfterSalesStatus.Approved => StatusTone.Positive,
        AfterSalesStatus.Rejected => StatusTone.Muted,
        _ => StatusTone.Accent,
    };

    /// <summary>類型後面補上想換成什麼（換尺碼 → 41）。</summary>
    private static string TypeDetail(AfterSalesRequestView request) => request.Type switch
    {
        AfterSalesType.SizeExchange when request.TargetSize is not null => $"{AfterSalesLabels.TypeText(request.Type)} → {request.TargetSize}",
        AfterSalesType.ColorExchange when request.TargetColor is not null => $"{AfterSalesLabels.TypeText(request.Type)} → {request.TargetColor}",
        _ => AfterSalesLabels.TypeText(request.Type),
    };

    private void OpenResubmit(AfterSalesRequestView request)
    {
        resubmitTarget = request;
        resubmitForm = new ResubmitFormModel { IsWorn = request.IsWorn };
        resubmitPhotos.Clear();

        // ⚠️ 必須是開窗前的最後一步；照片不在模型裡，以指紋納入比對（只加照片也算有變更）。
        dirtyTracker.Capture(resubmitForm, () => ShopPhotoPicker.Fingerprint(resubmitPhotos));
        modalVisible = true;
        Logger.LogInformation("Opened resubmit modal. RequestId={RequestId}", request.Id);
    }

    private Task OnResubmitPhotosSelectedAsync(InputFileChangeEventArgs args)
        => ShopPhotoPicker.AddAsync(args, resubmitPhotos, NotificationService, Logger);

    private void RemoveResubmitPhoto(Guid id) => resubmitPhotos.RemoveAll(x => x.Id == id);

    /// <summary>
    /// 「送出補件」。⚠️ 第一行的 modalVisible = true 不可移動（AntDesign 呼叫前已送出 VisibleChanged(false)），
    /// 之後的開關一律由 FormModalFlow 的回傳值決定 —— 失敗路徑只要 return false。args 可能是 null。
    /// </summary>
    private async Task OnResubmitOkAsync(MouseEventArgs args)
    {
        modalVisible = true;
        modalVisible = await FormModalFlow.RunOkAsync(
            ResubmitAsync,
            Logger,
            NotificationService,
            "送出補件時發生未預期的錯誤，請稍後再試。");
    }

    /// <summary>實際送出。回傳 true 表示可以關窗。</summary>
    private async Task<bool> ResubmitAsync()
    {
        if (resubmitTarget is null)
        {
            return true;
        }

        if (dirtyTracker.IsDirty(resubmitForm) == false)
        {
            ViewNotification.Info(NotificationService, "沒有任何變更，未送出補件。");
            dirtyTracker.Clear();
            return true;
        }

        if (await ConfirmDialog.AskAsync(ModalService, "確認送出補件", "確定要送出補充資料嗎？送出後申請會回到「已提交」，由客服重新審核。", "送出", "再檢查") == false)
        {
            return false;
        }

        var target = resubmitTarget;
        var uploads = ShopPhotoPicker.ToUploads(resubmitPhotos);
        SubmitResult result;
        try
        {
            result = await AfterSalesRequestService.ResubmitAsync(CustomerId, target.Id, resubmitForm.IsWorn, resubmitForm.Supplement, uploads);
        }
        finally
        {
            ShopPhotoPicker.DisposeAll(uploads);
        }

        if (result.Success == false)
        {
            Logger.LogInformation("Resubmit was not accepted. RequestId={RequestId}", target.Id);
            ViewNotification.Caution(NotificationService, result.Message);
            return false;
        }

        await ProcessMetricService.RecordAsync(target.CaseKey, target.Channel, ProcessActor.Customer, ProcessMetricActions.Resubmit, CustomerId, target.Id);
        Logger.LogInformation("Resubmitted after-sales request. RequestId={RequestId}", target.Id);
        ViewNotification.Warning(NotificationService, $"已送出補件，申請編號 {result.RequestNo}。");

        dirtyTracker.Clear();
        resubmitPhotos.Clear();
        await LoadAsync();
        return true;
    }

    /// <summary>
    /// 取消／✕／ESC 的共用出口。⚠️ 第一行的 modalVisible = true 不可移動；有未送出的內容先問過顧客。
    /// 照片清單只能在確定關窗之後才清掉，否則選「繼續編輯」會發現照片不見了。
    /// </summary>
    private async Task OnResubmitCancelAsync(MouseEventArgs args)
    {
        modalVisible = true;
        modalVisible = await FormModalFlow.ConfirmCloseAsync(ModalService, dirtyTracker.IsDirty(resubmitForm));

        if (modalVisible == false)
        {
            dirtyTracker.Clear();
            resubmitPhotos.Clear();
            Logger.LogDebug("Resubmit modal cancelled.");
        }
    }

    public void Dispose()
    {
        disposed = true;
        subscription?.Dispose();
    }
}

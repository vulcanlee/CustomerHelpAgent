using AntDesign;
using AntDesign.TableModels;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Shop;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Share.Shop;
using CustomerHelpAgent.Web.Components.Commons;

namespace CustomerHelpAgent.Web.Components.Views.AfterSales;

/// <summary>
/// 後台「售後審核」（/after-sales-review）：依狀態列出售後申請，點列開審核窗。
/// 傳統流程刻意分開：窗內預設只有申請內容，訂單、套用的售後規則、照片要客服各自點開（各記一筆埋點），
/// 再「要求補件」「核准」「駁回」。系統只呈現事實（簽收至今幾天），不替客服下判定。
/// 狀態變更由服務以狀態條件更新，兩位客服同時處理同一筆時後到的會收到失敗訊息。
/// </summary>
public partial class AfterSalesReviewView
{
    private static readonly AfterSalesStatus[] StatusOptions =
    [
        AfterSalesStatus.Submitted,
        AfterSalesStatus.NeedMoreInfo,
        AfterSalesStatus.Approved,
        AfterSalesStatus.Rejected,
    ];

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
    public AfterSalesPolicyService AfterSalesPolicyService { get; set; } = default!;

    [Inject]
    public ProcessMetricService ProcessMetricService { get; set; } = default!;

    [Inject]
    public TimeProvider TimeProvider { get; set; } = default!;

    [Inject]
    public ModalService ModalService { get; set; } = default!;

    [Inject]
    public NotificationService NotificationService { get; set; } = default!;

    [Inject]
    public ILogger<AfterSalesReviewView> Logger { get; set; } = default!;

    private string RoleMessage = string.Empty;
    private bool isAccessChecked;

    /// <summary>空字串＝全部；預設只看待處理的「已提交」。</summary>
    private string statusFilter = AfterSalesStatus.Submitted.ToString();
    private List<AfterSalesRequestView> requests = [];
    private int _pageIndex = 1;
    private int _pageSize = MagicObjectHelper.PageSize;
    private int _total;

    private bool modalVisible;
    private AfterSalesRequestView? current;
    private AfterSalesPolicyModel? appliedPolicy;
    private bool showOrder;
    private bool showPolicy;
    private bool showPhotos;
    private string staffNote = string.Empty;
    private bool busy;

    private int StaffId => CurrentUserService.CurrentUser.Id;

    protected override async Task OnInitializedAsync()
    {
        Logger.LogDebug("Initializing after-sales review view.");

        var checkResult = await AuthenticationStateHelper.Check(authStateProvider, NavigationManager);
        if (checkResult != AuthenticationCheckResult.Succeeded)
        {
            Logger.LogWarning("After-sales review initialization stopped because authentication check failed.");
            return;
        }

        isAccessChecked = true;

        if (AuthenticationStateHelper.CheckAccessPage(MagicObjectHelper.角色_售後審核) == false)
        {
            RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
            await AuthenticationStateHelper.RecordPageAccessDeniedAsync("/after-sales-review");
            Logger.LogWarning("After-sales review denied because current user has not this role permission.");
            return;
        }

        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        AfterSalesStatus? status = Enum.TryParse<AfterSalesStatus>(statusFilter, out var parsed) ? parsed : null;
        var result = await AfterSalesRequestService.GetReviewQueueAsync(
            new DataRequest { CurrentPage = _pageIndex, PageSize = _pageSize, Take = _pageSize },
            status);
        requests = result.Result.ToList();
        _total = result.Count;
        Logger.LogDebug("After-sales review queue reloaded. RequestStatus={RequestStatus}, Count={Count}", status, _total);
    }

    private async Task OnTableChange(QueryModel<AfterSalesRequestView> args)
    {
        _pageIndex = args.PageIndex;
        await ReloadAsync();
    }

    private async Task OnStatusFilterChangedAsync(string value)
    {
        statusFilter = value ?? string.Empty;
        _pageIndex = 1;
        await ReloadAsync();
    }

    private async Task OnRefreshAsync()
    {
        await ReloadAsync();
        ViewNotification.Warning(NotificationService, "已更新最新資料");
    }

    private async Task OnRowClickAsync(RowData<AfterSalesRequestView> row)
    {
        try
        {
            var detail = await AfterSalesRequestService.GetReviewDetailAsync(row.Data.Id);
            if (detail is null)
            {
                ViewNotification.Error(NotificationService, "找不到這筆申請，請重新整理。");
                await ReloadAsync();
                return;
            }

            current = detail;
            appliedPolicy = null;
            showOrder = false;
            showPolicy = false;
            showPhotos = false;
            staffNote = string.Empty;
            modalVisible = true;
            Logger.LogInformation("Opened after-sales review modal. RequestId={RequestId}", detail.Id);
            await RecordAsync(ProcessMetricActions.OpenRequest);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to open after-sales request. RequestId={RequestId}", row.Data.Id);
            ViewNotification.UnexpectedError(NotificationService, "開啟售後申請時發生未預期的錯誤，請稍後再試。", ex);
        }
    }

    private void CloseModal()
    {
        modalVisible = false;
        current = null;
    }

    private async Task OnShowOrderAsync()
    {
        showOrder = true;
        await RecordAsync(ProcessMetricActions.ViewOrder);
    }

    private async Task OnShowPolicyAsync()
    {
        if (current?.AppliedPolicyId is { } policyId)
        {
            appliedPolicy = await AfterSalesPolicyService.GetByIdAsync(policyId);
        }

        showPolicy = true;
        await RecordAsync(ProcessMetricActions.ViewPolicy);
    }

    private void OnShowPhotos() => showPhotos = true;

    private async Task OnRequestMoreInfoAsync()
    {
        if (current is not { } target)
        {
            return;
        }

        var note = staffNote;
        await RunActionAsync(
            target,
            () => AfterSalesRequestService.RequestMoreInfoAsync(target.Id, note),
            ProcessMetricActions.RequestMoreInfo,
            $"已要求補件，申請編號 {target.RequestNo}。");
    }

    private async Task OnDecideAsync(bool approve)
    {
        if (current is not { } target)
        {
            return;
        }

        var verb = approve ? "核准" : "駁回";
        var confirmed = approve
            ? await ConfirmDialog.AskAsync(ModalService, "確認核准", $"確定要核准申請 {target.RequestNo} 嗎？核准後無法再要求補件。", "核准")
            : await ConfirmDialog.AskDestructiveAsync(ModalService, "確認駁回", $"確定要駁回申請 {target.RequestNo} 嗎？駁回後無法再要求補件。", "駁回");
        if (!confirmed)
        {
            return;
        }

        var note = staffNote;
        await RunActionAsync(
            target,
            () => AfterSalesRequestService.DecideAsync(target.Id, approve, note),
            approve ? ProcessMetricActions.Approve : ProcessMetricActions.Reject,
            $"已{verb}申請 {target.RequestNo}。");
    }

    /// <summary>執行一個客服動作：失敗（含別人已先處理）顯示原因並更新畫面；成功記埋點、關窗、重新載入。</summary>
    private async Task RunActionAsync(AfterSalesRequestView target, Func<Task<VerifyRecordResult>> action, string metricAction, string successMessage)
    {
        busy = true;
        try
        {
            var result = await action();
            if (!result.Success)
            {
                Logger.LogInformation("After-sales review action rejected. RequestId={RequestId}, ProcessAction={ProcessAction}", target.Id, metricAction);
                ViewNotification.Error(NotificationService, result.Message);
                current = await AfterSalesRequestService.GetReviewDetailAsync(target.Id) ?? target;
                await ReloadAsync();
                return;
            }

            await ProcessMetricService.RecordAsync(target.CaseKey, target.Channel, ProcessActor.Staff, metricAction, StaffId, target.Id);
            Logger.LogInformation("After-sales review action completed. RequestId={RequestId}, ProcessAction={ProcessAction}", target.Id, metricAction);
            ViewNotification.Warning(NotificationService, successMessage);
            CloseModal();
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Unhandled exception during after-sales review action. RequestId={RequestId}, ProcessAction={ProcessAction}", target.Id, metricAction);
            ViewNotification.UnexpectedError(NotificationService, "處理售後申請時發生未預期的錯誤，請稍後再試。", ex);
        }
        finally
        {
            busy = false;
        }
    }

    /// <summary>客服的埋點沿用申請的 CaseKey 與管道，讓顧客與客服的步驟串在同一個案件上。</summary>
    private Task RecordAsync(string action)
    {
        var target = current;
        return target is null
            ? Task.CompletedTask
            : ProcessMetricService.RecordAsync(target.CaseKey, target.Channel, ProcessActor.Staff, action, StaffId, target.Id);
    }

    private string DaysSinceDeliveryText
    {
        get
        {
            var days = AfterSalesEligibility.DaysSinceDelivery(current?.DeliveredAt, TimeProvider.GetLocalNow().DateTime);
            return days is null ? "—" : $"{days} 天";
        }
    }

    private static string PhotoUrl(AfterSalesAttachmentView photo) => $"/api/after-sales-files/{photo.Id}/download";

    private static string ProductText(AfterSalesRequestView request) => $"{request.ProductName}（{request.Color}／{request.Size}）";

    private static string WornText(bool? isWorn) => isWorn switch
    {
        true => "已穿過",
        false => "未穿過",
        null => "未填寫",
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
}

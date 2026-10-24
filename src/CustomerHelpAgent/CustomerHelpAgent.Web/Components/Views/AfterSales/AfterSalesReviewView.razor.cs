using AntDesign;
using AntDesign.TableModels;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Agents;
using CustomerHelpAgent.Models.Shop;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Agents;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Share.Shop;
using CustomerHelpAgent.Web.Agents.PreReview;
using CustomerHelpAgent.Web.Components.Commons;

namespace CustomerHelpAgent.Web.Components.Views.AfterSales;

/// <summary>
/// 後台「售後審核」（/after-sales-review）：依狀態列出售後申請，點列開審核窗。
/// 傳統流程刻意分開：窗內預設只有申請內容，訂單、套用的售後規則、照片要客服各自點開（各記一筆埋點），
/// 再「要求補件」「核准」「駁回」。系統只呈現事實（簽收至今幾天），不替客服下判定。
/// Agent 管道的申請在「申請內容」後多一個 AI 初審面板，佇列多一欄 AI 初審標籤；審核窗分成「審核」與「AI 助手」兩個分頁
///（AI 助手：轉人工交接摘要＋只讀的客服 AI 助手，見 <see cref="StaffAssistantPanel"/>）。傳統管道完全不變（沒有分頁）。
/// 核准、駁回、要求補件一律經 <see cref="AfterSalesReviewCoordinator"/>（兩個管道相同）。
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
    public AfterSalesReviewCoordinator Coordinator { get; set; } = default!;

    [Inject]
    public PreReviewRunService PreReviewRunService { get; set; } = default!;

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

    /// <summary>佇列上 Agent 管道申請的最新一輪初審（申請 Id → 初審）。</summary>
    private Dictionary<int, PreReviewQueueStatus> preReviews = [];

    /// <summary>審核窗內面板回報的最新一輪初審（決策按鈕旁的提示用）。</summary>
    private PreReviewRun? latestRun;

    private const string PreReviewUnfinishedHint = "AI 初審尚未完成，直接決定會略過 AI 建議";

    private const string ReviewTabKey = "review";
    private const string AssistantTabKey = "assistant";

    /// <summary>Agent 管道審核窗目前的分頁；每次開窗回到「審核」。</summary>
    private string reviewTab = ReviewTabKey;

    /// <summary>這次開窗是否點開過「AI 助手」分頁：點開才建立助手，之後切回「審核」也保留對話；關窗或換一筆即重來。</summary>
    private bool assistantOpened;

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
        preReviews = await PreReviewRunService.GetLatestByRequestIdsAsync(
            requests.Where(x => x.Channel == AfterSalesChannel.Agent).Select(x => x.Id).ToList());
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
            latestRun = null;
            appliedPolicy = null;
            showOrder = false;
            showPolicy = false;
            showPhotos = false;
            staffNote = string.Empty;
            reviewTab = ReviewTabKey;
            assistantOpened = false;
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
        assistantOpened = false;
    }

    private void OnReviewTabChanged(string key)
    {
        reviewTab = key;
        assistantOpened |= key == AssistantTabKey;
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

    private void OnPreReviewChanged(PreReviewRun? run)
    {
        latestRun = run;
    }

    /// <summary>決策按鈕旁的提示：Agent 管道、最新一輪還在初審中。</summary>
    private bool PreReviewUnfinished => current?.Channel == AfterSalesChannel.Agent && IsUnfinished(latestRun);

    /// <summary>上一次執行失敗的原因（例如目標規格無庫存）：流程回到人工決策後，節點的備註帶著它。</summary>
    private string? PreviousFailure
    {
        get
        {
            if (latestRun is not { Status: PreReviewStatus.AwaitingDecision } run)
            {
                return null;
            }

            var node = run.Nodes.FirstOrDefault(x => x.Node == PreReviewNode.StaffDecision);
            return string.IsNullOrWhiteSpace(node?.Note) ? null : node.Note;
        }
    }

    private static bool IsUnfinished(PreReviewRun? run) => run?.Status is PreReviewStatus.Pending or PreReviewStatus.Running;

    private async Task OnRequestMoreInfoAsync()
        => await OnDecideAsync(StaffDecisionKind.NeedMoreInfo);

    private async Task OnDecideAsync(StaffDecisionKind kind)
    {
        if (current is not { } target)
        {
            return;
        }

        // 補件說明必填：先擋下，不要先跳「AI 初審尚未完成」的確認再被拒絕。
        if (kind == StaffDecisionKind.NeedMoreInfo && string.IsNullOrWhiteSpace(staffNote))
        {
            ViewNotification.Error(NotificationService, AfterSalesDecisionService.MoreInfoRequiredMessage);
            return;
        }

        if (!await AskStaffDecisionAsync(target, kind))
        {
            return;
        }

        var note = staffNote;
        var (metric, verb) = kind switch
        {
            StaffDecisionKind.Approve => (ProcessMetricActions.Approve, "核准"),
            StaffDecisionKind.Reject => (ProcessMetricActions.Reject, "駁回"),
            _ => (ProcessMetricActions.RequestMoreInfo, "要求補件"),
        };
        await RunActionAsync(
            target,
            () => Coordinator.DecideAsync(target.Id, kind, note),
            metric,
            result => kind == StaffDecisionKind.NeedMoreInfo
                ? $"已要求補件，申請編號 {target.RequestNo}。"
                : result.FulfillmentNo is null
                    ? $"已{verb}申請 {target.RequestNo}。"
                    : $"已{verb}申請 {target.RequestNo}，處理單號 {result.FulfillmentNo}。");
    }

    /// <summary>
    /// 決定前的確認。AI 初審還沒跑完（以資料庫最新狀態為準，不看畫面上可能過時的那份）→ 一律先確認「會略過 AI 建議」；
    /// 否則維持原本的流程：核准、駁回要確認，要求補件直接送出。
    /// </summary>
    private async Task<bool> AskStaffDecisionAsync(AfterSalesRequestView target, StaffDecisionKind kind)
    {
        var unfinished = target.Channel == AfterSalesChannel.Agent
            && IsUnfinished(await PreReviewRunService.GetLatestForRequestAsync(target.Id));
        var verb = kind switch
        {
            StaffDecisionKind.Approve => "核准",
            StaffDecisionKind.Reject => "駁回",
            _ => "要求補件",
        };

        if (unfinished)
        {
            var content = $"{PreReviewUnfinishedHint}。確定要直接{verb}申請 {target.RequestNo} 嗎？";
            return kind == StaffDecisionKind.Reject
                ? await ConfirmDialog.AskDestructiveAsync(ModalService, "AI 初審尚未完成", content, verb)
                : await ConfirmDialog.AskAsync(ModalService, "AI 初審尚未完成", content, verb);
        }

        return kind switch
        {
            StaffDecisionKind.Approve => await ConfirmDialog.AskAsync(ModalService, "確認核准", $"確定要核准申請 {target.RequestNo} 嗎？核准後無法再要求補件。", "核准"),
            StaffDecisionKind.Reject => await ConfirmDialog.AskDestructiveAsync(ModalService, "確認駁回", $"確定要駁回申請 {target.RequestNo} 嗎？駁回後無法再要求補件。", "駁回"),
            _ => true,
        };
    }

    /// <summary>執行一個客服動作：失敗（含別人已先處理）顯示原因並更新畫面；成功記埋點、關窗、重新載入。</summary>
    private async Task RunActionAsync(AfterSalesRequestView target, Func<Task<DecisionResult>> action, string metricAction, Func<DecisionResult, string> successMessage)
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
            ViewNotification.Warning(NotificationService, successMessage(result));
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

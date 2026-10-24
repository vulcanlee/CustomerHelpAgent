using AntDesign;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Business.Startup;
using CustomerHelpAgent.Models.Benefit;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Demo;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Web.Benefit;
using static CustomerHelpAgent.Web.Benefit.BenefitFamilyLayout;
using CustomerHelpAgent.Web.Components.Commons;
using CustomerHelpAgent.Web.Export;

namespace CustomerHelpAgent.Web.Components.Views.AfterSales;

/// <summary>
/// 後台「效益對照」（/benefit-comparison，M6）：展示者跑完劇本後給客戶看的對照頁。
/// <list type="bullet">
/// <item>上半部（實測）：依情境家族並排傳統、單一 Agent、多 Agent 的平均指標與相對傳統的差異，加每個指標一張長條圖；
/// 數字只來自 <see cref="BenefitMetricsService"/>（回放、無效的執行不計）。缺流程時提示要準備哪個劇本。</item>
/// <item>假設值與月度推估（假設／推估，另一種底色）：有「修改」權限才能改，改值立即以 <see cref="BenefitEstimator"/> 重算，
/// 按「儲存假設值」並確認後才寫入（<see cref="DemoAssumptionService"/> 驗證與稽核）。</item>
/// <item>下半部：可展開的每次執行明細，回放與無效的標「不計」。</item>
/// </list>
/// 不顯示顧客個資：只有示範帳號代號。
/// </summary>
public partial class BenefitComparisonView
{
    [Inject]
    public AuthenticationStateProvider authStateProvider { get; set; } = default!;

    [Inject]
    public AuthenticationStateHelper AuthenticationStateHelper { get; set; } = default!;

    [Inject]
    public NavigationManager NavigationManager { get; set; } = default!;

    [Inject]
    public BenefitMetricsService BenefitMetricsService { get; set; } = default!;

    [Inject]
    public DemoAssumptionService DemoAssumptionService { get; set; } = default!;

    [Inject]
    public TimeProvider TimeProvider { get; set; } = default!;

    [Inject]
    public ModalService ModalService { get; set; } = default!;

    [Inject]
    public NotificationService NotificationService { get; set; } = default!;

    [Inject]
    public IJSRuntime JSRuntime { get; set; } = default!;

    [Inject]
    public ISystemIdentity SystemIdentity { get; set; } = default!;

    [Inject]
    public CurrentUserService CurrentUserService { get; set; } = default!;

    [Inject]
    public IAuditLogService AuditLogService { get; set; } = default!;

    [Inject]
    public ILogger<BenefitComparisonView> Logger { get; set; } = default!;

    private static readonly ScenarioFlow[] AiFlows = [ScenarioFlow.SingleAgent, ScenarioFlow.MultiAgent];

    private string RoleMessage = string.Empty;
    private bool isAccessChecked;
    private bool isLoading;

    private BenefitComparison comparison = new();
    private IReadOnlyList<FamilySection> sections = [];

    private DateTime? startDate;
    private DateTime? endDate;
    private DateTime rangeFrom;
    private DateTime rangeTo;

    /// <summary>第一次讀取失敗時的訊息；有值時整頁只顯示這個錯誤（不渲染還沒有資料的區塊）。</summary>
    private string? loadError;

    // 先放預設值：讀取失敗或尚未讀完時，畫面上的假設值與「是否已修改」判斷都不會遇到 null。
    private DemoAssumptionView savedAssumption = DemoAssumptionService.DefaultView;
    private AssumptionDraft draft = AssumptionDraft.From(DemoAssumptionService.DefaultView);
    private bool isSaving;
    private string? assumptionError;

    private string? estimateFamily;
    private ScenarioFlow estimateFlow = ScenarioFlow.SingleAgent;

    private bool showDetails;

    protected override async Task OnInitializedAsync()
    {
        Logger.LogDebug("Initializing benefit comparison view.");

        var checkResult = await AuthenticationStateHelper.Check(authStateProvider, NavigationManager);
        if (checkResult != AuthenticationCheckResult.Succeeded)
        {
            Logger.LogWarning("Benefit comparison initialization stopped because authentication check failed.");
            return;
        }

        isAccessChecked = true;

        if (AuthenticationStateHelper.CheckAccessPage(MagicObjectHelper.角色_效益對照) == false)
        {
            RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
            await AuthenticationStateHelper.RecordPageAccessDeniedAsync("/benefit-comparison");
            Logger.LogWarning("Benefit comparison denied because current user has not this role permission.");
            return;
        }

        try
        {
            savedAssumption = await DemoAssumptionService.GetAsync();
            draft = AssumptionDraft.From(savedAssumption);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load benefit assumptions.");
            ViewNotification.UnexpectedError(NotificationService, LoadErrorMessage, ex);
            loadError = LoadErrorMessage;
            return;
        }

        SetWholeDays(Today, Today);
        if (await ReloadAsync() == false)
        {
            loadError = LoadErrorMessage;
        }
    }

    private const string LoadErrorMessage = "讀取效益對照時發生未預期的錯誤，請稍後再試。";

    private DateTime Today => TimeProvider.GetLocalNow().DateTime.Date;

    private bool CanEdit => AuthenticationStateHelper.CheckAccessAction(MagicObjectHelper.角色_效益對照, PermissionActions.Edit);

    #region 查詢範圍

    private void SetWholeDays(DateTime start, DateTime end)
    {
        startDate = start.Date;
        endDate = end.Date;
        rangeFrom = start.Date;
        rangeTo = end.Date.AddDays(1);
    }

    private async Task OnSearchAsync()
    {
        if (startDate is not { } start || endDate is not { } end)
        {
            ViewNotification.ValidationError(NotificationService, "請選擇起始日與結束日。");
            return;
        }

        if (end.Date < start.Date)
        {
            ViewNotification.ValidationError(NotificationService, "結束日不可早於起始日。");
            return;
        }

        SetWholeDays(start, end);
        await ReloadAsync();
    }

    private async Task OnTodayAsync()
    {
        SetWholeDays(Today, Today);
        await ReloadAsync();
    }

    /// <summary>最近 7 天（含今天）整天。</summary>
    private async Task OnLastSevenDaysAsync()
    {
        SetWholeDays(Today.AddDays(-6), Today);
        await ReloadAsync();
    }

    private async Task OnRefreshAsync()
    {
        if (await ReloadAsync())
        {
            ViewNotification.Warning(NotificationService, "已更新最新資料");
        }
    }

    /// <summary>重新計算；失敗時記錄並通知（不讓例外中斷畫面），回傳是否成功。</summary>
    private async Task<bool> ReloadAsync()
    {
        isLoading = true;
        try
        {
            comparison = await BenefitMetricsService.GetComparisonAsync(rangeFrom, rangeTo);
            sections = BuildSections(comparison);
            PickDefaultEstimateTarget();
            Logger.LogDebug("Benefit comparison reloaded. RunCount={RunCount}, FamilyCount={FamilyCount}", comparison.Runs.Count, sections.Count);
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to calculate benefit comparison.");
            ViewNotification.UnexpectedError(NotificationService, LoadErrorMessage, ex);
            return false;
        }
        finally
        {
            isLoading = false;
        }
    }

    private string RangeText
        => $"{rangeFrom:yyyy/MM/dd HH:mm} ～ {rangeTo:yyyy/MM/dd HH:mm}（不含結束時間）";

    #endregion

    #region 依家族分區

    /// <summary>端到端、客服處理只平均有值的執行；比有效次數少時寫出幾次有值。</summary>
    private static string? PartialSampleText(BenefitMetric metric, FlowMetrics metrics)
        => PartialSampleCount(metric, metrics) is { } samples ? $"{samples} 次有值，其餘未完成不計" : null;

    private static IReadOnlyList<BenefitMetric> ChartedMetrics { get; } = [.. BenefitMetricDisplay.Metrics.Where(x => x.Charted)];

    /// <summary>家族裡至少一種做法有可畫的平均值才畫圖；只有回放或不計的執行時，卡片已提示要準備的劇本，不再畫一排全是「無資料」的空圖。</summary>
    private static bool HasChartData(FamilySection section)
        => ChartedMetrics.Any(metric => section.Slots.Any(slot => Average(metric, slot.Metrics) is not null));

    private static string ChartedMetricNames => string.Join("、", ChartedMetrics.Select(x => x.Label));

    /// <summary>長條圖的替代文字：指標與三種做法各自的數字（缺資料寫「無資料」）。</summary>
    private static string ChartAriaLabel(FamilySection section, BenefitMetric metric, IReadOnlyList<double?> values)
        => $"{section.Family}：{metric.Label}，"
            + string.Join("、", section.Slots.Select((slot, index) =>
                $"{BenefitMetricDisplay.FlowLabel(slot.Flow)} {(values[index] is null ? "無資料" : metric.Format(values[index]))}"));

    /// <summary>示範模式外的案件：不計的筆數（回放＋無效）。</summary>
    private static int ExcludedCount(ChannelMetrics channel) => channel.ReplayExcludedCount + channel.ExcludedRunCount;

    #endregion

    #region 假設值與月度推估

    /// <summary>畫面上正在編輯的假設值（尚未儲存）；改值即重算推估。</summary>
    private sealed class AssumptionDraft
    {
        public double QueueWaitMinutesTraditional { get; set; }
        public double QueueWaitMinutesAi { get; set; }
        public double ContactsPerCaseTraditional { get; set; }
        public double ContactsPerCaseAi { get; set; }
        public double MinutesPerContact { get; set; }
        public double StaffCostPerMinuteTwd { get; set; }
        public int MonthlyCaseVolume { get; set; }

        public static AssumptionDraft From(DemoAssumptionView x) => new()
        {
            QueueWaitMinutesTraditional = x.QueueWaitMinutesTraditional,
            QueueWaitMinutesAi = x.QueueWaitMinutesAi,
            ContactsPerCaseTraditional = x.ContactsPerCaseTraditional,
            ContactsPerCaseAi = x.ContactsPerCaseAi,
            MinutesPerContact = x.MinutesPerContact,
            StaffCostPerMinuteTwd = x.StaffCostPerMinuteTwd,
            MonthlyCaseVolume = x.MonthlyCaseVolume,
        };

        public DemoAssumptionInput ToInput() => new(
            QueueWaitMinutesTraditional, QueueWaitMinutesAi, ContactsPerCaseTraditional, ContactsPerCaseAi,
            MinutesPerContact, StaffCostPerMinuteTwd, MonthlyCaseVolume);
    }

    private bool IsDirty => draft.ToInput() != AssumptionDraft.From(savedAssumption).ToInput();

    private IReadOnlyList<FamilySection> EstimateFamilies => [.. sections.Where(x => x.Slots.Any(s => s.Flow == ScenarioFlow.Traditional))];

    private FamilySection? EstimateSection => sections.FirstOrDefault(x => x.Family == estimateFamily);

    private IReadOnlyList<ScenarioFlow> EstimateFlows
        => EstimateSection is { } section ? [.. AiFlows.Where(flow => section.Slots.Any(s => s.Flow == flow))] : [];

    /// <summary>預設推估目標：第一個「傳統＋單一 Agent（或多 Agent）」資料足夠的家族；都不夠時選第一個有傳統做法的家族。</summary>
    private void PickDefaultEstimateTarget()
    {
        if (EstimateSection is not null)
        {
            return;
        }

        var input = draft.ToInput();
        foreach (var section in EstimateFamilies)
        {
            foreach (var flow in AiFlows)
            {
                var ai = section.Slots.FirstOrDefault(x => x.Flow == flow)?.Metrics;
                if (BenefitEstimator.Estimate(section.Traditional, ai, input).IsSufficient)
                {
                    estimateFamily = section.Family;
                    estimateFlow = flow;
                    return;
                }
            }
        }

        estimateFamily = EstimateFamilies.FirstOrDefault()?.Family;
        estimateFlow = ScenarioFlow.SingleAgent;
    }

    private void OnEstimateFamilyChanged(string? family)
    {
        estimateFamily = family;
        if (EstimateFlows.Contains(estimateFlow) == false)
        {
            estimateFlow = EstimateFlows.FirstOrDefault(ScenarioFlow.SingleAgent);
        }
    }

    private MonthlyEstimate Estimate
        => BenefitEstimator.Estimate(
            EstimateSection?.Traditional,
            EstimateSection?.Slots.FirstOrDefault(x => x.Flow == estimateFlow)?.Metrics,
            draft.ToInput());

    private void OnRevertAssumptions()
    {
        draft = AssumptionDraft.From(savedAssumption);
        assumptionError = null;
    }

    private async Task OnSaveAssumptionsAsync()
    {
        // 畫面藏按鈕不算授權：送出前再檢查一次動作權限。
        if (CanEdit == false)
        {
            ViewNotification.Error(NotificationService, "你沒有修改假設值的權限。");
            return;
        }

        var confirmed = await ConfirmDialog.AskAsync(
            ModalService,
            "儲存假設值",
            "儲存後，所有人看到的月度推估都會改用這組假設值；實測數字不受影響。確定要儲存嗎？",
            "儲存");
        if (confirmed == false)
        {
            return;
        }

        isSaving = true;
        try
        {
            var result = await DemoAssumptionService.SaveAsync(draft.ToInput());
            if (result.Success)
            {
                savedAssumption = result.Assumption;
                draft = AssumptionDraft.From(savedAssumption);
                assumptionError = null;
                ViewNotification.Warning(NotificationService, result.Message);
                Logger.LogInformation("Benefit assumptions saved from the comparison page.");
            }
            else
            {
                assumptionError = result.Message;
                ViewNotification.ValidationError(NotificationService, result.Message);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to save benefit assumptions.");
            ViewNotification.UnexpectedError(NotificationService, "儲存假設值時發生未預期的錯誤，請稍後再試。", ex);
        }
        finally
        {
            isSaving = false;
        }
    }

    /// <summary>唯讀時顯示的假設值：千分位、最多 2 位小數。</summary>
    private static string AssumptionText(double value) => value.ToString("#,0.##", System.Globalization.CultureInfo.InvariantCulture);

    #endregion

    #region 匯出

    private bool isExporting;

    /// <summary>
    /// 匯出目前畫面的對照（Excel 三張工作表或 PDF）：範圍、假設值（含尚未儲存的草稿）與選定的推估都與畫面一致。
    /// 能進這一頁就能匯出（檢視權限）；成功後寫稽核 <see cref="AuditActions.Demo.BenefitExport"/>（只記格式、範圍與筆數）。
    /// </summary>
    private async Task OnExportAsync(string format)
    {
        isExporting = true;
        try
        {
            var request = new BenefitExportRequest(
                comparison,
                draft.ToInput(),
                IsDirty,
                estimateFamily,
                estimateFlow,
                EstimateFamilies.Count == 0 ? null : Estimate,
                SystemIdentity.Name,
                SystemIdentity.Version,
                CurrentUserService.CurrentUser.Account ?? string.Empty,
                TimeProvider.GetLocalNow().DateTime);

            var isPdf = format == "pdf";
            var bytes = isPdf ? BenefitExport.BuildPdf(request) : BenefitExport.BuildXlsx(request);
            await JSRuntime.DownloadAsync(
                BenefitExport.FileName(comparison, format),
                bytes,
                isPdf ? "application/pdf" : TabularExport.XlsxContentType);

            Logger.LogInformation("Benefit comparison exported. Format={Format}, Bytes={Bytes}, Runs={Runs}", format, bytes.Length, comparison.Runs.Count);
            await ViewAudit.WriteAsync(
                AuditLogService, CurrentUserService, AuditActions.Demo.BenefitExport, "BenefitComparison", "*",
                $"format={format}; range={rangeFrom:yyyyMMdd}~{rangeTo:yyyyMMdd}; runs={comparison.Runs.Count}");
        }
        catch (JSDisconnectedException ex)
        {
            Logger.LogWarning(ex, "Benefit comparison export aborted because the circuit disconnected.");
        }
        catch (InvalidOperationException ex) when (format == "pdf")
        {
            // 只有 PDF 會載入內嵌中文字型（缺字型時丟 InvalidOperationException）；Excel 的同型例外與字型無關，走下面的一般錯誤。
            Logger.LogError(ex, "Benefit comparison export failed because the embedded font is missing.");
            ViewNotification.UnexpectedError(NotificationService, "匯出失敗：缺少內建中文字型，請確認建置產物完整。", ex);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Benefit comparison export failed.");
            ViewNotification.UnexpectedError(NotificationService, "匯出時發生未預期的錯誤，請稍後再試。", ex);
        }
        finally
        {
            isExporting = false;
        }
    }

    #endregion

    #region 明細

    private void ToggleDetails() => showDetails = !showDetails;

    private static StatusTone RunStatusTone(RunMetrics run)
        => run.ExclusionReason is not null ? StatusTone.Warning : run.IsCompleted ? StatusTone.Positive : StatusTone.Neutral;

    private static string RunTitle(RunMetrics run)
        => DemoScenarios.All.FirstOrDefault(x => x.Code == run.ScenarioCode)?.Title ?? run.ScenarioCode;

    #endregion
}

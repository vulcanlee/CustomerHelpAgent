using AntDesign;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Shop;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Web.Components.Commons;

namespace CustomerHelpAgent.Web.Components.Views.AfterSales;

/// <summary>
/// 後台「售後規則管理」（/after-sales-policies）：列出所有規則版本（含已失效）。
/// 規則不就地修改、也不刪除：現行版本的「新增版本」以該版預填 → <see cref="AfterSalesPolicyService.AddVersionAsync"/>，
/// 舊版本由服務在同一個交易內設定失效時間。
/// </summary>
public partial class AfterSalesPolicyView
{
    [Inject]
    public AuthenticationStateProvider authStateProvider { get; set; } = default!;

    [Inject]
    public AuthenticationStateHelper AuthenticationStateHelper { get; set; } = default!;

    [Inject]
    public NavigationManager NavigationManager { get; set; } = default!;

    [Inject]
    public AfterSalesPolicyService AfterSalesPolicyService { get; set; } = default!;

    [Inject]
    public TimeProvider TimeProvider { get; set; } = default!;

    [Inject]
    public ModalService ModalService { get; set; } = default!;

    [Inject]
    public NotificationService NotificationService { get; set; } = default!;

    [Inject]
    public ILogger<AfterSalesPolicyView> Logger { get; set; } = default!;

    private string RoleMessage = string.Empty;
    private bool isAccessChecked;

    private List<AfterSalesPolicyModel> policies = [];
    private DateTime now;

    private bool modalVisible;
    private string modalTitle = "新增規則版本";
    private AfterSalesPolicyModel draft = new();
    private readonly FormDirtyTracker dirtyTracker = new();

    protected override async Task OnInitializedAsync()
    {
        Logger.LogDebug("Initializing after-sales policy view.");

        var checkResult = await AuthenticationStateHelper.Check(authStateProvider, NavigationManager);
        if (checkResult != AuthenticationCheckResult.Succeeded)
        {
            Logger.LogWarning("After-sales policy initialization stopped because authentication check failed.");
            return;
        }

        isAccessChecked = true;

        if (AuthenticationStateHelper.CheckAccessPage(MagicObjectHelper.角色_售後規則) == false)
        {
            RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
            await AuthenticationStateHelper.RecordPageAccessDeniedAsync("/after-sales-policies");
            Logger.LogWarning("After-sales policy view denied because current user has not this role permission.");
            return;
        }

        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        now = TimeProvider.GetLocalNow().DateTime;
        policies = await AfterSalesPolicyService.GetVersionsAsync(includeExpired: true);
        Logger.LogDebug("After-sales policies reloaded. Count={Count}", policies.Count);
    }

    private async Task OnRefreshAsync()
    {
        await ReloadAsync();
        ViewNotification.Warning(NotificationService, "已更新最新資料");
    }

    /// <summary>失效時間已到（含新增版本時被關閉的舊版）。</summary>
    private bool IsExpired(AfterSalesPolicyModel policy) => policy.EffectiveTo is { } to && to <= now;

    private static string ConditionText(AfterSalesPolicyModel policy)
    {
        var conditions = new List<string>();
        if (policy.RequiresUnworn)
        {
            conditions.Add("須未穿著");
        }

        if (policy.RequiresPhoto)
        {
            conditions.Add("須附照片");
        }

        return conditions.Count == 0 ? "—" : string.Join("、", conditions);
    }

    private void OnAddVersion(AfterSalesPolicyModel policy)
    {
        draft = policy.Clone();
        // 服務以此比對目前生效版本，期間若有別人新增過版本就拒絕（避免覆蓋別人剛改的規則）。
        draft.BaseVersion = policy.Version;
        modalTitle = $"新增規則版本：{policy.Code}";
        // ⚠️ 必須是開窗前的最後一步：任何預設值都要先塞完，否則會被當成使用者的變更。
        dirtyTracker.Capture(draft);
        modalVisible = true;
        Logger.LogInformation("Opened add policy version modal. PolicyCode={PolicyCode}, Version={Version}", policy.Code, policy.Version);
    }

    /// <summary>
    /// 「新增版本」。⚠️ 第一行的 modalVisible = true 不可移動（AntDesign 呼叫前已送出 VisibleChanged(false)），
    /// 之後的開關一律由 FormModalFlow 的回傳值決定 —— 失敗路徑只要 return false。args 可能是 null。
    /// </summary>
    private async Task OnModalOkAsync(MouseEventArgs args)
    {
        modalVisible = true;
        modalVisible = await FormModalFlow.RunOkAsync(
            SaveAsync,
            Logger,
            NotificationService,
            "新增規則版本時發生未預期的錯誤，請稍後再試。");
    }

    /// <summary>實際送出。回傳 true 表示可以關窗。</summary>
    private async Task<bool> SaveAsync()
    {
        var error = Validate(draft);
        if (error is not null)
        {
            Logger.LogInformation("Policy version form validation failed. PolicyCode={PolicyCode}", draft.Code);
            ViewNotification.ValidationError(NotificationService, error);
            return false;
        }

        // 內容與目前版本一模一樣：不值得多一個版本。
        if (dirtyTracker.IsDirty(draft) == false)
        {
            ViewNotification.Info(NotificationService, "沒有任何變更，未新增版本。");
            dirtyTracker.Clear();
            return true;
        }

        if (await ConfirmDialog.AskAsync(ModalService, "確認新增版本",
                $"新增後 {draft.Code} 目前的 v{draft.Version} 會立即失效，之後送出的申請改套用新版本；已送出的申請不受影響。確定要新增嗎？",
                "新增版本", "再檢查") == false)
        {
            return false;
        }

        var result = await AfterSalesPolicyService.AddVersionAsync(draft);
        if (!result.Success)
        {
            ViewNotification.Error(NotificationService, result.Message);
            return false;
        }

        Logger.LogInformation("Policy version added from view. PolicyCode={PolicyCode}", draft.Code);
        ViewNotification.Warning(NotificationService, $"已新增 {draft.Code} 的新版本");
        dirtyTracker.Clear();
        await ReloadAsync();
        return true;
    }

    private static string? Validate(AfterSalesPolicyModel model)
    {
        if (string.IsNullOrWhiteSpace(model.Title))
        {
            return "請輸入規則標題。";
        }

        if (string.IsNullOrWhiteSpace(model.Source))
        {
            return "請輸入規則來源。";
        }

        if (string.IsNullOrWhiteSpace(model.Content))
        {
            return "請輸入規則條文。";
        }

        return model.WindowDays is < 1 ? "申請期限至少 1 天，不限請留空。" : null;
    }

    /// <summary>取消／✕／ESC 的共用出口。⚠️ 第一行的 modalVisible = true 不可移動；有未儲存的修改先問過使用者。</summary>
    private async Task OnModalCancelAsync(MouseEventArgs args)
    {
        modalVisible = true;
        modalVisible = await FormModalFlow.ConfirmCloseAsync(ModalService, dirtyTracker.IsDirty(draft));

        if (modalVisible == false)
        {
            dirtyTracker.Clear();
            Logger.LogDebug("Add policy version modal cancelled.");
        }
    }
}

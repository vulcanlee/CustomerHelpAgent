using AntDesign;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Agents;
using CustomerHelpAgent.Share.Agents;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Web.Components.Commons;

namespace CustomerHelpAgent.Web.Components.Views.Shop;

/// <summary>
/// 顧客「我的 AI 記憶」（/shop/memory）：列出 AI 經顧客確認後記住的偏好，可單筆或全部刪除（皆先經確認窗）。
/// 查詢與刪除都帶本人 Id，別人的記憶看不到也刪不到；刪除後下一輪對話就不再被注入。
/// </summary>
public partial class ShopMemoryView
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
    public CustomerMemoryService CustomerMemoryService { get; set; } = default!;

    [Inject]
    public ModalService ModalService { get; set; } = default!;

    [Inject]
    public NotificationService NotificationService { get; set; } = default!;

    [Inject]
    public ILogger<ShopMemoryView> Logger { get; set; } = default!;

    private string RoleMessage = string.Empty;
    private bool isAccessChecked;

    /// <summary>null 表示仍在載入。</summary>
    private IReadOnlyList<CustomerMemoryView>? memories;

    private int CustomerId => CurrentUserService.CurrentUser.Id;

    protected override async Task OnInitializedAsync()
    {
        Logger.LogDebug("Initializing shop memory view.");

        var checkResult = await AuthenticationStateHelper.Check(authStateProvider, NavigationManager);
        if (checkResult != AuthenticationCheckResult.Succeeded)
        {
            Logger.LogWarning("Shop memory initialization stopped because authentication check failed.");
            return;
        }

        isAccessChecked = true;

        if (AuthenticationStateHelper.CheckAccessPage(MagicObjectHelper.角色_顧客商店) == false)
        {
            RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
            await AuthenticationStateHelper.RecordPageAccessDeniedAsync("/shop/memory");
            Logger.LogWarning("Shop memory denied because current user has not this role permission.");
            return;
        }

        await LoadAsync();
    }

    private async Task LoadAsync() => memories = await CustomerMemoryService.GetAsync(CustomerId);

    private async Task DeleteAsync(CustomerMemoryView memory)
    {
        if (await ConfirmDialog.AskDestructiveAsync(
                ModalService,
                "確認刪除記憶",
                $"確定要刪除 AI 記住的「{MemoryLabels.KindText(memory.Kind)}：{memory.Value}」嗎？刪除後 AI 不會再參考這項偏好。",
                "刪除") == false)
        {
            return;
        }

        var result = await CustomerMemoryService.DeleteAsync(CustomerId, memory.Id);
        if (result.Success == false)
        {
            ViewNotification.Caution(NotificationService, result.Message);
        }
        else
        {
            ViewNotification.Warning(NotificationService, "已刪除這項記憶。");
        }

        await LoadAsync();
    }

    private async Task DeleteAllAsync()
    {
        if (await ConfirmDialog.AskDestructiveAsync(
                ModalService,
                "確認全部刪除",
                "確定要刪除 AI 記住的全部偏好嗎？刪除後 AI 不會再參考它們。",
                "全部刪除") == false)
        {
            return;
        }

        try
        {
            var count = await CustomerMemoryService.DeleteAllAsync(CustomerId);
            ViewNotification.Warning(NotificationService, $"已刪除 {count} 項記憶。");
            await LoadAsync();
        }
        catch (Exception ex)
        {
            // 服務的全部刪除會把資料庫錯誤往外丟（單筆刪除則回傳失敗結果），這裡接住，不讓頁面崩潰。
            Logger.LogError(ex, "Failed to delete all customer memories.");
            ViewNotification.UnexpectedError(NotificationService, "刪除記憶失敗，請稍後再試。", ex);
        }
    }
}

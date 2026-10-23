using AntDesign;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Web.Components.Commons;
using CustomerHelpAgent.Web.Components.Layout;

namespace CustomerHelpAgent.Web.Components.Views.Profiles;

/// <summary>
/// 個人資料（0.9.102 起）：只能改自己的姓名；Email、角色、團隊由管理員設定（唯讀顯示）；密碼狀態；最近 20 筆自己的登入紀錄。
/// 登入即可使用（比照 /ChangePassword，不經選單權限）。存檔後由 <see cref="ProfileService"/> 通知右上角立即更新。
/// </summary>
public partial class ProfileView
{
    private const int HistorySize = Business.Services.DataAccess.ProfileService.LoginHistorySize;

    private bool isAccessChecked;
    private bool isSaving;
    private ProfileInfo? profile;
    private List<LoginRecord> logins = [];
    private string nameInput = string.Empty;

    [Inject]
    public AuthenticationStateHelper AuthenticationStateHelper { get; set; } = default!;

    [Inject]
    public AuthenticationStateProvider AuthStateProvider { get; set; } = default!;

    [Inject]
    public NavigationManager NavigationManager { get; set; } = default!;

    [Inject]
    public CurrentUserService CurrentUserService { get; set; } = default!;

    [Inject]
    public ProfileService ProfileService { get; set; } = default!;

    [Inject]
    public NotificationService NotificationService { get; set; } = default!;

    [Inject]
    public TimeProvider TimeProvider { get; set; } = default!;

    [Inject]
    public ILogger<ProfileView> Logger { get; set; } = default!;

    private string Initials => UserInitials.From(profile?.Name, profile?.Account);

    protected override async Task OnInitializedAsync()
    {
        var checkResult = await AuthenticationStateHelper.Check(AuthStateProvider, NavigationManager);
        if (checkResult != AuthenticationCheckResult.Succeeded)
        {
            return;
        }

        await LoadAsync();
        isAccessChecked = true;
    }

    private async Task LoadAsync()
    {
        var userId = CurrentUserService.CurrentUser.Id;
        profile = await ProfileService.GetAsync(userId);
        logins = await ProfileService.GetRecentLoginsAsync(userId);
        nameInput = profile?.Name ?? string.Empty;
    }

    private async Task SaveAsync()
    {
        if (profile is null || isSaving)
        {
            return;
        }

        isSaving = true;
        try
        {
            var result = await ProfileService.UpdateNameAsync(profile.Id, nameInput, profile.ConcurrencyStamp);
            if (!result.Success)
            {
                ViewNotification.Error(NotificationService, result.Message);
                return;
            }

            await LoadAsync();
            ViewNotification.Warning(NotificationService, "已更新姓名");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Saving the profile failed unexpectedly.");
            ViewNotification.UnexpectedError(NotificationService, "儲存個人資料時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
        }
        finally
        {
            isSaving = false;
        }
    }

    private string ToLocalText(DateTime utc)
        => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeProvider.LocalTimeZone).ToString("yyyy-MM-dd HH:mm");

    /// <summary>稽核動作代碼 → 畫面文字。</summary>
    internal static string DescribeAction(string action) => action switch
    {
        AuditActions.Login.Success => "登入",
        AuditActions.Login.Failed => "登入失敗",
        AuditActions.Login.Disabled => "帳號停用中嘗試登入",
        AuditActions.Login.LockedOut => "連續輸錯，帳號被鎖定",
        AuditActions.Login.SsoSuccess => "Google 登入",
        AuditActions.Login.SsoFailed => "Google 登入失敗",
        AuditActions.Login.TwoFactorFailed => "兩步驟驗證碼錯誤",
        AuditActions.Login.SessionExpired => "其他地方變更了帳號，被登出",
        AuditActions.Logout => "登出",
        _ => action,
    };
}

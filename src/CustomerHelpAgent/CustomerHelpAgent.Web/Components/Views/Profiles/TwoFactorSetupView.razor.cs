using AntDesign;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Web.Auth;
using CustomerHelpAgent.Web.Components.Commons;

namespace CustomerHelpAgent.Web.Components.Views.Profiles;

/// <summary>
/// 兩步驟驗證的設定、備用碼與停用（0.9.104 起）。登入即可使用；必須使用卻還沒設定的人，每次換頁都會被帶到這裡。
/// 密鑰在輸入驗證碼確認之前只放在這個元件的記憶體，不存進資料庫。
/// 啟用與停用會換工作階段版本（其他裝置登出），完成後經 <see cref="SessionRefreshNavigator"/> 讓這台保持登入。
/// </summary>
public partial class TwoFactorSetupView
{
    private bool isAccessChecked;
    private bool isBusy;
    private bool isRequired;
    private bool isEnabled;
    private bool hasLocalPassword;
    private bool sessionChanged;
    private int remainingBackupCodes;
    private int userId;
    private string account = string.Empty;
    private TwoFactorEnrollment? enrollment;
    private string qrDataUri = string.Empty;
    private IReadOnlyList<string>? backupCodes;
    private string enableCode = string.Empty;
    private string regenerateCode = string.Empty;
    private string disableCode = string.Empty;

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
    public ITwoFactorService TwoFactorService { get; set; } = default!;

    [Inject]
    public SessionRefreshNavigator SessionRefreshNavigator { get; set; } = default!;

    [Inject]
    public NotificationService NotificationService { get; set; } = default!;

    [Inject]
    public ILogger<TwoFactorSetupView> Logger { get; set; } = default!;

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
        userId = CurrentUserService.CurrentUser.Id;
        var profile = await ProfileService.GetAsync(userId);
        account = profile?.Account ?? string.Empty;
        hasLocalPassword = profile?.HasLocalPassword ?? false;
        isEnabled = profile?.TwoFactorEnabled ?? false;
        isRequired = await TwoFactorService.IsRequiredAsync(userId);
        remainingBackupCodes = isEnabled ? await TwoFactorService.CountUnusedBackupCodesAsync(userId) : 0;
    }

    private void BeginEnrollment()
    {
        enrollment = TwoFactorService.BeginEnrollment(account);
        qrDataUri = QrCodeImage.ToPngDataUri(enrollment.ProvisioningUri);
    }

    private Task EnableAsync() => RunAsync(async () =>
    {
        if (enrollment is null)
        {
            return;
        }

        var result = await TwoFactorService.EnableAsync(userId, enrollment.Secret, enableCode);
        enableCode = string.Empty;
        if (!result.Success)
        {
            ViewNotification.Error(NotificationService, result.Message ?? "啟用失敗。");
            return;
        }

        enrollment = null;
        qrDataUri = string.Empty;
        backupCodes = result.BackupCodes;
        sessionChanged = true;
    });

    private Task RegenerateAsync() => RunAsync(async () =>
    {
        var result = await TwoFactorService.RegenerateBackupCodesAsync(userId, regenerateCode);
        regenerateCode = string.Empty;
        if (!result.Success)
        {
            ViewNotification.Error(NotificationService, result.Message ?? "重新產生失敗。");
            return;
        }

        backupCodes = result.BackupCodes;
    });

    private Task DisableAsync() => RunAsync(async () =>
    {
        var result = await TwoFactorService.DisableAsync(userId, disableCode);
        disableCode = string.Empty;
        if (!result.Success)
        {
            ViewNotification.Error(NotificationService, result.Message);
            return;
        }

        ViewNotification.Warning(NotificationService, "已停用兩步驟驗證");
        if (!await SessionRefreshNavigator.KeepSignedInAsync("/Profile"))
        {
            await LoadAsync();
        }
    });

    /// <summary>看完備用碼：剛啟用的話工作階段版本已換掉，換發這台的 Cookie 再回個人資料頁；只是重新產生就留在這頁。</summary>
    private Task ContinueAsync() => RunAsync(async () =>
    {
        backupCodes = null;
        if (sessionChanged)
        {
            sessionChanged = false;
            if (await SessionRefreshNavigator.KeepSignedInAsync("/Profile"))
            {
                return;
            }
        }

        await LoadAsync();
    });

    private async Task RunAsync(Func<Task> action)
    {
        if (isBusy)
        {
            return;
        }

        isBusy = true;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Two-factor setup action failed unexpectedly. UserId={UserId}", userId);
            ViewNotification.UnexpectedError(NotificationService, "處理兩步驟驗證時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
        }
        finally
        {
            isBusy = false;
        }
    }

    /// <summary>金鑰每 4 個字元空一格，方便手動輸入。</summary>
    internal static string FormatKey(string secret)
        => string.Join(' ', secret.Chunk(4).Select(chunk => new string(chunk)));
}

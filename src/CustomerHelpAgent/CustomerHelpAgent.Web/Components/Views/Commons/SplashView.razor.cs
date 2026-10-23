using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using CustomerHelpAgent.Business.Services.Other;

namespace CustomerHelpAgent.Web.Components.Views.Commons;

public partial class SplashView
{
    [Inject]
    public AuthenticationStateProvider authStateProvider { get; set; } = default!;
    [Inject]
    public AuthenticationStateHelper AuthenticationStateHelper { get; set; } = default!;
    [Inject]
    public NavigationManager NavigationManager { get; set; } = default!;
    [Inject]
    public ILogger<SplashView> Logger { get; set; } = default!;
    [Inject]
    public ISystemIdentity SystemIdentity { get; set; } = default!;

    /// <summary>
    /// 系統名稱，取自 ISystemIdentity（可在「系統參數」頁修改，0.9.98 起）。
    /// </summary>
    private string SystemName => SystemIdentity.Name;

    /// <summary>
    /// 系統簡短說明，取自 ISystemIdentity（可在「系統參數」頁修改，0.9.98 起）。
    /// </summary>
    private string SystemDescription => SystemIdentity.Description;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            Logger.LogDebug("Splash view running authentication check.");
            await Task.Delay(3000); // 啟動畫面停留 3 秒
            var checkResult = await AuthenticationStateHelper
            .Check(authStateProvider, NavigationManager);
            if (checkResult == AuthenticationCheckResult.Succeeded)
            {
                Logger.LogInformation("Splash view authentication succeeded. Redirecting to /app.");
                NavigationManager.NavigateTo("/app", true, true);
            }
            else
            {
                Logger.LogWarning("Splash view authentication check did not succeed. Result={CheckResult}", checkResult);
            }
        }
    }
}

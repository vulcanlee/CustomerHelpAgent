using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.WebUtilities;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Web.Auth;
using CustomerHelpAgent.Web.Components.Auths;

namespace CustomerHelpAgent.Web.Components.Layout;

/// <summary>
/// 展示模式提示列（1.0.5 起）：
/// <list type="bullet">
/// <item>Cookie 帶展示者 claim（<see cref="DemoClaims.PresenterId"/>）：「展示模式：目前以 {名稱} 身分檢視」＋「回到展示者」——
/// 互動元件換不了 Cookie，按下去取一張切回票券，整頁導到 <c>/Auths/DemoSwitch</c>。</item>
/// <item>有故障開關啟用：「故障模擬中」（訂閱 <see cref="DemoControlService.Changed"/>，控制台一改就更新）。</item>
/// <item>網址帶 <c>demoSwitch=failed</c>（切換頁拒絕後導回）：固定文字的錯誤提示，不顯示網址內容。</item>
/// </list>
/// 示範模式關閉時前兩項都不顯示。
/// </summary>
public partial class DemoIdentityBanner : IDisposable
{
    [Inject]
    private AuthenticationStateProvider AuthenticationStateProvider { get; set; } = default!;

    [Inject]
    private CurrentUserService CurrentUserService { get; set; } = default!;

    [Inject]
    private DemoControlService DemoControlService { get; set; } = default!;

    [Inject]
    private DemoSwitchTicketService TicketService { get; set; } = default!;

    [Inject]
    private NavigationManager NavigationManager { get; set; } = default!;

    [Inject]
    private ILogger<DemoIdentityBanner> Logger { get; set; } = default!;

    private bool isSwitched;
    private int currentUserId;
    private string displayName = string.Empty;
    private bool switchFailed;

    private bool FaultActive => DemoControlService.ActiveFaults.Count > 0;

    private bool ShowBanner => isSwitched || FaultActive || switchFailed;

    protected override async Task OnInitializedAsync()
    {
        var user = (await AuthenticationStateProvider.GetAuthenticationStateAsync()).User;
        isSwitched = DemoControlService.IsEnabled
            && user.HasClaim(x => x.Type == DemoClaims.PresenterId)
            && int.TryParse(user.FindFirstValue(ClaimTypes.Sid), out currentUserId);

        var current = CurrentUserService.CurrentUser;
        displayName = !string.IsNullOrWhiteSpace(current.Name) ? current.Name : current.Account;
        switchFailed = IsSwitchFailed(NavigationManager.Uri);

        DemoControlService.Changed += OnDemoControlChanged;
        NavigationManager.LocationChanged += OnLocationChanged;
    }

    private void SwitchBack()
    {
        var ticket = TicketService.IssueSwitchBack(currentUserId);
        Logger.LogInformation("Switching back to the demo presenter. UserId={UserId}", currentUserId);
        NavigationManager.NavigateTo(DemoSwitch.UrlFor(ticket), forceLoad: true);
    }

    private static bool IsSwitchFailed(string uri)
        => QueryHelpers.ParseQuery(new Uri(uri).Query).TryGetValue("demoSwitch", out var value) && value == "failed";

    private void OnDemoControlChanged() => InvokeAsync(StateHasChanged);

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        switchFailed = IsSwitchFailed(e.Location);
        InvokeAsync(StateHasChanged);
    }

    public void Dispose()
    {
        DemoControlService.Changed -= OnDemoControlChanged;
        NavigationManager.LocationChanged -= OnLocationChanged;
    }
}

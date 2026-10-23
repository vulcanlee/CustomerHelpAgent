using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Share.Helpers;

namespace CustomerHelpAgent.Web.Auth;

/// <summary>
/// 登入 Cookie 的工作階段檢查（0.9.103 起）：每次 HTTP 請求比對 Cookie 裡的工作階段版本與資料庫，不符（或帳號已停用、刪除）就登出。
///
/// 資料庫查詢經 <see cref="ISecurityStampService"/> 快取 <see cref="CookieSettings.ValidationIntervalMinutes"/> 分鐘 —— 別的行程換了版本，這裡最晚在那之後看到。
/// ⚠️ 不可把「上次檢查時間」存進票證再 <c>ShouldRenew</c>：每次續發都會把 Cookie 往後滑，「不滑動」與記住我的期限都會失效。
/// ⚠️ 已開著的 Blazor 頁面不經過這裡（SignalR 不跑 Cookie 中介軟體），由 <c>AuthenticationStateHelper.Check</c> 在換頁時比對。
/// <c>/Auths/RefreshSession</c> 放行舊版本：那一頁自己用一次性 ticket 嚴格比對後換發新 Cookie。
/// </summary>
public sealed class SecurityStampCookieEvents : CookieAuthenticationEvents
{
    internal const string RefreshSessionPath = "/Auths/RefreshSession";

    private readonly ISecurityStampService securityStampService;
    private readonly IOptionsMonitor<CookieSettings> cookieOptions;
    private readonly ILogger<SecurityStampCookieEvents> logger;

    public SecurityStampCookieEvents(ISecurityStampService securityStampService, IOptionsMonitor<CookieSettings> cookieOptions, ILogger<SecurityStampCookieEvents> logger)
    {
        this.securityStampService = securityStampService;
        this.cookieOptions = cookieOptions;
        this.logger = logger;
    }

    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        if (context.Principal is not { } principal
            || context.HttpContext.Request.Path.StartsWithSegments(RefreshSessionPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!int.TryParse(principal.FindFirstValue(ClaimTypes.Sid), out var userId) || userId <= 0)
        {
            return;
        }

        var interval = TimeSpan.FromMinutes(cookieOptions.CurrentValue.ValidationIntervalMinutes);
        if (await securityStampService.IsValidAsync(userId, principal.FindFirstValue(MagicObjectHelper.SecurityStampClaimType), interval, cancellationToken: context.HttpContext.RequestAborted))
        {
            return;
        }

        logger.LogInformation("Login cookie rejected because the session was revoked. UserId={UserId}", userId);
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(MagicObjectHelper.CookieScheme);
    }
}

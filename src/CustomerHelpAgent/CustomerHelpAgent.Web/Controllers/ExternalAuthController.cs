using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Web.Auth;
using System.Security.Claims;

namespace CustomerHelpAgent.Web.Controllers;

/// <summary>
/// Google OAuth2 第三方登入的瀏覽器導向端點（Challenge / Callback）。
/// 僅負責網頁 Cookie 登入；API 仍使用既有的帳密換 JWT 流程。
/// </summary>
[Route("Auths/Google")]
// Google OAuth 的導向入口，本來就必須匿名可達；明示標註以搭配
// Program.cs 的 MapControllers().RequireAuthorization()（未標註即拒絕）。
[AllowAnonymous]
public class ExternalAuthController : Controller
{
    private readonly ExternalLoginService externalLoginService;
    private readonly GoogleOAuthSettings googleOAuthSettings;
    private readonly ILogger<ExternalAuthController> logger;
    private readonly IAuditLogService auditLogService;
    private readonly ISecurityStampService securityStampService;
    private readonly TwoFactorLoginCookies twoFactorCookies;
    private readonly MyUserServiceLogin myUserServiceLogin;

    public ExternalAuthController(
        ExternalLoginService externalLoginService,
        IOptions<GoogleOAuthSettings> googleOAuthSettings,
        ILogger<ExternalAuthController> logger,
        IAuditLogService auditLogService,
        ISecurityStampService securityStampService,
        TwoFactorLoginCookies twoFactorCookies,
        MyUserServiceLogin myUserServiceLogin)
    {
        this.externalLoginService = externalLoginService;
        this.googleOAuthSettings = googleOAuthSettings.Value;
        this.logger = logger;
        this.auditLogService = auditLogService;
        this.securityStampService = securityStampService;
        this.twoFactorCookies = twoFactorCookies;
        this.myUserServiceLogin = myUserServiceLogin;
    }

    /// <summary>
    /// 觸發 Google OAuth2 驗證。
    /// </summary>
    [HttpGet("Login")]
    public IActionResult Login(string? returnUrl = null)
    {
        if (!googleOAuthSettings.IsConfigured)
        {
            logger.LogWarning("Google login requested but Google OAuth is not configured.");
            return Redirect("/Auths/Login");
        }

        var safeReturnUrl = GetSafeReturnUrl(returnUrl);
        var callbackUrl = Url.Action(nameof(Callback), "ExternalAuth", new { returnUrl = safeReturnUrl })
            ?? "/Auths/Google/Callback";

        var properties = new AuthenticationProperties
        {
            RedirectUri = callbackUrl,
        };
        return Challenge(properties, GoogleDefaults.AuthenticationScheme);
    }

    /// <summary>
    /// Google 驗證完成後的回呼：查找/建立帳號，依狀態導向待審核頁或完成 Cookie 登入。
    /// </summary>
    [HttpGet("Callback")]
    public async Task<IActionResult> Callback(string? returnUrl = null)
    {
        var result = await HttpContext.AuthenticateAsync(MagicObjectHelper.ExternalCookieScheme);
        if (!result.Succeeded || result.Principal is null)
        {
            logger.LogWarning("Google callback failed because external authentication did not succeed.");
            await auditLogService.WriteAsync(AuditActions.Login.SsoFailed, success: false, detail: "provider=Google; reason=AuthFailed");
            return Redirect("/Auths/Login");
        }

        var subject = result.Principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var email = result.Principal.FindFirstValue(ClaimTypes.Email);
        var name = result.Principal.FindFirstValue(ClaimTypes.Name) ?? email ?? string.Empty;

        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(email))
        {
            logger.LogWarning("Google callback rejected because subject or email claim is missing.");
            await auditLogService.WriteAsync(AuditActions.Login.SsoFailed, success: false, detail: "provider=Google; reason=MissingClaims");
            await HttpContext.SignOutAsync(MagicObjectHelper.ExternalCookieScheme);
            return Redirect("/Auths/Login");
        }

        var lookup = await externalLoginService.FindOrCreateAsync(
            GoogleDefaults.AuthenticationScheme,
            subject,
            email,
            name,
            googleOAuthSettings.DefaultRoleName);
        var user = lookup.User;

        // 清除暫存的外部登入身分
        await HttpContext.SignOutAsync(MagicObjectHelper.ExternalCookieScheme);

        var outcome = lookup.Evaluate(DateTime.UtcNow);

        // 已刪除的使用者（0.9.95 起）：不登入、不建新帳號，回登入頁顯示固定訊息；管理員還原後即可再登入。
        if (outcome == ExternalLoginOutcome.Deleted)
        {
            logger.LogInformation("Google login refused because the user is deleted. UserId={UserId}.", user.Id);
            await auditLogService.WriteAsync(
                AuditActions.Login.SsoFailed, success: false, actorUserId: user.Id, actorAccount: user.Account, detail: "provider=Google; reason=Deleted");
            return Redirect("/Auths/Login?sso=deleted");
        }

        if (outcome == ExternalLoginOutcome.Pending)
        {
            logger.LogInformation(
                "Google login user is disabled and awaiting approval. UserId={UserId}.",
                user.Id);
            await auditLogService.WriteAsync(
                AuditActions.Login.Disabled, success: false, actorUserId: user.Id, actorAccount: user.Account, detail: "provider=Google");
            return Redirect("/Auths/Pending");
        }

        // 帳號因密碼輸錯被鎖定時，Google 登入也不放行（0.9.101 起；之前可以用 Google 繞過鎖定）。
        // 對方已向 Google 證明身分，所以明確告知是鎖定，不必用模糊訊息。
        if (outcome == ExternalLoginOutcome.Locked)
        {
            logger.LogInformation("Google login refused because the account is locked. UserId={UserId}, LockoutEndUtc={LockoutEndUtc}.", user.Id, user.LockoutEndUtc);
            await auditLogService.WriteAsync(
                AuditActions.Login.SsoFailed, success: false, actorUserId: user.Id, actorAccount: user.Account, detail: "provider=Google; reason=Locked");
            return Redirect("/Auths/Login?sso=locked");
        }

        // 重疊回收時舊版程式建立的帳號可能還沒有工作階段版本（0.9.103 起）。
        user.SecurityStamp = await securityStampService.EnsureAsync(user.Id);

        // ⚠️ 帳號已啟用兩步驟驗證時，Google 登入也要第二步（0.9.104 起）：Google 會自動連結到同 Email 的既有帳號，
        // 不問的話，已啟用兩步驟驗證的本機帳號（包括管理員）可以改用 Google 繞過。沒有啟用的 Google 帳號照舊直接登入。
        if (user.TwoFactorEnabled && !string.IsNullOrEmpty(user.TwoFactorSecret))
        {
            if (!twoFactorCookies.IsDeviceRemembered(HttpContext, user.Id, user.SecurityStamp))
            {
                twoFactorCookies.SetPending(HttpContext, new PendingTwoFactorLogin(user.Id, user.SecurityStamp, false, GetSafeReturnUrl(returnUrl), GoogleDefaults.AuthenticationScheme));
                logger.LogInformation("Google login requires the second factor. UserId={UserId}.", user.Id);
                return Redirect("/Auths/TwoFactor");
            }

            var completed = await myUserServiceLogin.CompleteSecondFactorAsync(user.Id, null, rememberedDevice: true, GoogleDefaults.AuthenticationScheme);
            if (completed.User is null)
            {
                return Redirect("/Auths/Login?sso=locked");
            }

            await HttpContext.SignInAsync(MagicObjectHelper.CookieScheme, CookieClaims.Create(completed.User));
            return Redirect(GetSafeReturnUrl(returnUrl));
        }

        await HttpContext.SignInAsync(MagicObjectHelper.CookieScheme, CookieClaims.Create(user));

        logger.LogInformation(
            "Google login succeeded. UserId={UserId}, Account={Account}.",
            user.Id, user.Account);
        await auditLogService.WriteAsync(
            AuditActions.Login.SsoSuccess, success: true, actorUserId: user.Id, actorAccount: user.Account, detail: "provider=Google");

        return Redirect(GetSafeReturnUrl(returnUrl));
    }

    private string GetSafeReturnUrl(string? returnUrl)
    {
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return returnUrl;
        }

        return "/App";
    }
}

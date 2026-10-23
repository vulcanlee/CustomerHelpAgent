using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.Models.Systems;

namespace CustomerHelpAgent.Web.Auth;

/// <summary>密碼（或 Google）已通過、等待第二步的登入。</summary>
public sealed record PendingTwoFactorLogin(int UserId, string SecurityStamp, bool RememberMe, string ReturnUrl, string? Provider);

/// <summary>
/// 兩步驟驗證登入用的兩個 Cookie（0.9.104 起），都以 Data Protection 加密並設定到期時間：
/// <list type="bullet">
/// <item><c>.CustomerHelpAgent.TwoFactorPending</c>：密碼對了、還沒輸入驗證碼的那 5 分鐘（只存使用者 Id、工作階段版本、記住我、返回網址）。</item>
/// <item><c>.CustomerHelpAgent.TwoFactorDevice</c>：「記住這台裝置」（使用者 Id、工作階段版本）；⚠️ 版本換掉（改密碼、強制登出、重設兩步驟驗證）就失效。</item>
/// </list>
/// 名稱帶專案名（<c>New-StarterProject.ps1</c> 會一起換掉），HttpOnly、只在 HTTPS 下標 Secure。
/// </summary>
public sealed class TwoFactorLoginCookies
{
    internal const string PendingCookieName = ".CustomerHelpAgent.TwoFactorPending";
    internal const string DeviceCookieName = ".CustomerHelpAgent.TwoFactorDevice";
    internal static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(5);

    private readonly ITimeLimitedDataProtector pendingProtector;
    private readonly ITimeLimitedDataProtector deviceProtector;
    private readonly IOptionsMonitor<TwoFactorSettings> options;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<TwoFactorLoginCookies> logger;

    public TwoFactorLoginCookies(IDataProtectionProvider dataProtectionProvider, IOptionsMonitor<TwoFactorSettings> options, TimeProvider timeProvider, ILogger<TwoFactorLoginCookies> logger)
    {
        pendingProtector = dataProtectionProvider.CreateProtector("CustomerHelpAgent.TwoFactorPending.v1").ToTimeLimitedDataProtector();
        deviceProtector = dataProtectionProvider.CreateProtector("CustomerHelpAgent.TwoFactorDevice.v1").ToTimeLimitedDataProtector();
        this.options = options;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    public int RememberDeviceDays => options.CurrentValue.RememberDeviceDays;

    public void SetPending(HttpContext httpContext, PendingTwoFactorLogin pending)
    {
        var expires = timeProvider.GetUtcNow().Add(PendingLifetime);
        var payload = string.Join('\n', pending.UserId, pending.SecurityStamp, pending.RememberMe, pending.ReturnUrl, pending.Provider ?? string.Empty);
        httpContext.Response.Cookies.Append(PendingCookieName, pendingProtector.Protect(payload, expires), CreateOptions(httpContext, expires));
    }

    public PendingTwoFactorLogin? ReadPending(HttpContext httpContext)
    {
        if (!httpContext.Request.Cookies.TryGetValue(PendingCookieName, out var value) || Unprotect(pendingProtector, value) is not { } payload)
        {
            return null;
        }

        var parts = payload.Split('\n');
        return parts.Length == 5 && int.TryParse(parts[0], out var userId) && bool.TryParse(parts[2], out var rememberMe)
            ? new PendingTwoFactorLogin(userId, parts[1], rememberMe, parts[3], parts[4].Length == 0 ? null : parts[4])
            : null;
    }

    public void ClearPending(HttpContext httpContext) => httpContext.Response.Cookies.Delete(PendingCookieName, CreateOptions(httpContext, null));

    public void RememberDevice(HttpContext httpContext, int userId, string securityStamp)
    {
        var days = RememberDeviceDays;
        if (days <= 0)
        {
            return;
        }

        var expires = timeProvider.GetUtcNow().AddDays(days);
        httpContext.Response.Cookies.Append(DeviceCookieName, deviceProtector.Protect($"{userId}\n{securityStamp}", expires), CreateOptions(httpContext, expires));
    }

    /// <summary>這台裝置在期限內被這個人記住過，而且他的工作階段版本沒換過。</summary>
    public bool IsDeviceRemembered(HttpContext httpContext, int userId, string currentSecurityStamp)
    {
        if (RememberDeviceDays <= 0
            || !httpContext.Request.Cookies.TryGetValue(DeviceCookieName, out var value)
            || Unprotect(deviceProtector, value) is not { } payload)
        {
            return false;
        }

        var parts = payload.Split('\n');
        return parts.Length == 2 && int.TryParse(parts[0], out var rememberedUserId) && rememberedUserId == userId
            && Business.Services.Other.SecurityStamps.Matches(parts[1], currentSecurityStamp);
    }

    private string? Unprotect(ITimeLimitedDataProtector protector, string value)
    {
        try
        {
            return protector.Unprotect(value, out _);
        }
        catch (CryptographicException)
        {
            logger.LogDebug("Two-factor login cookie ignored because it is invalid or expired.");
            return null;
        }
    }

    private static CookieOptions CreateOptions(HttpContext httpContext, DateTimeOffset? expires) => new()
    {
        HttpOnly = true,
        Secure = httpContext.Request.IsHttps,
        SameSite = SameSiteMode.Lax,
        IsEssential = true,
        Path = "/",
        Expires = expires,
    };
}

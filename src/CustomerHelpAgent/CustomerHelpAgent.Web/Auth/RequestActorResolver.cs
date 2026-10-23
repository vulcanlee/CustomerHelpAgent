using System.Security.Claims;

namespace CustomerHelpAgent.Web.Auth;

/// <summary>
/// 從已驗證的身分取出可以寫進紀錄的 Account 與 UserId。
///
/// ⚠️ claim 對應在兩套機制中相反：Cookie 是 NameIdentifier=帳號、Sid=UserId、Name=<b>姓名（個資）</b>；
/// JWT 是 NameIdentifier=UserId、Name=帳號。
/// 依「帶了哪些 claim」判斷，不依路徑 —— <c>/api/project-files</c> 走 Cookie，依路徑會讀到姓名。
/// 先認 Cookie（有 Sid），確保 Cookie 身分永遠不會讀到 <see cref="ClaimTypes.Name"/>。
/// </summary>
public static class RequestActorResolver
{
    public static (string? Account, int? UserId) Resolve(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return (null, null);
        }

        var nameIdentifier = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (int.TryParse(principal.FindFirst(ClaimTypes.Sid)?.Value, out var cookieUserId) && cookieUserId > 0)
        {
            return (nameIdentifier, cookieUserId);
        }

        if (int.TryParse(nameIdentifier, out var jwtUserId) && jwtUserId > 0)
        {
            return (principal.FindFirst(ClaimTypes.Name)?.Value, jwtUserId);
        }

        return (null, null);
    }
}

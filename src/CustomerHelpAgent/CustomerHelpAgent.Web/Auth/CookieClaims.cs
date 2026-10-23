using System.Security.Claims;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Share.Helpers;

namespace CustomerHelpAgent.Web.Auth;

/// <summary>
/// 登入 Cookie 的 claims（0.9.103 起集中在這裡；之前登入頁與 Google 回呼各寫一份）。
/// ⚠️ Cookie 的 claim 對應：Sid＝使用者 Id、NameIdentifier＝帳號、Name＝姓名；與 JWT 相反（見 <see cref="RequestActorResolver"/>）。
/// 工作階段版本（<see cref="MagicObjectHelper.SecurityStampClaimType"/>）由 Cookie 驗證器與 <c>AuthenticationStateHelper.Check</c> 比對。
/// </summary>
public static class CookieClaims
{
    public static ClaimsPrincipal Create(MyUser user)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Role, "User"),
            new(ClaimTypes.Name, user.Name),
            new(ClaimTypes.NameIdentifier, user.Account),
            new(ClaimTypes.Sid, user.Id.ToString()),
            new(MagicObjectHelper.SecurityStampClaimType, user.SecurityStamp),
        };

        return new ClaimsPrincipal(new ClaimsIdentity(claims, MagicObjectHelper.CookieScheme));
    }
}

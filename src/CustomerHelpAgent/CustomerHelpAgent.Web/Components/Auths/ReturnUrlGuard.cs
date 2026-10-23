namespace CustomerHelpAgent.Web.Components.Auths;

/// <summary>
/// 登入後導回位址（ReturnUrl）的防護：只接受站內路徑，其餘一律回首頁。
///
/// 0.9.41 之前 Login.razor.cs 把查詢字串的 ReturnUrl 原封不動塞進 <c>AuthenticationProperties.RedirectUri</c>，
/// 而 Cookie handler 對「明寫的」RedirectUri 不做站內檢查 ——
/// <c>/Auths/Login?ReturnUrl=https://evil.com</c> 登入成功後就會被導去外站（open redirect）。
/// 規則比照 <c>Url.IsLocalUrl</c>（ExternalAuthController.GetSafeReturnUrl 用的就是它）。
/// </summary>
public static class ReturnUrlGuard
{
    public const string DefaultUrl = "/App";

    public static string Sanitize(string? returnUrl)
    {
        return IsLocalUrl(returnUrl) ? returnUrl! : DefaultUrl;
    }

    /// <summary>是否為站內路徑（<see cref="Sanitize"/> 的判斷；呼叫端要自己決定不合法時的預設值時用）。</summary>
    public static bool IsLocalUrl([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? url)
    {
        if (string.IsNullOrEmpty(url) || url[0] != '/')
        {
            return false;
        }

        // "/" 本身合法；"//host" 與 "/\host" 會被瀏覽器當成協定相對網址，導去外站。
        if (url.Length == 1)
        {
            return true;
        }

        if (url[1] == '/' || url[1] == '\\')
        {
            return false;
        }

        // 控制字元（例如 "/\t/evil.com"）會被瀏覽器剝掉，剝完就變成 "//evil.com"。
        return !url.Any(char.IsControl);
    }
}

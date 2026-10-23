using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Web.Auth;

namespace CustomerHelpAgent.Web.Components.Auths
{
    /// <summary>
    /// 換發這台裝置的登入 Cookie（0.9.103 起）：自己改了密碼或角色之後，工作階段版本已換掉，其他裝置被登出，這台用一次性 ticket 換一張新 Cookie。
    ///
    /// ⚠️ 必須同時成立才換發：ticket 有效且第一次使用、帶著原本的 Cookie（同一個使用者、舊版本相符）、ticket 的新版本等於資料庫目前的版本、帳號仍可登入。
    /// 任何一項不成立就走登出 —— 不可退而求其次直接用 ticket 登入（別人可以把自己的 ticket 連結寄給你）。
    /// Cookie 驗證器對這個路徑放行舊版本（見 <see cref="SecurityStampCookieEvents"/>）。
    /// </summary>
    public partial class RefreshSession
    {
        [CascadingParameter]
        private HttpContext HttpContext { get; set; } = default!;

        [SupplyParameterFromQuery(Name = "ticket")]
        private string? Ticket { get; set; }

        [SupplyParameterFromQuery(Name = "returnUrl")]
        private string? ReturnUrl { get; set; }

        [Inject]
        public SessionRefreshTicketService TicketService { get; set; } = default!;

        [Inject]
        public ISecurityStampService SecurityStampService { get; set; } = default!;

        [Inject]
        public MyUserServiceLogin MyUserServiceLogin { get; set; } = default!;

        [Inject]
        public NavigationManager NavigationManager { get; set; } = default!;

        [Inject]
        public ILogger<RefreshSession> Logger { get; set; } = default!;

        protected override async Task OnInitializedAsync()
        {
            // ticket 在網址裡：不讓它經 Referer 外流、也不讓瀏覽器快取這一頁。
            HttpContext.Response.Headers["Referrer-Policy"] = "no-referrer";
            HttpContext.Response.Headers.CacheControl = "no-store";

            var ticket = TicketService.Consume(Ticket);
            var current = await HttpContext.AuthenticateAsync(MagicObjectHelper.CookieScheme);
            if (ticket is null || !current.Succeeded || current.Principal is not { } principal || current.Properties is not { } properties)
            {
                Fail(null);
                return;
            }

            if (!int.TryParse(principal.FindFirstValue(ClaimTypes.Sid), out var userId) || userId != ticket.UserId
                || !SecurityStamps.Matches(principal.FindFirstValue(MagicObjectHelper.SecurityStampClaimType), ticket.OldStamp))
            {
                Fail(ticket.UserId);
                return;
            }

            var state = await SecurityStampService.GetStateAsync(userId, TimeSpan.Zero);
            var user = state.IsActive && SecurityStamps.Matches(ticket.NewStamp, state.SecurityStamp)
                ? await MyUserServiceLogin.GetActiveUserAsync(userId)
                : null;
            if (user is null)
            {
                Fail(userId);
                return;
            }

            // 沿用原本 Cookie 的「記住我」與到期時間：改密碼不應該讓 30 天重新起算。
            await HttpContext.SignInAsync(
                MagicObjectHelper.CookieScheme,
                CookieClaims.Create(user),
                new AuthenticationProperties { IsPersistent = properties.IsPersistent, ExpiresUtc = properties.ExpiresUtc });
            Logger.LogInformation("Login cookie refreshed for the current device. UserId={UserId}", userId);

            // 靜態 SSR 只有在登入頁 SignInAsync 才會自動導向，這裡要自己導；NavigateTo 不會中斷執行，之後不能再有程式。
            NavigationManager.NavigateTo(ReturnUrlGuard.Sanitize(ReturnUrl));
        }

        private void Fail(int? userId)
        {
            Logger.LogWarning("Login cookie refresh refused. UserId={UserId}", userId);
            NavigationManager.NavigateTo("/Auths/Logout?reason=session", forceLoad: true);
        }
    }
}

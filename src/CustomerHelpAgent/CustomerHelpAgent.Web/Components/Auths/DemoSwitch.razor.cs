using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Business.Startup;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Web.Auth;

namespace CustomerHelpAgent.Web.Components.Auths
{
    /// <summary>
    /// 展示身分切換（1.0.5 起，只在 <c>DemoSettings:Enabled</c> 時可用）：展示者（管理員）免密碼切換成示範帳號，或從示範帳號切回展示者。
    /// 互動元件取得 <see cref="DemoSwitchTicketService"/> 的一次性票券後整頁導來這裡，由這一頁換發 Cookie（工作階段 Cookie）。
    ///
    /// ⚠️ 必須同時成立才換發：示範模式開啟、票券有效且第一次使用、票券建立者等於目前 Cookie 的使用者。
    /// <list type="bullet">
    /// <item>切換：目前使用者是啟用中的管理員、Cookie 的版本等於資料庫目前的版本、目標在 <see cref="DemoSeedData.Accounts"/> 內、啟用中、有角色。
    /// 新 Cookie 就是目標帳號本身的 Cookie（<see cref="CookieClaims.Create"/>），另加 <see cref="DemoClaims"/> 記住展示者 ——
    /// 之後的工作階段檢查、改密碼與兩步驟驗證的導向都照目標帳號的規則走，不繞過。</item>
    /// <item>切回：Cookie 帶展示者 claims、展示者仍是啟用中的管理員。展示者的版本換過（被強制登出、改了密碼），
    /// 或目前（示範帳號）Cookie 的版本已不是資料庫的版本（Cookie 驗證器的快取可能還沒看到），就不切回，直接登出到登入頁。</item>
    /// </list>
    /// 切換成功導向票券的 <see cref="DemoSwitchTicket.ReturnUrl"/>（只接受站內路徑），沒有或不合法就照預設：顧客 <c>/shop</c>、其他 <c>/App</c>。
    /// ⚠️ 切換不經過目標帳號的登入第二步（兩步驟驗證）：這是刻意的免密碼設計，只限管理員、只在示範模式。
    /// 任何一項不成立：寫失敗稽核、導向 <c>/App?demoSwitch=failed</c>（目前是顧客則 <c>/shop?demoSwitch=failed</c>，
    /// 讓顧客版面的提示列看得到），版面顯示固定的錯誤提示，不換發 Cookie。
    /// </summary>
    public partial class DemoSwitch
    {
        internal const string FailedUrl = "/App?demoSwitch=failed";

        /// <summary>目前是顧客（沒有後台首頁、有顧客商店）時的失敗導向：顧客進不了 /App，提示要在商店版面顯示。</summary>
        internal const string CustomerFailedUrl = "/shop?demoSwitch=failed";

        /// <summary>互動元件取得票券後以 <c>forceLoad: true</c> 導向這個網址。</summary>
        public static string UrlFor(string ticket) => $"/Auths/DemoSwitch?ticket={Uri.EscapeDataString(ticket)}";

        [CascadingParameter]
        private HttpContext HttpContext { get; set; } = default!;

        [SupplyParameterFromQuery(Name = "ticket")]
        private string? Ticket { get; set; }

        [Inject]
        public DemoSwitchTicketService TicketService { get; set; } = default!;

        [Inject]
        public DemoControlService DemoControlService { get; set; } = default!;

        [Inject]
        public ISecurityStampService SecurityStampService { get; set; } = default!;

        [Inject]
        public MyUserServiceLogin MyUserServiceLogin { get; set; } = default!;

        [Inject]
        public IAuditLogService AuditLogService { get; set; } = default!;

        [Inject]
        public IPermissionChecker PermissionChecker { get; set; } = default!;

        [Inject]
        public NavigationManager NavigationManager { get; set; } = default!;

        [Inject]
        public ILogger<DemoSwitch> Logger { get; set; } = default!;

        protected override async Task OnInitializedAsync()
        {
            // 票券在網址裡：不讓它經 Referer 外流、也不讓瀏覽器快取這一頁。
            HttpContext.Response.Headers["Referrer-Policy"] = "no-referrer";
            HttpContext.Response.Headers.CacheControl = "no-store";

            // 先兌換（作廢）票券，再做其他檢查：失敗的票券也不能再用第二次。
            var principal = HttpContext.User;
            TicketService.TryRedeem(Ticket, out var ticket);
            var purpose = ticket?.Purpose
                ?? (principal.HasClaim(x => x.Type == DemoClaims.PresenterId) ? DemoSwitchPurpose.SwitchBack : DemoSwitchPurpose.SwitchIdentity);
            var action = purpose == DemoSwitchPurpose.SwitchBack ? AuditActions.Demo.SwitchBack : AuditActions.Demo.SwitchIdentity;

            if (!DemoControlService.IsEnabled)
            {
                await FailAsync(action, "DemoDisabled");
                return;
            }

            if (ticket is null)
            {
                await FailAsync(action, "InvalidTicket");
                return;
            }

            if (!int.TryParse(principal.FindFirstValue(ClaimTypes.Sid), out var currentUserId) || currentUserId != ticket.CreatorUserId)
            {
                await FailAsync(action, "TicketMismatch");
                return;
            }

            if (ticket.Purpose == DemoSwitchPurpose.SwitchIdentity)
            {
                await SwitchAsync(currentUserId, ticket.TargetAccount ?? string.Empty, ticket.ReturnUrl, principal);
            }
            else
            {
                await SwitchBackAsync(currentUserId, principal);
            }
        }

        private async Task SwitchAsync(int presenterId, string targetAccount, string? returnUrl, ClaimsPrincipal principal)
        {
            var action = AuditActions.Demo.SwitchIdentity;
            var presenter = await MyUserServiceLogin.GetActiveUserAsync(presenterId);
            if (presenter is not { IsAdmin: true })
            {
                await FailAsync(action, "NotAdmin");
                return;
            }

            // 已經是切換後的身分就不再往下切（展示者只記一層）。
            if (principal.HasClaim(x => x.Type == DemoClaims.PresenterId))
            {
                await FailAsync(action, "AlreadySwitched");
                return;
            }

            // Cookie 驗證器的版本比對有快取；換發前以資料庫為準再比一次。
            var presenterStamp = principal.FindFirstValue(MagicObjectHelper.SecurityStampClaimType);
            if (!SecurityStamps.Matches(presenterStamp, presenter.SecurityStamp))
            {
                await FailAsync(action, "SessionRevoked");
                return;
            }

            var demoAccount = DemoSeedData.Accounts.FirstOrDefault(x => string.Equals(x.Account, targetAccount, StringComparison.Ordinal));
            if (demoAccount is null)
            {
                await FailAsync(action, "NotDemoAccount");
                return;
            }

            var target = await MyUserServiceLogin.GetActiveUserByAccountAsync(demoAccount.Account);
            if (target is not { RoleViewId: not null })
            {
                await FailAsync(action, "TargetUnavailable");
                return;
            }

            target.SecurityStamp = await SecurityStampService.EnsureAsync(target.Id);
            var switched = CookieClaims.Create(target);
            ((ClaimsIdentity)switched.Identity!).AddClaims(
            [
                new Claim(DemoClaims.PresenterId, presenter.Id.ToString()),
                new Claim(DemoClaims.PresenterStamp, presenter.SecurityStamp),
            ]);

            await HttpContext.SignInAsync(MagicObjectHelper.CookieScheme, switched, new AuthenticationProperties { IsPersistent = false });
            Logger.LogInformation(
                "Demo identity switched. PresenterUserId={PresenterUserId}, TargetUserId={TargetUserId}, TargetAccount={TargetAccount}",
                presenter.Id, target.Id, target.Account);
            await AuditLogService.WriteAsync(
                action, success: true, actorUserId: presenter.Id, actorAccount: presenter.Account,
                targetType: nameof(MyUser), targetId: target.Id.ToString(), detail: $"target={target.Account}");

            // 靜態 SSR 的 NavigateTo 不會中斷執行，之後不能再有程式。
            var defaultUrl = demoAccount.RoleName == MagicObjectHelper.顧客角色 ? "/shop" : "/App";
            NavigationManager.NavigateTo(ReturnUrlGuard.IsLocalUrl(returnUrl) ? returnUrl : defaultUrl);
        }

        private async Task SwitchBackAsync(int currentUserId, ClaimsPrincipal principal)
        {
            var action = AuditActions.Demo.SwitchBack;
            var presenterStamp = principal.FindFirstValue(DemoClaims.PresenterStamp);
            if (!int.TryParse(principal.FindFirstValue(DemoClaims.PresenterId), out var presenterId) || string.IsNullOrEmpty(presenterStamp))
            {
                await FailAsync(action, "NoPresenter");
                return;
            }

            var presenter = await MyUserServiceLogin.GetActiveUserAsync(presenterId);
            if (presenter is not { IsAdmin: true })
            {
                await FailAsync(action, "PresenterUnavailable");
                return;
            }

            // 展示者在切換期間被強制登出或改了密碼：不切回，連這個衍生的工作階段一起結束，請展示者重新登入。
            if (!SecurityStamps.Matches(presenterStamp, presenter.SecurityStamp))
            {
                await RevokeAsync(action, "PresenterSessionRevoked");
                return;
            }

            // 縱深防禦：目前（示範帳號）的工作階段也要仍然有效。Cookie 驗證器的版本快取可能還沒看到別處換掉的版本，這裡以資料庫為準。
            var current = await MyUserServiceLogin.GetActiveUserAsync(currentUserId);
            if (current is null || !SecurityStamps.Matches(principal.FindFirstValue(MagicObjectHelper.SecurityStampClaimType), current.SecurityStamp))
            {
                await RevokeAsync(action, "SessionRevoked");
                return;
            }

            var from = RequestActorResolver.Resolve(principal).Account;
            await HttpContext.SignInAsync(MagicObjectHelper.CookieScheme, CookieClaims.Create(presenter), new AuthenticationProperties { IsPersistent = false });
            Logger.LogInformation("Demo identity switched back to the presenter. PresenterUserId={PresenterUserId}, FromAccount={FromAccount}", presenter.Id, from);
            await AuditLogService.WriteAsync(
                action, success: true, actorUserId: presenter.Id, actorAccount: presenter.Account,
                targetType: nameof(MyUser), targetId: presenter.Id.ToString(), detail: $"from={from}");

            NavigationManager.NavigateTo("/demo-console");
        }

        private async Task FailAsync(string action, string reason)
        {
            await WriteFailureAuditAsync(action, reason);
            NavigationManager.NavigateTo(await IsCustomerAsync() ? CustomerFailedUrl : FailedUrl);
        }

        /// <summary>工作階段已失效：寫失敗稽核、連這個工作階段一起結束，請展示者重新登入。</summary>
        private async Task RevokeAsync(string action, string reason)
        {
            await WriteFailureAuditAsync(action, reason);
            await HttpContext.SignOutAsync(MagicObjectHelper.CookieScheme);
            NavigationManager.NavigateTo("/Auths/Login", forceLoad: true);
        }

        /// <summary>目前使用者是顧客：沒有後台首頁、有顧客商店（與首頁把顧客導到 /shop 的判斷相同）。管理員兩者都有，不算顧客。</summary>
        private async Task<bool> IsCustomerAsync()
        {
            if (!int.TryParse(HttpContext.User.FindFirstValue(ClaimTypes.Sid), out var userId))
            {
                return false;
            }

            return !await PermissionChecker.HasPermissionAsync(userId, PermissionKey.For(MagicObjectHelper.角色_首頁, PermissionActions.View))
                && await PermissionChecker.HasPermissionAsync(userId, PermissionKey.For(MagicObjectHelper.角色_顧客商店, PermissionActions.View));
        }

        private async Task WriteFailureAuditAsync(string action, string reason)
        {
            var (account, userId) = RequestActorResolver.Resolve(HttpContext.User);
            Logger.LogWarning("Demo identity switch refused. Action={Action}, UserId={UserId}, Reason={Reason}", action, userId, reason);
            await AuditLogService.WriteAsync(action, success: false, actorUserId: userId, actorAccount: account, detail: $"reason={reason}");
        }
    }
}

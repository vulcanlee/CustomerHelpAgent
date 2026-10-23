using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Web.Auth;

namespace CustomerHelpAgent.Web.Components.Auths
{
    /// <summary>
    /// 登入第二步（0.9.104 起，匿名、靜態 SSR 表單）：密碼或 Google 已通過、帳號已啟用兩步驟驗證時，從登入頁帶過來。
    ///
    /// 誰在登入記在 5 分鐘的加密 Cookie（<see cref="TwoFactorLoginCookies"/>），不在網址或表單裡。
    /// 送出時重查工作階段版本（期間改了密碼或被強制登出就作廢）；驗證碼錯了計入登入失敗次數，達門檻鎖定並通知管理員。
    /// ⚠️ 靜態 SSR 下 <c>SignInAsync</c> 只在登入頁會自動導向，這裡要自己 <c>NavigateTo</c>；<c>NavigateTo</c> 之後一定要 <c>return</c>。
    /// </summary>
    public partial class TwoFactor
    {
        private string message = string.Empty;
        private PendingTwoFactorLogin? pending;

        [CascadingParameter]
        private HttpContext HttpContext { get; set; } = default!;

        [SupplyParameterFromForm]
        private InputModel Input { get; set; } = default!;

        [Inject]
        public TwoFactorLoginCookies TwoFactorCookies { get; set; } = default!;

        [Inject]
        public MyUserServiceLogin MyUserServiceLogin { get; set; } = default!;

        [Inject]
        public ISecurityStampService SecurityStampService { get; set; } = default!;

        [Inject]
        public IOptions<CookieSettings> CookieOptions { get; set; } = default!;

        [Inject]
        public NavigationManager NavigationManager { get; set; } = default!;

        [Inject]
        public ILogger<TwoFactor> Logger { get; set; } = default!;

        protected override void OnInitialized()
        {
            Input ??= new();
            HttpContext.Response.Headers.CacheControl = "no-store";

            pending = TwoFactorCookies.ReadPending(HttpContext);
            if (pending is null)
            {
                Logger.LogInformation("Two-factor page opened without a pending login; returning to the login page.");
                NavigationManager.NavigateTo("/Auths/Login");
            }
        }

        private async Task SubmitAsync()
        {
            message = string.Empty;
            if (pending is null)
            {
                return;
            }

            // 等待輸入驗證碼的期間改了密碼、被停用或被強制登出：這次登入作廢，重新從密碼開始。
            var state = await SecurityStampService.GetStateAsync(pending.UserId, TimeSpan.Zero);
            if (!state.IsActive || !SecurityStamps.Matches(pending.SecurityStamp, state.SecurityStamp))
            {
                Logger.LogInformation("Pending two-factor login discarded because the session changed. UserId={UserId}", pending.UserId);
                TwoFactorCookies.ClearPending(HttpContext);
                NavigationManager.NavigateTo("/Auths/Login");
                return;
            }

            var result = await MyUserServiceLogin.CompleteSecondFactorAsync(pending.UserId, Input.Code, rememberedDevice: false, pending.Provider);
            if (result.User is not { } user)
            {
                message = result.Message;
                Input.Code = string.Empty;
                return;
            }

            var properties = new AuthenticationProperties { IsPersistent = pending.RememberMe };
            if (pending.RememberMe)
            {
                properties.ExpiresUtc = DateTimeOffset.UtcNow.AddDays(CookieOptions.Value.RememberMeDays);
            }

            await HttpContext.SignInAsync(MagicObjectHelper.CookieScheme, CookieClaims.Create(user), properties);
            if (Input.RememberDevice)
            {
                TwoFactorCookies.RememberDevice(HttpContext, user.Id, user.SecurityStamp);
            }

            TwoFactorCookies.ClearPending(HttpContext);
            Logger.LogInformation("Two-factor login completed. UserId={UserId}", user.Id);
            NavigationManager.NavigateTo(ReturnUrlGuard.Sanitize(pending.ReturnUrl));
        }

        private sealed class InputModel
        {
            public string Code { get; set; } = string.Empty;

            public bool RememberDevice { get; set; }
        }
    }
}

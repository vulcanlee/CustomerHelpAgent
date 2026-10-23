using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Web.Auth;
using CustomerHelpAgent.Web.Configuration;
using System.Security.Claims;

namespace CustomerHelpAgent.Web.Components.Auths
{
    public partial class Login
    {
        string errorMessage = string.Empty;
        string captchaCode = string.Empty;

        [CascadingParameter]
        private HttpContext HttpContext { get; set; } = default!;

        [SupplyParameterFromForm]
        private InputModel Input { get; set; } = default!;

        [SupplyParameterFromQuery]
        private string? ReturnUrl { get; set; }

        /// <summary>重設密碼成功後導回本頁時帶 <c>?reset=1</c>，用來顯示成功訊息。</summary>
        [SupplyParameterFromQuery(Name = "reset")]
        private string? Reset { get; set; }

        /// <summary>
        /// Google 登入被拒時導回本頁帶 <c>?sso=代碼</c>（0.9.95 起）。只認得固定代碼、對應固定文字，
        /// 其他值一律忽略 —— 不把網址內容顯示到頁面上。
        /// </summary>
        [SupplyParameterFromQuery(Name = "sso")]
        private string? Sso { get; set; }

        [Inject]
        public ILogger<Login> Logger { get; set; } = default!;

        [Inject]
        public MyUserServiceLogin MyUserServiceLogin { get; set; } = default!;

        [Inject]
        public IOptions<GoogleOAuthSettings> GoogleOptions { get; set; } = default!;

        [Inject]
        public IOptions<CookieSettings> CookieOptions { get; set; } = default!;

        [Inject]
        public IOptions<EmailSettings> EmailOptions { get; set; } = default!;

        [Inject]
        public IOptionsMonitor<LockoutSettings> LockoutOptions { get; set; } = default!;

        [Inject]
        public TwoFactorLoginCookies TwoFactorCookies { get; set; } = default!;

        [Inject]
        public ISecurityStampService SecurityStampService { get; set; } = default!;

        string message = string.Empty;

        private bool ShowGoogleLogin => GoogleOptions.Value.IsConfigured;

        /// <summary>寄信未啟用（Provider = None）時沒有忘記密碼可用，連結不出現。</summary>
        private bool ShowForgotPassword => EmailOptions.Value.TryGetProvider(out var provider) && provider != EmailProvider.None;

        /// <summary>
        /// 登入失敗的訊息刻意不分「帳號不存在／密碼錯／鎖定中」（0.9.101 起），所以把鎖定規則固定寫在頁面上，
        /// 被鎖住的人才知道可以等、重設密碼或找管理員。
        /// </summary>
        private string LockoutHint
        {
            get
            {
                var settings = LockoutOptions.CurrentValue;
                var way = ShowForgotPassword ? "可以用「忘記密碼」重設，或請系統管理員解鎖" : "請稍後再試，或請系統管理員解鎖";
                return $"連續輸錯 {settings.MaxFailedAttempts} 次密碼會暫時鎖定 {settings.LockoutMinutes} 分鐘；被鎖定時{way}。";
            }
        }

        /// <summary>只在還沒送出登入表單時顯示；登入失敗的錯誤訊息優先。</summary>
        private bool ShowResetSucceeded => Reset == "1" && string.IsNullOrEmpty(message);

        /// <summary>只在還沒送出登入表單時顯示；登入失敗的錯誤訊息優先。</summary>
        private string? SsoFailureMessage => !string.IsNullOrEmpty(message)
            ? null
            : Sso switch
            {
                "deleted" => "此帳號已被刪除，請洽系統管理員。",
                "locked" => "此帳號因連續輸錯密碼已暫時鎖定，請稍後再試，或請系統管理員解鎖。",
                _ => null,
            };

        private string GoogleLoginUrl =>
            string.IsNullOrWhiteSpace(ReturnUrl)
                ? "/Auths/Google/Login"
                : $"/Auths/Google/Login?returnUrl={Uri.EscapeDataString(ReturnUrlGuard.Sanitize(ReturnUrl))}";

        protected override Task OnInitializedAsync()
        {
            Input ??= new();

            if (string.IsNullOrWhiteSpace(Input.CaptchaCode))
            {
                RefreshCaptcha();
            }
            else
            {
                captchaCode = Input.CaptchaCode;
            }

            Logger.LogDebug("Login component initialized. ReturnUrl={ReturnUrl}", ReturnUrl);
            return Task.CompletedTask;
        }

        public async Task LoginUser()
        {
            message = string.Empty;
            errorMessage = string.Empty;

            if (string.IsNullOrWhiteSpace(Input.Account))
            {
                message = "請輸入帳號";
                errorMessage = "alert-danger";
                Logger.LogWarning("Login submission rejected because account is empty.");
                return;
            }

            if (string.IsNullOrWhiteSpace(Input.Password))
            {
                message = "請輸入密碼";
                errorMessage = "alert-danger";
                Logger.LogInformation("Login submission rejected because password is empty. Account={Account}", Input.Account);
                return;
            }

            if (string.IsNullOrWhiteSpace(Input.CaptchaInput))
            {
                message = "請輸入驗證碼";
                errorMessage = "alert-danger";
                Logger.LogWarning("Login submission rejected because captcha is empty. Account={Account}", Input.Account);
                return;
            }

            if (!string.Equals(Input.CaptchaInput.Trim(), Input.CaptchaCode, StringComparison.Ordinal))
            {
                message = "驗證碼錯誤";
                errorMessage = "alert-danger";
                RefreshCaptcha();
                Input.CaptchaInput = string.Empty;
                Logger.LogWarning("Login submission rejected because captcha is invalid. Account={Account}", Input.Account);
                return;
            }

            var attempt = await MyUserServiceLogin.LoginAsync(Input.Account, Input.Password);
            (string result, MyUser? myUser) = attempt;

            // 已啟用兩步驟驗證（0.9.104 起）：記住過的裝置直接完成，否則帶到第二步（還不發 Cookie）。
            if (attempt.RequiresTwoFactor && attempt.User is { } pendingUser)
            {
                var stamp = await SecurityStampService.EnsureAsync(pendingUser.Id);
                if (!TwoFactorCookies.IsDeviceRemembered(HttpContext, pendingUser.Id, stamp))
                {
                    TwoFactorCookies.SetPending(HttpContext, new PendingTwoFactorLogin(pendingUser.Id, stamp, Input.RememberMe, ReturnUrlGuard.Sanitize(ReturnUrl), null));
                    NavigationManager.NavigateTo("/Auths/TwoFactor");
                    return;
                }

                (result, myUser) = await MyUserServiceLogin.CompleteSecondFactorAsync(pendingUser.Id, null, rememberedDevice: true);
            }

            if (!string.IsNullOrEmpty(result) || myUser is null)
            {
                Logger.LogWarning("Login failed for Account={Account}. Reason={Reason}", Input.Account, result);
                message = string.IsNullOrEmpty(result) ? "登入失敗，請重新確認帳號與密碼。" : result;
                RefreshCaptcha();
                Input.CaptchaInput = string.Empty;
            }
            else
            {
                string returnUrl = ReturnUrlGuard.Sanitize(ReturnUrl);
                var authProperties = new AuthenticationProperties
                {
                    IsPersistent = Input.RememberMe,
                    RedirectUri = returnUrl,
                };

                // 勾了「記住我」就走比較長的效期。
                // ⚠️ 不能靠 AddCookie 的 ExpireTimeSpan —— 那是整個 scheme 共用的，
                // 做不出兩種效期；明寫 ExpiresUtc 才能只覆蓋這一次登入的票證。
                if (Input.RememberMe)
                {
                    authProperties.ExpiresUtc =
                        DateTimeOffset.UtcNow.AddDays(CookieOptions.Value.RememberMeDays);
                }

                try
                {
                    await HttpContext.SignInAsync(
                        MagicObjectHelper.CookieScheme,
                        CookieClaims.Create(myUser),
                        authProperties);

                    Logger.LogInformation(
                        "Login succeeded for Account={Account}, UserId={UserId}, RedirectUri={RedirectUri}.",
                        Input.Account,
                        myUser.Id,
                        returnUrl);
                }
                catch (Exception ex)
                {
                    message = ex.Message;
                    RefreshCaptcha();
                    Input.CaptchaInput = string.Empty;
                    Logger.LogError(ex, "Sign-in failed for Account={Account}.", Input.Account);
                }
            }

            errorMessage = string.IsNullOrEmpty(message) ? string.Empty : "alert-danger";
        }

        private sealed class InputModel
        {
            public string Account { get; set; } = string.Empty;

            public string Password { get; set; } = string.Empty;

            public bool RememberMe { get; set; }

            public string CaptchaInput { get; set; } = string.Empty;

            public string CaptchaCode { get; set; } = string.Empty;
        }

        private void RefreshCaptcha()
        {
            captchaCode = AuthCaptcha.Generate();
            Input.CaptchaCode = captchaCode;
        }
    }
}

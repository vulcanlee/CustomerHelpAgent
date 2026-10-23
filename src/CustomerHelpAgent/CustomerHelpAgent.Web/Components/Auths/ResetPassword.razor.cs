using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Web.Configuration;

namespace CustomerHelpAgent.Web.Components.Auths
{
    /// <summary>
    /// 以信中連結設定新密碼（匿名、靜態 SSR 表單）。
    ///
    /// <para>token 第一次由網址 <c>?token=</c> 帶入，之後放在隱藏欄位隨表單往返。
    /// 開頁時只檢查、不消耗；真正送出成功才會作廢（見 <see cref="PasswordResetService.ResetAsync"/>）。</para>
    ///
    /// <para>⚠️ token 在網址裡，所以回應一律 <c>Referrer-Policy: no-referrer</c>（頁面載入的外部資源拿不到網址）
    /// 與 <c>Cache-Control: no-store</c>（瀏覽器與中間代理不留副本）。</para>
    ///
    /// <para>⚠️ 靜態 SSR 下 <c>NavigateTo</c> 不會丟例外中斷執行（csproj 的
    /// <c>BlazorDisableThrowNavigationException</c>），呼叫後一定要 <c>return</c>。</para>
    /// </summary>
    public partial class ResetPassword
    {
        private string message = string.Empty;
        private bool isTokenValid;
        private string? account;

        [CascadingParameter]
        private HttpContext HttpContext { get; set; } = default!;

        [SupplyParameterFromForm]
        private InputModel Input { get; set; } = default!;

        [SupplyParameterFromQuery(Name = "token")]
        private string? QueryToken { get; set; }

        [Inject]
        public PasswordResetService PasswordResetService { get; set; } = default!;

        [Inject]
        public IOptions<EmailSettings> EmailOptions { get; set; } = default!;

        [Inject]
        public NavigationManager NavigationManager { get; set; } = default!;

        [Inject]
        public ILogger<ResetPassword> Logger { get; set; } = default!;

        [Inject]
        public IPasswordPolicy PasswordPolicy { get; set; } = default!;

        private bool IsEmailEnabled => EmailOptions.Value.TryGetProvider(out var provider) && provider != EmailProvider.None;

        private string Subtitle => isTokenValid
            ? $"為帳號「{account}」設定新密碼。密碼規則：{PasswordPolicy.Describe()}"
            : "請使用重設密碼信中的連結開啟本頁。";

        protected override async Task OnInitializedAsync()
        {
            Input ??= new();

            HttpContext.Response.Headers["Referrer-Policy"] = "no-referrer";
            HttpContext.Response.Headers.CacheControl = "no-store";

            if (!IsEmailEnabled)
            {
                return;
            }

            // 表單送回時 token 在隱藏欄位；第一次開頁時在網址。
            if (string.IsNullOrWhiteSpace(Input.Token))
            {
                Input.Token = QueryToken ?? string.Empty;
            }

            (isTokenValid, account) = await PasswordResetService.ValidateTokenAsync(Input.Token);
            if (!isTokenValid)
            {
                Logger.LogInformation("Reset password page opened with an invalid or expired link.");
            }
        }

        private async Task SubmitAsync()
        {
            message = string.Empty;

            if (!IsEmailEnabled || !isTokenValid)
            {
                return;
            }

            var result = await PasswordResetService.ResetAsync(Input.Token, Input.NewPassword, Input.ConfirmPassword);
            if (result.Success)
            {
                NavigationManager.NavigateTo("/Auths/Login?reset=1");
                return;
            }

            message = result.Message;
            isTokenValid = result.Message != PasswordResetService.InvalidLinkMessage;
            Input.NewPassword = string.Empty;
            Input.ConfirmPassword = string.Empty;
        }

        private sealed class InputModel
        {
            public string Token { get; set; } = string.Empty;

            public string NewPassword { get; set; } = string.Empty;

            public string ConfirmPassword { get; set; } = string.Empty;
        }
    }
}

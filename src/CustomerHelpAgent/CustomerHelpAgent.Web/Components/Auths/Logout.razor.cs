using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Web.Auth;

namespace CustomerHelpAgent.Web.Components.Auths
{
    public partial class Logout
    {
        private readonly ILogger<Logout> logger;
        private readonly IAuditLogService auditLogService;
        string errorMessage = string.Empty;

        public Logout(ILogger<Logout> logger, IAuditLogService auditLogService)
        {
            this.logger = logger;
            this.auditLogService = auditLogService;
        }

        [CascadingParameter]
        private HttpContext HttpContext { get; set; } = default!;

        [Inject]
        public NavigationManager NavigationManager { get; set; } = default!;

        /// <summary>
        /// <c>session</c>：工作階段已失效、由系統登出（0.9.103 起）。那一筆已由 <c>AuthenticationStateHelper.Check</c> 記成
        /// <c>Login.SessionExpired</c>，這裡不再記一筆「使用者登出」。
        /// </summary>
        [SupplyParameterFromQuery(Name = "reason")]
        private string? Reason { get; set; }

        protected override async Task OnInitializedAsync()
        {
            try
            {
                // 登出後 User 就清空了，身分必須在 SignOut 之前取（LOG-15）。
                var (account, userId) = RequestActorResolver.Resolve(HttpContext.User);

                await HttpContext.SignOutAsync(MagicObjectHelper.CookieScheme);
                await Task.Delay(200);
                logger.LogInformation("User logout completed successfully. Account={Account}, UserId={UserId}", account, userId);

                // 未登入狀態打開登出頁（重複點擊、過期 Cookie）不算一次登出，不寫稽核。
                if (userId is not null && Reason != "session")
                {
                    await auditLogService.WriteAsync(AuditActions.Logout, success: true, actorUserId: userId, actorAccount: account);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Logout flow failed unexpectedly.");
                errorMessage = ex.Message;
            }

            NavigationManager.NavigateTo("/Auths/Login", forceLoad: true);
        }
    }
}

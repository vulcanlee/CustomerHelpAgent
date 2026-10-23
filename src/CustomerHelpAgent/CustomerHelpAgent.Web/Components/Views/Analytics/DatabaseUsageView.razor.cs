using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Web.Diagnostics;

namespace CustomerHelpAgent.Web.Components.Views.Analytics
{
    public partial class DatabaseUsageView
    {
        private readonly ILogger<DatabaseUsageView> logger;
        private readonly IDatabaseUsageService databaseUsageService;

        private DatabaseUsageReport? report;
        private string RoleMessage = string.Empty;
        private bool isAccessChecked;

        [Inject]
        public AuthenticationStateHelper AuthenticationStateHelper { get; set; } = default!;
        [Inject]
        public AuthenticationStateProvider authStateProvider { get; set; } = default!;
        [Inject]
        public NavigationManager NavigationManager { get; set; } = default!;

        public DatabaseUsageView(
            ILogger<DatabaseUsageView> logger,
            IDatabaseUsageService databaseUsageService)
        {
            this.logger = logger;
            this.databaseUsageService = databaseUsageService;
        }

        protected override async Task OnInitializedAsync()
        {
            var checkResult = await AuthenticationStateHelper.Check(authStateProvider, NavigationManager);
            if (checkResult != AuthenticationCheckResult.Succeeded)
            {
                return;
            }

            isAccessChecked = true;

            // 此頁為管理員專屬：權限鍵刻意未上架角色矩陣，因此以 CheckIsAdmin 直接判斷，
            // 與既有的系統健康監控頁一致。權限未通過前不讀取任何資料庫資訊。
            if (AuthenticationStateHelper.CheckIsAdmin() == false)
            {
                RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
                await AuthenticationStateHelper.RecordPageAccessDeniedAsync("/database-usage");
                logger.LogWarning("Database usage page denied because the current user is not an administrator.");
                return;
            }

            report = await databaseUsageService.GetReportAsync();
        }
    }
}

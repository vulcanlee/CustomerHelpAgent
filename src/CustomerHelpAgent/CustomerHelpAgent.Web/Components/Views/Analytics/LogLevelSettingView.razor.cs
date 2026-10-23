using AntDesign;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Web.Components.Commons;
using CustomerHelpAgent.Web.Diagnostics;
using CustomerHelpAgent.Business.Helpers;

namespace CustomerHelpAgent.Web.Components.Views.Analytics
{
    public partial class LogLevelSettingView
    {
        private static readonly (LogLevelRank Level, string Description)[] LevelOptions =
        [
            (LogLevelRank.Trace, "最詳細，含框架層級細節"),
            (LogLevelRank.Debug, "含查詢條件、換頁等排錯資訊"),
            (LogLevelRank.Info, "含使用者操作軌跡，日常建議值"),
            (LogLevelRank.Warn, "僅警告以上"),
            (LogLevelRank.Error, "僅錯誤以上"),
            (LogLevelRank.Fatal, "僅嚴重錯誤"),
        ];

        private readonly ILogger<LogLevelSettingView> logger;
        private readonly LogLevelRuntimeState logLevelState;
        private readonly IAuditLogService auditLogService;
        private readonly CurrentUserService currentUserService;
        private readonly ModalService modalService;
        private readonly MessageService messageService;

        private LogLevelRank selectedLevel = LogLevelRank.Info;
        private LogLevelRank effectiveLevel = LogLevelRank.Info;
        private LogLevelRank systemDefaultLevel = LogLevelRank.Info;
        private bool hasOverride;

        private string RoleMessage = string.Empty;
        private bool isAccessChecked;

        [Inject]
        public AuthenticationStateHelper AuthenticationStateHelper { get; set; } = default!;
        [Inject]
        public AuthenticationStateProvider authStateProvider { get; set; } = default!;
        [Inject]
        public NavigationManager NavigationManager { get; set; } = default!;

        public LogLevelSettingView(
            ILogger<LogLevelSettingView> logger,
            LogLevelRuntimeState logLevelState,
            IAuditLogService auditLogService,
            CurrentUserService currentUserService,
            ModalService modalService,
            MessageService messageService)
        {
            this.logger = logger;
            this.logLevelState = logLevelState;
            this.auditLogService = auditLogService;
            this.currentUserService = currentUserService;
            this.modalService = modalService;
            this.messageService = messageService;
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
            // 與同子功能表的其他頁面一致。權限未通過前不讀取任何設定資訊。
            if (AuthenticationStateHelper.CheckIsAdmin() == false)
            {
                RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
                await AuthenticationStateHelper.RecordPageAccessDeniedAsync("/log-level-setting");
                logger.LogWarning("Log level setting denied because the current user is not an administrator.");
                return;
            }

            RefreshState();
            selectedLevel = effectiveLevel;
        }

        /// <summary>每次進頁與每次操作後都重讀真實狀態，不快取。</summary>
        private void RefreshState()
        {
            effectiveLevel = logLevelState.EffectiveLevel;
            systemDefaultLevel = logLevelState.SystemDefaultLevel;
            hasOverride = logLevelState.OverrideLevel is not null;
        }

        private async Task OnApplyAsync()
        {
            var target = selectedLevel;

            // 只有調低到會暴增日誌量的等級才需要確認；調高不會有這個風險。
            if (target is LogLevelRank.Trace or LogLevelRank.Debug)
            {
                var confirmed = await ConfirmDialog.AskAsync(
                    modalService,
                    "確認調整日誌等級",
                    $"調整為 {LogLevelRankHelper.ToLevelText(target)} 會大幅增加日誌量，"
                        + "請在排查完問題後記得調回來。確定要套用嗎？",
                    "套用");

                if (confirmed == false)
                {
                    logger.LogDebug("Log level change cancelled by user. Target={Target}", target);
                    return;
                }
            }

            var previous = effectiveLevel;
            if (logLevelState.Apply(target) == false)
            {
                _ = messageService.ErrorAsync("套用失敗，請確認 NLog 設定。");
                return;
            }

            RefreshState();
            selectedLevel = effectiveLevel;
            _ = messageService.SuccessAsync($"已套用日誌等級：{LogLevelRankHelper.ToLevelText(target)}");

            await WriteAuditAsync(AuditActions.LogLevel.Apply, previous, target);
        }

        private async Task OnRestoreAsync()
        {
            var previous = effectiveLevel;
            if (logLevelState.RestoreDefault() == false)
            {
                _ = messageService.ErrorAsync("還原失敗，請確認 NLog 設定。");
                return;
            }

            RefreshState();
            selectedLevel = effectiveLevel;
            _ = messageService.SuccessAsync(
                $"已還原為系統預設等級：{LogLevelRankHelper.ToLevelText(effectiveLevel)}");

            await WriteAuditAsync(AuditActions.LogLevel.Restore, previous, effectiveLevel);
        }

        /// <summary>
        /// 稽核寫入失敗不應推翻「等級已經套用」這個事實，因此只記錯誤不向使用者報錯。
        /// </summary>
        private async Task WriteAuditAsync(string action, LogLevelRank previous, LogLevelRank current)
        {
            try
            {
                var currentUser = currentUserService.CurrentUser;
                await auditLogService.WriteAsync(
                    action,
                    success: true,
                    actorUserId: currentUser.Id,
                    actorAccount: currentUser.Account,
                    targetType: "NLog",
                    targetId: "logger:*",
                    detail: $"日誌最低等級由 {LogLevelRankHelper.ToLevelText(previous)} "
                        + $"調整為 {LogLevelRankHelper.ToLevelText(current)}（僅執行期有效）。");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to write audit log for log level change. Action={Action}", action);
            }
        }
    }
}

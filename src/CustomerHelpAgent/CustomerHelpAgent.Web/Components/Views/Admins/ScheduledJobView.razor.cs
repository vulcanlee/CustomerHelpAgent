using AntDesign;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.AdapterModel;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Web.Components.Commons;
using CustomerHelpAgent.Web.Scheduling;

namespace CustomerHelpAgent.Web.Components.Views.Admins
{
    /// <summary>
    /// 排程作業（0.9.96 起）：每個作業的執行時間、下次執行、最近一次結果、啟用開關、立即執行與執行紀錄。
    /// 管理員專屬：權限鍵刻意不進角色矩陣，以 CheckIsAdmin 判斷。
    /// </summary>
    public partial class ScheduledJobView : IDisposable
    {
        /// <summary>有作業在佇列或執行中時，每隔這麼久自動更新一次，結果出來不必手動重新整理。</summary>
        private static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromSeconds(5);

        private readonly ILogger<ScheduledJobView> logger;
        private readonly ScheduledJobOverviewService overviewService;
        private readonly ModalService modalService;
        private readonly NotificationService notificationService;

        private ScheduledJobOverview? overview;
        private bool isLoading;
        private string RoleMessage = string.Empty;
        private bool isAccessChecked;

        private bool runsVisible;
        private bool isRunsLoading;
        private string runsTitle = "執行紀錄";
        private List<JobRunAdapterModel> runs = [];

        private Timer? autoRefreshTimer;
        private bool disposed;

        [Inject]
        public AuthenticationStateHelper AuthenticationStateHelper { get; set; } = default!;

        [Inject]
        public AuthenticationStateProvider authStateProvider { get; set; } = default!;

        [Inject]
        public NavigationManager NavigationManager { get; set; } = default!;

        [Inject]
        public IAuditLogService AuditLogService { get; set; } = default!;

        [Inject]
        public CurrentUserService CurrentUserService { get; set; } = default!;

        public ScheduledJobView(
            ILogger<ScheduledJobView> logger,
            ScheduledJobOverviewService overviewService,
            ModalService modalService,
            NotificationService notificationService)
        {
            this.logger = logger;
            this.overviewService = overviewService;
            this.modalService = modalService;
            this.notificationService = notificationService;
        }

        protected override async Task OnInitializedAsync()
        {
            var checkResult = await AuthenticationStateHelper.Check(authStateProvider, NavigationManager);
            if (checkResult != AuthenticationCheckResult.Succeeded)
            {
                return;
            }

            isAccessChecked = true;

            if (AuthenticationStateHelper.CheckIsAdmin() == false)
            {
                RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
                await AuthenticationStateHelper.RecordPageAccessDeniedAsync("/scheduled-jobs");
                logger.LogWarning("Scheduled job view denied because the current user is not an administrator.");
                return;
            }

            await ReloadAsync();
        }

        private async Task ReloadAsync()
        {
            isLoading = true;
            try
            {
                overview = await overviewService.GetOverviewAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to load scheduled jobs.");
                ViewNotification.UnexpectedError(notificationService, "讀取排程作業時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
            finally
            {
                isLoading = false;
            }

            UpdateAutoRefresh();
        }

        private async Task OnRefreshAsync()
        {
            await ReloadAsync();
            ViewNotification.Info(notificationService, "已更新最新資料");
        }

        private async Task OnTriggerAsync(ScheduledJobOverviewItem item)
        {
            try
            {
                var ok = await ConfirmDialog.AskAsync(
                    modalService,
                    "立即執行",
                    $"要立即執行「{item.DisplayName}」嗎？執行會在背景進行，完成後這一頁會自動更新結果。",
                    "立即執行");
                if (!ok)
                {
                    return;
                }

                var account = CurrentUserService.CurrentUser.Account;
                if (!overviewService.TryTrigger(item.Name, account, CurrentUserService.CurrentUser.Id))
                {
                    ViewNotification.Caution(notificationService, "這個作業已經在執行中，請等它完成。");
                    return;
                }

                logger.LogInformation("Scheduled job triggered manually. JobName={JobName}", item.Name);
                await ViewAudit.WriteAsync(AuditLogService, CurrentUserService, AuditActions.Job.Trigger, "ScheduledJob", item.Name, $"job={item.Name}");
                ViewNotification.Warning(notificationService, $"已開始執行「{item.DisplayName}」。");
                await ReloadAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception while triggering a scheduled job.");
                ViewNotification.UnexpectedError(notificationService, "啟動排程作業時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
        }

        private async Task OnToggleEnabledAsync(ScheduledJobOverviewItem item, bool enabled)
        {
            try
            {
                await overviewService.SetEnabledAsync(item.Name, enabled, CurrentUserService.CurrentUser.Account);
                await ViewAudit.WriteAsync(
                    AuditLogService, CurrentUserService, enabled ? AuditActions.Job.Enable : AuditActions.Job.Disable,
                    "ScheduledJob", item.Name, $"job={item.Name}");
                logger.LogInformation("Scheduled job enabled state changed from the page. JobName={JobName}, Enabled={Enabled}", item.Name, enabled);
                ViewNotification.Warning(notificationService, enabled
                    ? $"已啟用「{item.DisplayName}」，從下一個時段開始依排程執行。"
                    : $"已停用「{item.DisplayName}」，不會再依排程執行（仍可按「立即執行」）。");
                await ReloadAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception while changing a scheduled job enabled state.");
                ViewNotification.UnexpectedError(notificationService, "切換排程作業時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
                await ReloadAsync();
            }
        }

        private async Task OnShowRunsAsync(ScheduledJobOverviewItem item)
        {
            runsTitle = $"執行紀錄：{item.DisplayName}";
            runsVisible = true;
            isRunsLoading = true;
            try
            {
                runs = await overviewService.GetRecentRunsAsync(item.Name);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to load scheduled job runs.");
                ViewNotification.UnexpectedError(notificationService, "讀取執行紀錄時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
            finally
            {
                isRunsLoading = false;
            }
        }

        private static bool IsRunning(ScheduledJobOverviewItem item) => item.LastRun?.Status == JobRunStatuses.Running;

        /// <summary>有作業在佇列或執行中就每 5 秒自動更新；都結束了就停掉，不白白查資料庫。</summary>
        private void UpdateAutoRefresh()
        {
            var busy = overview?.Items.Any(x => x.IsQueued || IsRunning(x)) == true;
            if (busy && autoRefreshTimer is null && !disposed)
            {
                autoRefreshTimer = new Timer(_ => _ = InvokeAsync(async () =>
                {
                    if (disposed)
                    {
                        return;
                    }

                    await ReloadAsync();
                    StateHasChanged();
                }), null, AutoRefreshInterval, AutoRefreshInterval);
            }
            else if (!busy && autoRefreshTimer is not null)
            {
                autoRefreshTimer.Dispose();
                autoRefreshTimer = null;
            }
        }

        private string ToLocalText(DateTime utc) => overviewService.ToLocal(utc).ToString("yyyy-MM-dd HH:mm:ss");

        private static string DurationText(long milliseconds)
            => milliseconds < 1000 ? $"{milliseconds} ms" : $"{milliseconds / 1000d:0.#} 秒";

        private static string StatusText(string status) => status switch
        {
            JobRunStatuses.Running => "執行中",
            JobRunStatuses.Succeeded => "成功",
            JobRunStatuses.Failed => "失敗",
            JobRunStatuses.Interrupted => "中斷",
            JobRunStatuses.Skipped => "略過",
            _ => status,
        };

        private static StatusTone ToneOf(string status) => status switch
        {
            JobRunStatuses.Succeeded => StatusTone.Positive,
            JobRunStatuses.Running => StatusTone.Accent,
            JobRunStatuses.Failed or JobRunStatuses.Interrupted => StatusTone.Warning,
            _ => StatusTone.Muted,
        };

        private static string TriggerText(string trigger) => trigger switch
        {
            JobRunTriggers.Schedule => "排程",
            JobRunTriggers.CatchUp => "補跑",
            JobRunTriggers.Manual => "手動",
            _ => trigger,
        };

        public void Dispose()
        {
            disposed = true;
            autoRefreshTimer?.Dispose();
            autoRefreshTimer = null;
        }
    }
}

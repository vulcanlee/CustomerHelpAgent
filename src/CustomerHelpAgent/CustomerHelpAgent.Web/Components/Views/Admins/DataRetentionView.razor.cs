using AntDesign;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Web.Components.Commons;
using CustomerHelpAgent.Web.DataRetention;
using CustomerHelpAgent.Web.Scheduling;
using CustomerHelpAgent.Web.Scheduling.Jobs;

namespace CustomerHelpAgent.Web.Components.Views.Admins
{
    /// <summary>
    /// 資料保留與清理（0.9.133 起）：每種資料的數量、檔案空間、最舊一筆、保留規則、下次會清掉幾筆與負責的排程；
    /// 「立即清理」走「排程作業」頁的同一個佇列，「前往」到細部刪除的頁面。
    /// 管理員專屬：權限鍵刻意不進角色矩陣，以 CheckIsAdmin 判斷。
    /// </summary>
    public partial class DataRetentionView : IDisposable
    {
        /// <summary>有清理作業在佇列或執行中時，每隔這麼久自動更新一次。</summary>
        private static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromSeconds(5);

        private readonly ILogger<DataRetentionView> logger;
        private readonly DataRetentionOverviewService overviewService;
        private readonly ScheduledJobOverviewService jobOverviewService;
        private readonly ModalService modalService;
        private readonly NotificationService notificationService;

        private DataRetentionOverview? overview;
        private bool isLoading;
        private string RoleMessage = string.Empty;
        private bool isAccessChecked;
        private Timer? autoRefreshTimer;
        private bool disposed;

        // 自訂清除窗（0.9.134 起）。
        private const string KeepDaysMode = "keepDays";
        private const string BeforeDateMode = "beforeDate";
        private bool customVisible;
        private string customTitle = "自訂清除";
        private DataRetentionRow? customRow;
        private string customMode = KeepDaysMode;
        private int? customDays = 30;
        private DateTime? customDate;
        private int? customPreview;
        private string? customError;

        /// <summary>預覽是非同步的：使用者連續改值時，只採用最後一次的結果。</summary>
        private int previewVersion;

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

        [Inject]
        public TimeProvider TimeProvider { get; set; } = default!;

        public DataRetentionView(
            ILogger<DataRetentionView> logger,
            DataRetentionOverviewService overviewService,
            ScheduledJobOverviewService jobOverviewService,
            ModalService modalService,
            NotificationService notificationService)
        {
            this.logger = logger;
            this.overviewService = overviewService;
            this.jobOverviewService = jobOverviewService;
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
                await AuthenticationStateHelper.RecordPageAccessDeniedAsync("/data-retention");
                logger.LogWarning("Data retention view denied because the current user is not an administrator.");
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
                logger.LogError(ex, "Failed to load the data retention overview.");
                ViewNotification.UnexpectedError(notificationService, "讀取資料保留資訊時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
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

        /// <summary>
        /// 立即清理：先重抓一次數字（確認窗顯示的要是最新的），再放進「立即執行」佇列，寫 <c>Job.Trigger</c> 稽核。
        /// 已刪除資料的五列共用一個作業，按任何一列都會五種一起清，確認窗逐項列出。
        /// </summary>
        private async Task OnPurgeNowAsync(DataRetentionRow row)
        {
            try
            {
                if (row.JobName is not { } jobName)
                {
                    return;
                }

                await ReloadAsync();
                var related = overview?.Rows.Where(x => x.JobName == jobName && x.Error is null).ToList() ?? [];
                var current = related.FirstOrDefault(x => x.Key == row.Key) ?? row;
                if (!current.CanPurgeNow)
                {
                    ViewNotification.Caution(notificationService, $"現在不能清理「{row.Label}」：{current.PurgeDisabledReason}。");
                    return;
                }

                var expected = related.Sum(x => x.Expected ?? 0);
                if (!await ConfirmDialog.AskDestructiveAsync(modalService, "立即清理", BuildConfirmContent(current, related, expected), "立即清理"))
                {
                    return;
                }

                if (!overviewService.TryTrigger(jobName, CurrentUserService.CurrentUser.Account, CurrentUserService.CurrentUser.Id))
                {
                    ViewNotification.Caution(notificationService, "這個清理作業已經在執行中，請等它完成。");
                    return;
                }

                logger.LogInformation("Data retention purge triggered manually. JobName={JobName}, Expected={Expected}", jobName, expected);
                await ViewAudit.WriteAsync(AuditLogService, CurrentUserService, AuditActions.Job.Trigger, "ScheduledJob", jobName,
                    $"job={jobName}; source=data-retention; expected={expected}");
                ViewNotification.Warning(notificationService, $"已開始清理「{current.Job?.DisplayName ?? row.Label}」，完成後這一頁會自動更新。");
                await ReloadAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception while triggering a data retention purge.");
                ViewNotification.UnexpectedError(notificationService, "啟動清理時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
        }

        /// <summary>已刪除的五種共用一個排程，除了第一列（專案）以外不重複顯示執行訊息。</summary>
        private static bool IsSoftDeleteFollower(DataRetentionRow row)
            => row.JobName == SoftDeletePurgeJob.JobName && row.Key != "deleted-project";

        private bool CanRunCustomPurge => customError is null && customPreview > 0;

        private CustomPurgeCutoff CurrentCutoff => customMode == KeepDaysMode
            ? new CustomPurgeCutoff(customDays, null)
            : new CustomPurgeCutoff(null, customDate is { } date ? DateOnly.FromDateTime(date) : null);

        private async Task OnOpenCustomPurgeAsync(DataRetentionRow row)
        {
            customRow = row;
            customTitle = $"自訂清除：{row.Label}";
            customMode = KeepDaysMode;
            customDays = 30;
            customDate = TimeProvider.GetLocalNow().DateTime.Date.AddDays(-30);
            customVisible = true;
            await UpdatePreviewAsync();
        }

        private async Task UpdatePreviewAsync()
        {
            if (customRow is null)
            {
                return;
            }

            var version = ++previewVersion;
            var cutoff = CurrentCutoff;
            customPreview = null;
            customError = cutoff.Validate(DateOnly.FromDateTime(TimeProvider.GetLocalNow().DateTime));
            if (customError is not null)
            {
                return;
            }

            try
            {
                var count = await overviewService.PreviewCustomAsync(customRow.Key, cutoff);
                if (version == previewVersion)
                {
                    customPreview = count;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to preview a custom purge. RowKey={RowKey}", customRow.Key);
                customError = $"無法計算會刪除幾筆：{ex.GetType().Name}";
            }
        }

        /// <summary>
        /// 「清除」。⚠️ 第一行的 customVisible = true 不可移動（同 FormModalFlow 的說明）：AntDesign 已先送出關窗，
        /// 這裡搶回來，之後由 RunOkAsync 的結果決定開關 —— 確認窗按取消時留在這個窗。
        /// </summary>
        private async Task OnCustomPurgeOkAsync()
        {
            customVisible = true;
            customVisible = await FormModalFlow.RunOkAsync(
                RunCustomPurgeAsync,
                logger,
                notificationService,
                "清除資料時發生未預期的錯誤，請稍後再試或聯絡系統管理員。");
        }

        /// <summary>實際清除；回傳 true 表示完成、可以關窗。</summary>
        private async Task<bool> RunCustomPurgeAsync()
        {
            if (customRow is not { } row || !CanRunCustomPurge)
            {
                return false;
            }

            var cutoff = CurrentCutoff;
            var expected = await overviewService.PreviewCustomAsync(row.Key, cutoff);
            if (!await ConfirmDialog.AskDestructiveAsync(modalService, "自訂清除",
                    $"將依「{cutoff.Describe()}」刪除「{row.Label}」約 {expected:N0} {row.CountUnit}{(row.Note is { } note ? $"，{note}" : string.Empty)}。\n此動作無法復原，也不會改到系統參數的保留天數。",
                    "清除"))
            {
                return false;
            }

            var outcome = await overviewService.PurgeCustomAsync(row.Key, cutoff, CancellationToken.None);
            if (outcome.Removed > 0)
            {
                await ViewAudit.WriteAsync(AuditLogService, CurrentUserService, outcome.AuditAction, outcome.TargetType, "*", cutoff.AuditDetail(outcome.Removed));
            }

            logger.LogInformation("Custom purge completed. RowKey={RowKey}, Removed={Removed}, Left={Left}, Failed={Failed}",
                row.Key, outcome.Removed, outcome.Left, outcome.Failed);
            var message = $"已刪除 {outcome.Removed:N0} {row.CountUnit}「{row.Label}」"
                + (outcome.Left > 0 ? $"；{outcome.Left:N0} {row.CountUnit}受保護、仍在使用或中途被還原，留下。" : "。");
            if (outcome.Failed)
            {
                ViewNotification.Error(notificationService, message + "途中有一筆失敗，已停止；詳細內容請看「系統例外紀錄」。");
            }
            else
            {
                ViewNotification.Warning(notificationService, message);
            }

            await ReloadAsync();
            return true;
        }

        internal static string BuildConfirmContent(DataRetentionRow row, IReadOnlyList<DataRetentionRow> related, int expected)
        {
            var lines = new List<string>();
            if (row.JobName == SoftDeletePurgeJob.JobName)
            {
                lines.Add($"已刪除資料是一起清理的，會同時永久刪除這五種中超過保留期限的資料（{row.RuleText}）：");
                lines.AddRange(related.Select(x => $"・{x.Label}：{x.Expected ?? 0:N0} {x.CountUnit}"));
                lines.Add($"共最多 {expected:N0} 筆；support 帳號、預設角色、仍在使用的角色與還有下屬的部門會留到下次。");
            }
            else
            {
                lines.Add($"將依目前的保留規則（{row.RuleText}）刪除「{row.Label}」中約 {expected:N0} {row.CountUnit}{(row.Note is { } note ? $"，{note}" : string.Empty)}。");
            }

            if (row.Job is { NextRunLocal: null })
            {
                lines.Add("這個作業目前不會依排程執行，這次只執行一次。");
            }

            lines.Add("清理在背景進行，此動作無法復原。");
            return string.Join('\n', lines);
        }

        private static string ParameterUrl(string group) => $"/system-parameters?group={Uri.EscapeDataString(group)}";

        private static string FolderText(FolderUsage? folder) => folder switch
        {
            null => "—",
            { Status: FolderUsageStatus.NotConfigured } => "未設定",
            { Status: FolderUsageStatus.Missing } => "0 B",
            { Status: FolderUsageStatus.Partial } => $"至少 {SizeFormatHelper.FormatBytes(folder.Bytes)}",
            _ => SizeFormatHelper.FormatBytes(folder.Bytes),
        };

        private string ToLocalText(DateTime utc) => jobOverviewService.ToLocal(utc).ToString("yyyy-MM-dd HH:mm");

        private static string StatusText(string status) => status switch
        {
            JobRunStatuses.Running => "執行中",
            JobRunStatuses.Succeeded => "成功",
            JobRunStatuses.Failed => "失敗",
            JobRunStatuses.Interrupted => "中斷",
            JobRunStatuses.Skipped => "略過",
            _ => status,
        };

        /// <summary>有清理作業在佇列或執行中就每 5 秒自動更新；都結束了就停掉，不白白查資料庫。</summary>
        private void UpdateAutoRefresh()
        {
            var busy = overview?.Rows.Any(x => x.Job is { } job && (job.IsQueued || job.LastRun?.Status == JobRunStatuses.Running)) == true;
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

        public void Dispose()
        {
            disposed = true;
            autoRefreshTimer?.Dispose();
            autoRefreshTimer = null;
        }
    }
}

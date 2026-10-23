using AntDesign;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.Business.Factories;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Web.Backup;
using CustomerHelpAgent.Web.Components.Commons;
using CustomerHelpAgent.Web.Configuration;
using CustomerHelpAgent.Web.Scheduling;
using CustomerHelpAgent.Web.Scheduling.Jobs;

namespace CustomerHelpAgent.Web.Components.Views.Admins
{
    /// <summary>
    /// 系統備份（0.9.99 起）：立即備份、備份清單、下載與刪除。管理員專屬：權限鍵刻意不進角色矩陣，以 CheckIsAdmin 判斷。
    /// 「立即備份」放進排程框架的佇列（與排程共用作業鎖），不在畫面事件裡直接執行。
    /// </summary>
    public partial class BackupView : IDisposable
    {
        private static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromSeconds(5);

        private readonly ILogger<BackupView> logger;
        private readonly BackupStore store;
        private readonly ScheduledJobOverviewService overviewService;
        private readonly IOptionsMonitor<BackupSettings> backupOptions;
        private readonly ModalService modalService;
        private readonly NotificationService notificationService;

        private List<BackupFileInfo> backups = [];
        private IEnumerable<BackupFileInfo> selectedRows = [];
        private bool batchDeleting;
        private ScheduledJobOverviewItem? job;
        private int keepCount;
        private bool isLoading;
        private string RoleMessage = string.Empty;
        private bool isAccessChecked;
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

        public BackupView(
            ILogger<BackupView> logger,
            BackupStore store,
            ScheduledJobOverviewService overviewService,
            IOptionsMonitor<BackupSettings> backupOptions,
            ModalService modalService,
            NotificationService notificationService)
        {
            this.logger = logger;
            this.store = store;
            this.overviewService = overviewService;
            this.backupOptions = backupOptions;
            this.modalService = modalService;
            this.notificationService = notificationService;
        }

        private bool IsBusy => job is not null && (job.IsQueued || job.LastRun?.Status == JobRunStatuses.Running);

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
                await AuthenticationStateHelper.RecordPageAccessDeniedAsync("/backups");
                logger.LogWarning("Backup view denied because the current user is not an administrator.");
                return;
            }

            await ReloadAsync();
        }

        private async Task ReloadAsync()
        {
            // 重新整理與刪除後都會走到這裡：清單換了，舊的勾選不再有效（0.9.132 起）。
            selectedRows = [];
            isLoading = true;
            try
            {
                backups = store.List().ToList();
                job = (await overviewService.GetOverviewAsync()).Items.FirstOrDefault(x => x.Name == SystemBackupJob.JobName);
                keepCount = backupOptions.CurrentValue.KeepCount;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to load backups.");
                ViewNotification.UnexpectedError(notificationService, "讀取備份清單時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
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

        private async Task OnBackupNowAsync()
        {
            try
            {
                var ok = await ConfirmDialog.AskAsync(modalService, "立即備份",
                    "要立即建立一份系統備份嗎？備份在背景進行，完成後這一頁會自動更新。", "立即備份");
                if (!ok)
                {
                    return;
                }

                if (!overviewService.TryTrigger(SystemBackupJob.JobName, CurrentUserService.CurrentUser.Account, CurrentUserService.CurrentUser.Id))
                {
                    ViewNotification.Caution(notificationService, "備份已經在執行中，請等它完成。");
                    return;
                }

                await ViewAudit.WriteAsync(AuditLogService, CurrentUserService, AuditActions.Job.Trigger, "ScheduledJob", SystemBackupJob.JobName, $"job={SystemBackupJob.JobName}");
                logger.LogInformation("Backup triggered manually from the backup page.");
                ViewNotification.Warning(notificationService, "已開始備份。");
                await ReloadAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception while triggering a backup.");
                ViewNotification.UnexpectedError(notificationService, "啟動備份時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
        }

        private void OnDownload(BackupFileInfo item)
        {
            // 由控制器回傳檔案（支援續傳、從資料庫確認是管理員、寫稽核）；整頁導覽才不會經 SignalR 傳大檔。
            NavigationManager.NavigateTo($"/api/backups/{Uri.EscapeDataString(item.FileName)}/download", forceLoad: true);
        }

        private async Task OnDeleteAsync(BackupFileInfo item)
        {
            try
            {
                var ok = await ConfirmDialog.AskDestructiveAsync(modalService, "刪除備份",
                    $"要刪除「{item.FileName}」嗎？刪除後無法復原。", "刪除");
                if (!ok)
                {
                    return;
                }

                var result = await DeleteOneAsync(item);
                if (!result.Success)
                {
                    ViewNotification.Caution(notificationService, result.Message);
                    await ReloadAsync();
                    return;
                }

                ViewNotification.Warning(notificationService, $"已刪除「{item.FileName}」。");
                await ReloadAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception while deleting a backup.");
                ViewNotification.UnexpectedError(notificationService, "刪除備份時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
        }

        /// <summary>刪除一份備份並記稽核；單筆刪除與「刪除選取」共用。</summary>
        private async Task<VerifyRecordResult> DeleteOneAsync(BackupFileInfo item)
        {
            if (!store.Delete(item.FileName))
            {
                return VerifyRecordResultFactory.Build(false, "找不到這份備份，可能已被刪除。");
            }

            await ViewAudit.WriteAsync(AuditLogService, CurrentUserService, AuditActions.Backup.Delete, "Backup", item.FileName, $"file={item.FileName}");
            logger.LogInformation("Backup deleted from the backup page. FileName={FileName}", item.FileName);
            return VerifyRecordResultFactory.Build(true);
        }

        /// <summary>「刪除選取」（0.9.132 起）：逐筆走 <see cref="DeleteOneAsync"/>。</summary>
        private async Task OnBatchDeleteAsync()
        {
            try
            {
                var selected = selectedRows.ToList();
                if (selected.Count == 0 || !await BatchDelete.AskAsync(modalService, selected.Select(x => x.FileName).ToList(), restorable: false))
                {
                    return;
                }

                batchDeleting = true;
                var outcome = await BatchDelete.RunAsync(selected, x => x.FileName, x => x.FileName, DeleteOneAsync, logger);
                BatchDelete.Notify(notificationService, outcome);
                await ReloadAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception while batch deleting backups.");
                ViewNotification.UnexpectedError(notificationService, "刪除備份時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
            finally
            {
                batchDeleting = false;
            }
        }

        private void UpdateAutoRefresh()
        {
            if (IsBusy && autoRefreshTimer is null && !disposed)
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
            else if (!IsBusy && autoRefreshTimer is not null)
            {
                autoRefreshTimer.Dispose();
                autoRefreshTimer = null;
            }
        }

        private string ToLocalText(DateTime utc) => overviewService.ToLocal(utc).ToString("yyyy-MM-dd HH:mm:ss");

        private static string ContentText(BackupManifest manifest)
        {
            var folders = string.Join("、", manifest.Folders.Select(x => $"{FolderLabel(x.SettingKey)} {x.FileCount}"));
            var missing = manifest.MissingFileCount > 0 ? $"；{manifest.MissingFileCount} 個檔案打包時已被刪除" : string.Empty;
            return $"資料庫＋{folders}{missing}";
        }

        private static string FolderLabel(string settingKey) => settingKey switch
        {
            nameof(ExternalFileSystem.ProjectFilePath) => "附件",
            nameof(ExternalFileSystem.AfterSalesFilePath) => "售後照片",
            nameof(ExternalFileSystem.ExceptionPath) => "例外堆疊",
            nameof(ExternalFileSystem.TokenUsagePath) => "Token 原始檔",
            nameof(ExternalFileSystem.AuditContentPath) => "稽核內容",
            nameof(ExternalFileSystem.DataProtectionKeyPath) => "金鑰",
            nameof(ExternalFileSystem.AiCallLogPath) => "AI 對話內容",
            _ => settingKey,
        };

        private static string TriggerText(string? trigger) => trigger switch
        {
            JobRunTriggers.Schedule => "排程",
            JobRunTriggers.CatchUp => "補跑",
            JobRunTriggers.Manual => "手動",
            _ => "—",
        };

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

        public void Dispose()
        {
            disposed = true;
            autoRefreshTimer?.Dispose();
            autoRefreshTimer = null;
        }
    }
}

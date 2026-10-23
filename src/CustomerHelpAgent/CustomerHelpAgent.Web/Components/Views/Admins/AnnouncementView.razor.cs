using AntDesign;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.AdapterModel;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Web.Components.Commons;
using CustomerHelpAgent.Web.Components.Layout;

namespace CustomerHelpAgent.Web.Components.Views.Admins
{
    /// <summary>
    /// 公告管理（0.9.100 起）：新增、修改、刪除公告。管理員專屬：權限鍵刻意不進角色矩陣，以 CheckIsAdmin 判斷。
    /// 存檔或刪除後讓公告快取失效並通知所有開著的畫面重新顯示。
    /// </summary>
    public partial class AnnouncementView
    {
        private readonly ILogger<AnnouncementView> logger;
        private readonly AnnouncementService announcementService;
        private readonly AnnouncementCache announcementCache;
        private readonly INotificationSignal signal;
        private readonly ModalService modalService;
        private readonly NotificationService notificationService;

        private List<AnnouncementAdapterModel> items = [];
        private IEnumerable<AnnouncementAdapterModel> selectedRows = [];
        private bool batchDeleting;
        private Table<AnnouncementAdapterModel>? table;
        private List<(int Id, string Name)> roleOptions = [];
        private List<(int Id, string Name)> teamOptions = [];
        private bool isLoading;
        private string RoleMessage = string.Empty;
        private bool isAccessChecked;

        private bool modalVisible;
        private AnnouncementAdapterModel editing = new();
        private string openedSnapshot = string.Empty;
        private string errorMessage = string.Empty;

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

        public AnnouncementView(
            ILogger<AnnouncementView> logger,
            AnnouncementService announcementService,
            AnnouncementCache announcementCache,
            INotificationSignal signal,
            ModalService modalService,
            NotificationService notificationService)
        {
            this.logger = logger;
            this.announcementService = announcementService;
            this.announcementCache = announcementCache;
            this.signal = signal;
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
                await AuthenticationStateHelper.RecordPageAccessDeniedAsync("/announcements");
                logger.LogWarning("Announcement view denied because the current user is not an administrator.");
                return;
            }

            await ReloadAsync();
        }

        private async Task ReloadAsync()
        {
            // 重新整理與刪除後都會走到這裡：勾選只在目前這一頁有效（0.9.132 起）。
            selectedRows = [];
            isLoading = true;
            try
            {
                items = await announcementService.GetAllAsync();
                (roleOptions, teamOptions) = await announcementService.GetTargetOptionsAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to load announcements.");
                ViewNotification.UnexpectedError(notificationService, "讀取公告時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
            finally
            {
                isLoading = false;
            }
        }

        private async Task OnRefreshAsync()
        {
            await ReloadAsync();
            ViewNotification.Info(notificationService, "已更新最新資料");
        }

        private void OnAdd()
        {
            var now = TimeZoneInfo.ConvertTimeFromUtc(TimeProvider.GetUtcNow().UtcDateTime, TimeProvider.LocalTimeZone);
            Open(new AnnouncementAdapterModel
            {
                StartAt = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0),
                TargetKind = AnnouncementTargetKinds.All,
            });
        }

        private void OnEdit(AnnouncementAdapterModel item)
            => Open(new AnnouncementAdapterModel
            {
                Id = item.Id,
                Title = item.Title,
                Content = item.Content,
                StartAt = item.StartAt,
                EndAt = item.EndAt,
                TargetKind = item.TargetKind,
                TargetId = item.TargetId,
                ConcurrencyStamp = item.ConcurrencyStamp,
            });

        private void Open(AnnouncementAdapterModel model)
        {
            editing = model;
            openedSnapshot = Snapshot(model);
            errorMessage = string.Empty;
            modalVisible = true;
        }

        private async Task OnModalOkAsync()
        {
            // 第一行先把 Visible 搶回來（見 FormModalFlow）。
            modalVisible = true;
            modalVisible = await FormModalFlow.RunOkAsync(SaveAsync, logger, notificationService, "儲存公告時發生未預期的錯誤，請稍後再試或聯絡系統管理員。");
        }

        private async Task OnModalCancelAsync()
        {
            modalVisible = true;
            modalVisible = await FormModalFlow.ConfirmCloseAsync(modalService, Snapshot(editing) != openedSnapshot);
        }

        private async Task<bool> SaveAsync()
        {
            errorMessage = string.Empty;
            var isNew = editing.Id == 0;
            var result = isNew
                ? await announcementService.AddAsync(editing, CurrentUserService.CurrentUser.Account)
                : await announcementService.UpdateAsync(editing);
            if (!result.Success)
            {
                errorMessage = result.Message;
                return false;
            }

            await ViewAudit.WriteAsync(
                AuditLogService, CurrentUserService, isNew ? AuditActions.Announcement.Create : AuditActions.Announcement.Update,
                "Announcement", editing.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                $"title={editing.Title}; target={editing.TargetKind}:{editing.TargetId}");
            Broadcast();
            ViewNotification.Warning(notificationService, isNew ? "已新增公告。" : "已更新公告。");
            await ReloadAsync();
            return true;
        }

        private async Task OnDeleteAsync(AnnouncementAdapterModel item)
        {
            try
            {
                var ok = await ConfirmDialog.AskDestructiveAsync(modalService, "刪除公告", $"要刪除「{item.Title}」嗎？刪除後立即不再顯示，無法復原。", "刪除");
                if (!ok)
                {
                    return;
                }

                var result = await announcementService.DeleteAsync(item.Id, item.ConcurrencyStamp);
                if (!result.Success)
                {
                    ViewNotification.Caution(notificationService, result.Message);
                    await ReloadAsync();
                    return;
                }

                await ViewAudit.WriteAsync(AuditLogService, CurrentUserService, AuditActions.Announcement.Delete, "Announcement",
                    item.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), $"title={item.Title}");
                Broadcast();
                ViewNotification.Warning(notificationService, $"已刪除「{item.Title}」。");
                await ReloadAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception while deleting an announcement.");
                ViewNotification.UnexpectedError(notificationService, "刪除公告時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
        }

        /// <summary>
        /// 本頁在畫面上分頁（DataSource 不變），換頁時不會經過 <see cref="ReloadAsync"/>，要自己清掉勾選。
        /// </summary>
        private void OnPageIndexChange(PaginationEventArgs args) => table?.SetSelection(Array.Empty<AnnouncementAdapterModel>());

        /// <summary>
        /// 「刪除選取」（0.9.132 起）：逐筆同單筆刪除（帶各自的版本戳記、成功才記稽核）；有刪掉任何一筆才廣播一次。
        /// </summary>
        private async Task OnBatchDeleteAsync()
        {
            try
            {
                var selected = selectedRows.ToList();
                if (selected.Count == 0 || !await BatchDelete.AskAsync(modalService, selected.Select(x => x.Title).ToList(), restorable: false,
                        note: "刪除後立即不再顯示。"))
                {
                    return;
                }

                batchDeleting = true;
                var outcome = await BatchDelete.RunAsync(selected, x => x.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), x => x.Title, async item =>
                {
                    var result = await announcementService.DeleteAsync(item.Id, item.ConcurrencyStamp);
                    if (result.Success)
                    {
                        await ViewAudit.WriteAsync(AuditLogService, CurrentUserService, AuditActions.Announcement.Delete, "Announcement",
                            item.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), $"title={item.Title}");
                    }

                    return result;
                }, logger);
                if (outcome.Succeeded > 0)
                {
                    Broadcast();
                }

                BatchDelete.Notify(notificationService, outcome);
                await ReloadAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception while batch deleting announcements.");
                ViewNotification.UnexpectedError(notificationService, "刪除公告時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
            finally
            {
                batchDeleting = false;
            }
        }

        /// <summary>公告快取失效，並通知所有開著的畫面重新顯示（同一個行程；其他行程最晚 60 秒）。</summary>
        private void Broadcast()
        {
            announcementCache.Invalidate();
            signal.PublishAnnouncementsChanged();
        }

        private string StatusText(AnnouncementAdapterModel item) => StateOf(item) switch
        {
            0 => "未開始",
            1 => "進行中",
            _ => "已結束",
        };

        private static StatusTone ToneOf(int state) => state switch
        {
            0 => StatusTone.Neutral,
            1 => StatusTone.Positive,
            _ => StatusTone.Muted,
        };

        private int StateOf(AnnouncementAdapterModel item)
        {
            var now = TimeZoneInfo.ConvertTimeFromUtc(TimeProvider.GetUtcNow().UtcDateTime, TimeProvider.LocalTimeZone);
            return now < item.StartAt ? 0 : item.EndAt is { } end && now >= end ? 2 : 1;
        }

        private static string Snapshot(AnnouncementAdapterModel x)
            => string.Join('|', x.Title, x.Content, x.StartAt.ToString("O"), x.EndAt?.ToString("O"), x.TargetKind, x.TargetId);
    }
}

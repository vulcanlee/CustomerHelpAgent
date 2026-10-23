using AntDesign;
using AntDesign.TableModels;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.AdapterModel;
using CustomerHelpAgent.Models.Admins;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Web.Components.Commons;

namespace CustomerHelpAgent.Web.Components.Views.Admins
{
    public partial class RoleViewView
    {
        private readonly ILogger<RoleViewView> logger;
        private readonly RoleViewService roleViewService;
        private readonly ModalService modalService;
        private readonly MessageService messageService;
        private readonly NotificationService notificationService;
        private readonly RolePermissionService rolePermissionService;
        private readonly TeamService teamService;
        List<string> availableTeams = new();
        ITable? table;
        int _pageIndex = 1;
        int _pageSize = MagicObjectHelper.PageSize;
        int _total = 0;
        string searchText = string.Empty;
        string sortField = string.Empty;
        string sortDirection = "None";

        List<RoleViewAdapterModel> roleViewAdapterModels = new();
        IEnumerable<RoleViewAdapterModel> selectedRows = [];
        bool batchDeleting;

        string modalTitle = "角色維護";
        bool modalVisible = false;
        RoleViewAdapterModel CurrentRecord = new();
        public EditContext? LocalEditContext { get; set; }
        bool isNewRecordMode;

        /// <summary>
        /// 未儲存變更偵測。開窗時 Capture、按取消／儲存時比對，
        /// 「改了又改回原值」視為無變更，不會白問使用者一次。
        /// </summary>
        private readonly FormDirtyTracker dirtyTracker = new();
        string RoleMessage = string.Empty;
        bool isAccessChecked;

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

        public RoleViewView(
            ILogger<RoleViewView> logger,
            RoleViewService roleViewService,
            ModalService modalService,
            MessageService messageService,
            NotificationService notificationService,
            RolePermissionService rolePermissionService,
            TeamService teamService)
        {
            this.logger = logger;
            this.roleViewService = roleViewService;
            this.modalService = modalService;
            this.messageService = messageService;
            this.notificationService = notificationService;
            this.rolePermissionService = rolePermissionService;
            this.teamService = teamService;
        }

        /// <summary>「資料保留與清理」頁的「前往」帶 <c>?deleted=1</c> 進來時為 true：一進頁面就顯示已刪除的資料（0.9.133 起）。</summary>
        [Parameter]
        public bool InitialShowDeleted { get; set; }

        protected override async Task OnInitializedAsync()
        {
            logger.LogDebug("Initializing role view management.");
            var checkResult = await AuthenticationStateHelper.Check(authStateProvider, NavigationManager);
            if (checkResult != AuthenticationCheckResult.Succeeded)
            {
                logger.LogWarning("Role view initialization stopped because authentication check failed.");
                return;
            }

            isAccessChecked = true;

            if (!AuthenticationStateHelper.CheckIsAdmin())
            {
                RoleMessage = "你沒有權限存取此頁面";
                await AuthenticationStateHelper.RecordPageAccessDeniedAsync("/roleviews");
                logger.LogWarning("Role view denied because current user is not an administrator.");
                return;
            }

            availableTeams = await teamService.GetAllEnabledNamesAsync();

            showDeleted = InitialShowDeleted;
            await ReloadAsync();
        }

        public async Task ReloadAsync()
        {
            // 換頁、排序、搜尋、切換與刪除後都會走到這裡：勾選只在目前這一頁有效（0.9.132 起）。
            selectedRows = [];
            logger.LogDebug(
                "Reloading role views. HasSearch={HasSearch}, SearchLength={SearchLength}, SortField={SortField}, SortDirection={SortDirection}, PageIndex={PageIndex}, PageSize={PageSize}",
                string.IsNullOrWhiteSpace(searchText) == false,
                searchText.Length,
                sortField,
                sortDirection,
                _pageIndex,
                _pageSize);

            var dataRequest = new DataRequest
            {
                Search = searchText,
                SortField = sortField,
                SortDescending = sortDirection == "Descending" ? true : sortDirection == "Ascending" ? false : (bool?)null,
                CurrentPage = _pageIndex,
                PageSize = _pageSize,
                Take = 0,
            };

            // 「顯示已刪除」開啟時改讀已刪除的資料（0.9.95 起）。
            DataRequestResult<RoleViewAdapterModel> dataRequestResult = showDeleted
                ? await roleViewService.GetDeletedAsync(dataRequest)
                : await roleViewService.GetAsync(dataRequest);

            roleViewAdapterModels = dataRequestResult.Result.ToList();
            _total = dataRequestResult.Count;
            logger.LogDebug("Role view list reloaded successfully. Count={Count}", _total);
            StateHasChanged();
        }

        async Task OnTableChange(QueryModel<RoleViewAdapterModel> args)
        {
            _pageIndex = args.PageIndex;

            if (args.SortModel?.Any() == true)
            {
                var tableSortModel = TableSortHelper.GetCurrentSortModel(args.SortModel);
                string sortValue = tableSortModel.SortDirection.ToString() ?? string.Empty;
                string resolvedSortField = TableSortHelper.ResolveSortFieldName(tableSortModel);
                sortDirection = sortValue;
                sortField = resolvedSortField;
            }
            else
            {
                sortField = string.Empty;
                sortDirection = "None";
            }

            logger.LogDebug("Role view table changed. PageIndex={PageIndex}, SortField={SortField}, SortDirection={SortDirection}", _pageIndex, sortField, sortDirection);
            await ReloadAsync();
        }


        async Task OnSearchAsync()
        {
            _pageIndex = 1;
            logger.LogInformation("Role view search triggered. HasSearch={HasSearch}, SearchLength={SearchLength}", string.IsNullOrWhiteSpace(searchText) == false, searchText.Length);
            await ReloadAsync();
            await ViewAudit.WriteSearchAsync(AuditLogService, CurrentUserService, AuditActions.Role.Search, "RoleView",
                AuditSearchCondition.Text("keyword", searchText), AuditSearchCondition.Of("showDeleted", showDeleted));
        }

        async Task OnRefreshAsync()
        {
            logger.LogInformation("Role view refresh triggered.");
            await ReloadAsync();

            ViewNotification.Warning(notificationService, "已更新最新資料");
        }

        async Task OnEditAsync(RoleViewAdapterModel roleViewAdapterModel)
        {
            isNewRecordMode = false;
            modalTitle = "修改角色";
            CurrentRecord = roleViewAdapterModel.Clone();
            // ⚠️ 必須是開窗前的最後一步：任何預設值都要先塞完，否則會被當成使用者的變更。
            dirtyTracker.Capture(CurrentRecord);

            modalVisible = true;
            logger.LogInformation("Opened edit modal for role view. RoleViewId={RoleViewId}, Name={RoleName}", roleViewAdapterModel.Id, roleViewAdapterModel.Name);
            await ViewAudit.WriteAsync(AuditLogService, CurrentUserService, AuditActions.Role.View, "RoleView",
                roleViewAdapterModel.Id.ToString(), $"name={roleViewAdapterModel.Name}");
        }

        /// <summary>
        /// 包住實際邏輯以捕捉未預期的例外：先前這些寫入操作完全沒有 try/catch，
        /// 例外會直接拆掉 Blazor circuit，使用者只看到畫面斷線、日誌上也留不下任何痕跡。
        /// </summary>
        async Task OnDeleteAsync(RoleViewAdapterModel roleViewAdapterModel)
        {
            try
            {
                await OnDeleteCoreAsync(roleViewAdapterModel);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception while deleting role.");
                ViewNotification.UnexpectedError(notificationService, "刪除角色時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
        }

        async Task OnDeleteCoreAsync(RoleViewAdapterModel roleViewAdapterModel)
        {
            logger.LogInformation("Delete role view requested. RoleViewId={RoleViewId}, Name={RoleName}", roleViewAdapterModel.Id, roleViewAdapterModel.Name);

            var ok = await ConfirmDialog.AskSoftDeleteRecordAsync(modalService);

            if (!ok)
            {
                logger.LogDebug("Role view delete cancelled by user. RoleViewId={RoleViewId}", roleViewAdapterModel.Id);
                return;
            }

            var result = await roleViewService.DeleteAsync(roleViewAdapterModel.Id);
            if (!result.Success)
            {
                // 0.9.93 之前這裡不看結果，失敗也顯示「刪除成功」（例如角色仍是某些使用者的主要角色時根本刪不掉）。
                logger.LogInformation("Role view delete rejected. RoleViewId={RoleViewId}, Message={Message}", roleViewAdapterModel.Id, result.Message);
                ViewNotification.Error(notificationService, result.Message);
                return;
            }

            logger.LogInformation("Role view delete completed. RoleViewId={RoleViewId}", roleViewAdapterModel.Id);

            ViewNotification.Warning(notificationService, "刪除成功");

            await ReloadAsync();
        }

        /// <summary>「刪除選取」（0.9.132 起）：逐筆走 <see cref="RoleViewService.DeleteAsync"/>，規則與稽核同單筆刪除。</summary>
        async Task OnBatchDeleteAsync()
        {
            try
            {
                var items = selectedRows.ToList();
                if (items.Count == 0 || !await BatchDelete.AskAsync(modalService, items.Select(x => x.Name).ToList(), restorable: true))
                {
                    return;
                }

                batchDeleting = true;
                var outcome = await BatchDelete.RunAsync(items, x => x.Id.ToString(), x => x.Name,
                    x => roleViewService.DeleteAsync(x.Id), logger);
                BatchDelete.Notify(notificationService, outcome);
                await ReloadAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception while batch deleting roles.");
                ViewNotification.UnexpectedError(notificationService, "刪除角色時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
            finally
            {
                batchDeleting = false;
            }
        }

        bool showDeleted;

        async Task OnToggleDeletedAsync()
        {
            showDeleted = !showDeleted;
            _pageIndex = 1;
            await ReloadAsync();
        }

        async Task OnRestoreAsync(RoleViewAdapterModel record)
        {
            try
            {
                var ok = await ConfirmDialog.AskAsync(modalService, "確認還原", $"要還原「{record.Name}」嗎？", "還原");
                if (!ok)
                {
                    return;
                }

                var result = await roleViewService.RestoreAsync(record.Id);
                if (!result.Success)
                {
                    logger.LogInformation("Role view restore rejected. RoleViewId={RoleViewId}, Message={Message}", record.Id, result.Message);
                    ViewNotification.Error(notificationService, result.Message);
                    return;
                }

                logger.LogInformation("Role view restore completed. RoleViewId={RoleViewId}", record.Id);
                ViewNotification.Warning(notificationService, "還原成功");
                await ReloadAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception while restoring role view.");
                ViewNotification.UnexpectedError(notificationService, "還原角色時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
        }

        async Task OnPurgeAsync(RoleViewAdapterModel record)
        {
            try
            {
                var ok = await ConfirmDialog.AskDestructiveAsync(
                    modalService,
                    "永久刪除",
                    $"永久刪除「{record.Name}」後無法復原，它的權限設定與「額外角色」關聯也會一併刪除。確定要永久刪除嗎？",
                    "永久刪除");
                if (!ok)
                {
                    return;
                }

                var result = await roleViewService.PurgeAsync(record.Id);
                if (!result.Success)
                {
                    logger.LogInformation("Role view purge rejected. RoleViewId={RoleViewId}, Message={Message}", record.Id, result.Message);
                    ViewNotification.Error(notificationService, result.Message);
                    return;
                }

                logger.LogInformation("Role view purge completed. RoleViewId={RoleViewId}", record.Id);
                ViewNotification.Warning(notificationService, "已永久刪除");
                await ReloadAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception while purging role view.");
                ViewNotification.UnexpectedError(notificationService, "永久刪除角色時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
        }

        async Task OnAddAsync(bool continueOnCapturedContext)
        {
            CurrentRecord = new();
            CurrentRecord.RolePermission = rolePermissionService.InitializePermissionSetting();

            isNewRecordMode = true;
            modalTitle = "新增角色";
            // ⚠️ 必須是開窗前的最後一步：任何預設值都要先塞完，否則會被當成使用者的變更。
            dirtyTracker.Capture(CurrentRecord);

            modalVisible = true;
            logger.LogInformation("Opened create modal for role view.");
        }

        /// <summary>
        /// 包住實際邏輯以捕捉未預期的例外：先前這些寫入操作完全沒有 try/catch，
        /// 例外會直接拆掉 Blazor circuit，使用者只看到畫面斷線、日誌上也留不下任何痕跡。
        /// </summary>
        /// <summary>
        /// 「儲存」按鈕。
        ///
        /// ⚠️ 第一行的 modalVisible = true 不可移動，也不可在它之前 await：
        /// AntDesign 在呼叫本方法**之前**就已送出 VisibleChanged(false)，這一行是在同一個
        /// render batch 內把它搶回來（那個 false 從來不會被畫出來，所以不會閃爍）。
        /// 之後的開關一律由 FormModalFlow 的回傳值決定 —— 失敗路徑只要 return false。
        /// ⚠️ args 可能是 null，不要解參考它。
        /// </summary>
        private async Task OnModalOKHandleAsync(MouseEventArgs args)
        {
            modalVisible = true;
            modalVisible = await FormModalFlow.RunOkAsync(
                SaveAsync,
                logger,
                notificationService,
                "儲存角色時發生未預期的錯誤，請稍後再試或聯絡系統管理員。");
        }

        /// <summary>
        /// 實際存檔。回傳 true 表示已完成、可以關窗；
        /// 任何驗證失敗或使用者中止都 return false，不必再碰 modalVisible。
        /// </summary>
        private async Task<bool> SaveAsync()
        {
            if (LocalEditContext?.Validate() == false)
            {
                IEnumerable<string> allErrors = LocalEditContext.GetValidationMessages();
                foreach (var error in allErrors)
                {
                    logger.LogInformation("Role view form validation failed. Error={Error}", error);
                    ViewNotification.ValidationError(notificationService, error);
                }

                return false;
            }

            // 一個字都沒改就按儲存：不值得白寫一筆，也不該白跳一次確認窗。
            if (isNewRecordMode == false && dirtyTracker.IsDirty(CurrentRecord) == false)
            {
                logger.LogDebug("Role save skipped because nothing changed. RoleViewId={RoleViewId}", CurrentRecord.Id);
                ViewNotification.Info(notificationService, "沒有任何變更，未進行儲存。");

                return true;
            }

            // 儲存前的二次確認。排在資料庫前置檢查之前：使用者若選「再檢查」，
            // 就不必白跑一次資料庫來回。
            if (await FormEditConfirm.AskSaveAsync(modalService) == false)
            {
                logger.LogDebug("Role save cancelled at save confirmation. RoleViewId={RoleViewId}", CurrentRecord.Id);
                return false;
            }

            if (isNewRecordMode)
            {
                var beforeAddCheckResult = await roleViewService.BeforeAddCheckAsync(CurrentRecord);
                if (!beforeAddCheckResult.Success)
                {
                    logger.LogInformation("Role view create pre-check failed. Name={RoleName}, Message={Message}", CurrentRecord.Name, beforeAddCheckResult.Message);
                    ViewNotification.Error(notificationService, beforeAddCheckResult.Message);

                    return false;
                }

                CurrentRecord.CreateAt = DateTime.Now;
                CurrentRecord.UpdateAt = DateTime.Now;

                var actionResult = await roleViewService.AddAsync(CurrentRecord);
                if (!actionResult.Success)
                {
                    // 0.9.92 之前這裡不看結果，失敗也顯示「新增成功」。
                    logger.LogInformation("Role view create rejected. Name={RoleName}, Message={Message}", CurrentRecord.Name, actionResult.Message);
                    ViewNotification.Error(notificationService, actionResult.Message);
                    return false;
                }

                logger.LogInformation("Role view create submitted. Name={RoleName}", CurrentRecord.Name);

                ViewNotification.Warning(notificationService, "新增成功");

                _ = messageService.SuccessAsync("新增成功");
            }
            else
            {
                var beforeUpdateCheckResult = await roleViewService.BeforeUpdateCheckAsync(CurrentRecord);
                if (!beforeUpdateCheckResult.Success)
                {
                    logger.LogInformation("Role view update pre-check failed. RoleViewId={RoleViewId}, Message={Message}", CurrentRecord.Id, beforeUpdateCheckResult.Message);
                    ViewNotification.Error(notificationService, beforeUpdateCheckResult.Message);

                    return false;
                }

                CurrentRecord.UpdateAt = DateTime.Now;
                var actionResult = await roleViewService.UpdateAsync(CurrentRecord);
                if (!actionResult.Success)
                {
                    // 0.9.92 之前這裡不看結果，失敗也顯示「修改成功」；0.9.93 起的並行衝突訊息也走這裡，表單維持開啟。
                    logger.LogInformation("Role view update rejected. RoleViewId={RoleViewId}, Message={Message}", CurrentRecord.Id, actionResult.Message);
                    ViewNotification.Error(notificationService, actionResult.Message);
                    return false;
                }

                logger.LogInformation("Role view update submitted. RoleViewId={RoleViewId}, Name={RoleName}", CurrentRecord.Id, CurrentRecord.Name);

                ViewNotification.Warning(notificationService, "修改成功");
            }

            await ReloadAsync();
            dirtyTracker.Clear();

            return true;
        }

        /// <summary>
        /// 取消／✕／ESC 的共用出口（遮罩已由 MaskClosable="false" 擋掉，不會走到這裡）。
        /// 有未儲存變更時先問過使用者；無變更直接關閉，不打擾。
        ///
        /// ⚠️ 第一行的 modalVisible = true 不可移動：AntDesign 呼叫本方法前已送出
        /// VisibleChanged(false)，要讓「繼續編輯」留住整窗輸入就得在這裡搶回來。
        /// ⚠️ args 可能是 null（ESC／✕ 走 Config.OnCancel.Invoke(null)），不要解參考它。
        /// </summary>
        private async Task OnModalCancelHandleAsync(MouseEventArgs args)
        {
            modalVisible = true;
            modalVisible = await FormModalFlow.ConfirmCloseAsync(modalService, dirtyTracker.IsDirty(CurrentRecord));

            if (modalVisible)
            {
                return;
            }

            dirtyTracker.Clear();
            logger.LogDebug("Role view modal cancelled.");
        }

        public void OnEditContestChanged(EditContext context)
        {
            LocalEditContext = context;
        }

        private void OnRoleTeamsChanged(IEnumerable<string> values)
        {
            CurrentRecord.DefaultTeams = values?.ToList() ?? [];
        }

        private void OnPermissionGroupChanged(RolePermissionGroup group, bool value)
        {
            group.Enable = value;

            if (value)
            {
                return;
            }

            foreach (var permission in group.Permissions)
            {
                permission.Enable = false;
            }
        }

        private void OnPermissionItemChanged(RolePermissionGroup group, RolePermissionNode role, bool value)
        {
            role.Enable = value;

            if (value)
            {
                group.Enable = true;
            }
        }

        /// <summary>權限矩陣的動作欄（代碼 → 顯示名）。</summary>
        private static readonly (string Key, string Label)[] PermissionActionItems =
        [
            (PermissionActions.View, "檢視"),
            (PermissionActions.Create, "新增"),
            (PermissionActions.Edit, "編輯"),
            (PermissionActions.Delete, "刪除"),
        ];

        private static bool GetActionChecked(RolePermissionNode node, string action)
            => node.Actions is not null && node.Actions.TryGetValue(action, out var enabled) && enabled;

        private void OnPermissionActionChanged(RolePermissionGroup group, RolePermissionNode node, string action, bool value)
        {
            node.Actions ??= new Dictionary<string, bool>();
            node.Actions[action] = value;

            if (value)
            {
                group.Enable = true;
            }
        }
    }
}

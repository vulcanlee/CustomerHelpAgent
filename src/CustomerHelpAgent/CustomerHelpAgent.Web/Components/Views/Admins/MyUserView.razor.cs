using AntDesign;
using AntDesign.TableModels;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.AdapterModel;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Web.Auth;
using CustomerHelpAgent.Web.Components.Commons;
using CustomerHelpAgent.Web.Export;

namespace CustomerHelpAgent.Web.Components.Views.Admins
{
    public partial class MyUserView
    {

        [Inject]
        public Microsoft.JSInterop.IJSRuntime JSRuntime { get; set; } = default!;

        [Inject]
        public IAuditLogService AuditLogService { get; set; } = default!;

        /// <summary>匯出 Excel（0.9.107 起）：與清單同一個查詢，超過 <see cref="BusinessExports.MaxRows"/> 筆不匯出。</summary>
        private async Task OnExportExcelAsync()
        {
            try
            {
                var outcome = await BusinessExports.BuildAsync(r => showDeleted ? myUserService.GetDeletedAsync(r) : myUserService.GetAsync(r), new DataRequest
                {
                    Search = searchText,
                    SortField = sortField,
                    SortDescending = sortDirection == "Descending" ? true : sortDirection == "Ascending" ? false : (bool?)null,
                }, "使用者", "users", BusinessExports.UserColumns);
                if (outcome.Error is { } error)
                {
                    ViewNotification.Caution(notificationService, error);
                    return;
                }

                await JSRuntime.DownloadAsync(outcome.FileName!, outcome.Content!, TabularExport.XlsxContentType);
                logger.LogInformation("User export downloaded. Rows={Rows}", outcome.Rows);
                await BusinessExports.WriteAuditAsync(AuditLogService, CurrentUserService, AuditActions.User.Export, "MyUser", outcome.Rows, showDeleted);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "User export failed.");
                ViewNotification.UnexpectedError(notificationService, $"匯出失敗：{ex.GetType().Name}。", ex);
            }
        }

        private readonly ILogger<MyUserView> logger;
        private readonly MyUserService myUserService;
        private readonly RoleViewService roleViewService;
        private readonly ModalService modalService;
        private readonly MessageService messageService;
        private readonly NotificationService notificationService;
        private readonly TeamService teamService;
        List<string> availableTeams = new();
        ITable? table;
        int _pageIndex = 1;
        int _pageSize = MagicObjectHelper.PageSize;
        int _total = 0;
        string searchText = string.Empty;
        string sortField = string.Empty;
        string sortDirection = "None";

        List<MyUserAdapterModel> myUserAdapterModels = new();
        IEnumerable<MyUserAdapterModel> selectedRows = [];
        bool batchDeleting;
        List<RoleViewAdapterModel> roleViewAdapterModels = new();

        string modalTitle = "使用者維護";
        bool modalVisible = false;
        MyUserAdapterModel CurrentRecord = new();
        public EditContext? LocalEditContext { get; set; }
        bool isNewRecordMode;
        string RoleMessage = string.Empty;
        bool isAccessChecked;

        /// <summary>
        /// 未儲存變更偵測。開窗時 Capture、按取消／儲存時比對，
        /// 「改了又改回原值」視為無變更，不會白問使用者一次。
        /// </summary>
        private readonly FormDirtyTracker dirtyTracker = new();

        [Inject]
        public AuthenticationStateHelper AuthenticationStateHelper { get; set; } = default!;
        [Inject]
        public AuthenticationStateProvider authStateProvider { get; set; } = default!;
        [Inject]
        public NavigationManager NavigationManager { get; set; } = default!;
        [Inject]
        public IPasswordPolicy PasswordPolicy { get; set; } = default!;
        [Inject]
        public CurrentUserService CurrentUserService { get; set; } = default!;
        [Inject]
        public SessionRefreshNavigator SessionRefreshNavigator { get; set; } = default!;
        [Inject]
        public ITwoFactorService TwoFactorService { get; set; } = default!;

        public MyUserView(
            ILogger<MyUserView> logger,
            MyUserService myUserService,
            RoleViewService roleViewService,
            ModalService modalService,
            MessageService messageService,
            NotificationService notificationService,
            TeamService teamService)
        {
            this.logger = logger;
            this.myUserService = myUserService;
            this.roleViewService = roleViewService;
            this.modalService = modalService;
            this.messageService = messageService;
            this.notificationService = notificationService;
            this.teamService = teamService;
        }

        /// <summary>「資料保留與清理」頁的「前往」帶 <c>?deleted=1</c> 進來時為 true：一進頁面就顯示已刪除的資料（0.9.133 起）。</summary>
        [Parameter]
        public bool InitialShowDeleted { get; set; }

        protected override async Task OnInitializedAsync()
        {
            logger.LogDebug("Initializing user management view.");
            var checkResult = await AuthenticationStateHelper.Check(authStateProvider, NavigationManager);
            if (checkResult != AuthenticationCheckResult.Succeeded)
            {
                logger.LogWarning("User management view initialization stopped because authentication check failed.");
                return;
            }

            isAccessChecked = true;

            if (!AuthenticationStateHelper.CheckIsAdmin())
            {
                RoleMessage = "你沒有權限存取此頁面";
                await AuthenticationStateHelper.RecordPageAccessDeniedAsync("/myusers");
                logger.LogWarning("User management view denied because current user is not an administrator.");
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
                "Reloading users. HasSearch={HasSearch}, SearchLength={SearchLength}, SortField={SortField}, SortDirection={SortDirection}, PageIndex={PageIndex}, PageSize={PageSize}",
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
            DataRequestResult<MyUserAdapterModel> dataRequestResult = showDeleted
                ? await myUserService.GetDeletedAsync(dataRequest)
                : await myUserService.GetAsync(dataRequest);

            myUserAdapterModels = dataRequestResult.Result.ToList();
            _total = dataRequestResult.Count;
            logger.LogDebug("User list reloaded successfully. Count={Count}", _total);
            StateHasChanged();
        }

        async Task OnTableChange(QueryModel<MyUserAdapterModel> args)
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

            logger.LogDebug("User table changed. PageIndex={PageIndex}, SortField={SortField}, SortDirection={SortDirection}", _pageIndex, sortField, sortDirection);
            await ReloadAsync();
        }


        async Task OnSearchAsync()
        {
            _pageIndex = 1;
            logger.LogInformation("User search triggered. HasSearch={HasSearch}, SearchLength={SearchLength}", string.IsNullOrWhiteSpace(searchText) == false, searchText.Length);
            await ReloadAsync();
            await ViewAudit.WriteSearchAsync(AuditLogService, CurrentUserService, AuditActions.User.Search, "MyUser",
                AuditSearchCondition.Text("keyword", searchText), AuditSearchCondition.Of("showDeleted", showDeleted));
        }

        async Task OnRefreshAsync()
        {
            logger.LogInformation("User refresh triggered.");
            await ReloadAsync();

            ViewNotification.Warning(notificationService, "已更新最新資料");
        }

        async Task OnEditAsync(MyUserAdapterModel myUserAdapterModel)
        {
            await LoadRoleViewsAsync();

            isNewRecordMode = false;
            modalTitle = "修改使用者";
            CurrentRecord = (await myUserService.GetAsync(myUserAdapterModel.Id)).Clone();
            var (additionalRoleIds, teamNames) = await myUserService.GetUserAssignmentsAsync(myUserAdapterModel.Id);
            CurrentRecord.AdditionalRoleIds = additionalRoleIds;
            CurrentRecord.TeamNames = teamNames;

            // ⚠️ 必須是開窗前的最後一步：任何預設值與關聯資料都要先塞完，
            //    否則那些值會被當成「使用者的變更」。
            dirtyTracker.Capture(CurrentRecord);

            modalVisible = true;
            logger.LogInformation("Opened edit modal for user. UserId={UserId}, Account={Account}", myUserAdapterModel.Id, myUserAdapterModel.Account);
            await ViewAudit.WriteAsync(AuditLogService, CurrentUserService, AuditActions.User.View, "MyUser",
                myUserAdapterModel.Id.ToString(), $"account={myUserAdapterModel.Account}");
        }

        /// <summary>
        /// 包住實際邏輯以捕捉未預期的例外：先前這些寫入操作完全沒有 try/catch，
        /// 例外會直接拆掉 Blazor circuit，使用者只看到畫面斷線、日誌上也留不下任何痕跡。
        /// </summary>
        async Task OnDeleteAsync(MyUserAdapterModel myUserAdapterModel)
        {
            try
            {
                await OnDeleteCoreAsync(myUserAdapterModel);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception while deleting user.");
                ViewNotification.UnexpectedError(notificationService, "刪除使用者時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
        }

        async Task OnDeleteCoreAsync(MyUserAdapterModel myUserAdapterModel)
        {
            logger.LogInformation("Delete user requested. UserId={UserId}, Account={Account}", myUserAdapterModel.Id, myUserAdapterModel.Account);

            var ok = await ConfirmDialog.AskSoftDeleteRecordAsync(modalService);

            if (!ok)
            {
                logger.LogDebug("User delete cancelled by user. UserId={UserId}", myUserAdapterModel.Id);
                return;
            }

            var result = await myUserService.DeleteAsync(myUserAdapterModel.Id);
            if (!result.Success)
            {
                // 0.9.93 之前這裡不看結果，失敗也顯示「刪除成功」。
                logger.LogInformation("User delete rejected. UserId={UserId}, Message={Message}", myUserAdapterModel.Id, result.Message);
                ViewNotification.Error(notificationService, result.Message);
                return;
            }

            logger.LogInformation("User delete completed. UserId={UserId}", myUserAdapterModel.Id);

            ViewNotification.Warning(notificationService, "刪除成功");

            await ReloadAsync();
        }

        /// <summary>管理員替使用者輸入新密碼時，自動勾選「下次登入須變更密碼」（仍可取消勾選）。</summary>
        void OnPasswordTyped()
        {
            if (!string.IsNullOrEmpty(CurrentRecord.Password))
            {
                CurrentRecord.MustChangePassword = true;
            }
        }

        static bool IsLocked(MyUserAdapterModel record) => record.LockoutEndUtc is { } end && end > DateTime.UtcNow;

        static string LockedText(MyUserAdapterModel record)
        {
            var local = DateTime.SpecifyKind(record.LockoutEndUtc!.Value, DateTimeKind.Utc).ToLocalTime();
            return local.Date == DateTime.Today ? $"鎖定至 {local:HH:mm}" : $"鎖定至 {local:MM-dd HH:mm}";
        }

        /// <summary>
        /// 點狀態標籤切換啟用／停用（0.9.120 起）：停用先確認，啟用直接切換。
        /// ⚠️ 走 <see cref="MyUserService.SetStatusAsync"/>，不可用清單列呼叫 UpdateAsync（會清掉這個人的額外角色與團隊）。
        /// </summary>
        async Task OnToggleStatusAsync(MyUserAdapterModel record)
        {
            try
            {
                var enable = !record.Status;
                if (!enable && !await ConfirmDialog.AskAsync(modalService, "停用使用者",
                        $"要停用「{record.Account}」嗎？他所有已登入的瀏覽器與 API 會被登出，之後也不能再登入，直到重新啟用。", "停用"))
                {
                    return;
                }

                var result = await myUserService.SetStatusAsync(record.Id, enable);
                if (!result.Success)
                {
                    logger.LogInformation("User status change rejected. UserId={UserId}, Message={Message}", record.Id, result.Message);
                    ViewNotification.Error(notificationService, result.Message);
                }
                else
                {
                    ViewNotification.Warning(notificationService, $"已{(enable ? "啟用" : "停用")}「{record.Account}」");
                }

                await ReloadAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception while changing user status.");
                ViewNotification.UnexpectedError(notificationService, "切換使用者狀態時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
        }

        /// <summary>自己那一列不顯示「強制登出」（要登出自己請用右上角的登出）。</summary>
        bool CanForceLogout(MyUserAdapterModel record) => record.Id != CurrentUserService.CurrentUser.Id;

        async Task OnForceLogoutAsync(MyUserAdapterModel record)
        {
            try
            {
                var ok = await ConfirmDialog.AskAsync(modalService, "強制登出", $"要讓「{record.Account}」所有已登入的瀏覽器與 API 都登出嗎？他需要重新登入。", "強制登出");
                if (!ok)
                {
                    return;
                }

                var result = await myUserService.ForceLogoutAsync(record.Id);
                if (!result.Success)
                {
                    ViewNotification.Error(notificationService, result.Message);
                    return;
                }

                logger.LogInformation("User force logout requested from list. UserId={UserId}", record.Id);
                ViewNotification.Warning(notificationService, $"已強制登出「{record.Account}」");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception while forcing a user to log out.");
                ViewNotification.UnexpectedError(notificationService, "強制登出時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
        }

        /// <summary>只在對方已啟用時顯示；自己那一列不顯示（自己到「兩步驟驗證」頁停用或重新設定）。</summary>
        bool CanResetTwoFactor(MyUserAdapterModel record) => record.TwoFactorEnabled && record.Id != CurrentUserService.CurrentUser.Id;

        async Task OnResetTwoFactorAsync(MyUserAdapterModel record)
        {
            try
            {
                var ok = await ConfirmDialog.AskAsync(modalService, "重設兩步驟驗證",
                    $"要清除「{record.Account}」的驗證器設定與備用碼嗎？對方所有已登入的地方都會登出，下次登入只需要密碼（若角色要求使用，會被帶去重新設定）。", "重設");
                if (!ok)
                {
                    return;
                }

                var result = await TwoFactorService.ResetAsync(record.Id);
                if (!result.Success)
                {
                    ViewNotification.Error(notificationService, result.Message);
                    return;
                }

                ViewNotification.Warning(notificationService, $"已重設「{record.Account}」的兩步驟驗證");
                await ReloadAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception while resetting two-factor authentication.");
                ViewNotification.UnexpectedError(notificationService, "重設兩步驟驗證時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
        }

        async Task OnUnlockAsync(MyUserAdapterModel record)
        {
            try
            {
                var ok = await ConfirmDialog.AskAsync(modalService, "解除鎖定", $"要解除「{record.Account}」的登入鎖定嗎？他可以立刻再登入。", "解鎖");
                if (!ok)
                {
                    return;
                }

                var result = await myUserService.UnlockAsync(record.Id);
                if (!result.Success)
                {
                    ViewNotification.Error(notificationService, result.Message);
                }
                else
                {
                    logger.LogInformation("User unlocked from list. UserId={UserId}", record.Id);
                    ViewNotification.Warning(notificationService, $"已解除「{record.Account}」的鎖定");
                }

                await ReloadAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception while unlocking user.");
                ViewNotification.UnexpectedError(notificationService, "解除鎖定時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
        }

        /// <summary>
        /// 「刪除選取」（0.9.132 起）：逐筆走 <see cref="MyUserService.DeleteAsync"/>，規則與稽核同單筆刪除。
        /// 清單與結果通知只列帳號：通知內文會寫進日誌，日誌不記姓名。
        /// </summary>
        async Task OnBatchDeleteAsync()
        {
            try
            {
                var items = selectedRows.ToList();
                if (items.Count == 0 || !await BatchDelete.AskAsync(modalService, items.Select(x => x.Account).ToList(), restorable: true))
                {
                    return;
                }

                batchDeleting = true;
                var outcome = await BatchDelete.RunAsync(items, x => x.Id.ToString(), x => x.Account,
                    x => myUserService.DeleteAsync(x.Id), logger);
                BatchDelete.Notify(notificationService, outcome);
                await ReloadAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception while batch deleting users.");
                ViewNotification.UnexpectedError(notificationService, "刪除使用者時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
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

        async Task OnRestoreAsync(MyUserAdapterModel record)
        {
            try
            {
                var ok = await ConfirmDialog.AskAsync(modalService, "確認還原", $"要還原「{record.Account}」嗎？", "還原");
                if (!ok)
                {
                    return;
                }

                var result = await myUserService.RestoreAsync(record.Id);
                if (!result.Success)
                {
                    logger.LogInformation("User restore rejected. UserId={UserId}, Message={Message}", record.Id, result.Message);
                    ViewNotification.Error(notificationService, result.Message);
                    return;
                }

                logger.LogInformation("User restore completed. UserId={UserId}", record.Id);
                ViewNotification.Warning(notificationService, "還原成功");
                await ReloadAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception while restoring user.");
                ViewNotification.UnexpectedError(notificationService, "還原使用者時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
        }

        async Task OnPurgeAsync(MyUserAdapterModel record)
        {
            try
            {
                var ok = await ConfirmDialog.AskDestructiveAsync(
                    modalService,
                    "永久刪除",
                    $"永久刪除「{record.Account}」後無法復原，他的角色與團隊設定也會一併刪除。確定要永久刪除嗎？",
                    "永久刪除");
                if (!ok)
                {
                    return;
                }

                var result = await myUserService.PurgeAsync(record.Id);
                if (!result.Success)
                {
                    logger.LogInformation("User purge rejected. UserId={UserId}, Message={Message}", record.Id, result.Message);
                    ViewNotification.Error(notificationService, result.Message);
                    return;
                }

                logger.LogInformation("User purge completed. UserId={UserId}", record.Id);
                ViewNotification.Warning(notificationService, "已永久刪除");
                await ReloadAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception while purging user.");
                ViewNotification.UnexpectedError(notificationService, "永久刪除使用者時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
        }

        async Task OnAddAsync(bool continueOnCapturedContext)
        {
            await LoadRoleViewsAsync();

            CurrentRecord = new();
            RoleViewAdapterModel? defaultRole = null;
            try
            {
                defaultRole = await roleViewService.Get預設新建帳號角色Async();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to load default role for new user creation.");
            }

            if (defaultRole is not null && defaultRole.Id != 0)
            {
                CurrentRecord.RoleViewId = defaultRole.Id;
            }
            else if (roleViewAdapterModels.Any())
            {
                CurrentRecord.RoleViewId = roleViewAdapterModels.First().Id;
            }

            isNewRecordMode = true;
            modalTitle = "新增使用者";
            // 新帳號的密碼是管理員設的，預設要求本人第一次登入時換掉（0.9.101 起）。
            CurrentRecord.MustChangePassword = true;

            // ⚠️ 必須是開窗前的最後一步：預設角色已經塞完才拍快照。
            dirtyTracker.Capture(CurrentRecord);

            modalVisible = true;
            logger.LogInformation("Opened create modal for user.");
        }

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
                "儲存使用者時發生未預期的錯誤，請稍後再試或聯絡系統管理員。");
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
                    logger.LogInformation("User form validation failed. Error={Error}", error);
                    ViewNotification.ValidationError(notificationService, error);
                }

                return false;
            }

            if (isNewRecordMode && string.IsNullOrWhiteSpace(CurrentRecord.Password))
            {
                logger.LogInformation("User create validation failed because password is empty. Account={Account}", CurrentRecord.Account);
                ViewNotification.ValidationError(notificationService, "新增使用者時必須輸入密碼。");

                return false;
            }

            // 一個字都沒改就按儲存：不值得白寫一筆，也不該白跳一次確認窗。
            if (isNewRecordMode == false && dirtyTracker.IsDirty(CurrentRecord) == false)
            {
                logger.LogDebug("User save skipped because nothing changed. UserId={UserId}", CurrentRecord.Id);
                ViewNotification.Info(notificationService, "沒有任何變更，未進行儲存。");

                return true;
            }

            // 儲存前的二次確認。排在資料庫前置檢查之前：使用者若選「再檢查」，
            // 就不必白跑一次資料庫來回。
            if (await FormEditConfirm.AskSaveAsync(modalService) == false)
            {
                logger.LogDebug("User save cancelled at save confirmation. UserId={UserId}", CurrentRecord.Id);
                return false;
            }

            if (await ConfirmTeamBindingAsync(CurrentRecord) == false)
            {
                logger.LogDebug("User save cancelled at team confirmation. UserId={UserId}", CurrentRecord.Id);
                return false;
            }

            if (isNewRecordMode)
            {
                var beforeAddCheckResult = await myUserService.BeforeAddCheckAsync(CurrentRecord);
                if (!beforeAddCheckResult.Success)
                {
                    logger.LogInformation("User create pre-check failed. Account={Account}, Message={Message}", CurrentRecord.Account, beforeAddCheckResult.Message);
                    ViewNotification.Error(notificationService, beforeAddCheckResult.Message);

                    return false;
                }

                CurrentRecord.CreateAt = DateTime.Now;
                CurrentRecord.UpdateAt = DateTime.Now;

                var actionResult = await myUserService.AddAsync(CurrentRecord);
                if (!actionResult.Success)
                {
                    // 0.9.92 之前這裡不看結果，失敗也顯示「新增成功」。
                    logger.LogInformation("User create rejected. Account={Account}, Message={Message}", CurrentRecord.Account, actionResult.Message);
                    ViewNotification.Error(notificationService, actionResult.Message);
                    return false;
                }

                logger.LogInformation("User create submitted. Account={Account}", CurrentRecord.Account);

                ViewNotification.Warning(notificationService, "新增成功");

                _ = messageService.SuccessAsync("新增成功");
            }
            else
            {
                var beforeUpdateCheckResult = await myUserService.BeforeUpdateCheckAsync(CurrentRecord);
                if (!beforeUpdateCheckResult.Success)
                {
                    logger.LogInformation("User update pre-check failed. UserId={UserId}, Message={Message}", CurrentRecord.Id, beforeUpdateCheckResult.Message);
                    ViewNotification.Error(notificationService, beforeUpdateCheckResult.Message);

                    return false;
                }

                CurrentRecord.UpdateAt = DateTime.Now;

                var actionResult = await myUserService.UpdateAsync(CurrentRecord);
                if (!actionResult.Success)
                {
                    // 0.9.92 之前這裡不看結果，失敗也顯示「修改成功」；0.9.93 起的並行衝突訊息也走這裡，表單維持開啟。
                    logger.LogInformation("User update rejected. UserId={UserId}, Message={Message}", CurrentRecord.Id, actionResult.Message);
                    ViewNotification.Error(notificationService, actionResult.Message);
                    return false;
                }

                logger.LogInformation("User update submitted. UserId={UserId}, Account={Account}", CurrentRecord.Id, CurrentRecord.Account);

                ViewNotification.Warning(notificationService, "修改成功");

                // 管理員改了自己的密碼、角色或管理員身分：自己的工作階段版本已換掉，換發這台裝置的 Cookie（0.9.103 起）。
                if (await SessionRefreshNavigator.KeepSignedInAsync("/myusers"))
                {
                    return true;
                }
            }

            await ReloadAsync();
            dirtyTracker.Clear();

            return true;
        }

        /// <summary>
        /// 儲存前對「團隊欄位留空」提出警告。使用者的有效團隊＝直接綁定的團隊 ∪ 其所有角色的
        /// 預設團隊（見 EffectiveTeamResolver），所以留空不等於沒有團隊 —— 訊息依角色實際有沒有
        /// 預設團隊分成兩種措辭。管理員不受團隊行級過濾，提醒對他沒有意義，直接放行。
        /// 回傳 true 表示可以繼續儲存。
        /// </summary>
        private async Task<bool> ConfirmTeamBindingAsync(MyUserAdapterModel record)
        {
            if (record.TeamNames.Count > 0 || record.IsAdmin)
            {
                return true;
            }

            List<int> roleIds = new(record.AdditionalRoleIds);
            if (record.RoleViewId.HasValue)
            {
                roleIds.Add(record.RoleViewId.Value);
            }

            List<string> inheritedTeams = roleViewAdapterModels
                .Where(x => roleIds.Contains(x.Id))
                .SelectMany(x => x.DefaultTeams)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            string content = inheritedTeams.Count > 0
                ? $"未直接指定團隊，此使用者將沿用其角色的預設團隊（{string.Join("、", inheritedTeams)}）。確定要這樣儲存嗎？"
                : "未直接指定團隊，且其角色也沒有預設團隊，此使用者將只能看到無團隊標記的公開紀錄。確定要這樣儲存嗎？";

            return await TeamBindingConfirm.AskAsync(modalService, content);
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

            if (modalVisible == false)
            {
                dirtyTracker.Clear();
                logger.LogDebug("User modal cancelled. UserId={UserId}", CurrentRecord.Id);
            }
        }

        public void OnEditContestChanged(EditContext context)
        {
            LocalEditContext = context;
        }

        private void OnAdditionalRolesChanged(IEnumerable<int> values)
        {
            CurrentRecord.AdditionalRoleIds = values?.ToList() ?? new List<int>();
        }

        private void OnUserTeamsChanged(IEnumerable<string> values)
        {
            CurrentRecord.TeamNames = values?.ToList() ?? new List<string>();
        }

        #region 複製使用者（0.9.127 起）

        string copyModalTitle = "複製使用者";
        bool copyModalVisible;
        MyUserAdapterModel copyRecord = new();
        string copySourceAccount = string.Empty;
        List<string> copySkippedTeams = new();
        EditContext? copyEditContext;
        bool copyPasswordVisible;
        bool copyResultVisible;
        string copyResultText = string.Empty;
        private readonly FormDirtyTracker copyDirtyTracker = new();

        /// <summary>唯讀摘要的角色欄：主要角色在前、額外角色在後。</summary>
        string CopyRoleSummary
        {
            get
            {
                var names = new List<string>();
                if (roleViewAdapterModels.FirstOrDefault(r => r.Id == copyRecord.RoleViewId) is { } primary)
                {
                    names.Add($"{primary.Name}（主）");
                }

                names.AddRange(roleViewAdapterModels.Where(r => copyRecord.AdditionalRoleIds.Contains(r.Id)).Select(r => r.Name));
                return names.Count > 0 ? string.Join("、", names) : "（無）";
            }
        }

        /// <summary>
        /// 清單的「複製」：以這一列為範本開精簡窗。角色、團隊、狀態、管理員照抄（唯讀），
        /// 帳號、名稱、密碼、Email 留空；support 不會有這個按鈕，伺服器端仍會再擋一次。
        /// </summary>
        async Task OnCopyAsync(MyUserAdapterModel source)
        {
            try
            {
                await LoadRoleViewsAsync();
                var copy = await myUserService.BuildCopyDraftAsync(source.Id);
                if (copy is null)
                {
                    ViewNotification.Caution(notificationService, "這位使用者不能當作複製範本，或已不存在，請重新整理後再試。");
                    return;
                }

                copyRecord = copy.Value.Draft;
                copySkippedTeams = copy.Value.SkippedTeamNames;
                copySourceAccount = source.Account;
                copyModalTitle = $"複製使用者（來源：{source.Account} {source.Name}）";
                copyPasswordVisible = false;

                // ⚠️ 必須是開窗前的最後一步：範本內容不算使用者的變更。
                copyDirtyTracker.Capture(copyRecord);

                copyModalVisible = true;
                logger.LogInformation("Opened copy modal for user. SourceUserId={SourceUserId}, SourceAccount={SourceAccount}", source.Id, source.Account);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception while opening user copy modal.");
                ViewNotification.UnexpectedError(notificationService, "開啟複製視窗時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
        }

        void OnGeneratePassword()
        {
            copyRecord.Password = PasswordPolicy.Generate();
            copyPasswordVisible = true;
        }

        /// <summary>「建立」：第一行搶回 Visible 的理由同 <see cref="OnModalOKHandleAsync"/>。</summary>
        private async Task OnCopyModalOKHandleAsync(MouseEventArgs args)
        {
            copyModalVisible = true;
            copyModalVisible = await FormModalFlow.RunOkAsync(
                SaveCopyAsync,
                logger,
                notificationService,
                "建立使用者時發生未預期的錯誤，請稍後再試或聯絡系統管理員。");
        }

        /// <summary>
        /// 複製建立。刻意省掉一般新增的「確認儲存」：帶入的設定已列在窗內，按「建立」就是確認；
        /// 只在管理員、團隊留空、Email 已有人用這三種情況多問一次。
        /// </summary>
        private async Task<bool> SaveCopyAsync()
        {
            if (copyEditContext?.Validate() == false)
            {
                foreach (var error in copyEditContext.GetValidationMessages())
                {
                    logger.LogInformation("User copy form validation failed. Error={Error}", error);
                    ViewNotification.ValidationError(notificationService, error);
                }

                return false;
            }

            if (string.IsNullOrWhiteSpace(copyRecord.Password))
            {
                ViewNotification.ValidationError(notificationService, "新增使用者時必須輸入密碼。");
                return false;
            }

            if (copyRecord.IsAdmin && !await ConfirmDialog.AskAsync(modalService, "建立管理員帳號",
                    $"範本「{copySourceAccount}」是管理員，新帳號也會是管理員：略過所有權限檢查，並能使用「系統管理」底下的所有畫面。確定要建立嗎？", "建立"))
            {
                return false;
            }

            if (await ConfirmTeamBindingAsync(copyRecord) == false)
            {
                return false;
            }

            var beforeAddCheckResult = await myUserService.BeforeAddCheckAsync(copyRecord);
            if (!beforeAddCheckResult.Success)
            {
                logger.LogInformation("User copy pre-check failed. Account={Account}, Message={Message}", copyRecord.Account, beforeAddCheckResult.Message);
                ViewNotification.Error(notificationService, beforeAddCheckResult.Message);
                return false;
            }

            if (!string.IsNullOrWhiteSpace(copyRecord.Email))
            {
                var usedBy = await myUserService.GetAccountsUsingEmailAsync(copyRecord.Email);
                if (usedBy.Count > 0 && !await ConfirmDialog.AskAsync(modalService, "Email 已有人使用",
                        $"「{copyRecord.Email.Trim()}」已被 {string.Join("、", usedBy)} 使用，忘記密碼的重設信會寄到同一個信箱。仍要建立嗎？", "仍要建立"))
                {
                    return false;
                }
            }

            copyRecord.CreateAt = DateTime.Now;
            copyRecord.UpdateAt = DateTime.Now;
            var actionResult = await myUserService.AddAsync(copyRecord, copySourceAccount);
            if (!actionResult.Success)
            {
                logger.LogInformation("User copy rejected. Account={Account}, Message={Message}", copyRecord.Account, actionResult.Message);
                ViewNotification.Error(notificationService, actionResult.Message);
                return false;
            }

            logger.LogInformation("User created by copy. Account={Account}, SourceAccount={SourceAccount}", copyRecord.Account, copySourceAccount);
            ViewNotification.Warning(notificationService, "新增成功");

            // 給系統根網址就好：未登入的人打開首頁，開場畫面會自動導到登入頁（0.9.128 起）。
            copyResultText = $"登入網址：{NavigationManager.BaseUri}{Environment.NewLine}帳號：{copyRecord.Account}{Environment.NewLine}密碼：{copyRecord.Password}"
                + (copyRecord.MustChangePassword ? $"{Environment.NewLine}第一次登入後會要求變更密碼。" : string.Empty);
            copyRecord = new();
            copyDirtyTracker.Clear();

            await ReloadAsync();
            copyResultVisible = true;
            await CopyResultToClipboardAsync();
            return true;
        }

        private async Task OnCopyModalCancelHandleAsync(MouseEventArgs args)
        {
            copyModalVisible = true;
            copyModalVisible = await FormModalFlow.ConfirmCloseAsync(modalService, copyDirtyTracker.IsDirty(copyRecord));

            if (copyModalVisible == false)
            {
                copyRecord = new();
                copyDirtyTracker.Clear();
            }
        }

        public void OnCopyEditContextChanged(EditContext context)
        {
            copyEditContext = context;
        }

        /// <summary>交付窗的「複製」：複製後不關窗（第一行搶回 Visible，理由同上）。</summary>
        private async Task OnCopyResultCopyAsync(MouseEventArgs args)
        {
            copyResultVisible = true;
            await CopyResultToClipboardAsync();
        }

        /// <summary>關窗時把含明碼的交付文字清掉。</summary>
        private void OnCopyResultClose(MouseEventArgs args)
        {
            copyResultVisible = false;
            copyResultText = string.Empty;
        }

        /// <summary>
        /// 走 appClipboard.copyText。建立成功時的自動複製不是使用者手勢觸發的，部分瀏覽器會拒絕，
        /// 這時請使用者按交付窗的「複製」再試一次。
        /// </summary>
        private async Task CopyResultToClipboardAsync()
        {
            try
            {
                if (await JSRuntime.InvokeAsync<bool>("appClipboard.copyText", copyResultText))
                {
                    ViewNotification.Info(notificationService, "已將登入資訊複製到剪貼簿。");
                }
                else
                {
                    ViewNotification.Warning(notificationService, "瀏覽器拒絕存取剪貼簿，請按「複製」再試一次，或手動選取複製。");
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to copy created user credentials to clipboard.");
                ViewNotification.Warning(notificationService, "瀏覽器拒絕存取剪貼簿，請按「複製」再試一次，或手動選取複製。");
            }
        }

        #endregion

        private async Task LoadRoleViewsAsync()
        {
            roleViewAdapterModels = await myUserService.GetRoleViewsAsync();
            logger.LogDebug("Loaded role views for user view. Count={Count}", roleViewAdapterModels.Count);
        }
    }
}

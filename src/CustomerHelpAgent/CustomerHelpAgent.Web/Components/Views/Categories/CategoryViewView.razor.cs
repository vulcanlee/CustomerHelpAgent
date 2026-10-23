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
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Web.Components.Commons;
using CustomerHelpAgent.Web.Export;

namespace CustomerHelpAgent.Web.Components.Views.Categories
{
    public partial class CategoryViewView
    {

        [Inject]
        public Microsoft.JSInterop.IJSRuntime JSRuntime { get; set; } = default!;

        [Inject]
        public IAuditLogService AuditLogService { get; set; } = default!;

        [Inject]
        public CurrentUserService CurrentUserService { get; set; } = default!;

        /// <summary>匯出 Excel（0.9.107 起）：與清單同一個查詢，超過 <see cref="BusinessExports.MaxRows"/> 筆不匯出。</summary>
        private async Task OnExportExcelAsync()
        {
            try
            {
                var outcome = await BusinessExports.BuildAsync(r => showDeleted ? categoryService.GetDeletedAsync(r) : categoryService.GetAsync(r), new DataRequest
                {
                    Search = searchText,
                    SortField = sortField,
                    SortDescending = sortDirection == "Descending" ? true : sortDirection == "Ascending" ? false : (bool?)null,
                }, "分類", "categories", BusinessExports.CategoryColumns);
                if (outcome.Error is { } error)
                {
                    ViewNotification.Caution(notificationService, error);
                    return;
                }

                await JSRuntime.DownloadAsync(outcome.FileName!, outcome.Content!, TabularExport.XlsxContentType);
                logger.LogInformation("Category export downloaded. Rows={Rows}", outcome.Rows);
                await BusinessExports.WriteAuditAsync(AuditLogService, CurrentUserService, AuditActions.Category.Export, "Category", outcome.Rows, showDeleted);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Category export failed.");
                ViewNotification.UnexpectedError(notificationService, $"匯出失敗：{ex.GetType().Name}。", ex);
            }
        }

        private readonly ILogger<CategoryViewView> logger;
        private readonly CategoryService categoryService;
        private readonly TeamService teamService;
        private readonly IRecordAccessScopeProvider accessScope;
        private readonly ModalService modalService;
        private readonly MessageService messageService;
        private readonly NotificationService notificationService;
        ITable? table;
        int _pageIndex = 1;
        int _pageSize = MagicObjectHelper.PageSize;
        int _total = 0;
        string searchText = string.Empty;
        string sortField = string.Empty;
        string sortDirection = "None";

        List<CategoryAdapterModel> categoryAdapterModels = new();
        IEnumerable<CategoryAdapterModel> selectedRows = [];
        bool batchDeleting;
        List<string> availableTeams = new();

        /// <summary>
        /// 表單「適用團隊」的選項（0.9.105 起）：管理員是全部；其他人只有自己範圍內的團隊（含下屬部門），
        /// 加上這筆原本就有的。伺服器端 <c>RecordTeamScope.CheckAssignment</c> 會再檢查一次。
        /// </summary>
        IEnumerable<string> AssignableTeams => currentScope.IsAdmin
            ? availableTeams
            : availableTeams.Where(t => currentScope.Teams.Contains(t, StringComparer.OrdinalIgnoreCase) || CurrentRecord.Teams.Contains(t, StringComparer.OrdinalIgnoreCase));

        /// <summary>目前使用者的存取範圍，用於判斷「存檔後自己就看不到這筆」的提醒。</summary>
        RecordAccessScope currentScope = new(false, []);

        string modalTitle = "分類維護";
        bool modalVisible = false;
        CategoryAdapterModel CurrentRecord = new();
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

        public CategoryViewView(
            ILogger<CategoryViewView> logger,
            CategoryService categoryService,
            TeamService teamService,
            IRecordAccessScopeProvider accessScope,
            ModalService modalService,
            MessageService messageService,
            NotificationService notificationService)
        {
            this.logger = logger;
            this.categoryService = categoryService;
            this.teamService = teamService;
            this.accessScope = accessScope;
            this.modalService = modalService;
            this.messageService = messageService;
            this.notificationService = notificationService;
        }

        /// <summary>「資料保留與清理」頁的「前往」帶 <c>?deleted=1</c> 進來時為 true：一進頁面就顯示已刪除的資料（0.9.133 起）。</summary>
        [Parameter]
        public bool InitialShowDeleted { get; set; }

        protected override async Task OnInitializedAsync()
        {
            logger.LogDebug("Initializing category management view.");
            var checkResult = await AuthenticationStateHelper.Check(authStateProvider, NavigationManager);
            if (checkResult != AuthenticationCheckResult.Succeeded)
            {
                logger.LogWarning("Category view initialization stopped because authentication check failed.");
                return;
            }

            isAccessChecked = true;

            if (AuthenticationStateHelper.CheckAccessPage(MagicObjectHelper.角色_分類清單) == false)
            {
                RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
                await AuthenticationStateHelper.RecordPageAccessDeniedAsync("/categories");
                logger.LogWarning("Category view denied because current user has not this role permission.");
                return;
            }

            availableTeams = await teamService.GetAllEnabledNamesAsync();
            currentScope = await accessScope.GetAsync();

            // 只有看得到「顯示已刪除」按鈕的人（刪除權限）才套用。
            showDeleted = InitialShowDeleted && AuthenticationStateHelper.CheckAccessAction(MagicObjectHelper.角色_分類清單, PermissionActions.Delete);
            await ReloadAsync();
        }

        public async Task ReloadAsync()
        {
            // 換頁、排序、搜尋、切換與刪除後都會走到這裡：勾選只在目前這一頁有效（0.9.132 起）。
            selectedRows = [];
            logger.LogDebug(
                "Reloading categories. HasSearch={HasSearch}, SearchLength={SearchLength}, SortField={SortField}, SortDirection={SortDirection}, PageIndex={PageIndex}, PageSize={PageSize}",
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

            // 「顯示已刪除」開啟時改讀已刪除的資料（0.9.94 起）。
            DataRequestResult<CategoryAdapterModel> dataRequestResult = showDeleted
                ? await categoryService.GetDeletedAsync(dataRequest)
                : await categoryService.GetAsync(dataRequest);

            categoryAdapterModels = dataRequestResult.Result.ToList();
            _total = dataRequestResult.Count;
            logger.LogDebug("Category list reloaded successfully. Count={Count}", _total);
            StateHasChanged();
        }

        async Task OnTableChange(QueryModel<CategoryAdapterModel> args)
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

            logger.LogDebug("Category table changed. PageIndex={PageIndex}, SortField={SortField}, SortDirection={SortDirection}", _pageIndex, sortField, sortDirection);
            await ReloadAsync();
        }


        async Task OnSearchAsync()
        {
            _pageIndex = 1;
            logger.LogInformation("Category search triggered. HasSearch={HasSearch}, SearchLength={SearchLength}", string.IsNullOrWhiteSpace(searchText) == false, searchText.Length);
            await ReloadAsync();
            await ViewAudit.WriteSearchAsync(AuditLogService, CurrentUserService, AuditActions.Category.Search, "Category",
                AuditSearchCondition.Text("keyword", searchText), AuditSearchCondition.Of("showDeleted", showDeleted));
        }

        async Task OnRefreshAsync()
        {
            logger.LogInformation("Category refresh triggered.");
            await ReloadAsync();

            ViewNotification.Warning(notificationService, "已更新最新資料");
        }

        /// <summary>
        /// 一律指派新清單：CurrentRecord 是 MemberwiseClone 出來的，
        /// 直接異動原清單會連帶改到表格上那一列的資料。
        /// </summary>
        void OnRecordTeamsChanged(IEnumerable<string> values)
        {
            CurrentRecord.Teams = values?.ToList() ?? new List<string>();
        }

        async Task OnEditAsync(CategoryAdapterModel categoryAdapterModel)
        {
            isNewRecordMode = false;
            modalTitle = "修改分類";
            CurrentRecord = categoryAdapterModel.Clone();
            // ⚠️ 必須是開窗前的最後一步：任何預設值都要先塞完，否則會被當成使用者的變更。
            dirtyTracker.Capture(CurrentRecord);

            modalVisible = true;
            logger.LogInformation("Opened edit modal for category. CategoryId={CategoryId}, Name={Name}", categoryAdapterModel.Id, categoryAdapterModel.Name);
            await ViewAudit.WriteAsync(AuditLogService, CurrentUserService, AuditActions.Category.View, "Category",
                categoryAdapterModel.Id.ToString(), $"name={categoryAdapterModel.Name}");
        }

        /// <summary>
        /// 點狀態標籤切換啟用／停用（0.9.120 起）：停用先確認，啟用直接切換；只改狀態，不經修改窗。
        /// </summary>
        async Task OnToggleEnabledAsync(CategoryAdapterModel record)
        {
            try
            {
                var enable = !record.IsEnabled;
                if (!enable && !await ConfirmDialog.AskAsync(modalService, "停用分類",
                        $"要停用「{record.Name}」嗎？停用後它不會出現在下拉選單，已使用這個分類的資料不受影響。", "停用"))
                {
                    return;
                }

                var result = await categoryService.SetEnabledAsync(record.Id, enable);
                if (!result.Success)
                {
                    logger.LogInformation("Category status change rejected. CategoryId={CategoryId}, Message={Message}", record.Id, result.Message);
                    ViewNotification.Error(notificationService, result.Message);
                }
                else
                {
                    ViewNotification.Warning(notificationService, $"已{(enable ? "啟用" : "停用")}「{record.Name}」");
                }

                await ReloadAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception while changing category status.");
                ViewNotification.UnexpectedError(notificationService, "切換分類狀態時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
        }

        /// <summary>
        /// 包住實際邏輯以捕捉未預期的例外：先前這些寫入操作完全沒有 try/catch，
        /// 例外會直接拆掉 Blazor circuit，使用者只看到畫面斷線、日誌上也留不下任何痕跡。
        /// </summary>
        async Task OnDeleteAsync(CategoryAdapterModel categoryAdapterModel)
        {
            try
            {
                await OnDeleteCoreAsync(categoryAdapterModel);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception while deleting category.");
                ViewNotification.UnexpectedError(notificationService, "刪除分類時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
        }

        async Task OnDeleteCoreAsync(CategoryAdapterModel categoryAdapterModel)
        {
            logger.LogInformation("Delete category requested. CategoryId={CategoryId}, Name={Name}", categoryAdapterModel.Id, categoryAdapterModel.Name);

            var ok = await ConfirmDialog.AskSoftDeleteRecordAsync(modalService);

            if (!ok)
            {
                logger.LogDebug("Category delete cancelled by user. CategoryId={CategoryId}", categoryAdapterModel.Id);
                return;
            }

            var result = await categoryService.DeleteAsync(categoryAdapterModel.Id);
            if (!result.Success)
            {
                // 0.9.93 之前這裡不看結果，失敗也顯示「刪除成功」。
                logger.LogInformation("Category delete rejected. CategoryId={CategoryId}, Message={Message}", categoryAdapterModel.Id, result.Message);
                ViewNotification.Error(notificationService, result.Message);
                return;
            }

            logger.LogInformation("Category delete completed. CategoryId={CategoryId}", categoryAdapterModel.Id);

            ViewNotification.Warning(notificationService, "刪除成功");

            await ReloadAsync();
        }

        /// <summary>「刪除選取」（0.9.132 起）：逐筆走 <see cref="CategoryService.DeleteAsync"/>，規則與稽核同單筆刪除。</summary>
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
                    x => categoryService.DeleteAsync(x.Id), logger);
                BatchDelete.Notify(notificationService, outcome);
                await ReloadAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception while batch deleting categories.");
                ViewNotification.UnexpectedError(notificationService, "刪除分類時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
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

        async Task OnRestoreAsync(CategoryAdapterModel record)
        {
            try
            {
                var ok = await ConfirmDialog.AskAsync(modalService, "確認還原", $"要還原「{record.Name}」嗎？", "還原");
                if (!ok)
                {
                    return;
                }

                var result = await categoryService.RestoreAsync(record.Id);
                if (!result.Success)
                {
                    logger.LogInformation("Category restore rejected. CategoryId={CategoryId}, Message={Message}", record.Id, result.Message);
                    ViewNotification.Error(notificationService, result.Message);
                    return;
                }

                logger.LogInformation("Category restore completed. CategoryId={CategoryId}", record.Id);
                ViewNotification.Warning(notificationService, "還原成功");
                await ReloadAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception while restoring category.");
                ViewNotification.UnexpectedError(notificationService, "還原分類時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
        }

        async Task OnPurgeAsync(CategoryAdapterModel record)
        {
            try
            {
                var ok = await ConfirmDialog.AskDestructiveAsync(
                    modalService,
                    "永久刪除",
                    $"永久刪除「{record.Name}」後無法復原。確定要永久刪除嗎？",
                    "永久刪除");
                if (!ok)
                {
                    return;
                }

                var result = await categoryService.PurgeAsync(record.Id);
                if (!result.Success)
                {
                    logger.LogInformation("Category purge rejected. CategoryId={CategoryId}, Message={Message}", record.Id, result.Message);
                    ViewNotification.Error(notificationService, result.Message);
                    return;
                }

                logger.LogInformation("Category purge completed. CategoryId={CategoryId}", record.Id);
                ViewNotification.Warning(notificationService, "已永久刪除");
                await ReloadAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception while purging category.");
                ViewNotification.UnexpectedError(notificationService, "永久刪除分類時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
        }

        async Task OnAddAsync()
        {
            CurrentRecord = new();
            isNewRecordMode = true;
            modalTitle = "新增分類";
            // ⚠️ 必須是開窗前的最後一步：任何預設值都要先塞完，否則會被當成使用者的變更。
            dirtyTracker.Capture(CurrentRecord);

            modalVisible = true;
            logger.LogInformation("Opened create modal for category.");
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
                "儲存分類時發生未預期的錯誤，請稍後再試或聯絡系統管理員。");
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
                    logger.LogInformation("Category form validation failed. Error={Error}", error);
                    ViewNotification.ValidationError(notificationService, error);
                }

                return false;
            }

            // 一個字都沒改就按儲存：不值得白寫一筆，也不該白跳一次確認窗。
            if (isNewRecordMode == false && dirtyTracker.IsDirty(CurrentRecord) == false)
            {
                logger.LogDebug("Category save skipped because nothing changed. CategoryId={CategoryId}", CurrentRecord.Id);
                ViewNotification.Info(notificationService, "沒有任何變更，未進行儲存。");

                return true;
            }

            // 儲存前的二次確認。排在資料庫前置檢查之前：使用者若選「再檢查」，
            // 就不必白跑一次資料庫來回。
            if (await FormEditConfirm.AskSaveAsync(modalService) == false)
            {
                logger.LogDebug("Category save cancelled at save confirmation. CategoryId={CategoryId}", CurrentRecord.Id);
                return false;
            }

            // 名稱重複檢查排在團隊確認對話窗之前：這個檢查便宜、且失敗時必定不能儲存，
            // 沒有理由讓使用者先回答一個對話窗才被告知名稱重複。
            if (isNewRecordMode)
            {
                var beforeAddCheckResult = await categoryService.BeforeAddCheckAsync(CurrentRecord);
                if (!beforeAddCheckResult.Success)
                {
                    logger.LogInformation("Category create pre-check failed. Name={Name}, Message={Message}", CurrentRecord.Name, beforeAddCheckResult.Message);
                    ViewNotification.Error(notificationService, beforeAddCheckResult.Message);

                    return false;
                }
            }
            else
            {
                var beforeUpdateCheckResult = await categoryService.BeforeUpdateCheckAsync(CurrentRecord);
                if (!beforeUpdateCheckResult.Success)
                {
                    logger.LogInformation("Category update pre-check failed. CategoryId={CategoryId}, Message={Message}", CurrentRecord.Id, beforeUpdateCheckResult.Message);
                    ViewNotification.Error(notificationService, beforeUpdateCheckResult.Message);

                    return false;
                }
            }

            if (await ConfirmTeamBindingAsync() == false)
            {
                logger.LogDebug("Category save cancelled at team confirmation. CategoryId={CategoryId}", CurrentRecord.Id);

                // 保持 Modal 開啟，讓使用者回到原本的編輯內容重新指定團隊。
                return false;
            }

            VerifyRecordResult actionResult;

            if (isNewRecordMode)
            {
                CurrentRecord.CreatedAt = DateTime.Now;
                CurrentRecord.UpdatedAt = DateTime.Now;

                actionResult = await categoryService.AddAsync(CurrentRecord);
                logger.LogInformation("Category create submitted. Name={Name}", CurrentRecord.Name);
            }
            else
            {
                CurrentRecord.UpdatedAt = DateTime.Now;
                actionResult = await categoryService.UpdateAsync(CurrentRecord);
                logger.LogInformation("Category update submitted. CategoryId={CategoryId}, Name={Name}", CurrentRecord.Id, CurrentRecord.Name);
            }

            // 前置檢查通過不代表寫得進去：唯一索引在並發時仍會擋下，
            // 忽略這個回傳值會讓失敗的儲存顯示成「新增成功」。
            if (!actionResult.Success)
            {
                ViewNotification.Error(notificationService, actionResult.Message);

                return false;
            }

            ViewNotification.Warning(notificationService, isNewRecordMode ? "新增成功" : "修改成功");

            if (isNewRecordMode)
            {
                _ = messageService.SuccessAsync("新增成功");
            }

            await ReloadAsync();
            dirtyTracker.Clear();

            return true;
        }

        /// <summary>
        /// 儲存前對「團隊設定」提出警告。兩種情況只會擇一提醒：
        /// 1. 完全沒指定團隊 —— 這筆會變成所有人都看得到的公用分類。
        /// 2. 指定的團隊與自己所屬團隊沒有交集 —— 存檔後自己就會在清單上看不到它。
        /// 回傳 true 表示可以繼續儲存。
        /// </summary>
        private async Task<bool> ConfirmTeamBindingAsync()
        {
            if (CurrentRecord.Teams.Count == 0)
            {
                return await TeamBindingConfirm.AskAsync(
                    modalService,
                    "此分類未指定適用團隊，將出現在所有使用者的分類下拉清單中（視同公用分類）。確定要這樣儲存嗎？");
            }

            if (currentScope.IsAdmin == false
                && currentScope.Teams.Count > 0
                && CurrentRecord.Teams.Any(x => currentScope.Teams.Any(
                    team => string.Equals(x, team, StringComparison.OrdinalIgnoreCase))) == false)
            {
                return await TeamBindingConfirm.AskAsync(
                    modalService,
                    "此分類指定的團隊不包含你所屬的任何團隊，儲存後你將無法在分類清單中看到這筆資料。確定要這樣儲存嗎？");
            }

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

            if (modalVisible == false)
            {
                dirtyTracker.Clear();
                logger.LogDebug("Category modal cancelled.");
            }
        }

        public void OnEditContestChanged(EditContext context)
        {
            LocalEditContext = context;
        }
    }
}

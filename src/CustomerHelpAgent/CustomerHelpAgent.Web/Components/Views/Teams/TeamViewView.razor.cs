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

namespace CustomerHelpAgent.Web.Components.Views.Teams
{
    public partial class TeamViewView
    {

        [Inject]
        public Microsoft.JSInterop.IJSRuntime JSRuntime { get; set; } = default!;

        [Inject]
        public IAuditLogService AuditLogService { get; set; } = default!;

        [Inject]
        public CurrentUserService CurrentUserService { get; set; } = default!;

        /// <summary>匯出的「上層部門」欄：已刪除的上層顯示「（已刪除的部門）」。</summary>
        private string? ParentNameForExport(int? parentId) => parentId is { } id ? teamNames.GetValueOrDefault(id, "（已刪除的部門）") : null;

        /// <summary>匯出 Excel（0.9.107 起）：與清單同一個查詢，超過 <see cref="BusinessExports.MaxRows"/> 筆不匯出。</summary>
        private async Task OnExportExcelAsync()
        {
            try
            {
                var outcome = IsTreeMode
                    ? BusinessExports.FromRows(await teamService.GetAllAsync(), "團隊", "teams", BusinessExports.TeamColumns(ParentNameForExport))
                    : await BusinessExports.BuildAsync(r => showDeleted ? teamService.GetDeletedAsync(r) : teamService.GetAsync(r), new DataRequest
                    {
                        Search = searchText,
                        SortField = sortField,
                        SortDescending = sortDirection == "Descending" ? true : sortDirection == "Ascending" ? false : (bool?)null,
                    }, "團隊", "teams", BusinessExports.TeamColumns(ParentNameForExport));
                if (outcome.Error is { } error)
                {
                    ViewNotification.Caution(notificationService, error);
                    return;
                }

                await JSRuntime.DownloadAsync(outcome.FileName!, outcome.Content!, TabularExport.XlsxContentType);
                logger.LogInformation("Team export downloaded. Rows={Rows}", outcome.Rows);
                await BusinessExports.WriteAuditAsync(AuditLogService, CurrentUserService, AuditActions.Team.Export, "Team", outcome.Rows, showDeleted);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Team export failed.");
                ViewNotification.UnexpectedError(notificationService, $"匯出失敗：{ex.GetType().Name}。", ex);
            }
        }

        private readonly ILogger<TeamViewView> logger;
        private readonly TeamService teamService;
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

        List<TeamAdapterModel> teamAdapterModels = new();
        IEnumerable<TeamAdapterModel> selectedRows = [];
        bool batchDeleting;

        /// <summary>每個團隊的上層 Id，給「刪除選取」排出先下屬、後上層的順序（0.9.132 起）。</summary>
        Dictionary<int, int?> parentIds = new();

        /// <summary>部門樹（0.9.105 起）：上層 Id → 下屬；名稱查詢給「上層部門」欄與表單用。</summary>
        ILookup<int, TeamAdapterModel> childrenLookup = Enumerable.Empty<TeamAdapterModel>().ToLookup(x => 0);
        Dictionary<int, string> teamNames = new();
        List<TeamNode> parentCandidates = new();

        bool IsTreeMode => !showDeleted && string.IsNullOrWhiteSpace(searchText);

        IEnumerable<TeamAdapterModel> ChildrenOf(TeamAdapterModel item) => IsTreeMode ? childrenLookup[item.Id] : [];

        string ParentNameOf(TeamAdapterModel item)
            => item.ParentId is { } parentId ? teamNames.GetValueOrDefault(parentId, "（已刪除的部門）") : "—";

        /// <summary>下拉以字串綁定（與其他表單的多選一致），留白＝最上層。</summary>
        string? ParentIdText
        {
            get => CurrentRecord.ParentId?.ToString();
            set => CurrentRecord.ParentId = int.TryParse(value, out var id) ? id : null;
        }

        string modalTitle = "團隊維護";
        bool modalVisible = false;
        TeamAdapterModel CurrentRecord = new();
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

        public TeamViewView(
            ILogger<TeamViewView> logger,
            TeamService teamService,
            ModalService modalService,
            MessageService messageService,
            NotificationService notificationService)
        {
            this.logger = logger;
            this.teamService = teamService;
            this.modalService = modalService;
            this.messageService = messageService;
            this.notificationService = notificationService;
        }

        /// <summary>「資料保留與清理」頁的「前往」帶 <c>?deleted=1</c> 進來時為 true：一進頁面就顯示已刪除的資料（0.9.133 起）。</summary>
        [Parameter]
        public bool InitialShowDeleted { get; set; }

        protected override async Task OnInitializedAsync()
        {
            logger.LogDebug("Initializing team management view.");
            var checkResult = await AuthenticationStateHelper.Check(authStateProvider, NavigationManager);
            if (checkResult != AuthenticationCheckResult.Succeeded)
            {
                logger.LogWarning("Team view initialization stopped because authentication check failed.");
                return;
            }

            isAccessChecked = true;

            if (AuthenticationStateHelper.CheckAccessPage(MagicObjectHelper.角色_團隊清單) == false)
            {
                RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
                await AuthenticationStateHelper.RecordPageAccessDeniedAsync("/teams");
                logger.LogWarning("Team view denied because current user has not this role permission.");
                return;
            }

            // 只有看得到「顯示已刪除」按鈕的人（刪除權限）才套用。
            showDeleted = InitialShowDeleted && AuthenticationStateHelper.CheckAccessAction(MagicObjectHelper.角色_團隊清單, PermissionActions.Delete);
            await ReloadAsync();
        }

        public async Task ReloadAsync()
        {
            // 換頁、排序、搜尋、切換與刪除後都會走到這裡：勾選只在目前這一頁有效（0.9.132 起）。
            selectedRows = [];
            logger.LogDebug(
                "Reloading teams. HasSearch={HasSearch}, SearchLength={SearchLength}, SortField={SortField}, SortDirection={SortDirection}, PageIndex={PageIndex}, PageSize={PageSize}",
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

            var all = await teamService.GetAllAsync();
            teamNames = all.ToDictionary(x => x.Id, x => x.Name);
            parentIds = all.ToDictionary(x => x.Id, x => x.ParentId);
            if (IsTreeMode)
            {
                // 一次載入全部在畫面上組樹；上層不存在（資料異常）的當作最上層，不會消失。
                childrenLookup = all.Where(x => x.ParentId is { } p && teamNames.ContainsKey(p)).ToLookup(x => x.ParentId!.Value);
                var roots = SortRoots(all.Where(x => x.ParentId is not { } p || !teamNames.ContainsKey(p))).ToList();
                _total = roots.Count;
                teamAdapterModels = roots.Skip((_pageIndex - 1) * _pageSize).Take(_pageSize).ToList();
                logger.LogDebug("Team tree reloaded successfully. Count={Count}, Roots={Roots}", all.Count, _total);
                StateHasChanged();
                return;
            }

            // 「顯示已刪除」開啟時改讀已刪除的資料（0.9.94 起）。
            DataRequestResult<TeamAdapterModel> dataRequestResult = showDeleted
                ? await teamService.GetDeletedAsync(dataRequest)
                : await teamService.GetAsync(dataRequest);

            teamAdapterModels = dataRequestResult.Result.ToList();
            _total = dataRequestResult.Count;
            logger.LogDebug("Team list reloaded successfully. Count={Count}", _total);
            StateHasChanged();
        }

        /// <summary>樹狀模式的最上層排序：沿用欄位排序，預設依名稱。</summary>
        IEnumerable<TeamAdapterModel> SortRoots(IEnumerable<TeamAdapterModel> roots)
        {
            var descending = sortDirection == "Descending";
            return sortField switch
            {
                nameof(TeamAdapterModel.Code) => descending ? roots.OrderByDescending(x => x.Code) : roots.OrderBy(x => x.Code),
                nameof(TeamAdapterModel.IsEnabled) => descending ? roots.OrderByDescending(x => x.IsEnabled) : roots.OrderBy(x => x.IsEnabled),
                nameof(TeamAdapterModel.UpdatedAt) => descending ? roots.OrderByDescending(x => x.UpdatedAt) : roots.OrderBy(x => x.UpdatedAt),
                nameof(TeamAdapterModel.Name) when descending => roots.OrderByDescending(x => x.Name),
                _ => roots.OrderBy(x => x.Name),
            };
        }

        async Task OnTableChange(QueryModel<TeamAdapterModel> args)
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

            logger.LogDebug("Team table changed. PageIndex={PageIndex}, SortField={SortField}, SortDirection={SortDirection}", _pageIndex, sortField, sortDirection);
            await ReloadAsync();
        }


        async Task OnSearchAsync()
        {
            _pageIndex = 1;
            logger.LogInformation("Team search triggered. HasSearch={HasSearch}, SearchLength={SearchLength}", string.IsNullOrWhiteSpace(searchText) == false, searchText.Length);
            await ReloadAsync();
            await ViewAudit.WriteSearchAsync(AuditLogService, CurrentUserService, AuditActions.Team.Search, "Team",
                AuditSearchCondition.Text("keyword", searchText), AuditSearchCondition.Of("showDeleted", showDeleted));
        }

        async Task OnRefreshAsync()
        {
            logger.LogInformation("Team refresh triggered.");
            await ReloadAsync();

            ViewNotification.Warning(notificationService, "已更新最新資料");
        }

        async Task OnEditAsync(TeamAdapterModel teamAdapterModel)
        {
            isNewRecordMode = false;
            modalTitle = "修改團隊";
            CurrentRecord = teamAdapterModel.Clone();
            parentCandidates = await teamService.GetParentCandidatesAsync(teamAdapterModel.Id);
            // ⚠️ 必須是開窗前的最後一步：任何預設值都要先塞完，否則會被當成使用者的變更。
            dirtyTracker.Capture(CurrentRecord);

            modalVisible = true;
            logger.LogInformation("Opened edit modal for team. TeamId={TeamId}, Name={Name}", teamAdapterModel.Id, teamAdapterModel.Name);
            await ViewAudit.WriteAsync(AuditLogService, CurrentUserService, AuditActions.Team.View, "Team",
                teamAdapterModel.Id.ToString(), $"name={teamAdapterModel.Name}");
        }

        /// <summary>
        /// 點狀態標籤切換啟用／停用（0.9.120 起）：停用先確認，啟用直接切換；只改狀態，不經修改窗。
        /// </summary>
        async Task OnToggleEnabledAsync(TeamAdapterModel record)
        {
            try
            {
                var enable = !record.IsEnabled;
                if (!enable && !await ConfirmDialog.AskAsync(modalService, "停用團隊",
                        $"要停用「{record.Name}」嗎？停用後它不會出現在下拉選單，已綁定的人員與資料不受影響。", "停用"))
                {
                    return;
                }

                var result = await teamService.SetEnabledAsync(record.Id, enable);
                if (!result.Success)
                {
                    logger.LogInformation("Team status change rejected. TeamId={TeamId}, Message={Message}", record.Id, result.Message);
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
                logger.LogError(ex, "Unhandled exception while changing team status.");
                ViewNotification.UnexpectedError(notificationService, "切換團隊狀態時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
        }

        /// <summary>
        /// 包住實際邏輯以捕捉未預期的例外：先前這些寫入操作完全沒有 try/catch，
        /// 例外會直接拆掉 Blazor circuit，使用者只看到畫面斷線、日誌上也留不下任何痕跡。
        /// </summary>
        async Task OnDeleteAsync(TeamAdapterModel teamAdapterModel)
        {
            try
            {
                await OnDeleteCoreAsync(teamAdapterModel);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception while deleting team.");
                ViewNotification.UnexpectedError(notificationService, "刪除團隊時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
        }

        async Task OnDeleteCoreAsync(TeamAdapterModel teamAdapterModel)
        {
            logger.LogInformation("Delete team requested. TeamId={TeamId}, Name={Name}", teamAdapterModel.Id, teamAdapterModel.Name);

            var ok = await ConfirmDialog.AskSoftDeleteRecordAsync(modalService);

            if (!ok)
            {
                logger.LogDebug("Team delete cancelled by user. TeamId={TeamId}", teamAdapterModel.Id);
                return;
            }

            var result = await teamService.DeleteAsync(teamAdapterModel.Id);
            if (!result.Success)
            {
                // 0.9.93 之前這裡不看結果，失敗也顯示「刪除成功」。
                logger.LogInformation("Team delete rejected. TeamId={TeamId}, Message={Message}", teamAdapterModel.Id, result.Message);
                ViewNotification.Error(notificationService, result.Message);
                return;
            }

            logger.LogInformation("Team delete completed. TeamId={TeamId}", teamAdapterModel.Id);

            ViewNotification.Warning(notificationService, "刪除成功");

            await ReloadAsync();
        }

        /// <summary>
        /// 「刪除選取」（0.9.132 起）：逐筆走 <see cref="TeamService.DeleteAsync"/>，規則與稽核同單筆刪除。
        /// 由深到淺刪：同時勾了上層與它全部的下屬時，上層不會因「還有下屬部門」被擋下；
        /// 只勾上層、下屬仍在時照原規則失敗。
        /// </summary>
        async Task OnBatchDeleteAsync()
        {
            try
            {
                var items = BatchDelete.DeepestFirst(selectedRows, x => DepthOf(x.Id));
                if (items.Count == 0 || !await BatchDelete.AskAsync(modalService, items.Select(x => x.Name).ToList(), restorable: true))
                {
                    return;
                }

                batchDeleting = true;
                var outcome = await BatchDelete.RunAsync(items, x => x.Id.ToString(), x => x.Name,
                    x => teamService.DeleteAsync(x.Id), logger);
                BatchDelete.Notify(notificationService, outcome);
                await ReloadAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception while batch deleting teams.");
                ViewNotification.UnexpectedError(notificationService, "刪除團隊時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
            finally
            {
                batchDeleting = false;
            }
        }

        /// <summary>往上數到最上層的層數；上限為團隊總數，資料若有循環也不會卡住。</summary>
        int DepthOf(int id)
        {
            var depth = 0;
            while (parentIds.GetValueOrDefault(id) is { } parent && depth < parentIds.Count)
            {
                id = parent;
                depth++;
            }

            return depth;
        }

        bool showDeleted;

        async Task OnToggleDeletedAsync()
        {
            showDeleted = !showDeleted;
            _pageIndex = 1;
            await ReloadAsync();
        }

        async Task OnRestoreAsync(TeamAdapterModel record)
        {
            try
            {
                var ok = await ConfirmDialog.AskAsync(modalService, "確認還原", $"要還原「{record.Name}」嗎？", "還原");
                if (!ok)
                {
                    return;
                }

                var result = await teamService.RestoreAsync(record.Id);
                if (!result.Success)
                {
                    logger.LogInformation("Team restore rejected. TeamId={TeamId}, Message={Message}", record.Id, result.Message);
                    ViewNotification.Error(notificationService, result.Message);
                    return;
                }

                logger.LogInformation("Team restore completed. TeamId={TeamId}", record.Id);
                ViewNotification.Warning(notificationService, "還原成功");
                await ReloadAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception while restoring team.");
                ViewNotification.UnexpectedError(notificationService, "還原團隊時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
        }

        async Task OnPurgeAsync(TeamAdapterModel record)
        {
            try
            {
                var ok = await ConfirmDialog.AskDestructiveAsync(
                    modalService,
                    "永久刪除",
                    $"永久刪除「{record.Name}」後無法復原，使用者與這個團隊的關聯也會一併刪除。確定要永久刪除嗎？",
                    "永久刪除");
                if (!ok)
                {
                    return;
                }

                var result = await teamService.PurgeAsync(record.Id);
                if (!result.Success)
                {
                    logger.LogInformation("Team purge rejected. TeamId={TeamId}, Message={Message}", record.Id, result.Message);
                    ViewNotification.Error(notificationService, result.Message);
                    return;
                }

                logger.LogInformation("Team purge completed. TeamId={TeamId}", record.Id);
                ViewNotification.Warning(notificationService, "已永久刪除");
                await ReloadAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception while purging team.");
                ViewNotification.UnexpectedError(notificationService, "永久刪除團隊時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
        }

        async Task OnAddAsync()
        {
            CurrentRecord = new();
            isNewRecordMode = true;
            modalTitle = "新增團隊";
            parentCandidates = await teamService.GetParentCandidatesAsync(0);
            // ⚠️ 必須是開窗前的最後一步：任何預設值都要先塞完，否則會被當成使用者的變更。
            dirtyTracker.Capture(CurrentRecord);

            modalVisible = true;
            logger.LogInformation("Opened create modal for team.");
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
                "儲存團隊時發生未預期的錯誤，請稍後再試或聯絡系統管理員。");
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
                    logger.LogInformation("Team form validation failed. Error={Error}", error);
                    ViewNotification.ValidationError(notificationService, error);
                }

                return false;
            }

            // 一個字都沒改就按儲存：不值得白寫一筆，也不該白跳一次確認窗。
            if (isNewRecordMode == false && dirtyTracker.IsDirty(CurrentRecord) == false)
            {
                logger.LogDebug("Team save skipped because nothing changed. TeamId={TeamId}", CurrentRecord.Id);
                ViewNotification.Info(notificationService, "沒有任何變更，未進行儲存。");

                return true;
            }

            // 儲存前的二次確認。排在資料庫前置檢查之前：使用者若選「再檢查」，
            // 就不必白跑一次資料庫來回。
            if (await FormEditConfirm.AskSaveAsync(modalService) == false)
            {
                logger.LogDebug("Team save cancelled at save confirmation. TeamId={TeamId}", CurrentRecord.Id);
                return false;
            }

            if (isNewRecordMode)
            {
                var beforeAddCheckResult = await teamService.BeforeAddCheckAsync(CurrentRecord);
                if (!beforeAddCheckResult.Success)
                {
                    logger.LogInformation("Team create pre-check failed. Name={Name}, Message={Message}", CurrentRecord.Name, beforeAddCheckResult.Message);
                    ViewNotification.Error(notificationService, beforeAddCheckResult.Message);

                    return false;
                }

                CurrentRecord.CreatedAt = DateTime.Now;
                CurrentRecord.UpdatedAt = DateTime.Now;

                var actionResult = await teamService.AddAsync(CurrentRecord);
                logger.LogInformation("Team create submitted. Name={Name}", CurrentRecord.Name);

                // 前置檢查通過不代表寫得進去：唯一索引在並發時仍會擋下，
                // 忽略這個回傳值會讓失敗的儲存顯示成「新增成功」。
                if (!actionResult.Success)
                {
                    ViewNotification.Error(notificationService, actionResult.Message);

                    return false;
                }

                ViewNotification.Warning(notificationService, "新增成功");

                _ = messageService.SuccessAsync("新增成功");
            }
            else
            {
                var beforeUpdateCheckResult = await teamService.BeforeUpdateCheckAsync(CurrentRecord);
                if (!beforeUpdateCheckResult.Success)
                {
                    logger.LogInformation("Team update pre-check failed. TeamId={TeamId}, Message={Message}", CurrentRecord.Id, beforeUpdateCheckResult.Message);
                    ViewNotification.Error(notificationService, beforeUpdateCheckResult.Message);

                    return false;
                }

                CurrentRecord.UpdatedAt = DateTime.Now;
                var actionResult = await teamService.UpdateAsync(CurrentRecord);
                logger.LogInformation("Team update submitted. TeamId={TeamId}, Name={Name}", CurrentRecord.Id, CurrentRecord.Name);

                if (!actionResult.Success)
                {
                    ViewNotification.Error(notificationService, actionResult.Message);

                    return false;
                }

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

            if (modalVisible == false)
            {
                dirtyTracker.Clear();
                logger.LogDebug("Team modal cancelled.");
            }
        }

        public void OnEditContestChanged(EditContext context)
        {
            LocalEditContext = context;
        }
    }
}

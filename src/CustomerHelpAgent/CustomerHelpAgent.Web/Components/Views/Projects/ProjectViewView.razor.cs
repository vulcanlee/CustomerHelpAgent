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

namespace CustomerHelpAgent.Web.Components.Views.Projects;

public partial class ProjectViewView
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
            var outcome = await BusinessExports.BuildAsync(r => showDeleted ? projectService.GetDeletedAsync(r) : projectService.GetAsync(r), new DataRequest
            {
                Search = searchText,
                SortField = sortField,
                SortDescending = sortDirection == "Descending" ? true : sortDirection == "Ascending" ? false : (bool?)null,
                CategoryFilters = selectedCategoryFilters.ToList(),
                TeamFilters = selectedTeamFilters.ToList(),
            }, "專案", "projects", BusinessExports.ProjectColumns);
            if (outcome.Error is { } error)
            {
                ViewNotification.Caution(notificationService, error);
                return;
            }

            await JSRuntime.DownloadAsync(outcome.FileName!, outcome.Content!, TabularExport.XlsxContentType);
            logger.LogInformation("Project export downloaded. Rows={Rows}", outcome.Rows);
            await BusinessExports.WriteAuditAsync(AuditLogService, CurrentUserService, AuditActions.Project.Export, "Project", outcome.Rows, showDeleted);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Project export failed.");
            ViewNotification.UnexpectedError(notificationService, $"匯出失敗：{ex.GetType().Name}。", ex);
        }
    }

    private readonly ILogger<ProjectViewView> logger;
    private readonly ProjectService projectService;
    private readonly CategoryService categoryService;
    private readonly TeamService teamService;
    private readonly ModalService modalService;
    private readonly MessageService messageService;
    private readonly NotificationService notificationService;
    private readonly IRecordAccessScopeProvider accessScope;
    private ITable? table;

    /// <summary>目前使用者的有效團隊；新增專案時用來預填團隊欄位。</summary>
    private RecordAccessScope currentScope = new(false, []);

    private List<string> availableCategories = [];
    private List<string> availableTeams = [];

    /// <summary>
    /// 表單「團隊」的選項（0.9.105 起）：管理員是全部；其他人只有自己範圍內的團隊（含下屬部門），
    /// 加上這筆原本就有的（才看得到、也能保留）。伺服器端 <c>RecordTeamScope.CheckAssignment</c> 會再檢查一次。
    /// </summary>
    private IEnumerable<string> AssignableTeams => currentScope.IsAdmin
        ? availableTeams
        : availableTeams.Where(t => currentScope.Teams.Contains(t, StringComparer.OrdinalIgnoreCase) || CurrentRecord.Teams.Contains(t, StringComparer.OrdinalIgnoreCase));
    private List<string> selectedCategoryFilters = [];
    private List<string> selectedTeamFilters = [];

    /// <summary>編輯 Modal 的分類選項；與工具列過濾器不同，會額外帶上這筆紀錄的既有值。</summary>
    private List<string> modalCategoryOptions = [];

    /// <summary>這筆紀錄已貼、但目前使用者看不到的分類（顯示時加註，避免被誤認為可自由選取）。</summary>
    private readonly HashSet<string> restrictedCategoryValues = new(StringComparer.OrdinalIgnoreCase);
    private int _pageIndex = 1;
    private int _pageSize = MagicObjectHelper.PageSize;
    private int _total;
    private string searchText = string.Empty;
    private string sortField = string.Empty;
    private string sortDirection = "None";

    private List<ProjectAdapterModel> projectAdapterModels = [];
    private IEnumerable<ProjectAdapterModel> selectedRows = [];
    private bool batchDeleting;
    private readonly List<PendingUploadFileItem> pendingUploadFiles = [];
    private readonly HashSet<int> removedFileIds = [];

    private string modalTitle = "專案維護";
    private bool modalVisible;
    private ProjectAdapterModel CurrentRecord = new();
    public EditContext? LocalEditContext { get; set; }
    private bool isNewRecordMode;
    private string RoleMessage = string.Empty;
    private bool isAccessChecked;

    /// <summary>
    /// 未儲存變更偵測。開窗時 Capture、按取消／儲存時比對，
    /// 「改了又改回原值」視為無變更，不會白問使用者一次。
    /// </summary>
    private readonly FormDirtyTracker dirtyTracker = new();

    private IReadOnlyList<string> StatusOptions => ProjectAdapterModel.StatusOptions;
    private IReadOnlyList<string> PriorityOptions => ProjectAdapterModel.PriorityOptions;

    [Inject]
    public AuthenticationStateHelper AuthenticationStateHelper { get; set; } = default!;

    [Inject]
    public AuthenticationStateProvider authStateProvider { get; set; } = default!;

    [Inject]
    public NavigationManager NavigationManager { get; set; } = default!;

    [Inject]
    public ITeamTreeCache TeamTreeCache { get; set; } = default!;

    /// <summary>新增時預設帶入的團隊：範圍裡最上層的那些（0.9.105 起範圍含下屬部門，全帶會讓專案對所有下屬公開）。</summary>
    private List<string> defaultTeams = [];

    public ProjectViewView(
        ILogger<ProjectViewView> logger,
        ProjectService projectService,
        CategoryService categoryService,
        TeamService teamService,
        ModalService modalService,
        MessageService messageService,
        NotificationService notificationService,
        IRecordAccessScopeProvider accessScope)
    {
        this.logger = logger;
        this.projectService = projectService;
        this.categoryService = categoryService;
        this.teamService = teamService;
        this.modalService = modalService;
        this.messageService = messageService;
        this.notificationService = notificationService;
        this.accessScope = accessScope;
    }

    /// <summary>「資料保留與清理」頁的「前往」帶 <c>?deleted=1</c> 進來時為 true：一進頁面就顯示已刪除的資料（0.9.133 起）。</summary>
    [Parameter]
    public bool InitialShowDeleted { get; set; }

    protected override async Task OnInitializedAsync()
    {
        logger.LogDebug("Initializing project management view.");
        var checkResult = await AuthenticationStateHelper.Check(authStateProvider, NavigationManager);
        if (checkResult != AuthenticationCheckResult.Succeeded)
        {
            logger.LogWarning("Project management view initialization stopped because authentication check failed.");
            return;
        }

        isAccessChecked = true;

        if (AuthenticationStateHelper.CheckAccessPage(MagicObjectHelper.角色_專案項目) == false)
        {
            RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
            await AuthenticationStateHelper.RecordPageAccessDeniedAsync("/projects");
            logger.LogWarning("Project management view denied because current user has not this role permission.");
            return;
        }

        availableCategories = await categoryService.GetAllEnabledNamesAsync();
        availableTeams = await teamService.GetAllEnabledNamesAsync();
        currentScope = await accessScope.GetAsync();
        var tree = await TeamTreeCache.GetAsync();
        defaultTeams = currentScope.Teams
            .Where(t => !tree.AncestorsAndSelf(t).Skip(1).Any(a => currentScope.Teams.Contains(a, StringComparer.OrdinalIgnoreCase)))
            .ToList();

        // 只有看得到「顯示已刪除」按鈕的人（刪除權限）才套用。
        showDeleted = InitialShowDeleted && AuthenticationStateHelper.CheckAccessAction(MagicObjectHelper.角色_專案項目, PermissionActions.Delete);
        await ReloadAsync();
    }

    public async Task ReloadAsync()
    {
        // 換頁、排序、搜尋、篩選、切換與刪除後都會走到這裡：勾選只在目前這一頁有效（0.9.132 起）。
        selectedRows = [];
        logger.LogDebug(
            "Reloading projects. HasSearch={HasSearch}, SearchLength={SearchLength}, SortField={SortField}, SortDirection={SortDirection}, PageIndex={PageIndex}, PageSize={PageSize}",
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
            CategoryFilters = selectedCategoryFilters.ToList(),
            TeamFilters = selectedTeamFilters.ToList(),
        };

        // 「顯示已刪除」開啟時改讀已刪除的資料（0.9.94 起）。
        DataRequestResult<ProjectAdapterModel> dataRequestResult = showDeleted
            ? await projectService.GetDeletedAsync(dataRequest)
            : await projectService.GetAsync(dataRequest);

        projectAdapterModels = dataRequestResult.Result.ToList();
        _total = dataRequestResult.Count;
        logger.LogDebug("Project list reloaded successfully. Count={Count}", _total);
        StateHasChanged();
    }

    private async Task OnCategoryFilterChanged(IEnumerable<string> values)
    {
        selectedCategoryFilters = values?.ToList() ?? [];
        _pageIndex = 1;
        await ReloadAsync();
    }

    private async Task OnTeamFilterChanged(IEnumerable<string> values)
    {
        selectedTeamFilters = values?.ToList() ?? [];
        _pageIndex = 1;
        await ReloadAsync();
    }

    private void OnRecordCategoriesChanged(IEnumerable<string> values)
    {
        CurrentRecord.Categories = values?.ToList() ?? [];
    }

    /// <summary>
    /// 編輯 Modal 的分類選項 = 目前可見分類 ∪ 這筆紀錄已貼但已限定其他團隊的分類。
    /// 後者若不列出，AntDesign 的多選 Select 會把它視為未知值，使用者一存檔就被靜默清掉。
    /// </summary>
    private void BuildModalCategoryOptions()
    {
        restrictedCategoryValues.Clear();

        var options = new List<string>(availableCategories);
        var visible = new HashSet<string>(availableCategories, StringComparer.OrdinalIgnoreCase);

        foreach (var category in CurrentRecord.Categories.Where(x => visible.Contains(x) == false))
        {
            options.Add(category);
            restrictedCategoryValues.Add(category);
        }

        modalCategoryOptions = options;
    }

    /// <summary>選項顯示文字；Value 一律維持原始名稱，只有顯示加註。</summary>
    private string CategoryOptionLabel(string name)
        => restrictedCategoryValues.Contains(name) ? $"{name}（已限定其他團隊）" : name;

    private void OnRecordTeamsChanged(IEnumerable<string> values)
    {
        CurrentRecord.Teams = values?.ToList() ?? [];
    }

    private async Task OnTableChange(QueryModel<ProjectAdapterModel> args)
    {
        _pageIndex = args.PageIndex;

        if (args.SortModel?.Any() == true)
        {
            var tableSortModel = TableSortHelper.GetCurrentSortModel(args.SortModel);
            sortDirection = tableSortModel.SortDirection.ToString() ?? string.Empty;
            sortField = TableSortHelper.ResolveSortFieldName(tableSortModel);
        }
        else
        {
            sortField = string.Empty;
            sortDirection = "None";
        }

        logger.LogDebug("Project table changed. PageIndex={PageIndex}, SortField={SortField}, SortDirection={SortDirection}", _pageIndex, sortField, sortDirection);
        await ReloadAsync();
    }


    private async Task OnSearchAsync()
    {
        _pageIndex = 1;
        logger.LogInformation("Project search triggered. HasSearch={HasSearch}, SearchLength={SearchLength}", string.IsNullOrWhiteSpace(searchText) == false, searchText.Length);
        await ReloadAsync();
        await ViewAudit.WriteSearchAsync(AuditLogService, CurrentUserService, AuditActions.Project.Search, "Project",
            AuditSearchCondition.Text("keyword", searchText), AuditSearchCondition.Of("categories", selectedCategoryFilters), AuditSearchCondition.Of("teams", selectedTeamFilters), AuditSearchCondition.Of("showDeleted", showDeleted));
    }

    private async Task OnRefreshAsync()
    {
        logger.LogInformation("Project refresh triggered.");
        await ReloadAsync();

        ViewNotification.Warning(notificationService, "已更新最新資料");
    }

    private async Task OnEditAsync(ProjectAdapterModel projectAdapterModel)
    {
        isNewRecordMode = false;
        modalTitle = "修改專案";
        // 與其他檢視一致：編輯前一律 Clone() 隔離，避免雙向綁定污染來源資料。
        // 本檢視另有 RemoveExistingFile 會直接 mutate Files 集合，更需要隔離。
        CurrentRecord = (await projectService.GetAsync(projectAdapterModel.Id)).Clone();
        pendingUploadFiles.Clear();
        removedFileIds.Clear();
        BuildModalCategoryOptions();

        // ⚠️ 必須是開窗前的最後一步：任何預設值與關聯資料都要先塞完。
        //    第二個參數是檔案指紋 —— 待上傳與待刪除的檔案不在 ProjectAdapterModel 裡，
        //    不納入比對的話，「只加了檔案、沒動欄位」會被判定為無變更而直接關窗。
        dirtyTracker.Capture(CurrentRecord, UploadStateFingerprint);

        modalVisible = true;
        logger.LogInformation("Opened edit modal for project. ProjectId={ProjectId}, Title={Title}", projectAdapterModel.Id, projectAdapterModel.Title);
        await ViewAudit.WriteAsync(AuditLogService, CurrentUserService, AuditActions.Project.View, "Project",
            projectAdapterModel.Id.ToString(), $"title={projectAdapterModel.Title}");
    }

    /// <summary>
    /// 包住實際邏輯以捕捉未預期的例外：先前這些寫入操作完全沒有 try/catch，
    /// 例外會直接拆掉 Blazor circuit，使用者只看到畫面斷線、日誌上也留不下任何痕跡。
    /// </summary>
    private async Task OnDeleteAsync(ProjectAdapterModel projectAdapterModel)
    {
        try
        {
            await OnDeleteCoreAsync(projectAdapterModel);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception while deleting project.");
            ViewNotification.UnexpectedError(notificationService, "刪除專案時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
        }
    }

    private async Task OnDeleteCoreAsync(ProjectAdapterModel projectAdapterModel)
    {
        logger.LogInformation("Delete project requested. ProjectId={ProjectId}, Title={Title}", projectAdapterModel.Id, projectAdapterModel.Title);

        var beforeDeleteCheckResult = await projectService.BeforeDeleteCheckAsync(projectAdapterModel);
        if (!beforeDeleteCheckResult.Success)
        {
            logger.LogInformation("Project delete pre-check failed. ProjectId={ProjectId}, Message={Message}", projectAdapterModel.Id, beforeDeleteCheckResult.Message);
            ViewNotification.Error(notificationService, beforeDeleteCheckResult.Message);
            return;
        }

        var ok = await ConfirmDialog.AskSoftDeleteRecordAsync(modalService);

        if (!ok)
        {
            logger.LogDebug("Project delete cancelled by user. ProjectId={ProjectId}", projectAdapterModel.Id);
            return;
        }

        var result = await projectService.DeleteAsync(projectAdapterModel.Id);
        if (!result.Success)
        {
            // 0.9.93 之前這裡不看結果，失敗也顯示「刪除成功」。
            logger.LogInformation("Project delete rejected. ProjectId={ProjectId}, Message={Message}", projectAdapterModel.Id, result.Message);
            ViewNotification.Error(notificationService, result.Message);
            return;
        }

        logger.LogInformation("Project delete completed. ProjectId={ProjectId}", projectAdapterModel.Id);

        ViewNotification.Warning(notificationService, "刪除成功");

        await ReloadAsync();
    }

    /// <summary>
    /// 「刪除選取」（0.9.132 起）：每筆同單筆刪除，先 <see cref="ProjectService.BeforeDeleteCheckAsync"/> 再刪，
    /// 規則與稽核都沿用。
    /// </summary>
    private async Task OnBatchDeleteAsync()
    {
        try
        {
            var items = selectedRows.ToList();
            if (items.Count == 0 || !await BatchDelete.AskAsync(modalService, items.Select(x => x.Title).ToList(), restorable: true))
            {
                return;
            }

            batchDeleting = true;
            var outcome = await BatchDelete.RunAsync(items, x => x.Id.ToString(), x => x.Title, async x =>
            {
                var check = await projectService.BeforeDeleteCheckAsync(x);
                return check.Success ? await projectService.DeleteAsync(x.Id) : check;
            }, logger);
            BatchDelete.Notify(notificationService, outcome);
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception while batch deleting projects.");
            ViewNotification.UnexpectedError(notificationService, "刪除專案時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
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

    async Task OnRestoreAsync(ProjectAdapterModel record)
    {
        try
        {
            var ok = await ConfirmDialog.AskAsync(modalService, "確認還原", $"要還原「{record.Title}」嗎？", "還原");
            if (!ok)
            {
                return;
            }

            var result = await projectService.RestoreAsync(record.Id);
            if (!result.Success)
            {
                logger.LogInformation("Project restore rejected. ProjectId={ProjectId}, Message={Message}", record.Id, result.Message);
                ViewNotification.Error(notificationService, result.Message);
                return;
            }

            logger.LogInformation("Project restore completed. ProjectId={ProjectId}", record.Id);
            ViewNotification.Warning(notificationService, "還原成功");
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception while restoring project.");
            ViewNotification.UnexpectedError(notificationService, "還原專案時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
        }
    }

    async Task OnPurgeAsync(ProjectAdapterModel record)
    {
        try
        {
            var ok = await ConfirmDialog.AskDestructiveAsync(
                modalService,
                "永久刪除",
                $"永久刪除「{record.Title}」後無法復原，附件檔案也會一併刪除。確定要永久刪除嗎？",
                "永久刪除");
            if (!ok)
            {
                return;
            }

            var result = await projectService.PurgeAsync(record.Id);
            if (!result.Success)
            {
                logger.LogInformation("Project purge rejected. ProjectId={ProjectId}, Message={Message}", record.Id, result.Message);
                ViewNotification.Error(notificationService, result.Message);
                return;
            }

            logger.LogInformation("Project purge completed. ProjectId={ProjectId}", record.Id);
            ViewNotification.Warning(notificationService, "已永久刪除");
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception while purging project.");
            ViewNotification.UnexpectedError(notificationService, "永久刪除專案時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
        }
    }

    private Task OnAddAsync(bool continueOnCapturedContext)
    {
        CurrentRecord = new ProjectAdapterModel
        {
            Status = StatusOptions.First(),
            Priority = PriorityOptions[1],
            CompletionPercentage = 0,
            Files = [],
            // 預設帶入使用者範圍裡最上層的團隊；已停用（不在選項內）的略過，否則多選 Select 會視為未知值。
            Teams = defaultTeams
                .Where(t => availableTeams.Contains(t, StringComparer.OrdinalIgnoreCase))
                .ToList()
        };

        pendingUploadFiles.Clear();
        removedFileIds.Clear();
        BuildModalCategoryOptions();
        isNewRecordMode = true;
        modalTitle = "新增專案";

        // ⚠️ 必須是開窗前的最後一步；第二個參數見 OnEditAsync 的說明。
        dirtyTracker.Capture(CurrentRecord, UploadStateFingerprint);

        modalVisible = true;
        logger.LogInformation("Opened create modal for project.");
        return Task.CompletedTask;
    }

    private async Task OnProjectFilesSelectedAsync(InputFileChangeEventArgs args)
    {
        foreach (var file in args.GetMultipleFiles(1000))
        {
            if (file.Size > ProjectService.MaxUploadFileSize)
            {
                ViewNotification.Error(notificationService, $"{file.Name} 超過 1GB 限制");
                continue;
            }

            pendingUploadFiles.Add(new PendingUploadFileItem
            {
                Id = Guid.NewGuid(),
                File = file
            });
        }

        await InvokeAsync(StateHasChanged);
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
            "儲存專案時發生未預期的錯誤，請稍後再試或聯絡系統管理員。");
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
                logger.LogInformation("Project form validation failed. Error={Error}", error);
                ViewNotification.ValidationError(notificationService, error);
            }

            return false;
        }

        // 一個字都沒改就按儲存：不值得白寫一筆，也不該白跳一次確認窗。
        if (isNewRecordMode == false && dirtyTracker.IsDirty(CurrentRecord) == false)
        {
            logger.LogDebug("Project save skipped because nothing changed. ProjectId={ProjectId}", CurrentRecord.Id);
            ViewNotification.Info(notificationService, "沒有任何變更，未進行儲存。");

            return true;
        }

        // 儲存前的二次確認。排在檔案讀取與資料庫前置檢查之前：
        // 使用者若選「再檢查」，就不必白讀一次檔案、白跑一次資料庫來回。
        if (await FormEditConfirm.AskSaveAsync(modalService) == false)
        {
            logger.LogDebug("Project save cancelled at save confirmation. ProjectId={ProjectId}", CurrentRecord.Id);
            return false;
        }

        if (CurrentRecord.Teams.Count == 0
            && await TeamBindingConfirm.AskAsync(
                modalService,
                "此專案未指定團隊，將對所有使用者公開可見。確定要這樣儲存嗎？") == false)
        {
            logger.LogDebug("Project save cancelled at team confirmation. ProjectId={ProjectId}", CurrentRecord.Id);

            // 保持 Modal 開啟，讓使用者回到原本的編輯內容重新指定團隊。
            return false;
        }

        var uploadInputs = new List<ProjectUploadFileInput>();
        var uploadStreams = new List<Stream>();

        try
        {
            foreach (var pendingUploadFile in pendingUploadFiles)
            {
                var stream = pendingUploadFile.File.OpenReadStream(ProjectService.MaxUploadFileSize);
                uploadStreams.Add(stream);
                uploadInputs.Add(new ProjectUploadFileInput
                {
                    FileName = pendingUploadFile.File.Name,
                    ContentType = pendingUploadFile.File.ContentType,
                    FileSize = pendingUploadFile.File.Size,
                    Content = stream
                });
            }

            VerifyRecordResult actionResult;

            if (isNewRecordMode)
            {
                var beforeAddCheckResult = await projectService.BeforeAddCheckAsync(CurrentRecord, uploadInputs);
                if (!beforeAddCheckResult.Success)
                {
                    logger.LogInformation("Project create pre-check failed. Title={Title}, Message={Message}", CurrentRecord.Title, beforeAddCheckResult.Message);
                    ViewNotification.Error(notificationService, beforeAddCheckResult.Message);

                    return false;
                }

                CurrentRecord.CreatedAt = DateTime.Now;
                CurrentRecord.UpdatedAt = DateTime.Now;

                actionResult = await projectService.AddAsync(CurrentRecord, uploadInputs);
                logger.LogInformation("Project create submitted. Title={Title}", CurrentRecord.Title);
            }
            else
            {
                var beforeUpdateCheckResult = await projectService.BeforeUpdateCheckAsync(CurrentRecord, uploadInputs);
                if (!beforeUpdateCheckResult.Success)
                {
                    logger.LogInformation("Project update pre-check failed. ProjectId={ProjectId}, Message={Message}", CurrentRecord.Id, beforeUpdateCheckResult.Message);
                    ViewNotification.Error(notificationService, beforeUpdateCheckResult.Message);

                    return false;
                }

                CurrentRecord.UpdatedAt = DateTime.Now;
                actionResult = await projectService.UpdateAsync(CurrentRecord, uploadInputs, removedFileIds);
                logger.LogInformation("Project update submitted. ProjectId={ProjectId}, Title={Title}", CurrentRecord.Id, CurrentRecord.Title);
            }

            if (!actionResult.Success)
            {
                ViewNotification.Error(notificationService, actionResult.Message);

                return false;
            }

            pendingUploadFiles.Clear();
            removedFileIds.Clear();

            ViewNotification.Warning(notificationService, isNewRecordMode ? "新增成功" : "修改成功");

            if (isNewRecordMode)
            {
                _ = messageService.SuccessAsync("新增成功");
            }

            await ReloadAsync();
            dirtyTracker.Clear();

            return true;
        }
        finally
        {
            foreach (var uploadStream in uploadStreams)
            {
                uploadStream.Dispose();
            }
        }
    }

    /// <summary>
    /// 取消／✕／ESC 的共用出口（遮罩已由 MaskClosable="false" 擋掉，不會走到這裡）。
    /// 有未儲存變更時先問過使用者；無變更直接關閉，不打擾。
    ///
    /// ⚠️ 第一行的 modalVisible = true 不可移動：AntDesign 呼叫本方法前已送出
    /// VisibleChanged(false)，要讓「繼續編輯」留住整窗輸入就得在這裡搶回來。
    /// ⚠️ args 可能是 null（ESC／✕ 走 Config.OnCancel.Invoke(null)），不要解參考它。
    /// ⚠️ 待上傳／待刪除的檔案只能在**確定要關窗之後**才清掉，
    ///    否則使用者選「繼續編輯」會發現選好的檔案不見了。
    /// </summary>
    private async Task OnModalCancelHandleAsync(MouseEventArgs args)
    {
        modalVisible = true;
        modalVisible = await FormModalFlow.ConfirmCloseAsync(modalService, dirtyTracker.IsDirty(CurrentRecord));

        if (modalVisible)
        {
            return;
        }

        pendingUploadFiles.Clear();
        removedFileIds.Clear();
        dirtyTracker.Clear();
        logger.LogDebug("Project modal cancelled.");
    }

    /// <summary>
    /// 待上傳與待刪除檔案的指紋。這些狀態不在 ProjectAdapterModel 裡，
    /// 但使用者會認為「我剛加了一個檔案」就是變更，因此一併納入比對。
    /// ⚠️ 不能直接把 IBrowserFile 丟給 FormDirtyTracker —— 它不可序列化。
    /// </summary>
    private string UploadStateFingerprint()
        => string.Join('|', pendingUploadFiles.Select(x => $"{x.File.Name}:{x.File.Size}"))
           + "#"
           + string.Join(',', removedFileIds.OrderBy(x => x));

    public void OnEditContestChanged(EditContext context)
    {
        LocalEditContext = context;
    }

    private void RemovePendingFile(Guid fileId)
    {
        var file = pendingUploadFiles.FirstOrDefault(x => x.Id == fileId);
        if (file is not null)
        {
            pendingUploadFiles.Remove(file);
        }
    }

    private void RemoveExistingFile(int fileId)
    {
        var file = CurrentRecord.Files.FirstOrDefault(x => x.Id == fileId);
        if (file is null)
        {
            return;
        }

        removedFileIds.Add(fileId);
        CurrentRecord.Files.Remove(file);
    }

    private static string GetProjectFileDownloadUrl(int fileId)
    {
        return $"/api/project-files/{fileId}/download";
    }

    private static string FormatFileSize(long fileSize)
        => SizeFormatHelper.FormatBytes(fileSize);

    /// <summary>
    /// 清單的狀態徽章語氣。對應表刻意留在呼叫端 ——
    /// <see cref="StatusPill"/> 不該認得「暫緩」這種只有本模組才懂的字。
    /// </summary>
    /// <remarks>
    /// 合法值由 <see cref="ProjectAdapterModel.StatusOptions"/> 界定、服務層把關，
    /// 因此預設分支只會接到舊資料，不是正常路徑。
    /// </remarks>
    private static StatusTone ResolveStatusTone(string status) => status switch
    {
        "已完成" => StatusTone.Positive,
        "進行中" => StatusTone.Accent,
        "暫緩" => StatusTone.Warning,
        "未開始" => StatusTone.Muted,
        _ => StatusTone.Neutral,
    };

    private sealed class PendingUploadFileItem
    {
        public Guid Id { get; set; }

        public IBrowserFile File { get; set; } = default!;
    }
}

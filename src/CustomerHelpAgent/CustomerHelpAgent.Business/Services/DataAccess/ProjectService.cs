using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Factories;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.AdapterModel;
using CustomerHelpAgent.Models.Systems;

namespace CustomerHelpAgent.Business.Services.DataAccess;

public class ProjectService
{
    public const long MaxUploadFileSize = 1024L * 1024L * 1024L;

    private readonly IDbContextFactory<BackendDBContext> contextFactory;
    private readonly IRecordAccessScopeProvider accessScope;
    private readonly string projectFileRootPath;
    private readonly ProjectFileStore fileStore;
    private readonly IReadOnlyCollection<string> allowedUploadExtensions;
    private readonly IAuditLogService auditLogService;
    private readonly CurrentUserService currentUserService;

    public IMapper Mapper { get; }
    public ILogger<ProjectService> Logger { get; }

    public ProjectService(
        IDbContextFactory<BackendDBContext> contextFactory,
        IMapper mapper,
        ILogger<ProjectService> logger,
        IOptions<SystemSettings> systemSettings,
        IRecordAccessScopeProvider accessScope,
        IAuditLogService auditLogService,
        CurrentUserService currentUserService,
        ProjectFileStore fileStore)
    {
        this.fileStore = fileStore;
        this.contextFactory = contextFactory;
        Mapper = mapper;
        Logger = logger;
        this.accessScope = accessScope;
        this.auditLogService = auditLogService;
        this.currentUserService = currentUserService;
        projectFileRootPath = systemSettings.Value.ExternalFileSystem.ProjectFilePath;
        allowedUploadExtensions = systemSettings.Value.Upload.AllowedExtensions;
    }

    public async Task<DataRequestResult<ProjectAdapterModel>> GetAsync(DataRequest dataRequest)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogDebug(
            "Loading projects. HasSearch={HasSearch}, SearchLength={SearchLength}, SortField={SortField}, SortDescending={SortDescending}, CurrentPage={CurrentPage}, PageSize={PageSize}, Take={Take}",
            string.IsNullOrWhiteSpace(dataRequest.Search) == false,
            dataRequest.Search?.Length ?? 0,
            dataRequest.SortField,
            dataRequest.SortDescending,
            dataRequest.CurrentPage,
            dataRequest.PageSize,
            dataRequest.Take);

        DataRequestResult<ProjectAdapterModel> result = new();
        IQueryable<Project> dataSource = context.Project.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(dataRequest.Search))
        {
            var search = dataRequest.Search.Trim();
            dataSource = dataSource.Where(x =>
                x.Title.Contains(search) ||
                (x.Description ?? string.Empty).Contains(search) ||
                x.Status.Contains(search) ||
                x.Priority.Contains(search) ||
                x.Owner.Contains(search));
        }

        if (dataRequest.CategoryFilters.Count > 0)
        {
            dataSource = dataSource.Where(TagStringHelper.BuildContainsAnyPredicate<Project>(x => x.Categories, dataRequest.CategoryFilters));
        }

        if (dataRequest.TeamFilters.Count > 0)
        {
            dataSource = dataSource.Where(TagStringHelper.BuildContainsAnyPredicate<Project>(x => x.Teams, dataRequest.TeamFilters));
        }

        var scope = await accessScope.GetAsync();
        dataSource = RecordTeamScope.Apply(dataSource, x => x.Teams, scope);

        IOrderedQueryable<Project>? sorted = null;

        if (!string.IsNullOrWhiteSpace(dataRequest.SortField))
        {
            if (dataRequest.SortField == nameof(ProjectAdapterModel.Title))
            {
                sorted = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.Title).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.Title).ThenBy(x => x.Id)
                        : null;
            }
            else if (dataRequest.SortField == nameof(ProjectAdapterModel.StartDate))
            {
                sorted = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.StartDate).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.StartDate).ThenBy(x => x.Id)
                        : null;
            }
            else if (dataRequest.SortField == nameof(ProjectAdapterModel.EndDate))
            {
                sorted = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.EndDate).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.EndDate).ThenBy(x => x.Id)
                        : null;
            }
            else if (dataRequest.SortField == nameof(ProjectAdapterModel.Status))
            {
                sorted = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.Status).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.Status).ThenBy(x => x.Id)
                        : null;
            }
            else if (dataRequest.SortField == nameof(ProjectAdapterModel.Priority))
            {
                sorted = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.Priority).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.Priority).ThenBy(x => x.Id)
                        : null;
            }
            else if (dataRequest.SortField == nameof(ProjectAdapterModel.CompletionPercentage))
            {
                sorted = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.CompletionPercentage).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.CompletionPercentage).ThenBy(x => x.Id)
                        : null;
            }
            else if (dataRequest.SortField == nameof(ProjectAdapterModel.Owner))
            {
                sorted = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.Owner).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.Owner).ThenBy(x => x.Id)
                        : null;
            }
            else if (dataRequest.SortField == nameof(ProjectAdapterModel.CreatedAt))
            {
                sorted = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
                        : null;
            }
            else if (dataRequest.SortField == nameof(ProjectAdapterModel.UpdatedAt))
            {
                sorted = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.UpdatedAt).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.UpdatedAt).ThenBy(x => x.Id)
                        : null;
            }
        }

        // Skip/Take 一定要搭配 OrderBy，否則 SQLite 不保證回傳順序，分頁會重複或漏資料。
        // 未指定欄位、欄位不認得、方向為 null —— 三種情況一律退回預設排序。
        dataSource = sorted ?? dataSource.OrderByDescending(x => x.UpdatedAt).ThenByDescending(x => x.Id);

        result.Count = await dataSource.CountAsync();
        dataSource = dataSource.Skip((dataRequest.CurrentPage - 1) * dataRequest.PageSize);
        if (dataRequest.Take != 0)
        {
            dataSource = dataSource.Take(dataRequest.PageSize);
        }

        var records = await dataSource.ToListAsync();
        result.Result = Mapper.Map<List<ProjectAdapterModel>>(records);
        Logger.LogDebug("Loaded projects successfully. Count={Count}", result.Count);
        return result;
    }

    public async Task<ProjectAdapterModel> GetAsync(int id)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogDebug("Loading project by id. ProjectId={ProjectId}", id);

        Project? item = await context.Project
            .AsNoTracking()
            .Include(x => x.Files)
            .FirstOrDefaultAsync(x => x.Id == id);

        if (item is null)
        {
            Logger.LogInformation("Project not found. ProjectId={ProjectId}", id);
            return new ProjectAdapterModel();
        }

        var scope = await accessScope.GetAsync();
        if (!RecordTeamScope.CanAccess(item.Teams, scope))
        {
            Logger.LogWarning("Project access denied by team scope. ProjectId={ProjectId}", id);
            return new ProjectAdapterModel();
        }

        return Mapper.Map<ProjectAdapterModel>(item);
    }

    /// <summary>
    /// 寫一筆稽核（LOG-14）。操作者取自 <see cref="CurrentUserService"/>：本服務只由 Blazor 畫面呼叫；
    /// Web API 走 Repository，稽核寫在對應的 Controller。AuditLogService 失敗不拋出。
    /// </summary>
    private Task WriteAuditAsync(string action, int targetId, string detail, string? content = null)
    {
        var user = currentUserService.CurrentUser;
        return auditLogService.WriteAsync(
            action,
            success: true,
            actorUserId: user.Id > 0 ? user.Id : null,
            actorAccount: user.Id > 0 ? user.Account : null,
            targetType: "Project",
            targetId: targetId.ToString(),
            detail: detail,
            content: content);
    }

    public async Task<VerifyRecordResult> AddAsync(ProjectAdapterModel paraObject, IEnumerable<ProjectUploadFileInput>? uploadFiles = null)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogInformation("Creating project. Title={Title}, Owner={Owner}", paraObject.Title, paraObject.Owner);

        try
        {
            // 非管理員只能指定自己範圍內的團隊（0.9.105 起），否則建出來的專案自己也看不到。
            if (RecordTeamScope.CheckAssignment(null, TagStringHelper.ToStored(paraObject.Teams), await accessScope.GetAsync()) is { } teamError)
            {
                Logger.LogInformation("Project create rejected by team assignment rule. Title={Title}", paraObject.Title);
                return VerifyRecordResultFactory.Build(false, teamError);
            }

            Project itemParameter = Mapper.Map<Project>(paraObject);
            itemParameter.Files = [];
            itemParameter.ConcurrencyStamp = ConcurrencyStampHelper.New();

            await context.Project.AddAsync(itemParameter);
            await context.SaveChangesAsync();

            var saveFilesResult = await SaveNewFilesAsync(context, itemParameter, uploadFiles);
            if (!saveFilesResult.Success)
            {
                return saveFilesResult;
            }

            Logger.LogInformation("Project created successfully. ProjectId={ProjectId}, Title={Title}", itemParameter.Id, itemParameter.Title);
            await WriteAuditAsync(AuditActions.Project.Create, itemParameter.Id, $"title={itemParameter.Title}");
            await WriteFileAuditAsync(AuditActions.Project.FileUpload, itemParameter.Id, uploadFiles?.Count() ?? 0);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to create project. Title={Title}", paraObject.Title);
            return VerifyRecordResultFactory.Build(false, "新增專案失敗。", ex);
        }
    }

    public async Task<VerifyRecordResult> UpdateAsync(
        ProjectAdapterModel paraObject,
        IEnumerable<ProjectUploadFileInput>? uploadFiles = null,
        IEnumerable<int>? removedFileIds = null)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogInformation("Updating project. ProjectId={ProjectId}, Title={Title}", paraObject.Id, paraObject.Title);

        try
        {
            Project? currentItem = await context.Project
                .Include(x => x.Files)
                .FirstOrDefaultAsync(x => x.Id == paraObject.Id);

            if (currentItem == null)
            {
                Logger.LogWarning("Project update rejected because record was not found. ProjectId={ProjectId}", paraObject.Id);
                return VerifyRecordResultFactory.Build(false, "找不到要修改的專案資料。");
            }

            // 0.9.105 之前修改不檢查團隊範圍：看不到的專案只要知道 Id 就能改（刪除、還原早已檢查）。
            var scope = await accessScope.GetAsync();
            if (!RecordTeamScope.CanAccess(currentItem.Teams, scope))
            {
                Logger.LogWarning("Project update denied by team scope. ProjectId={ProjectId}", paraObject.Id);
                return VerifyRecordResultFactory.Build(false, "這筆專案不在你的團隊範圍內，無法修改。");
            }

            if (RecordTeamScope.CheckAssignment(currentItem.Teams, TagStringHelper.ToStored(paraObject.Teams), scope) is { } teamError)
            {
                Logger.LogInformation("Project update rejected by team assignment rule. ProjectId={ProjectId}", paraObject.Id);
                return VerifyRecordResultFactory.Build(false, teamError);
            }

            // 修改前的快照（0.9.131 起，給稽核差異用）：要在逐欄指定之前取，之後 currentItem 就是新值了。
            var before = (Project)context.Entry(currentItem).CurrentValues.ToObject();

            currentItem.Title = paraObject.Title;
            currentItem.Description = paraObject.Description;
            currentItem.StartDate = paraObject.StartDate;
            currentItem.EndDate = paraObject.EndDate;
            currentItem.Status = paraObject.Status;
            currentItem.Priority = paraObject.Priority;
            currentItem.CompletionPercentage = paraObject.CompletionPercentage;
            currentItem.Owner = paraObject.Owner;
            currentItem.Categories = TagStringHelper.ToStored(paraObject.Categories);
            currentItem.Teams = TagStringHelper.ToStored(paraObject.Teams);
            currentItem.UpdatedAt = paraObject.UpdatedAt;
            var diff = AuditDiff.Compare(before, currentItem).Render($"專案 #{currentItem.Id}（{before.Title}）修改");

            // 先載入再複製欄位的寫法：一定要以使用者開窗時的版本號比對，否則永遠偵測不到衝突。
            // 衝突時第一次 SaveChanges 就會失敗，附件的新增與刪除都不會執行。
            ConcurrencyStampHelper.Apply(context.Entry(currentItem), paraObject.ConcurrencyStamp);
            SoftDeleteHelper.ProtectFlags(context.Entry(currentItem));
            await context.SaveChangesAsync();

            var saveFilesResult = await SaveNewFilesAsync(context, currentItem, uploadFiles);
            if (!saveFilesResult.Success)
            {
                return saveFilesResult;
            }

            var removeFilesResult = await RemoveProjectFilesAsync(context, currentItem, removedFileIds);
            if (!removeFilesResult.Success)
            {
                return removeFilesResult;
            }

            Logger.LogInformation("Project updated successfully. ProjectId={ProjectId}, Title={Title}", currentItem.Id, currentItem.Title);
            await WriteAuditAsync(AuditActions.Project.Update, currentItem.Id, $"title={currentItem.Title}", diff);
            await WriteFileAuditAsync(AuditActions.Project.FileUpload, currentItem.Id, uploadFiles?.Count() ?? 0);
            await WriteFileAuditAsync(AuditActions.Project.FileDelete, currentItem.Id, removedFileIds?.Count() ?? 0);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // 別人在這段期間先存過或刪除了這筆：使用者情境而非系統錯誤（LOG-11），記 Information、不帶例外物件。
            Logger.LogInformation("Project update rejected by concurrency conflict. ProjectId={ProjectId}", paraObject.Id);
            return VerifyRecordResultFactory.Build(false, ConcurrencyStampHelper.ConflictMessage, ex);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to update project. ProjectId={ProjectId}, Title={Title}", paraObject.Id, paraObject.Title);
            return VerifyRecordResultFactory.Build(false, "修改專案失敗。", ex);
        }
    }

    /// <summary>
    /// 刪除 = 軟刪除（0.9.94 起）：資料與附件實體檔都保留，可在「顯示已刪除」中還原；
    /// 附件在永久刪除（<see cref="PurgeAsync"/>）時才刪。軟刪除期間附件無法下載（見 GetFileDownloadAsync）。
    /// </summary>
    public async Task<VerifyRecordResult> DeleteAsync(int id)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogInformation("Deleting project. ProjectId={ProjectId}", id);

        try
        {
            Project? item = await context.Project.FirstOrDefaultAsync(x => x.Id == id);

            if (item == null)
            {
                Logger.LogWarning("Project deletion rejected because record was not found. ProjectId={ProjectId}", id);
                return VerifyRecordResultFactory.Build(false, "找不到要刪除的專案資料。");
            }

            // 0.9.93 之前刪除完全不檢查團隊範圍；畫面上看不到不代表伺服器端不該擋。
            var scope = await accessScope.GetAsync();
            if (!RecordTeamScope.CanAccess(item.Teams, scope))
            {
                Logger.LogWarning("Project deletion denied by team scope. ProjectId={ProjectId}", id);
                return VerifyRecordResultFactory.Build(false, "這筆專案不在你的團隊範圍內，無法刪除。");
            }

            SoftDeleteHelper.MarkDeleted(item, currentUserService.CurrentUser.Id > 0 ? currentUserService.CurrentUser.Account : null);
            await context.SaveChangesAsync();

            Logger.LogInformation("Project deleted successfully. ProjectId={ProjectId}, Title={Title}", id, item.Title);
            await WriteAuditAsync(AuditActions.Project.Delete, id, $"title={item.Title}");
            return VerifyRecordResultFactory.Build(true);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            Logger.LogInformation("Project deletion rejected by concurrency conflict. ProjectId={ProjectId}", id);
            return VerifyRecordResultFactory.Build(false, ConcurrencyStampHelper.ConflictMessage, ex);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to delete project. ProjectId={ProjectId}", id);
            return VerifyRecordResultFactory.Build(false, "刪除專案失敗。", ex);
        }
    }

    /// <summary>已刪除的專案（「顯示已刪除」清單）。沿用搜尋與團隊範圍，固定依刪除時間由新到舊。</summary>
    public async Task<DataRequestResult<ProjectAdapterModel>> GetDeletedAsync(DataRequest dataRequest)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        IQueryable<Project> dataSource = context.Project
            .IgnoreQueryFilters([ISoftDeletable.FilterName])
            .AsNoTracking()
            .Where(x => x.IsDeleted);

        if (!string.IsNullOrWhiteSpace(dataRequest.Search))
        {
            var search = dataRequest.Search.Trim();
            dataSource = dataSource.Where(x =>
                x.Title.Contains(search) ||
                (x.Description ?? string.Empty).Contains(search) ||
                x.Status.Contains(search) ||
                x.Priority.Contains(search) ||
                x.Owner.Contains(search));
        }

        var scope = await accessScope.GetAsync();
        dataSource = RecordTeamScope.Apply(dataSource, x => x.Teams, scope);

        dataSource = dataSource.OrderByDescending(x => x.DeletedAt).ThenByDescending(x => x.Id);

        var result = new DataRequestResult<ProjectAdapterModel> { Count = await dataSource.CountAsync() };
        dataSource = dataSource.Skip((dataRequest.CurrentPage - 1) * dataRequest.PageSize);
        if (dataRequest.Take != 0)
        {
            dataSource = dataSource.Take(dataRequest.PageSize);
        }

        result.Result = Mapper.Map<List<ProjectAdapterModel>>(await dataSource.ToListAsync());
        return result;
    }

    public async Task<VerifyRecordResult> RestoreAsync(int id)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogInformation("Restoring project. ProjectId={ProjectId}", id);

        try
        {
            Project? item = await context.Project
                .IgnoreQueryFilters([ISoftDeletable.FilterName])
                .FirstOrDefaultAsync(x => x.Id == id && x.IsDeleted);

            if (item == null)
            {
                Logger.LogWarning("Project restore rejected because deleted record was not found. ProjectId={ProjectId}", id);
                return VerifyRecordResultFactory.Build(false, "找不到要還原的專案（可能已被還原或永久刪除）。");
            }

            var scope = await accessScope.GetAsync();
            if (!RecordTeamScope.CanAccess(item.Teams, scope))
            {
                Logger.LogWarning("Project restore denied by team scope. ProjectId={ProjectId}", id);
                return VerifyRecordResultFactory.Build(false, "這筆專案不在你的團隊範圍內，無法還原。");
            }

            SoftDeleteHelper.Restore(item);
            await context.SaveChangesAsync();

            Logger.LogInformation("Project restored successfully. ProjectId={ProjectId}, Title={Title}", id, item.Title);
            await WriteAuditAsync(AuditActions.Project.Restore, id, $"title={item.Title}");
            return VerifyRecordResultFactory.Build(true);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            Logger.LogInformation("Project restore rejected by concurrency conflict. ProjectId={ProjectId}", id);
            return VerifyRecordResultFactory.Build(false, ConcurrencyStampHelper.ConflictMessage, ex);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to restore project. ProjectId={ProjectId}", id);
            return VerifyRecordResultFactory.Build(false, "還原專案失敗。", ex);
        }
    }

    /// <summary>
    /// 永久刪除：只能對已刪除的專案執行，無法復原。
    /// ⚠️ 順序是「先提交資料庫、成功後才刪附件實體檔」：0.9.93 之前是先刪檔再寫資料庫，資料庫失敗時檔案已經不見了。
    /// 刪檔失敗只記警告（資料列已不存在，留下的是孤兒檔，不影響資料正確性）。
    /// </summary>
    public async Task<VerifyRecordResult> PurgeAsync(int id)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogInformation("Purging project. ProjectId={ProjectId}", id);

        try
        {
            Project? item = await context.Project
                .IgnoreQueryFilters([ISoftDeletable.FilterName])
                .Include(x => x.Files)
                .FirstOrDefaultAsync(x => x.Id == id && x.IsDeleted);

            if (item == null)
            {
                Logger.LogWarning("Project purge rejected because deleted record was not found. ProjectId={ProjectId}", id);
                return VerifyRecordResultFactory.Build(false, "找不到要永久刪除的專案（只能永久刪除已刪除的資料）。");
            }

            var scope = await accessScope.GetAsync();
            if (!RecordTeamScope.CanAccess(item.Teams, scope))
            {
                Logger.LogWarning("Project purge denied by team scope. ProjectId={ProjectId}", id);
                return VerifyRecordResultFactory.Build(false, "這筆專案不在你的團隊範圍內，無法永久刪除。");
            }

            var files = item.Files.ToList();
            context.Project.Remove(item);
            await context.SaveChangesAsync();

            foreach (var file in files)
            {
                // 經 ProjectFileStore：路徑跑出附件根目錄時拒絕，刪不掉只記警告（0.9.97 起）。
                fileStore.Delete(file.RelativePath);
            }

            Logger.LogInformation("Project purged successfully. ProjectId={ProjectId}, Title={Title}, Files={FileCount}", id, item.Title, files.Count);
            await WriteAuditAsync(AuditActions.Project.Purge, id, $"title={item.Title}; files={files.Count}");
            return VerifyRecordResultFactory.Build(true);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            Logger.LogInformation("Project purge rejected by concurrency conflict. ProjectId={ProjectId}", id);
            return VerifyRecordResultFactory.Build(false, "這筆專案已被其他人還原或變更，沒有執行永久刪除。", ex);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to purge project. ProjectId={ProjectId}", id);
            return VerifyRecordResultFactory.Build(false, "永久刪除專案失敗。", ex);
        }
    }

    public Task<VerifyRecordResult> BeforeAddCheckAsync(ProjectAdapterModel paraObject, IEnumerable<ProjectUploadFileInput>? uploadFiles = null)
    {
        Logger.LogDebug("Running pre-create validation for project. Title={Title}", paraObject.Title);
        return ValidateBusinessRulesAsync(paraObject, uploadFiles);
    }

    public async Task<VerifyRecordResult> BeforeUpdateCheckAsync(
        ProjectAdapterModel paraObject,
        IEnumerable<ProjectUploadFileInput>? uploadFiles = null)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogDebug("Running pre-update validation for project. ProjectId={ProjectId}, Title={Title}", paraObject.Id, paraObject.Title);

        Project? searchItem = await context.Project
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == paraObject.Id);

        if (searchItem == null)
        {
            Logger.LogInformation("Pre-update validation failed because project was not found. ProjectId={ProjectId}", paraObject.Id);
            return VerifyRecordResultFactory.Build(false, "要修改的專案資料不存在。");
        }

        return await ValidateBusinessRulesAsync(paraObject, uploadFiles);
    }

    public Task<VerifyRecordResult> BeforeDeleteCheckAsync(ProjectAdapterModel paraObject)
    {
        Logger.LogDebug("Running pre-delete validation for project. ProjectId={ProjectId}, Title={Title}", paraObject.Id, paraObject.Title);
        return Task.FromResult(VerifyRecordResultFactory.Build(true));
    }

    public async Task<ProjectFileDownloadResult?> GetFileDownloadAsync(int projectFileId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var file = await context.ProjectFile
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == projectFileId);

        if (file is null)
        {
            return null;
        }

        var parent = await context.Project.AsNoTracking().FirstOrDefaultAsync(p => p.Id == file.ProjectId);

        // ⚠️ 父專案查不到（0.9.94 起最常見的原因：已被軟刪除）就不提供下載。
        // 不能交給下面的團隊檢查：IsTeamAccessible(null) 的語意是「沒有團隊 = 公開」，
        // 原本限團隊的附件會因此對任何有專案檢視權限的人公開。
        if (parent is null)
        {
            Logger.LogInformation("Project file download refused because the project is deleted or missing. ProjectFileId={ProjectFileId}", projectFileId);
            return null;
        }

        var scope = await accessScope.GetAsync();
        if (!RecordTeamScope.CanAccess(parent?.Teams, scope))
        {
            Logger.LogWarning("Project file download denied by team scope. ProjectFileId={ProjectFileId}", projectFileId);
            return null;
        }

        var fullPath = fileStore.TryResolveFullPath(file.RelativePath);
        if (fullPath is null)
        {
            Logger.LogWarning(
                "Project file download refused because the resolved path escapes the configured root. ProjectFileId={ProjectFileId}, RelativePath={RelativePath}",
                projectFileId, file.RelativePath);
            return null;
        }

        if (!File.Exists(fullPath))
        {
            Logger.LogWarning("Project file metadata exists but physical file was not found. ProjectFileId={ProjectFileId}, FullPath={FullPath}", projectFileId, fullPath);
            return null;
        }

        return new ProjectFileDownloadResult
        {
            Content = File.OpenRead(fullPath),
            ContentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
            DownloadFileName = file.OriginalFileName
        };
    }

    private Task<VerifyRecordResult> ValidateBusinessRulesAsync(ProjectAdapterModel paraObject, IEnumerable<ProjectUploadFileInput>? uploadFiles)
    {
        if (paraObject.StartDate.HasValue && paraObject.EndDate.HasValue && paraObject.EndDate.Value < paraObject.StartDate.Value)
        {
            Logger.LogWarning("Project validation failed because end date is earlier than start date. Title={Title}", paraObject.Title);
            return Task.FromResult(VerifyRecordResultFactory.Build(false, "結束日期不可早於開始日期。"));
        }

        if (ProjectAdapterModel.StatusOptions.Contains(paraObject.Status) == false)
        {
            Logger.LogWarning("Project validation failed because status is invalid. Title={Title}, Status={Status}", paraObject.Title, paraObject.Status);
            return Task.FromResult(VerifyRecordResultFactory.Build(false, "專案狀態不合法。"));
        }

        if (ProjectAdapterModel.PriorityOptions.Contains(paraObject.Priority) == false)
        {
            Logger.LogWarning("Project validation failed because priority is invalid. Title={Title}, Priority={Priority}", paraObject.Title, paraObject.Priority);
            return Task.FromResult(VerifyRecordResultFactory.Build(false, "專案優先順序不合法。"));
        }

        if (paraObject.CompletionPercentage < 0 || paraObject.CompletionPercentage > 100)
        {
            Logger.LogWarning("Project validation failed because completion percentage is out of range. Title={Title}, CompletionPercentage={CompletionPercentage}", paraObject.Title, paraObject.CompletionPercentage);
            return Task.FromResult(VerifyRecordResultFactory.Build(false, "完成百分比必須介於 0 到 100。"));
        }

        if (uploadFiles is not null)
        {
            foreach (var uploadFile in uploadFiles)
            {
                if (uploadFile.FileSize > MaxUploadFileSize)
                {
                    Logger.LogWarning("Project upload validation failed because file exceeded the size limit. FileName={FileName}, FileSize={FileSize}", uploadFile.FileName, uploadFile.FileSize);
                    return Task.FromResult(VerifyRecordResultFactory.Build(false, $"檔案 {uploadFile.FileName} 超過 1GB 限制"));
                }

                // 副檔名白名單。儲存檔名雖已改為 GUID，但副檔名會保留、下載時也會回吐
                // ContentType；允許 .html / .svg 等同在自己的網域上開一個 stored XSS。
                if (!UploadFileTypePolicy.IsAllowed(uploadFile.FileName, allowedUploadExtensions))
                {
                    Logger.LogWarning("Project upload validation failed because the file extension is not allowed. FileName={FileName}", uploadFile.FileName);
                    return Task.FromResult(VerifyRecordResultFactory.Build(false, $"檔案 {uploadFile.FileName} 的類型不在允許清單中。"));
                }
            }
        }

        if (string.IsNullOrWhiteSpace(projectFileRootPath))
        {
            Logger.LogWarning("Project upload validation failed because ProjectFilePath is not configured.");
            return Task.FromResult(VerifyRecordResultFactory.Build(false, "尚未設定專案附件儲存目錄"));
        }

        return Task.FromResult(VerifyRecordResultFactory.Build(true));
    }

    /// <summary>
    /// 由 Add/Update 呼叫，**必須沿用呼叫端的 context**（同一個工作單元，
    /// 實體檔案落地與資料表紀錄要一起成敗），不可自行 CreateDbContext。
    /// </summary>
    private async Task<VerifyRecordResult> SaveNewFilesAsync(BackendDBContext context, Project project, IEnumerable<ProjectUploadFileInput>? uploadFiles)
    {
        if (uploadFiles is null)
        {
            return VerifyRecordResultFactory.Build(true);
        }

        List<ProjectFile> newFiles = [];
        List<string> createdFullPaths = [];

        try
        {
            foreach (var uploadFile in uploadFiles)
            {
                if (uploadFile.Content == Stream.Null)
                {
                    continue;
                }

                var fileMetadata = await SavePhysicalFileAsync(project, uploadFile);
                newFiles.Add(fileMetadata.File);
                createdFullPaths.Add(fileMetadata.FullPath);
            }

            if (newFiles.Count > 0)
            {
                await context.ProjectFile.AddRangeAsync(newFiles);
                await context.SaveChangesAsync();
            }

            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            foreach (var fullPath in createdFullPaths)
            {
                TryDeleteFile(fullPath);
            }

            Logger.LogError(ex, "Failed to save project files. ProjectId={ProjectId}", project.Id);
            return VerifyRecordResultFactory.Build(false, "專案附件儲存失敗", ex);
        }
    }

    /// <summary>
    /// 由 Add/Update 呼叫，**必須沿用呼叫端的 context**（同一個工作單元，
    /// 實體檔案落地與資料表紀錄要一起成敗），不可自行 CreateDbContext。
    /// </summary>
    private async Task<VerifyRecordResult> RemoveProjectFilesAsync(BackendDBContext context, Project project, IEnumerable<int>? removedFileIds)
    {
        if (removedFileIds is null)
        {
            return VerifyRecordResultFactory.Build(true);
        }

        var removedFileIdSet = removedFileIds.Distinct().ToHashSet();
        if (removedFileIdSet.Count == 0)
        {
            return VerifyRecordResultFactory.Build(true);
        }

        var filesToRemove = project.Files
            .Where(x => removedFileIdSet.Contains(x.Id))
            .ToList();

        // ⚠️ 先存資料庫、成功後才刪實體檔（0.9.97 起）：0.9.96 之前是先刪檔再存，
        // 存檔失敗時資料列還在、檔案卻已經不見了。刪檔經 ProjectFileStore（有根目錄檢查）。
        var relativePaths = filesToRemove.Select(x => x.RelativePath).ToList();
        foreach (var file in filesToRemove)
        {
            context.ProjectFile.Remove(file);
        }

        if (filesToRemove.Count > 0)
        {
            await context.SaveChangesAsync();
        }

        foreach (var relativePath in relativePaths)
        {
            fileStore.Delete(relativePath);
        }

        return VerifyRecordResultFactory.Build(true);
    }

    private async Task<(ProjectFile File, string FullPath)> SavePhysicalFileAsync(Project project, ProjectUploadFileInput uploadFile)
    {
        var originalFileName = Path.GetFileName(uploadFile.FileName);
        var extension = Path.GetExtension(originalFileName);
        var year = project.CreatedAt.Year.ToString("0000");
        var month = project.CreatedAt.Month.ToString("00");
        var relativePath = Path.Combine(year, month, $"{Guid.NewGuid():N}{extension}");
        var fullPath = GetFullPath(relativePath);
        var directoryPath = Path.GetDirectoryName(fullPath);

        if (!string.IsNullOrWhiteSpace(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }

        if (uploadFile.Content.CanSeek)
        {
            uploadFile.Content.Position = 0;
        }

        await using (var targetStream = File.Create(fullPath))
        {
            await uploadFile.Content.CopyToAsync(targetStream);
        }

        // ⚠️ 不使用 uploadFile.ContentType：呼叫端可以上傳 .txt 卻宣稱是 text/html。
        // 一律依副檔名對應（副檔名本身已通過白名單驗證）。
        var contentType = UploadFileTypePolicy.ResolveContentType(originalFileName);

        return (
            new ProjectFile
            {
                ProjectId = project.Id,
                OriginalFileName = originalFileName,
                StoredFileName = Path.GetFileName(fullPath),
                RelativePath = relativePath.Replace('\\', '/'),
                ContentType = contentType,
                FileSize = uploadFile.FileSize,
                CreatedAt = DateTime.Now
            },
            fullPath);
    }

    /// <summary>附件上傳／刪除的稽核：只記筆數，沒有異動就不寫。</summary>
    private Task WriteFileAuditAsync(string action, int projectId, int count)
        => count > 0 ? WriteAuditAsync(action, projectId, $"count={count}") : Task.CompletedTask;

    /// <summary>只用在上傳失敗時回收這一次剛寫入的檔案（路徑是這裡自己產生的）；其他刪檔一律經 <see cref="ProjectFileStore"/>。</summary>
    private void TryDeleteFile(string fullPath)
    {
        if (!File.Exists(fullPath))
        {
            return;
        }

        File.Delete(fullPath);
    }

    private string GetFullPath(string relativePath)
    {
        var normalizedRelativePath = relativePath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);

        return Path.Combine(projectFileRootPath, normalizedRelativePath);
    }

    public class ProjectFileDownloadResult
    {
        public Stream Content { get; set; } = Stream.Null;

        public string ContentType { get; set; } = "application/octet-stream";

        public string DownloadFileName { get; set; } = string.Empty;
    }
}

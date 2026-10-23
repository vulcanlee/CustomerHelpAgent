using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Factories;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.AdapterModel;
using CustomerHelpAgent.Models.Systems;

namespace CustomerHelpAgent.Business.Services.DataAccess;

public class TeamService
{
    private readonly IDbContextFactory<BackendDBContext> contextFactory;
    private readonly IAuditLogService auditLogService;
    private readonly CurrentUserService currentUserService;
    private readonly ITeamTreeCache teamTree;

    public IMapper Mapper { get; }
    public ILogger<TeamService> Logger { get; }

    public TeamService(
        IDbContextFactory<BackendDBContext> contextFactory,
        IMapper mapper,
        ILogger<TeamService> logger,
        IAuditLogService auditLogService,
        CurrentUserService currentUserService,
        ITeamTreeCache teamTree)
    {
        this.contextFactory = contextFactory;
        Mapper = mapper;
        Logger = logger;
        this.auditLogService = auditLogService;
        this.currentUserService = currentUserService;
        this.teamTree = teamTree;
    }

    /// <summary>所有未刪除的部門（依名稱），團隊清單在畫面上組成樹（0.9.105 起）。</summary>
    public async Task<List<TeamAdapterModel>> GetAllAsync()
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var records = await context.Team.AsNoTracking().OrderBy(x => x.Name).ThenBy(x => x.Id).ToListAsync();
        return Mapper.Map<List<TeamAdapterModel>>(records);
    }

    /// <summary>可以當作上層的部門：排除自己與自己的所有下屬（新增時 <paramref name="teamId"/> 傳 0）。存檔時伺服器會再檢查一次。</summary>
    public async Task<List<TeamNode>> GetParentCandidatesAsync(int teamId)
    {
        var tree = await teamTree.GetAsync();
        var excluded = teamId > 0 ? tree.DescendantIds(teamId) : new HashSet<int>();
        return tree.Nodes.Where(x => x.Id != teamId && !excluded.Contains(x.Id)).OrderBy(x => x.Name).ToList();
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
            targetType: "Team",
            targetId: targetId.ToString(),
            detail: detail,
            content: content);
    }

    public async Task<DataRequestResult<TeamAdapterModel>> GetAsync(DataRequest dataRequest)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogDebug(
            "Loading teams. HasSearch={HasSearch}, SearchLength={SearchLength}, SortField={SortField}, SortDescending={SortDescending}, CurrentPage={CurrentPage}, PageSize={PageSize}, Take={Take}",
            string.IsNullOrWhiteSpace(dataRequest.Search) == false,
            dataRequest.Search?.Length ?? 0,
            dataRequest.SortField,
            dataRequest.SortDescending,
            dataRequest.CurrentPage,
            dataRequest.PageSize,
            dataRequest.Take);

        DataRequestResult<TeamAdapterModel> result = new();
        IQueryable<Team> dataSource = context.Team.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(dataRequest.Search))
        {
            dataSource = dataSource.Where(x =>
                x.Name.Contains(dataRequest.Search) ||
                (x.Code != null && x.Code.Contains(dataRequest.Search)) ||
                (x.Description != null && x.Description.Contains(dataRequest.Search)));
        }

        IOrderedQueryable<Team>? sorted = null;

        if (!string.IsNullOrWhiteSpace(dataRequest.SortField))
        {
            if (dataRequest.SortField == nameof(TeamAdapterModel.Name))
            {
                sorted = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.Name).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.Name).ThenBy(x => x.Id)
                        : null;
            }
            else if (dataRequest.SortField == nameof(TeamAdapterModel.Code))
            {
                sorted = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.Code).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.Code).ThenBy(x => x.Id)
                        : null;
            }
            else if (dataRequest.SortField == nameof(TeamAdapterModel.IsEnabled))
            {
                sorted = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.IsEnabled).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.IsEnabled).ThenBy(x => x.Id)
                        : null;
            }
            else if (dataRequest.SortField == nameof(TeamAdapterModel.UpdatedAt))
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

        List<Team> records = await dataSource.ToListAsync();
        result.Result = Mapper.Map<List<TeamAdapterModel>>(records);
        Logger.LogDebug("Loaded teams successfully. Count={Count}", result.Count);
        return result;
    }

    public async Task<TeamAdapterModel> GetAsync(int id)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogDebug("Loading team by id. TeamId={TeamId}", id);

        Team? item = await context.Team
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id);

        if (item is null)
        {
            Logger.LogInformation("Team not found. TeamId={TeamId}", id);
            return new TeamAdapterModel();
        }

        return Mapper.Map<TeamAdapterModel>(item);
    }

    public async Task<VerifyRecordResult> AddAsync(TeamAdapterModel paraObject)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogInformation("Creating team. Name={TeamName}", paraObject.Name);

        try
        {
            // 檢查上層與寫入在同一個交易內（SQLite 的寫入交易會排隊）。
            await using var transaction = await context.Database.BeginTransactionAsync();
            if (await TeamHierarchy.ValidateParentAsync(context, 0, paraObject.ParentId) is { } parentError)
            {
                Logger.LogInformation("Team create rejected by parent rule. Name={TeamName}, ParentId={ParentId}", paraObject.Name, paraObject.ParentId);
                return VerifyRecordResultFactory.Build(false, parentError);
            }

            Team itemParameter = Mapper.Map<Team>(paraObject);
            itemParameter.ConcurrencyStamp = ConcurrencyStampHelper.New();
            itemParameter.CreatedAt = DateTime.Now;
            itemParameter.UpdatedAt = DateTime.Now;

            await context.Team.AddAsync(itemParameter);
            await context.SaveChangesAsync();
            await transaction.CommitAsync();
            teamTree.Invalidate();

            Logger.LogInformation("Team created successfully. TeamId={TeamId}, Name={TeamName}", itemParameter.Id, itemParameter.Name);
            await WriteAuditAsync(AuditActions.Team.Create, itemParameter.Id, $"name={itemParameter.Name}; parentId={itemParameter.ParentId}");
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            // 前置檢查與寫入不在同一個交易裡，唯一索引是最後一道防線；
            // 命中時要給明確訊息，不要被泛用的「新增團隊失敗。」蓋掉。
            // 名稱重複是使用者錯誤（LOG-11）：記 Information、不帶例外物件，不進系統例外紀錄。
            if (UniqueConstraintHelper.TryGetFriendlyMessage(ex, out var conflictMessage))
            {
                Logger.LogInformation("Team create rejected by unique constraint. Name={TeamName}", paraObject.Name);
                return VerifyRecordResultFactory.Build(false, conflictMessage, ex);
            }

            Logger.LogError(ex, "Failed to create team. Name={TeamName}", paraObject.Name);
            return VerifyRecordResultFactory.Build(false, "新增團隊失敗。", ex);
        }
    }

    public async Task<VerifyRecordResult> UpdateAsync(TeamAdapterModel paraObject)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogInformation("Updating team. TeamId={TeamId}, Name={TeamName}", paraObject.Id, paraObject.Name);

        try
        {
            await using var transaction = await context.Database.BeginTransactionAsync();
            Team? item = await context.Team
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == paraObject.Id);

            if (item == null)
            {
                Logger.LogWarning("Team update rejected because record was not found. TeamId={TeamId}", paraObject.Id);
                return VerifyRecordResultFactory.Build(false, "找不到要修改的團隊資料。");
            }

            if (await TeamHierarchy.ValidateParentAsync(context, item.Id, paraObject.ParentId) is { } parentError)
            {
                Logger.LogInformation("Team update rejected by parent rule. TeamId={TeamId}, ParentId={ParentId}", item.Id, paraObject.ParentId);
                return VerifyRecordResultFactory.Build(false, parentError);
            }

            Team itemData = Mapper.Map<Team>(paraObject);
            itemData.CreatedAt = item.CreatedAt;
            itemData.UpdatedAt = DateTime.Now;

            // 修改前後差異（0.9.131 起）寫進稽核的詳細內容檔；item 是 AsNoTracking 讀出的修改前資料。
            var diff = AuditDiff.Compare(item, itemData).Render($"團隊 #{itemData.Id}（{item.Name}）修改");

            // 改名（0.9.105 起）：紀錄以名稱引用團隊，同一個交易內把專案、分類、角色預設團隊的舊名稱一併換掉。
            var renamed = await RenameReferencesAsync(context, item, itemData.Name);
            if (renamed.Error is { } renameError)
            {
                return VerifyRecordResultFactory.Build(false, renameError);
            }

            var entry = context.Entry(itemData);
            entry.State = EntityState.Modified;
            ConcurrencyStampHelper.Apply(entry, paraObject.ConcurrencyStamp);
            SoftDeleteHelper.ProtectFlags(entry);
            await context.SaveChangesAsync();
            await transaction.CommitAsync();
            teamTree.Invalidate();

            Logger.LogInformation("Team updated successfully. TeamId={TeamId}, Name={TeamName}", itemData.Id, itemData.Name);
            await WriteAuditAsync(AuditActions.Team.Update, itemData.Id, $"name={itemData.Name}; parentId={itemData.ParentId}" + (renamed.Result is { } counts ? $"; from={item.Name}; {counts}" : string.Empty), diff);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // 別人在這段期間先存過或刪除了這筆：使用者情境而非系統錯誤（LOG-11），記 Information、不帶例外物件。
            Logger.LogInformation("Team update rejected by concurrency conflict. TeamId={TeamId}", paraObject.Id);
            return VerifyRecordResultFactory.Build(false, ConcurrencyStampHelper.ConflictMessage, ex);
        }
        catch (Exception ex)
        {
            if (UniqueConstraintHelper.TryGetFriendlyMessage(ex, out var conflictMessage))
            {
                Logger.LogInformation("Team update rejected by unique constraint. TeamId={TeamId}, Name={TeamName}", paraObject.Id, paraObject.Name);
                return VerifyRecordResultFactory.Build(false, conflictMessage, ex);
            }

            Logger.LogError(ex, "Failed to update team. TeamId={TeamId}, Name={TeamName}", paraObject.Id, paraObject.Name);
            return VerifyRecordResultFactory.Build(false, "修改團隊失敗。", ex);
        }
    }

    /// <summary>
    /// 清單上點狀態標籤切換啟用／停用（0.9.120 起）：只改 <c>IsEnabled</c>，不走整筆覆寫的 <see cref="UpdateAsync"/>。
    /// 參數是目標值（重複點不會反轉）；已經是目標值時回成功、不寫稽核。
    /// 這是使用者的編輯動作，所以換版本號：別人開著這筆的修改窗存檔時會得到並行衝突，而不是把狀態蓋回去。
    /// </summary>
    public async Task<VerifyRecordResult> SetEnabledAsync(int id, bool enabled)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        try
        {
            var item = await context.Team.AsNoTracking()
                .Where(x => x.Id == id)
                .Select(x => new { x.Name, x.IsEnabled })
                .FirstOrDefaultAsync();
            if (item == null)
            {
                Logger.LogInformation("Team status change rejected because record was not found. TeamId={TeamId}", id);
                return VerifyRecordResultFactory.Build(false, "找不到這個團隊，可能已被刪除。");
            }

            var rows = item.IsEnabled == enabled ? 0 : await context.Team
                .Where(x => x.Id == id && x.IsEnabled != enabled)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.IsEnabled, enabled)
                    .SetProperty(x => x.UpdatedAt, DateTime.Now)
                    .SetProperty(x => x.ConcurrencyStamp, ConcurrencyStampHelper.New()));
            if (rows == 0)
            {
                return VerifyRecordResultFactory.Build(true);
            }

            teamTree.Invalidate();
            Logger.LogInformation("Team status changed. TeamId={TeamId}, Enabled={Enabled}", id, enabled);
            await WriteAuditAsync(enabled ? AuditActions.Team.Enable : AuditActions.Team.Disable, id, $"name={item.Name}");
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to change team status. TeamId={TeamId}, Enabled={Enabled}", id, enabled);
            return VerifyRecordResultFactory.Build(false, "切換團隊狀態失敗。", ex);
        }
    }

    /// <summary>
    /// 刪除 = 軟刪除（0.9.94 起）：資料仍在，可在「顯示已刪除」中還原；永久刪除見 <see cref="PurgeAsync"/>。
    /// 使用者與團隊的關聯（UserTeam）保留，團隊還原後成員關係自動恢復；查詢時經由 context.Team 的過濾自然排除。
    /// </summary>
    public async Task<VerifyRecordResult> DeleteAsync(int id)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogInformation("Deleting team. TeamId={TeamId}", id);

        try
        {
            Team? item = await context.Team.FirstOrDefaultAsync(x => x.Id == id);

            if (item == null)
            {
                Logger.LogWarning("Team deletion rejected because record was not found. TeamId={TeamId}", id);
                return VerifyRecordResultFactory.Build(false, "找不到要刪除的團隊資料。");
            }

            // 有下屬部門時不可刪（0.9.105 起，使用者決定）：否則下屬變成掛在已刪除部門底下。
            var children = await TeamHierarchy.CountActiveChildrenAsync(context, id);
            if (children > 0)
            {
                Logger.LogInformation("Team deletion rejected because it still has child teams. TeamId={TeamId}, ChildCount={ChildCount}", id, children);
                return VerifyRecordResultFactory.Build(false, TeamHierarchy.HasChildrenMessage(children));
            }

            SoftDeleteHelper.MarkDeleted(item, currentUserService.CurrentUser.Id > 0 ? currentUserService.CurrentUser.Account : null);
            await context.SaveChangesAsync();
            teamTree.Invalidate();

            Logger.LogInformation("Team deleted successfully. TeamId={TeamId}, Name={TeamName}", id, item.Name);
            await WriteAuditAsync(AuditActions.Team.Delete, id, $"name={item.Name}");
            return VerifyRecordResultFactory.Build(true);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            Logger.LogInformation("Team deletion rejected by concurrency conflict. TeamId={TeamId}", id);
            return VerifyRecordResultFactory.Build(false, ConcurrencyStampHelper.ConflictMessage, ex);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to delete team. TeamId={TeamId}", id);
            return VerifyRecordResultFactory.Build(false, "刪除團隊失敗。", ex);
        }
    }

    /// <summary>已刪除的團隊（「顯示已刪除」清單），固定依刪除時間由新到舊。</summary>
    public async Task<DataRequestResult<TeamAdapterModel>> GetDeletedAsync(DataRequest dataRequest)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        IQueryable<Team> dataSource = context.Team
            .IgnoreQueryFilters([ISoftDeletable.FilterName])
            .AsNoTracking()
            .Where(x => x.IsDeleted);

        if (!string.IsNullOrWhiteSpace(dataRequest.Search))
        {
            dataSource = dataSource.Where(x =>
                x.Name.Contains(dataRequest.Search) ||
                (x.Code != null && x.Code.Contains(dataRequest.Search)) ||
                (x.Description != null && x.Description.Contains(dataRequest.Search)));
        }

        dataSource = dataSource.OrderByDescending(x => x.DeletedAt).ThenByDescending(x => x.Id);

        var result = new DataRequestResult<TeamAdapterModel> { Count = await dataSource.CountAsync() };
        dataSource = dataSource.Skip((dataRequest.CurrentPage - 1) * dataRequest.PageSize);
        if (dataRequest.Take != 0)
        {
            dataSource = dataSource.Take(dataRequest.PageSize);
        }

        result.Result = Mapper.Map<List<TeamAdapterModel>>(await dataSource.ToListAsync());
        return result;
    }

    public async Task<VerifyRecordResult> RestoreAsync(int id)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogInformation("Restoring team. TeamId={TeamId}", id);

        try
        {
            Team? item = await context.Team
                .IgnoreQueryFilters([ISoftDeletable.FilterName])
                .FirstOrDefaultAsync(x => x.Id == id && x.IsDeleted);

            if (item == null)
            {
                Logger.LogWarning("Team restore rejected because deleted record was not found. TeamId={TeamId}", id);
                return VerifyRecordResultFactory.Build(false, "找不到要還原的團隊（可能已被還原或永久刪除）。");
            }

            // 刪除期間可能有人建立了同名或同代號的團隊（部分唯一索引允許），規則與新增時相同：去空白後不分大小寫。
            var name = item.Name.ToLower();
            if (await context.Team.AnyAsync(x => x.Id != id && x.Name.ToLower() == name))
            {
                Logger.LogInformation("Team restore rejected because an active team has the same name. TeamId={TeamId}", id);
                return VerifyRecordResultFactory.Build(false, $"已有同名的團隊「{item.Name}」，無法還原。請先將現有的同名團隊改名後再還原。");
            }

            if (item.Code is { } code)
            {
                var lowerCode = code.ToLower();
                if (await context.Team.AnyAsync(x => x.Id != id && x.Code != null && x.Code.ToLower() == lowerCode))
                {
                    Logger.LogInformation("Team restore rejected because an active team has the same code. TeamId={TeamId}", id);
                    return VerifyRecordResultFactory.Build(false, $"已有代號為「{code}」的團隊，無法還原。請先將現有團隊的代號改掉後再還原。");
                }
            }

            if (await TeamHierarchy.ValidateRestoreAsync(context, item) is { } parentError)
            {
                Logger.LogInformation("Team restore rejected because its parent is deleted. TeamId={TeamId}", id);
                return VerifyRecordResultFactory.Build(false, parentError);
            }

            SoftDeleteHelper.Restore(item);
            await context.SaveChangesAsync();
            teamTree.Invalidate();

            Logger.LogInformation("Team restored successfully. TeamId={TeamId}, Name={TeamName}", id, item.Name);
            await WriteAuditAsync(AuditActions.Team.Restore, id, $"name={item.Name}");
            return VerifyRecordResultFactory.Build(true);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            Logger.LogInformation("Team restore rejected by concurrency conflict. TeamId={TeamId}", id);
            return VerifyRecordResultFactory.Build(false, ConcurrencyStampHelper.ConflictMessage, ex);
        }
        catch (Exception ex)
        {
            if (UniqueConstraintHelper.TryGetFriendlyMessage(ex, out var conflictMessage))
            {
                Logger.LogInformation("Team restore rejected by unique constraint. TeamId={TeamId}", id);
                return VerifyRecordResultFactory.Build(false, conflictMessage, ex);
            }

            Logger.LogError(ex, "Failed to restore team. TeamId={TeamId}", id);
            return VerifyRecordResultFactory.Build(false, "還原團隊失敗。", ex);
        }
    }

    /// <summary>永久刪除：只能對已刪除的團隊執行，無法復原；使用者與團隊的關聯一併刪除（資料庫 Cascade）。</summary>
    public async Task<VerifyRecordResult> PurgeAsync(int id)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogInformation("Purging team. TeamId={TeamId}", id);

        try
        {
            Team? item = await context.Team
                .IgnoreQueryFilters([ISoftDeletable.FilterName])
                .FirstOrDefaultAsync(x => x.Id == id && x.IsDeleted);

            if (item == null)
            {
                Logger.LogWarning("Team purge rejected because deleted record was not found. TeamId={TeamId}", id);
                return VerifyRecordResultFactory.Build(false, "找不到要永久刪除的團隊（只能永久刪除已刪除的資料）。");
            }

            if (await TeamHierarchy.IsParentOfAnyAsync(context, id))
            {
                Logger.LogInformation("Team purge rejected because other teams still use it as parent. TeamId={TeamId}", id);
                return VerifyRecordResultFactory.Build(false, "還有其他部門（含已刪除的）以它為上層部門，請先永久刪除那些部門。");
            }

            context.Team.Remove(item);
            await context.SaveChangesAsync();

            Logger.LogInformation("Team purged successfully. TeamId={TeamId}, Name={TeamName}", id, item.Name);
            await WriteAuditAsync(AuditActions.Team.Purge, id, $"name={item.Name}");
            return VerifyRecordResultFactory.Build(true);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            Logger.LogInformation("Team purge rejected by concurrency conflict. TeamId={TeamId}", id);
            return VerifyRecordResultFactory.Build(false, "這筆團隊已被其他人還原或變更，沒有執行永久刪除。", ex);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to purge team. TeamId={TeamId}", id);
            return VerifyRecordResultFactory.Build(false, "永久刪除團隊失敗。", ex);
        }
    }

    public async Task<VerifyRecordResult> BeforeAddCheckAsync(TeamAdapterModel paraObject)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogDebug("Running pre-create validation for team. Name={TeamName}", paraObject.Name);

        var name = NameNormalizer.Normalize(paraObject.Name);
        var nameItem = await context.Team
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Name.ToLower() == name.ToLower());

        if (nameItem != null)
        {
            Logger.LogInformation("Pre-create validation failed because team name already exists. Name={TeamName}", paraObject.Name);
            return VerifyRecordResultFactory.Build(false, "團隊名稱已存在，無法新增。");
        }

        // 用 `is { } code` 取得不可為 null 的 string：外層的 null 檢查流程狀態
        // 不會延伸到底下的查詢 lambda 內。
        if (NameNormalizer.NormalizeOptional(paraObject.Code) is { } code)
        {
            var codeItem = await context.Team
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Code != null && x.Code.ToLower() == code.ToLower());

            if (codeItem != null)
            {
                Logger.LogInformation("Pre-create validation failed because team code already exists. Code={TeamCode}", paraObject.Code);
                return VerifyRecordResultFactory.Build(false, "團隊代號已存在，無法新增。");
            }
        }

        return VerifyRecordResultFactory.Build(true);
    }

    public async Task<VerifyRecordResult> BeforeUpdateCheckAsync(TeamAdapterModel paraObject)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogDebug("Running pre-update validation for team. TeamId={TeamId}, Name={TeamName}", paraObject.Id, paraObject.Name);

        var searchItem = await context.Team
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == paraObject.Id);

        if (searchItem == null)
        {
            Logger.LogInformation("Pre-update validation failed because team was not found. TeamId={TeamId}", paraObject.Id);
            return VerifyRecordResultFactory.Build(false, "要修改的團隊資料不存在。");
        }

        var name = NameNormalizer.Normalize(paraObject.Name);
        var nameItem = await context.Team
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Name.ToLower() == name.ToLower() && x.Id != paraObject.Id);

        if (nameItem != null)
        {
            Logger.LogInformation("Pre-update validation failed because team name already exists. TeamId={TeamId}, Name={TeamName}", paraObject.Id, paraObject.Name);
            return VerifyRecordResultFactory.Build(false, "團隊名稱已存在，無法修改。");
        }

        // 用 `is { } code` 取得不可為 null 的 string：外層的 null 檢查流程狀態
        // 不會延伸到底下的查詢 lambda 內。
        if (NameNormalizer.NormalizeOptional(paraObject.Code) is { } code)
        {
            var codeItem = await context.Team
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Code != null && x.Code.ToLower() == code.ToLower() && x.Id != paraObject.Id);

            if (codeItem != null)
            {
                Logger.LogInformation("Pre-update validation failed because team code already exists. TeamId={TeamId}, Code={TeamCode}", paraObject.Id, paraObject.Code);
                return VerifyRecordResultFactory.Build(false, "團隊代號已存在，無法修改。");
            }
        }

        return VerifyRecordResultFactory.Build(true);
    }

    public Task<VerifyRecordResult> BeforeDeleteCheckAsync(TeamAdapterModel paraObject)
    {
        Logger.LogDebug("Running pre-delete validation for team. TeamId={TeamId}, Name={TeamName}", paraObject.Id, paraObject.Name);
        return Task.FromResult(VerifyRecordResultFactory.Build(true));
    }

    /// <summary>名稱有變時同步更新引用；改成已刪除團隊用過的名稱會讓那個團隊留下的資料被這個部門看到，擋下。</summary>
    private async Task<(TeamRenameResult? Result, string? Error)> RenameReferencesAsync(BackendDBContext context, Team current, string newName)
    {
        if (string.Equals(current.Name, newName, StringComparison.Ordinal))
        {
            return (null, null);
        }

        var lower = newName.ToLower();
        if (await context.Team.IgnoreQueryFilters([ISoftDeletable.FilterName]).AnyAsync(x => x.IsDeleted && x.Id != current.Id && x.Name.ToLower() == lower))
        {
            Logger.LogInformation("Team rename rejected because a deleted team used the name. TeamId={TeamId}", current.Id);
            return (null, DeletedNameMessage(newName));
        }

        return (await TeamHierarchy.RenameReferencesAsync(context, current.Name, newName), null);
    }

    public static string DeletedNameMessage(string name)
        => $"已刪除的團隊用過「{name}」這個名稱，改成它會讓那個團隊留下的資料被這個部門看到。請換一個名稱，或先永久刪除那個團隊。";

    /// <summary>
    /// 取得所有啟用中的團隊名稱（依名稱排序），供其他頁面下拉選取使用。
    /// </summary>
    public async Task<List<string>> GetAllEnabledNamesAsync()
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        return await context.Team
            .AsNoTracking()
            .Where(x => x.IsEnabled)
            .OrderBy(x => x.Name)
            .Select(x => x.Name)
            .ToListAsync();
    }
}

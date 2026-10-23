using Microsoft.EntityFrameworkCore;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Dtos.Commons;
using Microsoft.Extensions.Logging;
using CustomerHelpAgent.Business.Services.Other;

namespace CustomerHelpAgent.Business.Repositories;

/// <summary>
/// Web API 的分類存取。0.9.105 起與 Blazor 的 <c>CategoryService</c> 套用同一套團隊範圍（<see cref="RecordTeamScope"/>，分類的反向規則）：
/// 清單只回看得到的，單筆、修改、刪除看不到的一律當作不存在（controller 回 404）。
/// </summary>
public class CategoryRepository
{
    private readonly BackendDBContext context;
    private readonly IRecordAccessScopeProvider accessScope;
    private readonly ILogger<CategoryRepository> logger;

    public CategoryRepository(BackendDBContext context, IRecordAccessScopeProvider accessScope, ILogger<CategoryRepository> logger)
    {
        this.context = context;
        this.accessScope = accessScope;
        this.logger = logger;
    }

    #region 查詢方法

    public async Task<Category?> GetByIdAsync(int id)
    {
        var category = await context.Category.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        return category is not null && RecordTeamScope.CanAccessCategory(category.Teams, await accessScope.GetAsync()) ? category : null;
    }

    /// <summary>非管理員指定團隊的規則（新加的團隊要在自己範圍內、不可清成公開）；新增時 <paramref name="id"/> 傳 null。回傳錯誤訊息。</summary>
    public async Task<string?> CheckTeamAssignmentAsync(int? id, string? requestedTeams)
    {
        var current = id is { } existingId
            ? await context.Category.AsNoTracking().Where(x => x.Id == existingId).Select(x => x.Teams).FirstOrDefaultAsync()
            : null;
        return RecordTeamScope.CheckAssignment(current, requestedTeams, await accessScope.GetAsync());
    }

    public async Task<PagedResult<Category>> GetPagedAsync(CategorySearchRequestDto request)
    {
        var query = RecordTeamScope.ApplyCategory(context.Category.AsNoTracking(), await accessScope.GetAsync());

        if (!string.IsNullOrEmpty(request.Keyword))
        {
            query = query.Where(x =>
                x.Name.Contains(request.Keyword) ||
                (x.Description != null && x.Description.Contains(request.Keyword)));
        }

        if (request.IsEnabled.HasValue)
        {
            query = query.Where(x => x.IsEnabled == request.IsEnabled.Value);
        }

        query = request.SortBy?.ToLower() switch
        {
            "name" => request.SortDescending ? query.OrderByDescending(x => x.Name) : query.OrderBy(x => x.Name),
            "isenabled" => request.SortDescending ? query.OrderByDescending(x => x.IsEnabled) : query.OrderBy(x => x.IsEnabled),
            "createdat" => request.SortDescending ? query.OrderByDescending(x => x.CreatedAt) : query.OrderBy(x => x.CreatedAt),
            "updatedat" => request.SortDescending ? query.OrderByDescending(x => x.UpdatedAt) : query.OrderBy(x => x.UpdatedAt),
            _ => query.OrderByDescending(x => x.UpdatedAt),
        };

        var totalCount = await query.CountAsync();

        // 記在 Debug：這是排查「為什麼查不到資料」的第一手線索。
        // 只記筆數與分頁參數，不記關鍵字內容 —— 使用者的搜尋字串可能包含個資。
        logger.LogDebug(
            "Paged category query executed. PageIndex={PageIndex}, PageSize={PageSize}, SortBy={SortBy}, SortDescending={SortDescending}, HasKeyword={HasKeyword}, TotalCount={TotalCount}",
            request.PageIndex, request.PageSize, request.SortBy, request.SortDescending,
            string.IsNullOrWhiteSpace(request.Keyword) == false, totalCount);
        var items = await query
            .Skip((request.PageIndex - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync();

        return new PagedResult<Category>
        {
            Items = items,
            PageIndex = request.PageIndex,
            PageSize = request.PageSize,
            TotalCount = totalCount
        };
    }

    /// <summary>
    /// 名稱是否已被使用。比對語意必須與 Blazor 路徑的 BeforeAddCheckAsync 一致
    /// （先正規化、再不分大小寫），否則同一份資料會出現「UI 擋得下、API 擋不下」。
    /// </summary>
    public async Task<bool> ExistsByNameAsync(string name, int? excludeId = null)
    {
        var normalized = NameNormalizer.Normalize(name);
        var query = context.Category.Where(x => x.Name.ToLower() == normalized.ToLower());
        if (excludeId.HasValue)
        {
            query = query.Where(x => x.Id != excludeId.Value);
        }
        return await query.AnyAsync();
    }

    #endregion

    #region 新增 / 更新 / 刪除

    public async Task<Category> AddAsync(Category category)
    {
        category.CreatedAt = DateTime.Now;
        category.UpdatedAt = DateTime.Now;

        // 不信任客戶端傳來的版本號（POST 會忽略它）；AutoMapper 從 DTO 映射時可能是 null。
        // 一律由資料庫配號：0.9.93 之前會直接採用客戶端傳來的 Id，與既有資料撞號時回 500（軟刪除的列也一直佔著 Id）。
        category.Id = 0;
        category.ConcurrencyStamp = ConcurrencyStampHelper.New();
        await context.Category.AddAsync(category);
        await context.SaveChangesAsync();

        return category;
    }

    public async Task<UpdateOutcome> UpdateAsync(Category category)
    {
        var existing = await context.Category.FindAsync(category.Id);
        if (existing == null || !RecordTeamScope.CanAccessCategory(existing.Teams, await accessScope.GetAsync()))
        {
            return UpdateOutcome.NotFound;
        }

        // 修改前的快照（0.9.131 起，給稽核差異用）：SetValues 之後 existing 就是新值了。
        var before = (Category)context.Entry(existing).CurrentValues.ToObject();

        category.UpdatedAt = DateTime.Now;
        category.CreatedAt = existing.CreatedAt;

        context.Entry(existing).CurrentValues.SetValues(category);
        var diff = AuditDiff.Compare(before, existing).Render($"分類 #{existing.Id}（{before.Name}）修改");
        // FindAsync 載入的 OriginalValue 是資料庫目前的版本號，必須改成客戶端帶來的值才比對得出衝突；
        // 衝突時 SaveChanges 丟 DbUpdateConcurrencyException，由 controller 轉成 409。
        ConcurrencyStampHelper.Apply(context.Entry(existing), category.ConcurrencyStamp);
        SoftDeleteHelper.ProtectFlags(context.Entry(existing));
        await context.SaveChangesAsync();

        return new UpdateOutcome(true, diff);
    }

    /// <summary>
    /// 軟刪除（0.9.94 起）：可在畫面的「顯示已刪除」中還原或永久刪除。
    /// FindAsync 會套用軟刪除過濾，已刪除的資料回 false（controller 回 404）。
    /// </summary>
    public async Task<bool> DeleteAsync(int id, string? actorAccount)
    {
        var category = await context.Category.FindAsync(id);
        if (category == null || !RecordTeamScope.CanAccessCategory(category.Teams, await accessScope.GetAsync()))
        {
            return false;
        }

        SoftDeleteHelper.MarkDeleted(category, actorAccount);
        await context.SaveChangesAsync();

        return true;
    }

    #endregion
}

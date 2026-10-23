using Microsoft.EntityFrameworkCore;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Services.Other;

namespace CustomerHelpAgent.Business.Helpers;

/// <summary>改名同步更新了幾筆引用（寫進稽核）。</summary>
public sealed record TeamRenameResult(int Projects, int Categories, int Roles)
{
    public override string ToString() => $"renamedProjects={Projects}; renamedCategories={Categories}; renamedRoles={Roles}";
}

/// <summary>
/// 部門樹的寫入規則（0.9.105 起）。Blazor 的 <c>TeamService</c> 與 Web API 的 <c>TeamRepository</c> 共用，
/// 一律傳入呼叫端的 DbContext，在呼叫端的交易內執行 —— SQLite 的寫入交易會排隊，兩個同時「互設對方為上層」的請求不會都通過。
/// </summary>
public static class TeamHierarchy
{
    public const string ParentIsSelfMessage = "上層部門不能是自己。";
    public const string ParentIsDescendantMessage = "上層部門不能是自己的下屬部門。";
    public const string ParentNotFoundMessage = "找不到選擇的上層部門，可能已被刪除。";
    public const string TooDeepMessage = "部門層級太深或資料有誤，請選擇其他上層部門。";

    /// <summary>上層部門不可是自己、自己的下屬、已刪除或不存在的部門；回傳錯誤訊息，沒問題回 null。新增時 <paramref name="teamId"/> 傳 0。</summary>
    public static async Task<string?> ValidateParentAsync(BackendDBContext context, int teamId, int? parentId)
    {
        if (parentId is not { } parent)
        {
            return null;
        }

        if (parent == teamId)
        {
            return ParentIsSelfMessage;
        }

        // 經軟刪除過濾：已刪除的部門不在字典裡。
        var parents = await context.Team.AsNoTracking().Select(x => new { x.Id, x.ParentId }).ToDictionaryAsync(x => x.Id, x => x.ParentId);
        if (!parents.ContainsKey(parent))
        {
            return ParentNotFoundMessage;
        }

        var visited = new HashSet<int>();
        int? current = parent;
        while (current is { } id)
        {
            if (id == teamId)
            {
                return ParentIsDescendantMessage;
            }

            if (!visited.Add(id) || visited.Count > TeamTree.MaxDepth)
            {
                return TooDeepMessage;
            }

            current = parents.GetValueOrDefault(id);
        }

        return null;
    }

    /// <summary>未刪除的下屬部門數；大於 0 時不可刪除。</summary>
    public static Task<int> CountActiveChildrenAsync(BackendDBContext context, int teamId)
        => context.Team.CountAsync(x => x.ParentId == teamId);

    public static string HasChildrenMessage(int count) => $"這個部門底下還有 {count} 個下屬部門，請先把它們移到其他上層部門或刪除後再刪除。";

    /// <summary>還原時上層部門仍是已刪除：擋下（否則還原出一個掛在已刪除部門底下的部門）。</summary>
    public static async Task<string?> ValidateRestoreAsync(BackendDBContext context, Team item)
    {
        if (item.ParentId is not { } parentId)
        {
            return null;
        }

        var parent = await context.Team.IgnoreQueryFilters([ISoftDeletable.FilterName]).AsNoTracking()
            .Where(x => x.Id == parentId).Select(x => new { x.Name, x.IsDeleted }).FirstOrDefaultAsync();
        return parent is { IsDeleted: true }
            ? $"上層部門「{parent.Name}」也在已刪除清單中，請先還原上層部門。"
            : null;
    }

    /// <summary>仍被當作上層部門（含已刪除的下屬）：永久刪除要先刪下屬，否則外鍵擋下。</summary>
    public static Task<bool> IsParentOfAnyAsync(BackendDBContext context, int teamId)
        => context.Team.IgnoreQueryFilters([ISoftDeletable.FilterName]).AnyAsync(x => x.ParentId == teamId);

    /// <summary>
    /// 改名時把所有引用舊名稱的地方換成新名稱：專案與分類的「團隊」、角色的預設團隊（都含已刪除的，還原後才不會變成孤兒）。
    /// 比對去空白、不分大小寫；被改的列換版本號（別人開著的編輯窗存檔時會提示衝突，而不是把舊名稱寫回去）。不呼叫 SaveChanges。
    /// </summary>
    public static async Task<TeamRenameResult> RenameReferencesAsync(BackendDBContext context, string oldName, string newName)
    {
        var from = (oldName ?? string.Empty).Trim();
        var to = (newName ?? string.Empty).Trim();
        if (from.Length == 0 || to.Length == 0 || string.Equals(from, to, StringComparison.Ordinal))
        {
            return new TeamRenameResult(0, 0, 0);
        }

        // 只取 Id 與標籤字串在記憶體比對：SQLite 的 lower() 只處理 ASCII，用它比對中英混合的名稱不可靠。
        var projectIds = (await context.Project.IgnoreQueryFilters([ISoftDeletable.FilterName]).AsNoTracking()
                .Where(x => x.Teams != null).Select(x => new { x.Id, x.Teams }).ToListAsync())
            .Where(x => Contains(TagStringHelper.ToList(x.Teams), from)).Select(x => x.Id).ToList();
        foreach (var project in await context.Project.IgnoreQueryFilters([ISoftDeletable.FilterName]).Where(x => projectIds.Contains(x.Id)).ToListAsync())
        {
            project.Teams = Replace(project.Teams, from, to);
            project.ConcurrencyStamp = ConcurrencyStampHelper.New();
        }

        var categoryIds = (await context.Category.IgnoreQueryFilters([ISoftDeletable.FilterName]).AsNoTracking()
                .Where(x => x.Teams != null).Select(x => new { x.Id, x.Teams }).ToListAsync())
            .Where(x => Contains(TagStringHelper.ToList(x.Teams), from)).Select(x => x.Id).ToList();
        foreach (var category in await context.Category.IgnoreQueryFilters([ISoftDeletable.FilterName]).Where(x => categoryIds.Contains(x.Id)).ToListAsync())
        {
            category.Teams = Replace(category.Teams, from, to);
            category.ConcurrencyStamp = ConcurrencyStampHelper.New();
        }

        var roleIds = (await context.RoleView.IgnoreQueryFilters([ISoftDeletable.FilterName]).AsNoTracking()
                .Select(x => new { x.Id, x.DefaultTeamsJson }).ToListAsync())
            .Where(x => Contains(TeamJsonHelper.Deserialize(x.DefaultTeamsJson), from)).Select(x => x.Id).ToList();
        foreach (var role in await context.RoleView.IgnoreQueryFilters([ISoftDeletable.FilterName]).Where(x => roleIds.Contains(x.Id)).ToListAsync())
        {
            role.DefaultTeamsJson = TeamJsonHelper.Serialize(TeamJsonHelper.Deserialize(role.DefaultTeamsJson).Select(x => Matches(x, from) ? to : x));
            role.ConcurrencyStamp = ConcurrencyStampHelper.New();
        }

        return new TeamRenameResult(projectIds.Count, categoryIds.Count, roleIds.Count);
    }

    private static bool Matches(string? value, string name) => string.Equals((value ?? string.Empty).Trim(), name, StringComparison.OrdinalIgnoreCase);

    private static bool Contains(IEnumerable<string> values, string name) => values.Any(x => Matches(x, name));

    private static string? Replace(string? stored, string from, string to)
        => TagStringHelper.ToStored(TagStringHelper.ToList(stored).Select(x => Matches(x, from) ? to : x));
}

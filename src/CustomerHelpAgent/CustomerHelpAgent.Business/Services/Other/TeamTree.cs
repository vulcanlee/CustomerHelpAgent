using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using CustomerHelpAgent.AccessDatas;

namespace CustomerHelpAgent.Business.Services.Other;

/// <summary>部門樹的一個節點（只含走訪需要的欄位）。</summary>
public sealed record TeamNode(int Id, string Name, int? ParentId);

/// <summary>
/// 部門樹的不可變快照（0.9.105 起），只含未刪除的部門。名稱比對去空白、不分大小寫（與紀錄上的團隊標籤相同）。
/// 走訪一律帶 visited 與深度上限：資料庫被直接改成循環時也不會無窮迴圈。
/// </summary>
public sealed class TeamTree
{
    /// <summary>走訪的深度上限；正常的組織不會超過，超過代表資料有誤（循環已由寫入端擋下）。</summary>
    public const int MaxDepth = 32;

    private readonly Dictionary<int, TeamNode> byId;
    private readonly Dictionary<string, TeamNode> byName;
    private readonly ILookup<int, TeamNode> children;

    public TeamTree(IEnumerable<TeamNode> nodes)
    {
        var list = nodes.ToList();
        byId = list.ToDictionary(x => x.Id);
        byName = new Dictionary<string, TeamNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in list)
        {
            byName.TryAdd(node.Name.Trim(), node);
        }

        children = list.Where(x => x.ParentId is { } parent && byId.ContainsKey(parent)).ToLookup(x => x.ParentId!.Value);
    }

    public static TeamTree Empty { get; } = new([]);

    public IReadOnlyCollection<TeamNode> Nodes => byId.Values;

    public TeamNode? Find(int id) => byId.GetValueOrDefault(id);

    public TeamNode? Find(string? name) => name is null ? null : byName.GetValueOrDefault(name.Trim());

    public IEnumerable<TeamNode> ChildrenOf(int id) => children[id];

    /// <summary>名稱清單加上每個部門的所有下屬（不認得的名稱原樣保留），去重、保留原順序。</summary>
    public IReadOnlyList<string> ExpandWithDescendants(IEnumerable<string> names)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var raw in names)
        {
            var name = (raw ?? string.Empty).Trim();
            if (name.Length == 0 || !seen.Add(name))
            {
                continue;
            }

            result.Add(name);
            if (Find(name) is { } node)
            {
                foreach (var id in DescendantIds(node.Id))
                {
                    if (seen.Add(byId[id].Name.Trim()))
                    {
                        result.Add(byId[id].Name.Trim());
                    }
                }
            }
        }

        return result;
    }

    /// <summary>這個部門與它所有上層的名稱（不認得的名稱只回它自己）。看得到這個部門資料的人＝指派了其中任一個的人。</summary>
    public IReadOnlyList<string> AncestorsAndSelf(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return [];
        }

        if (Find(trimmed) is not { } node)
        {
            return [trimmed];
        }

        var result = new List<string> { node.Name.Trim() };
        var visited = new HashSet<int> { node.Id };
        var current = node;
        for (var depth = 0; depth < MaxDepth && current.ParentId is { } parentId && byId.TryGetValue(parentId, out var parent) && visited.Add(parent.Id); depth++)
        {
            result.Add(parent.Name.Trim());
            current = parent;
        }

        return result;
    }

    /// <summary>所有下屬部門的 Id（不含自己）。</summary>
    public IReadOnlySet<int> DescendantIds(int id)
    {
        var result = new HashSet<int>();
        var frontier = new List<int> { id };
        for (var depth = 0; depth < MaxDepth && frontier.Count > 0; depth++)
        {
            var next = new List<int>();
            foreach (var parent in frontier)
            {
                foreach (var child in children[parent])
                {
                    if (child.Id != id && result.Add(child.Id))
                    {
                        next.Add(child.Id);
                    }
                }
            }

            frontier = next;
        }

        return result;
    }
}

/// <summary>
/// 部門樹的行程內快取（0.9.105 起，singleton）。存活 60 秒；本行程的寫入（新增、修改、刪除、還原、永久刪除，含 Web API）呼叫 <see cref="Invalidate"/> 立即失效，
/// 其他行程（IIS 重疊回收）最晚 60 秒後看到。
/// </summary>
public interface ITeamTreeCache
{
    Task<TeamTree> GetAsync();

    void Invalidate();
}

public sealed class TeamTreeCache : ITeamTreeCache
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    private readonly IDbContextFactory<BackendDBContext> contextFactory;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<TeamTreeCache> logger;
    private readonly SemaphoreSlim gate = new(1, 1);
    private (TeamTree Tree, DateTimeOffset LoadedAt, long Version)? cached;
    private long version;

    public TeamTreeCache(IDbContextFactory<BackendDBContext> contextFactory, TimeProvider timeProvider, ILogger<TeamTreeCache> logger)
    {
        this.contextFactory = contextFactory;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    public async Task<TeamTree> GetAsync()
    {
        if (cached is { } hit && hit.Version == Interlocked.Read(ref version) && timeProvider.GetUtcNow() - hit.LoadedAt < Lifetime)
        {
            return hit.Tree;
        }

        await gate.WaitAsync();
        try
        {
            var current = Interlocked.Read(ref version);
            if (cached is { } again && again.Version == current && timeProvider.GetUtcNow() - again.LoadedAt < Lifetime)
            {
                return again.Tree;
            }

            await using var context = await contextFactory.CreateDbContextAsync();
            var nodes = await context.Team.AsNoTracking().Select(x => new TeamNode(x.Id, x.Name, x.ParentId)).ToListAsync();
            var tree = new TeamTree(nodes);
            cached = (tree, timeProvider.GetUtcNow(), current);
            logger.LogDebug("Team tree loaded. TeamCount={TeamCount}", nodes.Count);
            return tree;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>版本號遞增：正在載入中的舊資料也不會被當成新的存回去。</summary>
    public void Invalidate() => Interlocked.Increment(ref version);
}

using System.Text.Json;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Web.Caching;
using CustomerHelpAgent.Web.Components.Commons;

namespace CustomerHelpAgent.Web.Components.Layout;

/// <summary>
/// 頁面操作說明的讀取與路由比對。比照 <see cref="SidebarMenuService"/>：同一個 <see cref="ICacheService"/>、
/// 以 <c>ContentRootPath</c> 組路徑、讀不到就記 warning 回空，絕不拋例外讓 layout 掛掉。
/// </summary>
/// <remarks>
/// 索引與內容都走 <see cref="ICacheService"/>，改了 .md 檔在快取到期後即生效，不必重啟。
/// </remarks>
public sealed class PageHelpService
{
    private const string TopicsCacheKey = "help:topics:raw";

    private readonly IWebHostEnvironment environment;
    private readonly ILogger<PageHelpService> logger;
    private readonly ICacheService cacheService;

    public PageHelpService(
        IWebHostEnvironment environment,
        ILogger<PageHelpService> logger,
        ICacheService cacheService)
    {
        this.environment = environment;
        this.logger = logger;
        this.cacheService = cacheService;
    }

    public async Task<IReadOnlyList<PageHelpTopicModel>> LoadTopicsAsync()
        => await cacheService.GetOrCreateAsync<List<PageHelpTopicModel>>(
            TopicsCacheKey,
            () => Task.FromResult(ReadTopicsFromDisk().ToList()));

    private IReadOnlyList<PageHelpTopicModel> ReadTopicsFromDisk()
    {
        var topicsFilePath = Path.Combine(environment.ContentRootPath, MagicObjectHelper.頁面說明索引定義);
        if (!File.Exists(topicsFilePath))
        {
            logger.LogWarning("Page help topics file not found: {TopicsFilePath}", topicsFilePath);
            return [];
        }

        try
        {
            using var stream = File.OpenRead(topicsFilePath);
            return JsonSerializer.Deserialize<List<PageHelpTopicModel>>(stream, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? [];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load page help topics from {TopicsFilePath}", topicsFilePath);
            return [];
        }
    }

    /// <summary>
    /// 以目前路徑找出對應的說明登記。<b>精確比對，刻意不用前綴比對</b>：
    /// 頂欄標題用的 <c>MainLayout.IsMatchingUrl</c> 是前綴語意，沿用的話 <c>/projects/5</c> 之類的子路徑
    /// 會靜默顯示清單頁的說明。
    /// </summary>
    public static PageHelpTopicModel? MatchTopic(IReadOnlyList<PageHelpTopicModel>? topics, string? currentPath)
    {
        if (topics is null || topics.Count == 0)
        {
            return null;
        }

        var key = NormalizePath(currentPath);
        return topics.FirstOrDefault(topic => string.Equals(NormalizePath(topic.Route), key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>剝掉 query／fragment 與頭尾斜線。比對一律不分大小寫，由呼叫端決定。</summary>
    public static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var value = path.Trim();
        var queryIndex = value.IndexOfAny(['?', '#']);
        if (queryIndex >= 0)
        {
            value = value[..queryIndex];
        }

        return value.Trim('/');
    }

    /// <summary>
    /// 路由 → 說明檔名：去頭尾斜線、剩下的斜線換成 <c>-</c>、轉小寫、加 <c>.md</c>。
    /// 例：<c>/ChangePassword</c> → <c>changepassword.md</c>；<c>/log-level-setting</c> → <c>log-level-setting.md</c>。
    /// 純函式，由測試釘住，也是作者「這一頁的檔案該叫什麼」的唯一依據。
    /// </summary>
    public static string ToSlugFileName(string? route)
    {
        var value = NormalizePath(route);
        return value.Length == 0 ? string.Empty : $"{value.Replace('/', '-').ToLowerInvariant()}.md";
    }

    /// <summary>
    /// 「相關頁面」依權限過濾：受選單控管、但此人不在授權選單裡的頁面隱藏（點進去只會看到無權限）；
    /// 根本不在 Menu.json 的頁面（例如 /ChangePassword）登入者都能進，照常顯示。
    /// 授權選單與側邊欄同源（<see cref="SidebarMenuService.LoadAuthorizedMenuItemsAsync"/>），管理員短路自然成立。
    /// </summary>
    /// <param name="allMenuUrls">Menu.json 全部網址（已正規化，不分大小寫）。</param>
    /// <param name="authorizedMenuUrls">此人授權選單的網址（已正規化，不分大小寫）。</param>
    public static List<PageHelpRelatedPage> FilterVisibleRelatedPages(
        IEnumerable<PageHelpRelatedPage> relatedPages,
        IReadOnlySet<string> allMenuUrls,
        IReadOnlySet<string> authorizedMenuUrls)
        => relatedPages
            .Where(page =>
            {
                var key = NormalizePath(page.Route);
                return !allMenuUrls.Contains(key) || authorizedMenuUrls.Contains(key);
            })
            .ToList();

    /// <summary>讀取並解析說明內容。永不回 null、永不拋例外：檔案讀不到一律回佔位文件。</summary>
    public async Task<PageHelpDocument> LoadDocumentAsync(PageHelpTopicModel topic)
    {
        ArgumentNullException.ThrowIfNull(topic);

        var markdown = await cacheService.GetOrCreateAsync<string>(
            $"help:doc:{topic.File}",
            () => Task.FromResult(ReadDocumentFromDisk(topic.File)));

        return PageHelpMarkdownParser.Parse(markdown);
    }

    private string ReadDocumentFromDisk(string file)
    {
        // 路徑穿越防護：只接受單純檔名。file 來自資料檔而非使用者輸入，但這是零成本的防線。
        if (string.IsNullOrWhiteSpace(file)
            || file.Contains('/', StringComparison.Ordinal)
            || file.Contains('\\', StringComparison.Ordinal)
            || file.Contains("..", StringComparison.Ordinal))
        {
            logger.LogWarning("Page help file name is not a plain file name, rejected: {File}", file);
            return string.Empty;
        }

        var documentPath = Path.Combine(environment.ContentRootPath, MagicObjectHelper.頁面說明內容目錄, file);
        if (!File.Exists(documentPath))
        {
            // 發行環境若所有說明都變佔位內容，先看這筆 warning —— 通常是 csproj 漏了 Datas\Help 的 Content 規則。
            logger.LogWarning("Page help document not found: {DocumentPath}", documentPath);
            return string.Empty;
        }

        try
        {
            // ⚠️ 一律用 File.ReadAllText：它會去掉 BOM。留下 U+FEFF 的話第一行的 '#' 不在行首，前言解析會錯位。
            return File.ReadAllText(documentPath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to read page help document {DocumentPath}", documentPath);
            return string.Empty;
        }
    }
}

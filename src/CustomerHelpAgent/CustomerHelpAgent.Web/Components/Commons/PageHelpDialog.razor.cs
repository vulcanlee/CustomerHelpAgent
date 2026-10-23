using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;
using CustomerHelpAgent.Web.Components.Layout;

namespace CustomerHelpAgent.Web.Components.Commons;

/// <summary>說明窗的字體大小（0.9.121 起）。</summary>
public enum PageHelpFontSize
{
    Small,
    Medium,
    Large,
}

/// <summary>
/// 說明窗字體大小的選項、CSS class 與 localStorage 存值的對照。
/// 內文約 14／16／20px（0.9.124 起，預設「中」）；倍率寫在 PageHelpDialog.razor.css 的 <c>--page-help-scale</c>。
/// </summary>
public static class PageHelpFontSizes
{
    /// <summary>存在 localStorage 的鍵（每個瀏覽器各自記，所有頁面的說明窗共用）。</summary>
    public const string StorageKey = "app.helpFontSize";

    public static IReadOnlyList<(PageHelpFontSize Size, string Label)> Options { get; } =
    [
        (PageHelpFontSize.Small, "小"),
        (PageHelpFontSize.Medium, "中"),
        (PageHelpFontSize.Large, "大"),
    ];

    public static string CssClass(PageHelpFontSize size) => size switch
    {
        PageHelpFontSize.Small => "page-help-font-small",
        PageHelpFontSize.Large => "page-help-font-large",
        _ => "page-help-font-medium",
    };

    public static string ToStored(PageHelpFontSize size) => size.ToString().ToLowerInvariant();

    /// <summary>沒存過、被竄改或讀不到時一律回「中」。</summary>
    public static PageHelpFontSize Parse(string? stored) => stored switch
    {
        "small" => PageHelpFontSize.Small,
        "large" => PageHelpFontSize.Large,
        _ => PageHelpFontSize.Medium,
    };
}

/// <summary>
/// 頂欄的「操作說明」按鈕與說明窗。自己取路徑、自己載索引、自己訂閱導覽事件，
/// MainLayout 只負責把已授權的選單傳進來（供「相關頁面」依權限過濾）。
/// </summary>
/// <remarks>
/// 本元件與 MainLayout 一樣跨導覽存活：<see cref="OnInitializedAsync"/> 一個 circuit 只跑一次，
/// 之後每次導覽都是記憶體內比對。
/// </remarks>
public partial class PageHelpDialog : IDisposable
{
    /// <summary>章節導覽「全部」的識別鍵，刻意用不可能與章節標題相同的字串。</summary>
    private const string AllSectionsKey = "__all__";

    [Inject]
    private PageHelpService PageHelpService { get; set; } = default!;

    [Inject]
    private SidebarMenuService SidebarMenuService { get; set; } = default!;

    [Inject]
    private NavigationManager NavigationManager { get; set; } = default!;

    [Inject]
    private ILogger<PageHelpDialog> Logger { get; set; } = default!;

    [Inject]
    private IJSRuntime JS { get; set; } = default!;

    /// <summary>此人已授權的選單（與側邊欄同源）。</summary>
    [Parameter]
    public IReadOnlyList<SidebarMenuItemModel> MenuItems { get; set; } = [];

    private IReadOnlyList<PageHelpTopicModel> topics = [];
    private PageHelpTopicModel? topic;
    private PageHelpDocument? document;
    private List<PageHelpRelatedPage> visibleRelatedPages = [];
    private bool visible;
    private string activeSection = AllSectionsKey;
    private PageHelpFontSize fontSize = PageHelpFontSize.Medium;
    private bool fontSizeLoaded;

    private string ModalTitle => topic is null ? "操作說明" : $"{topic.Title}・操作說明";

    private IEnumerable<PageHelpSection> VisibleSections
        => document is null
            ? []
            : activeSection == AllSectionsKey
                ? document.Sections
                : document.Sections.Where(x => x.Heading == activeSection);

    protected override async Task OnInitializedAsync()
    {
        topics = await PageHelpService.LoadTopicsAsync();
        ResolveTopic();
        NavigationManager.LocationChanged += OnLocationChanged;
    }

    /// <summary>
    /// 導覽後必須關窗並重新比對路由；元件跨導覽存活，不關的話會繼續顯示上一頁的說明。
    /// </summary>
    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        visible = false;
        document = null;
        activeSection = AllSectionsKey;
        ResolveTopic();
        InvokeAsync(StateHasChanged);
    }

    private void ResolveTopic()
        => topic = PageHelpService.MatchTopic(topics, NavigationManager.ToBaseRelativePath(NavigationManager.Uri));

    private async Task OpenAsync()
    {
        if (topic is null)
        {
            return;
        }

        Logger.LogDebug("Opening page help dialog. Route={Route}", topic.Route);

        activeSection = AllSectionsKey;
        document = await PageHelpService.LoadDocumentAsync(topic);

        var authorizedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        SidebarMenuService.CollectUrls(MenuItems, authorizedUrls);
        visibleRelatedPages = PageHelpService.FilterVisibleRelatedPages(
            document.RelatedPages,
            await SidebarMenuService.LoadAllMenuUrlsAsync(),
            authorizedUrls);

        await LoadFontSizeAsync();
        visible = true;
    }

    private void Close() => visible = false;

    /// <summary>第一次開窗時讀一次上次的選擇；元件跨導覽存活，之後沿用記憶體裡的值。</summary>
    private async Task LoadFontSizeAsync()
    {
        if (fontSizeLoaded)
        {
            return;
        }

        fontSizeLoaded = true;
        try
        {
            fontSize = PageHelpFontSizes.Parse(await JS.InvokeAsync<string?>("localStorage.getItem", PageHelpFontSizes.StorageKey));
        }
        catch (JSException ex)
        {
            // 隱私模式或被封鎖時 localStorage 會拋例外：用「中」，不影響開窗。
            Logger.LogDebug(ex, "Page help font size could not be read; using the default.");
        }
    }

    private async Task SetFontSizeAsync(PageHelpFontSize size)
    {
        fontSize = size;
        try
        {
            await JS.InvokeVoidAsync("localStorage.setItem", PageHelpFontSizes.StorageKey, PageHelpFontSizes.ToStored(size));
        }
        catch (JSException ex)
        {
            // 存不了只是下次回到「中」，目前畫面照樣套用。
            Logger.LogDebug(ex, "Page help font size could not be saved.");
        }
    }

    private string FontButtonCssClass(PageHelpFontSize size)
        => fontSize == size
            ? "page-help-font-button page-help-font-button-active"
            : "page-help-font-button";

    private void SelectSection(string sectionKey) => activeSection = sectionKey;

    private string NavCssClass(string sectionKey)
        => activeSection == sectionKey
            ? "page-help-nav-item page-help-nav-item-active"
            : "page-help-nav-item";

    private void NavigateTo(string route)
    {
        // 先關窗再導覽（LocationChanged 也會關，這裡明示語意、不依賴事件順序）。
        visible = false;
        NavigationManager.NavigateTo(route);
    }

    public void Dispose() => NavigationManager.LocationChanged -= OnLocationChanged;
}

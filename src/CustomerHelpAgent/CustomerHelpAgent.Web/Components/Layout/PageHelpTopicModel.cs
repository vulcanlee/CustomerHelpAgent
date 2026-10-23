namespace CustomerHelpAgent.Web.Components.Layout;

/// <summary>
/// <c>Datas/HelpTopics.json</c> 的一筆：一條路由的頁面操作說明登記。比照 <see cref="SidebarMenuItemModel"/>，純 POCO。
/// </summary>
/// <remarks>
/// 刻意不把說明掛在 Menu.json：Menu.json 只涵蓋選單頁（<c>/ChangePassword</c> 不在裡面），
/// 而且選單的網址比對是<b>前綴</b>語意（<c>MainLayout.IsMatchingUrl</c>），說明需要<b>精確</b>比對。
/// </remarks>
public sealed class PageHelpTopicModel
{
    /// <summary>路由樣板。參數段寫成 <c>{id}</c>（不帶型別約束），比對時該段萬用。</summary>
    public string Route { get; set; } = string.Empty;

    /// <summary>對話窗標題（會接上「・操作說明」）。與選單名稱一致。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary><c>Datas/Help</c> 底下的檔名（不含路徑），須符合 <see cref="PageHelpService.ToSlugFileName"/>。</summary>
    public string File { get; set; } = string.Empty;
}

namespace CustomerHelpAgent.Models.Systems;

/// <summary>
/// 售後展示的示範資料（<c>DemoSettings</c> 區段）。<see cref="Enabled"/> 時啟動會建立示範角色、帳號與商品、訂單、售後規則
/// （<c>DemoAccountSeeder</c>、<c>DemoCatalogSeeder</c>；只補不存在的，不覆寫後來的修改）。
/// 出貨值：<c>appsettings.json</c> 開啟、<c>appsettings.Production.json</c> 關閉。
/// </summary>
public class DemoSettings
{
    public const string SectionName = "DemoSettings";

    /// <summary>是否建立示範資料。</summary>
    public bool Enabled { get; set; }

    /// <summary>示範帳號（customer1、customer2、agent1、merchant1）共用的密碼，只在建立帳號時使用；開啟時至少 8 個字元。</summary>
    public string DemoUserPassword { get; set; } = "";
}

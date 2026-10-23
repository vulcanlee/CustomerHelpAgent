using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Models.Systems;

namespace CustomerHelpAgent.Business.Startup;

/// <summary>
/// 售後展示的示範商品、訂單、物流與售後規則（<see cref="DemoSeedData.CreateCatalog"/>）。
/// <c>DemoSettings:Enabled</c> 且資料庫還沒有任何商品時才寫入（只寫一次）；簽收、出貨等相對時間以寫入當下的本地時間計算。
/// 訂單掛在 <see cref="DemoAccountSeeder"/>（Order 50）建立的 customer1、customer2 底下。
/// </summary>
public sealed class DemoCatalogSeeder : IDatabaseSeeder
{
    private readonly BackendDBContext dbContext;
    private readonly DemoSettings demoSettings;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<DemoCatalogSeeder> logger;

    public DemoCatalogSeeder(
        BackendDBContext dbContext,
        IOptions<DemoSettings> demoSettings,
        TimeProvider timeProvider,
        ILogger<DemoCatalogSeeder> logger)
    {
        this.dbContext = dbContext;
        this.demoSettings = demoSettings.Value;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    public int Order => 60;

    public string Name => "售後展示示範商品與訂單";

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        if (!demoSettings.Enabled || await dbContext.Product.AnyAsync(cancellationToken))
        {
            return;
        }

        var catalog = await WriteCatalogAsync(dbContext, timeProvider.GetLocalNow().DateTime, cancellationToken);

        logger.LogInformation(
            "Seeded demo catalog. ProductCount={ProductCount}, OrderCount={OrderCount}, PolicyCount={PolicyCount}",
            catalog.Products.Count, catalog.Orders.Count, catalog.Policies.Count);
    }

    /// <summary>
    /// 以 <see cref="DemoSeedData.CreateCatalog"/> 建立示範目錄並寫入（含 <c>SaveChanges</c>；交易由呼叫端決定）。
    /// 本 seeder 與一鍵重置（<c>DemoResetService</c>）共用。示範顧客以帳號查詢且不理會軟刪除（軟刪除的帳號仍掛著訂單）；找不到就拋例外。
    /// </summary>
    internal static async Task<DemoSeedData.DemoCatalog> WriteCatalogAsync(BackendDBContext dbContext, DateTime now, CancellationToken cancellationToken)
    {
        var userIdByAccount = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var account in new[] { DemoSeedData.Customer1, DemoSeedData.Customer2 })
        {
            userIdByAccount[account] = await dbContext.MyUser
                .IgnoreQueryFilters([ISoftDeletable.FilterName])
                .Where(x => x.Account == account)
                .OrderBy(x => x.IsDeleted)
                .ThenBy(x => x.Id)
                .Select(x => (int?)x.Id)
                .FirstOrDefaultAsync(cancellationToken)
                ?? throw new InvalidOperationException($"找不到示範帳號「{account}」，無法建立示範訂單；DemoAccountSeeder 應先執行。");
        }

        var catalog = DemoSeedData.CreateCatalog(now, userIdByAccount);
        dbContext.Product.AddRange(catalog.Products);
        dbContext.SalesOrder.AddRange(catalog.Orders);
        dbContext.Shipment.AddRange(catalog.Shipments);
        dbContext.AfterSalesPolicy.AddRange(catalog.Policies);
        await dbContext.SaveChangesAsync(cancellationToken);
        return catalog;
    }
}

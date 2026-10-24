using System.Globalization;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Share.Shop;

namespace CustomerHelpAgent.Business.Startup;

/// <summary>
/// 售後展示的示範資料定義（角色、帳號、商品、訂單、物流、售後規則）。只有資料、沒有 I/O；
/// 寫入由 <see cref="DemoAccountSeeder"/>、<see cref="DemoCatalogSeeder"/> 負責，日後的「重置示範資料」也重用這一份。
/// 相對時間（簽收、出貨、規則生效）一律以呼叫端傳入的 <c>now</c>（本地時間）計算。
/// 收件人、地址與手機都是虛構值。
/// </summary>
public static class DemoSeedData
{
    public const string Customer1 = "customer1";
    public const string Customer2 = "customer2";
    public const string Agent1 = "agent1";
    public const string Merchant1 = "merchant1";

    public const string RunningShoesCode = "SH-RUN01";
    public const string JacketCode = "AP-JKT01";

    public const string PolicySource = "售後服務說明（2026 年版）";

    public sealed record DemoRole(string Name, IReadOnlyList<string> PermissionKeys);

    public sealed record DemoAccount(string Account, string Name, string RoleName);

    /// <summary><see cref="CreateCatalog"/> 的結果：彼此以導覽屬性相連，一次 <c>SaveChanges</c> 即可寫入。</summary>
    public sealed record DemoCatalog(
        IReadOnlyList<Product> Products,
        IReadOnlyList<SalesOrder> Orders,
        IReadOnlyList<Shipment> Shipments,
        IReadOnlyList<AfterSalesPolicy> Policies);

    public static IReadOnlyList<DemoRole> Roles { get; } =
    [
        new(MagicObjectHelper.顧客角色, [MagicObjectHelper.角色_顧客商店, MagicObjectHelper.角色_登出]),
        new(MagicObjectHelper.客服角色,
        [
            MagicObjectHelper.角色_售後服務,
            MagicObjectHelper.角色_售後審核,
            MagicObjectHelper.角色_Agent執行紀錄,
            MagicObjectHelper.角色_首頁,
            MagicObjectHelper.角色_登出,
        ]),
        new(MagicObjectHelper.店家角色,
        [
            MagicObjectHelper.角色_售後服務,
            MagicObjectHelper.角色_售後審核,
            MagicObjectHelper.角色_Agent執行紀錄,
            MagicObjectHelper.角色_售後規則,
            // 效益對照（M6）：店家只能看；修改假設值留給管理員。
            PermissionKey.For(MagicObjectHelper.角色_效益對照, PermissionActions.View),
            MagicObjectHelper.角色_首頁,
            MagicObjectHelper.角色_登出,
        ]),
    ];

    public static IReadOnlyList<DemoAccount> Accounts { get; } =
    [
        new(Customer1, "王大同", MagicObjectHelper.顧客角色),
        new(Customer2, "陳怡君", MagicObjectHelper.顧客角色),
        new(Agent1, "客服小李", MagicObjectHelper.客服角色),
        new(Merchant1, "店長阿明", MagicObjectHelper.店家角色),
    ];

    /// <summary>
    /// 建立示範目錄（商品、規格、訂單、物流、售後規則）。⚠️ 每一列都帶<b>固定 Id</b>（1.0.5 起）：初次寫入與一鍵重置
    /// 都經 <c>DemoCatalogSeeder.WriteCatalogAsync</c>，產生完全相同的 Id，錄製檔裡工具參數／結果的 <c>orderItemId</c> 等才能在重置後回放。
    /// 編號：商品 1 慢跑鞋、2 外套；規格依建立順序 1–22（慢跑鞋 黑 38–44＝1–7、白 38–44＝8–14；外套 藏青 S/M/L/XL＝15–18、灰＝19–22）；
    /// 訂單與明細 1–4（＝SO-DEMO-0001～0004，一張訂單一筆明細）；物流 1（訂單 4）、物流事件 1–3；售後規則 1–5（依下方順序）。
    /// 售後申請與 Agent 相關資料表不固定 Id。新增或調整示範資料時只能往後加編號，不要改動既有編號。
    /// </summary>
    /// <param name="now">本地時間的「現在」。</param>
    /// <param name="userIdByAccount">至少含 <see cref="Customer1"/>、<see cref="Customer2"/> 的使用者 Id。</param>
    public static DemoCatalog CreateCatalog(DateTime now, IReadOnlyDictionary<string, int> userIdByAccount)
    {
        var shoes = new Product { Code = RunningShoesCode, Name = "城市慢跑鞋", Category = ProductCategories.Shoes, Price = 2680m };
        foreach (var color in new[] { "黑", "白" })
        {
            for (var size = 38; size <= 44; size++)
            {
                var stock = (color, size) switch
                {
                    ("黑", 41) => 5,
                    ("白", 42) => 0,
                    _ => 3,
                };
                shoes.Variants.Add(new ProductVariant { Color = color, Size = size.ToString(CultureInfo.InvariantCulture), Stock = stock });
            }
        }

        var jacket = new Product { Code = JacketCode, Name = "輕量防風外套", Category = ProductCategories.Apparel, Price = 1980m };
        foreach (var color in new[] { "藏青", "灰" })
        {
            foreach (var size in new[] { "S", "M", "L", "XL" })
            {
                jacket.Variants.Add(new ProductVariant { Color = color, Size = size, Stock = 4 });
            }
        }

        var customer1 = userIdByAccount[Customer1];
        var customer2 = userIdByAccount[Customer2];

        var orders = new List<SalesOrder>
        {
            // 旗艦劇本：顧客 1 的兩雙鞋都在 7 天內簽收（換尺碼、品質問題）。
            Order("SO-DEMO-0001", customer1, "王大同", "臺北市中正區示範路 1 號", "0900-000-001",
                now.AddDays(-5), now.AddDays(-3), Variant(shoes, "黑", "40"), shoes.Price),
            Order("SO-DEMO-0002", customer1, "王大同", "臺北市中正區示範路 1 號", "0900-000-001",
                now.AddDays(-4), now.AddDays(-2), Variant(shoes, "白", "41"), shoes.Price),
            // 顧客 2：已超過 7 天的外套、已出貨未送達的外套（催件）。
            Order("SO-DEMO-0003", customer2, "陳怡君", "新竹市東區範例街 22 號", "0900-000-002",
                now.AddDays(-13), now.AddDays(-10), Variant(jacket, "藏青", "M"), jacket.Price),
            Order("SO-DEMO-0004", customer2, "陳怡君", "新竹市東區範例街 22 號", "0900-000-002",
                now.AddDays(-5), null, Variant(jacket, "灰", "L"), jacket.Price),
        };

        var shipment = new Shipment
        {
            SalesOrder = orders[3],
            Carrier = "示範物流",
            TrackingNo = "DEMO-TRK-0004",
            StatusText = "運送中",
            ShippedAt = now.AddDays(-4),
            Events =
            [
                new ShipmentEvent { OccurredAt = now.AddDays(-4), Location = "臺北倉儲中心", Description = "商品已出貨" },
                new ShipmentEvent { OccurredAt = now.AddDays(-3), Location = "桃園轉運站", Description = "包裹已抵達轉運站" },
                new ShipmentEvent { OccurredAt = now.AddDays(-2), Location = "新竹營業所", Description = "配送延遲，待重新安排配送" },
            ],
        };

        var effectiveFrom = now.AddDays(-90);
        var policies = new List<AfterSalesPolicy>
        {
            Policy("SIZE-EXCHANGE", AfterSalesType.SizeExchange, "換尺碼", 7, requiresUnworn: true, requiresPhoto: false,
                "簽收 7 天內、商品未穿過（未下水、吊牌完整），可申請換同款其他尺碼，一次為限。", effectiveFrom),
            Policy("COLOR-EXCHANGE", AfterSalesType.ColorExchange, "換顏色", 7, requiresUnworn: true, requiresPhoto: false,
                "簽收 7 天內、商品未穿過，可申請換同款其他顏色，一次為限。", effectiveFrom),
            Policy("RETURN-REFUND", AfterSalesType.ReturnRefund, "退貨退款", 7, requiresUnworn: true, requiresPhoto: false,
                "簽收 7 天內可申請退貨退款，商品需保持全新、包裝完整；退款於收到退貨後 5 個工作天內退回原付款方式。", effectiveFrom),
            Policy("QUALITY-ISSUE", AfterSalesType.QualityIssue, "品質問題", 180, requiresUnworn: false, requiresPhoto: true,
                "商品出現開膠、脫線等品質問題，簽收 180 天內可申請，需先上傳照片供客服核驗，核驗通過後可換新或退款。", effectiveFrom),
            Policy("SHIPMENT-URGE", AfterSalesType.ShipmentUrge, "物流催件", null, requiresUnworn: false, requiresPhoto: false,
                "訂單出貨後超過 3 天未送達可申請催件，客服將於 1 個工作天內回覆物流進度。", effectiveFrom),
        };

        AssignFixedIds([shoes, jacket], orders, shipment, policies);
        return new DemoCatalog([shoes, jacket], orders, [shipment], policies);
    }

    /// <summary>依建立順序指定固定 Id（見 <see cref="CreateCatalog"/> 的編號說明）。</summary>
    private static void AssignFixedIds(IReadOnlyList<Product> products, IReadOnlyList<SalesOrder> orders, Shipment shipment, IReadOnlyList<AfterSalesPolicy> policies)
    {
        var variantId = 0;
        for (var i = 0; i < products.Count; i++)
        {
            products[i].Id = i + 1;
            foreach (var variant in products[i].Variants)
            {
                variant.Id = ++variantId;
            }
        }

        var itemId = 0;
        for (var i = 0; i < orders.Count; i++)
        {
            orders[i].Id = i + 1;
            foreach (var item in orders[i].Items)
            {
                item.Id = ++itemId;
            }
        }

        shipment.Id = 1;
        for (var i = 0; i < shipment.Events.Count; i++)
        {
            shipment.Events[i].Id = i + 1;
        }

        for (var i = 0; i < policies.Count; i++)
        {
            policies[i].Id = i + 1;
        }
    }

    private static ProductVariant Variant(Product product, string color, string size)
        => product.Variants.Single(x => x.Color == color && x.Size == size);

    private static SalesOrder Order(
        string orderNo, int customerUserId, string recipientName, string address, string phone,
        DateTime orderedAt, DateTime? deliveredAt, ProductVariant variant, decimal unitPrice)
        => new()
        {
            OrderNo = orderNo,
            CustomerUserId = customerUserId,
            OrderedAt = orderedAt,
            DeliveredAt = deliveredAt,
            RecipientName = recipientName,
            ShippingAddress = address,
            RecipientPhone = phone,
            Items = [new SalesOrderItem { ProductVariant = variant, Quantity = 1, UnitPrice = unitPrice }],
        };

    private static AfterSalesPolicy Policy(
        string code, AfterSalesType type, string title, int? windowDays, bool requiresUnworn, bool requiresPhoto,
        string content, DateTime effectiveFrom)
        => new()
        {
            Code = code,
            Type = type,
            Category = ProductCategories.All,
            Title = title,
            Content = content,
            Source = PolicySource,
            Version = 1,
            EffectiveFrom = effectiveFrom,
            WindowDays = windowDays,
            RequiresUnworn = requiresUnworn,
            RequiresPhoto = requiresPhoto,
            CreatedAt = effectiveFrom,
        };
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Models.Shop;

namespace CustomerHelpAgent.Business.Services.DataAccess;

/// <summary>顧客訂單服務（唯讀）：只回傳登入顧客本人的訂單，且不會回傳收件人手機。</summary>
public class ShopOrderService
{
    private readonly IDbContextFactory<BackendDBContext> contextFactory;
    private readonly ILogger<ShopOrderService> logger;

    public ShopOrderService(IDbContextFactory<BackendDBContext> contextFactory, ILogger<ShopOrderService> logger)
    {
        this.contextFactory = contextFactory;
        this.logger = logger;
    }

    private static string BuildStatusText(DateTime? deliveredAt, DateTime? shippedAt) =>
        deliveredAt is not null ? "已簽收" : shippedAt is not null ? "配送中" : "處理中";

    private static ShopOrderItemView ToItemView(SalesOrderItem x) => new(
        x.Id,
        x.ProductVariant!.Product!.Name,
        x.ProductVariant.Product.Category,
        x.ProductVariant.Color,
        x.ProductVariant.Size,
        x.Quantity,
        x.UnitPrice);

    public async Task<IReadOnlyList<ShopOrderSummary>> GetMyOrdersAsync(int customerUserId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var orders = await context.SalesOrder
            .AsNoTracking()
            .Include(x => x.Items.OrderBy(i => i.Id)).ThenInclude(i => i.ProductVariant!).ThenInclude(v => v.Product)
            .Where(x => x.CustomerUserId == customerUserId)
            .OrderByDescending(x => x.OrderedAt).ThenByDescending(x => x.Id)
            .ToListAsync();

        var orderIds = orders.Select(x => x.Id).ToList();
        var shippedAtByOrder = (await context.Shipment
            .AsNoTracking()
            .Where(x => orderIds.Contains(x.SalesOrderId))
            .OrderBy(x => x.Id)
            .Select(x => new { x.SalesOrderId, x.ShippedAt })
            .ToListAsync())
            .GroupBy(x => x.SalesOrderId)
            .ToDictionary(g => g.Key, g => g.First().ShippedAt);

        return orders
            .Select(o => ToSummary(o, shippedAtByOrder.GetValueOrDefault(o.Id)))
            .ToList();
    }

    public async Task<ShopOrderDetail?> GetMyOrderDetailAsync(int customerUserId, int orderId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var order = await context.SalesOrder
            .AsNoTracking()
            .Include(x => x.Items.OrderBy(i => i.Id)).ThenInclude(i => i.ProductVariant!).ThenInclude(v => v.Product)
            .FirstOrDefaultAsync(x => x.Id == orderId && x.CustomerUserId == customerUserId);
        if (order is null)
        {
            logger.LogInformation("Order not found for customer. OrderId={OrderId}", orderId);
            return null;
        }

        var shipment = await context.Shipment
            .AsNoTracking()
            .Include(x => x.Events)
            .Where(x => x.SalesOrderId == order.Id)
            .OrderBy(x => x.Id)
            .FirstOrDefaultAsync();

        var shipmentView = shipment is null
            ? null
            : new ShopShipmentView(
                shipment.Carrier,
                shipment.TrackingNo,
                shipment.StatusText,
                shipment.ShippedAt,
                shipment.Events
                    .OrderBy(e => e.OccurredAt).ThenBy(e => e.Id)
                    .Select(e => new ShopShipmentEventView(e.OccurredAt, e.Location, e.Description))
                    .ToList());

        return new ShopOrderDetail(
            ToSummary(order, shipment?.ShippedAt),
            order.RecipientName,
            order.ShippingAddress,
            shipmentView);
    }

    public async Task<ShopOrderItemView?> GetMyOrderItemAsync(int customerUserId, int orderItemId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var item = await context.SalesOrderItem
            .AsNoTracking()
            .Include(x => x.ProductVariant!).ThenInclude(v => v.Product)
            .FirstOrDefaultAsync(x => x.Id == orderItemId && x.SalesOrder!.CustomerUserId == customerUserId);
        if (item is null)
        {
            logger.LogInformation("Order item not found for customer. OrderItemId={OrderItemId}", orderItemId);
            return null;
        }

        return ToItemView(item);
    }

    /// <summary>
    /// 訂單商品所屬商品的全部規格（含目前這一個，依 Id 排序），供換尺碼／換顏色的下拉選項使用。
    /// 不是本人的訂單商品回空清單。刻意不看庫存：傳統流程由客服審核時再判斷。
    /// </summary>
    public async Task<IReadOnlyList<(string Color, string Size)>> GetVariantOptionsAsync(int customerUserId, int orderItemId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var productId = await context.SalesOrderItem
            .AsNoTracking()
            .Where(x => x.Id == orderItemId && x.SalesOrder!.CustomerUserId == customerUserId)
            .Select(x => (int?)x.ProductVariant!.ProductId)
            .FirstOrDefaultAsync();
        if (productId is null)
        {
            logger.LogInformation("Order item not found for customer. OrderItemId={OrderItemId}", orderItemId);
            return [];
        }

        var variants = await context.ProductVariant
            .AsNoTracking()
            .Where(x => x.ProductId == productId)
            .OrderBy(x => x.Id)
            .Select(x => new { x.Color, x.Size })
            .ToListAsync();
        return variants.Select(x => (x.Color, x.Size)).ToList();
    }

    private static ShopOrderSummary ToSummary(SalesOrder order, DateTime? shippedAt) => new(
        order.Id,
        order.OrderNo,
        order.OrderedAt,
        order.DeliveredAt,
        BuildStatusText(order.DeliveredAt, shippedAt),
        order.Items.Select(ToItemView).ToList());
}

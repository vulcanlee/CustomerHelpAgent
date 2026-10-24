using System.Globalization;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Shop;
using CustomerHelpAgent.Share.Shop;

namespace CustomerHelpAgent.Business.Services.DataAccess;

/// <summary>
/// 售後決策執行（M4）：客服決定（核准／駁回／補件）的唯一寫入點，傳統審核頁與 AI 審核流程共用。
/// 純程式、不呼叫模型；單一交易內完成「狀態守門的條件更新 →（核准時）開處理單、扣庫存或加物流事件」，
/// commit 之後才通知顧客與寫稽核（兩者失敗都不影響已生效的決定）。
/// ⚠️ 狀態以 <c>ExecuteUpdateAsync</c> 加 <c>Status == Submitted</c> 條件更新，兩位客服同時核准只有一個成功；
/// 庫存以 <c>Stock &gt;= Quantity</c> 條件扣除，不會扣成負數；扣不到就整筆回滾。
/// </summary>
public class AfterSalesDecisionService
{
    public const string OutOfStockMessage = "目標規格已無庫存，無法核准；請改駁回或聯繫顧客。";

    /// <summary>品質問題補寄同規格時，同規格已無庫存。</summary>
    public const string SameSpecOutOfStockMessage = "同規格已無庫存，無法補寄；請改駁回或聯繫顧客。";
    public const string MoreInfoRequiredMessage = "請填寫需要顧客補充的內容。";
    public const string FailedMessage = "審核失敗。";
    private const string ShipmentFollowUpDescription = "客服已催件，物流中心處理中";
    private const string ShipmentFollowUpLocation = "客服中心";
    private const string CustomerPageLink = "/shop/after-sales";

    private readonly IDbContextFactory<BackendDBContext> contextFactory;
    private readonly INotificationSender notificationSender;
    private readonly IAuditLogService auditLogService;
    private readonly CurrentUserService currentUserService;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<AfterSalesDecisionService> logger;

    public AfterSalesDecisionService(
        IDbContextFactory<BackendDBContext> contextFactory,
        INotificationSender notificationSender,
        IAuditLogService auditLogService,
        CurrentUserService currentUserService,
        TimeProvider timeProvider,
        ILogger<AfterSalesDecisionService> logger)
    {
        this.contextFactory = contextFactory;
        this.notificationSender = notificationSender;
        this.auditLogService = auditLogService;
        this.currentUserService = currentUserService;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    private sealed record RequestInfo(
        string RequestNo,
        int CustomerUserId,
        AfterSalesType Type,
        string? TargetSize,
        string? TargetColor,
        int ProductId,
        int VariantId,
        string Color,
        string Size,
        int SalesOrderId,
        int Quantity,
        decimal UnitPrice);

    public async Task<DecisionResult> ApplyAsync(int requestId, StaffDecisionKind decision, string? note, int? staffUserId)
    {
        var text = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (decision == StaffDecisionKind.NeedMoreInfo && text is null)
        {
            return Fail(MoreInfoRequiredMessage);
        }

        var now = timeProvider.GetLocalNow().DateTime;
        var newStatus = TargetStatus(decision);

        RequestInfo info;
        AfterSalesFulfillment? fulfillment = null;
        try
        {
            await using var context = await contextFactory.CreateDbContextAsync();
            await using var transaction = await context.Database.BeginTransactionAsync();

            var query = context.AfterSalesRequest.Where(x => x.Id == requestId && x.Status == AfterSalesStatus.Submitted);
            // 一律覆寫 StaffNote：沒填備註時要清掉要求補件時留下的備註，不能讓顧客在結案後還看到「請補照片」。
            // 要求補件只是把球丟回顧客，不算結案，因此不寫 DecidedAt／DecidedByUserId（與既有行為一致）。
            var affected = decision == StaffDecisionKind.NeedMoreInfo
                ? await query.ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, newStatus)
                    .SetProperty(x => x.StaffNote, text)
                    .SetProperty(x => x.UpdatedAt, now))
                : await query.ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, newStatus)
                    .SetProperty(x => x.UpdatedAt, now)
                    .SetProperty(x => x.DecidedAt, now)
                    .SetProperty(x => x.DecidedByUserId, staffUserId)
                    .SetProperty(x => x.StaffNote, text));
            if (affected == 0)
            {
                logger.LogInformation("Decision skipped because the status changed. RequestId={RequestId}", requestId);
                return Fail(AfterSalesRequestService.StatusChangedMessage);
            }

            info = await context.AfterSalesRequest
                .AsNoTracking()
                .Where(x => x.Id == requestId)
                .Select(x => new RequestInfo(
                    x.RequestNo,
                    x.CustomerUserId,
                    x.Type,
                    x.TargetSize,
                    x.TargetColor,
                    x.SalesOrderItem!.ProductVariant!.ProductId,
                    x.SalesOrderItem.ProductVariantId,
                    x.SalesOrderItem.ProductVariant.Color,
                    x.SalesOrderItem.ProductVariant.Size,
                    x.SalesOrderItem.SalesOrderId,
                    x.SalesOrderItem.Quantity,
                    x.SalesOrderItem.UnitPrice))
                .SingleAsync();

            if (decision == StaffDecisionKind.Approve)
            {
                fulfillment = await FulfillAsync(context, requestId, info, now);
                if (fulfillment is null)
                {
                    logger.LogInformation("Approval rolled back because the target variant is out of stock. RequestId={RequestId}", requestId);
                    return Fail(info.Type == AfterSalesType.QualityIssue ? SameSpecOutOfStockMessage : OutOfStockMessage);
                }
            }

            await transaction.CommitAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to apply after-sales decision. RequestId={RequestId}, Decision={Decision}", requestId, decision);
            return Fail(FailedMessage);
        }

        logger.LogInformation(
            "After-sales request decided. RequestId={RequestId}, RequestStatus={RequestStatus}, FulfillmentKind={FulfillmentKind}",
            requestId, newStatus, fulfillment?.Kind);
        // 先通知顧客再寫稽核：決定已 commit，稽核失敗不可讓顧客收不到通知，也不可往外丟例外。
        await NotifyCustomerAsync(info, newStatus, fulfillment, text, now);
        try
        {
            await WriteAuditsAsync(requestId, decision, newStatus, fulfillment, staffUserId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to write audit log for after-sales decision. RequestId={RequestId}", requestId);
        }

        return new DecisionResult(true, string.Empty, newStatus, fulfillment?.ReferenceNo);
    }

    /// <summary>
    /// 這筆申請目前是否正是「這個決定」生效後的樣子：狀態是這個決定的目標狀態，而且核准／駁回由同一位客服決定
    /// （要求補件不記決定者，改比對補件說明）。是就回成功結果（核准附處理單號），否則回 null。
    /// 用途：<see cref="ApplyAsync"/> 已 commit，呼叫端卻在記下結果前當機或失敗（例如 Workflow 執行節點的 superstep 沒寫進 checkpoint），
    /// 重跑時得到「狀態已變更」——這時用它辨認「其實是自己這次的決定已經生效」，不要當成失敗。
    /// </summary>
    public async Task<DecisionResult?> GetAppliedResultAsync(int requestId, StaffDecisionKind decision, string? note, int? staffUserId)
    {
        var text = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        var target = TargetStatus(decision);
        await using var context = await contextFactory.CreateDbContextAsync();
        var row = await context.AfterSalesRequest
            .AsNoTracking()
            .Where(x => x.Id == requestId)
            .Select(x => new { x.Status, x.DecidedByUserId, x.StaffNote })
            .FirstOrDefaultAsync();
        if (row is null || row.Status != target)
        {
            return null;
        }

        var sameDecision = decision == StaffDecisionKind.NeedMoreInfo ? row.StaffNote == text : row.DecidedByUserId == staffUserId;
        if (!sameDecision)
        {
            return null;
        }

        var referenceNo = decision == StaffDecisionKind.Approve
            ? await context.AfterSalesFulfillment
                .AsNoTracking()
                .Where(x => x.AfterSalesRequestId == requestId)
                .OrderBy(x => x.Id)
                .Select(x => x.ReferenceNo)
                .FirstOrDefaultAsync()
            : null;
        return new DecisionResult(true, string.Empty, target, referenceNo);
    }

    public async Task<AfterSalesFulfillmentView?> GetFulfillmentAsync(int requestId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        return await context.AfterSalesFulfillment
            .AsNoTracking()
            .Where(x => x.AfterSalesRequestId == requestId)
            .Select(ToView)
            .FirstOrDefaultAsync();
    }

    /// <summary>一次取回多筆申請的處理單（列表頁用，避免逐筆查詢）；沒有處理單的申請不在結果內。</summary>
    public async Task<IReadOnlyDictionary<int, AfterSalesFulfillmentView>> GetFulfillmentsAsync(IReadOnlyCollection<int> requestIds)
    {
        if (requestIds.Count == 0)
        {
            return new Dictionary<int, AfterSalesFulfillmentView>();
        }

        await using var context = await contextFactory.CreateDbContextAsync();
        var rows = await context.AfterSalesFulfillment
            .AsNoTracking()
            .Where(x => requestIds.Contains(x.AfterSalesRequestId))
            .OrderBy(x => x.Id)
            .Select(ToView)
            .ToListAsync();
        return rows.GroupBy(x => x.AfterSalesRequestId).ToDictionary(g => g.Key, g => g.First());
    }

    /// <summary>處理單 → 檢視模型的唯一投影（單筆與批次查詢共用）。</summary>
    private static readonly Expression<Func<AfterSalesFulfillment, AfterSalesFulfillmentView>> ToView = x => new AfterSalesFulfillmentView(
        x.Id,
        x.AfterSalesRequestId,
        x.Kind,
        x.ReferenceNo,
        x.ProductVariantId,
        x.ProductVariant == null ? null : x.ProductVariant.Color + " / " + x.ProductVariant.Size,
        x.Quantity,
        x.RefundAmount,
        x.Note,
        x.CreatedAt);

    private static DecisionResult Fail(string message) => new(false, message, null, null);

    private static AfterSalesStatus TargetStatus(StaffDecisionKind decision) => decision switch
    {
        StaffDecisionKind.Approve => AfterSalesStatus.Approved,
        StaffDecisionKind.Reject => AfterSalesStatus.Rejected,
        _ => AfterSalesStatus.NeedMoreInfo,
    };

    /// <summary>核准時的處理：開處理單、扣庫存或加物流事件。目標規格（品質問題為同規格）無庫存回 null（呼叫端整筆回滾）。</summary>
    private static async Task<AfterSalesFulfillment?> FulfillAsync(BackendDBContext context, int requestId, RequestInfo info, DateTime now)
    {
        var fulfillment = new AfterSalesFulfillment
        {
            AfterSalesRequestId = requestId,
            // 處理單號要用到 Id，先放唯一的暫時值，取得 Id 後在同一個交易內改成正式單號。
            ReferenceNo = Guid.NewGuid().ToString("N"),
            ProductVariantId = info.VariantId,
            Quantity = info.Quantity,
            CreatedAt = now,
        };

        string prefix;
        switch (info.Type)
        {
            case AfterSalesType.SizeExchange or AfterSalesType.ColorExchange:
                var targetSize = info.Type == AfterSalesType.SizeExchange ? info.TargetSize : info.Size;
                var targetColor = info.Type == AfterSalesType.ColorExchange ? info.TargetColor : info.Color;
                var targetId = await context.ProductVariant
                    .AsNoTracking()
                    .Where(x => x.ProductId == info.ProductId && x.Color == targetColor && x.Size == targetSize)
                    .OrderBy(x => x.Id)
                    .Select(x => (int?)x.Id)
                    .FirstOrDefaultAsync();
                if (targetId is not { } target || !await DecrementStockAsync(context, target, info.Quantity))
                {
                    return null;
                }

                prefix = "EX";
                fulfillment.Kind = FulfillmentKind.ExchangeOrder;
                fulfillment.ProductVariantId = target;
                fulfillment.Note = $"換貨：{info.Color} / {info.Size} → {targetColor} / {targetSize}";
                break;
            case AfterSalesType.QualityIssue:
                if (!await DecrementStockAsync(context, info.VariantId, info.Quantity))
                {
                    return null;
                }

                prefix = "RP";
                fulfillment.Kind = FulfillmentKind.ReplacementOrder;
                fulfillment.Note = $"品質問題補寄同規格：{info.Color} / {info.Size}";
                break;
            case AfterSalesType.ReturnRefund:
                prefix = "RF";
                fulfillment.Kind = FulfillmentKind.RefundRecord;
                fulfillment.RefundAmount = info.UnitPrice * info.Quantity;
                fulfillment.Note = "退貨退款";
                break;
            default:
                prefix = "SF";
                fulfillment.Kind = FulfillmentKind.ShipmentFollowUp;
                fulfillment.Note = "客服催件";
                var shipmentId = await context.Shipment
                    .AsNoTracking()
                    .Where(x => x.SalesOrderId == info.SalesOrderId)
                    .OrderByDescending(x => x.Id)
                    .Select(x => (int?)x.Id)
                    .FirstOrDefaultAsync();
                if (shipmentId is { } id)
                {
                    context.ShipmentEvent.Add(new ShipmentEvent
                    {
                        ShipmentId = id,
                        OccurredAt = now,
                        Location = ShipmentFollowUpLocation,
                        Description = ShipmentFollowUpDescription,
                    });
                }

                break;
        }

        context.AfterSalesFulfillment.Add(fulfillment);
        await context.SaveChangesAsync();
        fulfillment.ReferenceNo = string.Create(CultureInfo.InvariantCulture, $"{prefix}{now:yyyyMMdd}{fulfillment.Id:D4}");
        await context.SaveChangesAsync();
        return fulfillment;
    }

    private static async Task<bool> DecrementStockAsync(BackendDBContext context, int variantId, int quantity)
        => await context.ProductVariant
            .Where(x => x.Id == variantId && x.Stock >= quantity)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Stock, x => x.Stock - quantity)) > 0;

    private async Task NotifyCustomerAsync(RequestInfo info, AfterSalesStatus status, AfterSalesFulfillment? fulfillment, string? note, DateTime decidedAt)
    {
        var (title, body) = status switch
        {
            AfterSalesStatus.Approved => ($"售後申請 {info.RequestNo} 已核准", $"處理單號：{fulfillment?.ReferenceNo}"),
            AfterSalesStatus.Rejected => ($"售後申請 {info.RequestNo} 已駁回", note ?? "請至「我的申請」查看。"),
            _ => ($"售後申請 {info.RequestNo} 需要補件", note ?? "請至「我的申請」補件。"),
        };
        try
        {
            var result = await notificationSender.SendAsync(new NotificationRequest(
                NotificationCategories.AfterSalesUpdated,
                title,
                body,
                CustomerPageLink,
                NotificationTarget.Users(info.CustomerUserId),
                AlsoEmail: false,
                // 尾碼用本次決定的時間：同一申請可能多次要求補件，不加尾碼第二次通知會被去重吃掉。
                SourceKey: $"aftersales:{info.RequestNo}:{status}:{decidedAt.Ticks}"));
            if (result.Failed)
            {
                logger.LogWarning("Customer notification for after-sales decision failed. RequestStatus={RequestStatus}", status);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Customer notification for after-sales decision failed. RequestStatus={RequestStatus}", status);
        }
    }

    private async Task WriteAuditsAsync(
        int requestId, StaffDecisionKind decision, AfterSalesStatus status, AfterSalesFulfillment? fulfillment, int? staffUserId)
    {
        var user = currentUserService.CurrentUser;
        var account = staffUserId is not null && user.Id == staffUserId ? user.Account : null;
        var targetId = requestId.ToString(CultureInfo.InvariantCulture);

        await auditLogService.WriteAsync(
            decision == StaffDecisionKind.NeedMoreInfo ? AuditActions.Shop.RequestMoreInfo : AuditActions.Shop.RequestDecide,
            success: true,
            actorUserId: staffUserId,
            actorAccount: account,
            targetType: nameof(AfterSalesRequest),
            targetId: targetId,
            detail: $"status={status}");
        if (fulfillment is not null)
        {
            await auditLogService.WriteAsync(
                AuditActions.AfterSales.Fulfilled,
                success: true,
                actorUserId: staffUserId,
                actorAccount: account,
                targetType: nameof(AfterSalesRequest),
                targetId: targetId,
                detail: $"kind={fulfillment.Kind}, referenceNo={fulfillment.ReferenceNo}");
        }
    }
}

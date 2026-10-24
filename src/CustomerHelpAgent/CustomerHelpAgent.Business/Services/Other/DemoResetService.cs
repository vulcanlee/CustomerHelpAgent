using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Startup;
using CustomerHelpAgent.Models.Systems;

namespace CustomerHelpAgent.Business.Services.Other;

/// <summary>一鍵重置的結果；<see cref="Message"/> 是給畫面顯示的中文。</summary>
public sealed record DemoResetResult(bool Success, string Message, int DeletedRequests, int DeletedSessions);

/// <summary>
/// 一鍵重置示範資料（1.0.5 起，Scoped）：刪掉業務資料後以 <see cref="DemoSeedData.CreateCatalog"/> 重新寫入，
/// 訂單簽收、出貨等相對時間以<b>重置當下</b>計算，庫存與售後規則版本回到初始值。
///
/// <list type="bullet">
/// <item>只在 <c>DemoSettings:Enabled</c> 時可用，否則回失敗、不動資料。</item>
/// <item>刪除與重新寫入在同一個交易內，依外鍵順序 <c>ExecuteDelete</c>：附件 → 處理單 → 示範顧客的售後通知 → 示範顧客的 AI 長期記憶 → 申請 → 確認卡 → 會話 → 物流事件 → 物流
/// → 訂單明細 → 訂單 → 規格 → 商品 → 售後規則。任何一步失敗整批回滾。</item>
/// <item>commit 之後才刪附件實體檔（<see cref="AfterSalesFileStore.Delete"/> 不拋例外，刪不掉只記警告），
/// 再通知 <see cref="IDemoResetParticipant"/>（例如清空對話中暫存的照片），最後寫稽核 <see cref="AuditActions.Demo.Reset"/>。</item>
/// <item><b>不刪</b>：流程度量、Agent 執行軌跡、Token 用量、AI 對話紀錄、錄製檔、稽核——它們是歷史紀錄，
/// 其中的申請 Id、會話識別碼在重置後就對不到資料（本來就沒有外鍵）。</item>
/// <item>會話與確認卡一併刪除：開著的顧客頁再送出或按確認時，Runner 找不到會話而拒絕（「找不到這段對話…請重新整理頁面」），
/// 舊確認卡不可能再被確認。</item>
/// <item>目錄資料（商品、規格、訂單、明細、物流、物流事件、售後規則）以 <see cref="DemoSeedData.CreateCatalog"/> 的<b>固定 Id</b> 重新寫入，
/// 每次重置與初次寫入的 Id 完全相同（錄製檔裡的 <c>orderItemId</c> 等才能回放）；售後申請與 Agent 資料表仍是自動遞增。</item>
/// </list>
/// </summary>
public sealed class DemoResetService
{
    private readonly IDbContextFactory<BackendDBContext> contextFactory;
    private readonly AfterSalesFileStore fileStore;
    private readonly DemoSettings demoSettings;
    private readonly TimeProvider timeProvider;
    private readonly IAuditLogService auditLogService;
    private readonly CurrentUserService currentUserService;
    private readonly IEnumerable<IDemoResetParticipant> participants;
    private readonly ILogger<DemoResetService> logger;

    public DemoResetService(
        IDbContextFactory<BackendDBContext> contextFactory,
        AfterSalesFileStore fileStore,
        IOptions<DemoSettings> demoSettings,
        TimeProvider timeProvider,
        IAuditLogService auditLogService,
        CurrentUserService currentUserService,
        IEnumerable<IDemoResetParticipant> participants,
        ILogger<DemoResetService> logger)
    {
        this.contextFactory = contextFactory;
        this.fileStore = fileStore;
        this.demoSettings = demoSettings.Value;
        this.timeProvider = timeProvider;
        this.auditLogService = auditLogService;
        this.currentUserService = currentUserService;
        this.participants = participants;
        this.logger = logger;
    }

    /// <summary>
    /// 上次成功重置的時間（本地時間；展示控制台顯示用）：取最新一筆成功的 <see cref="AuditActions.Demo.Reset"/> 稽核，
    /// 不另存欄位 —— 稽核本來就記下每一次重置。沒有（或已被資料保留清掉）回 null。
    /// </summary>
    public async Task<DateTime?> GetLastResetAtAsync()
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var utc = await context.AuditLog.AsNoTracking()
            .Where(x => x.Action == AuditActions.Demo.Reset && x.Success)
            .OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.Id)
            .Select(x => (DateTime?)x.OccurredAt)
            .FirstOrDefaultAsync();
        return utc is { } value
            ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(value, DateTimeKind.Utc), timeProvider.LocalTimeZone)
            : null;
    }

    private static async Task DeleteAfterSalesNotificationsAsync(BackendDBContext context)
    {
        var customerIds = await context.MyUser.IgnoreQueryFilters([ISoftDeletable.FilterName])
            .Where(x => x.Account == DemoSeedData.Customer1 || x.Account == DemoSeedData.Customer2)
            .Select(x => x.Id)
            .ToListAsync();
        await context.Notification
            .Where(x => x.Category == NotificationCategories.AfterSalesUpdated && customerIds.Contains(x.RecipientUserId))
            .ExecuteDeleteAsync();
        await context.NotificationDispatch
            .Where(x => x.SourceKey.StartsWith("aftersales:") && customerIds.Contains(x.RecipientUserId))
            .ExecuteDeleteAsync();
    }

    /// <summary>示範顧客（customer1／customer2）的 AI 長期記憶（M5）；其他帳號的記憶不動。</summary>
    private static async Task DeleteDemoCustomerMemoriesAsync(BackendDBContext context)
    {
        var customerIds = await context.MyUser.IgnoreQueryFilters([ISoftDeletable.FilterName])
            .Where(x => x.Account == DemoSeedData.Customer1 || x.Account == DemoSeedData.Customer2)
            .Select(x => x.Id)
            .ToListAsync();
        await context.CustomerMemory.Where(x => customerIds.Contains(x.CustomerUserId)).ExecuteDeleteAsync();
    }

    public async Task<DemoResetResult> ResetAsync()
    {
        if (!demoSettings.Enabled)
        {
            logger.LogWarning("Demo data reset rejected because demo mode is disabled.");
            return new DemoResetResult(false, "示範模式未開啟（DemoSettings:Enabled），無法重置示範資料。", 0, 0);
        }

        var user = currentUserService.CurrentUser;
        int? userId = user.Id > 0 ? user.Id : null;
        var actorAccount = userId is null ? null : user.Account;

        List<string> photoPaths;
        int deletedRequests;
        int deletedSessions;
        try
        {
            await using var context = await contextFactory.CreateDbContextAsync();
            // Microsoft.Data.Sqlite 預設以 BEGIN IMMEDIATE 開始交易：先拿到寫入鎖，讀到的附件路徑與接著刪除的資料列一致。
            await using var transaction = await context.Database.BeginTransactionAsync();

            photoPaths = await context.AfterSalesAttachment.AsNoTracking()
                .OrderBy(x => x.Id)
                .Select(x => x.RelativePath)
                .ToListAsync();

            await context.AfterSalesAttachment.ExecuteDeleteAsync();
            // 處理單（M4）有外鍵指向申請與規格，必須在申請與規格之前刪；
            // 示範顧客收到的售後通知與其去重紀錄一併清掉，重置後同編號的新申請才收得到通知。
            await context.AfterSalesFulfillment.ExecuteDeleteAsync();
            // AI 初審（M4）：節點狀態 → 初審 → checkpoint（checkpoint 無外鍵，但屬示範資料）；初審有外鍵指向申請，必須在申請之前刪。
            await context.PreReviewNodeState.ExecuteDeleteAsync();
            await context.PreReviewRun.ExecuteDeleteAsync();
            await context.WorkflowCheckpoint.ExecuteDeleteAsync();
            await DeleteAfterSalesNotificationsAsync(context);
            await DeleteDemoCustomerMemoriesAsync(context);
            deletedRequests = await context.AfterSalesRequest.ExecuteDeleteAsync();
            await context.AgentConfirmation.ExecuteDeleteAsync();
            deletedSessions = await context.AgentSessionRecord.ExecuteDeleteAsync();
            await context.ShipmentEvent.ExecuteDeleteAsync();
            await context.Shipment.ExecuteDeleteAsync();
            await context.SalesOrderItem.ExecuteDeleteAsync();
            await context.SalesOrder.ExecuteDeleteAsync();
            await context.ProductVariant.ExecuteDeleteAsync();
            await context.Product.ExecuteDeleteAsync();
            await context.AfterSalesPolicy.ExecuteDeleteAsync();

            await DemoCatalogSeeder.WriteCatalogAsync(context, timeProvider.GetLocalNow().DateTime, CancellationToken.None);
            await transaction.CommitAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Demo data reset failed; the transaction was rolled back.");
            await auditLogService.WriteAsync(
                AuditActions.Demo.Reset,
                success: false,
                actorUserId: userId,
                actorAccount: actorAccount,
                detail: "reset failed");
            return new DemoResetResult(false, "重置示範資料失敗，資料維持原狀。", 0, 0);
        }

        var failedFiles = photoPaths.Count(path => !fileStore.Delete(path));
        foreach (var participant in participants)
        {
            try
            {
                participant.OnDemoDataReset();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Demo reset participant failed. Participant={Participant}", participant.GetType().Name);
            }
        }

        logger.LogInformation(
            "Demo data reset. DeletedRequests={DeletedRequests}, DeletedSessions={DeletedSessions}, DeletedFiles={DeletedFiles}, FailedFiles={FailedFiles}",
            deletedRequests, deletedSessions, photoPaths.Count - failedFiles, failedFiles);
        await auditLogService.WriteAsync(
            AuditActions.Demo.Reset,
            success: true,
            actorUserId: userId,
            actorAccount: actorAccount,
            detail: $"requests={deletedRequests}, sessions={deletedSessions}, files={photoPaths.Count}, failedFiles={failedFiles}");

        return new DemoResetResult(true, "示範資料已重置。", deletedRequests, deletedSessions);
    }
}

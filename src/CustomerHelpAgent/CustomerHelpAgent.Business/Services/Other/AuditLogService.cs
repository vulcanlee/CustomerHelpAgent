using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;

namespace CustomerHelpAgent.Business.Services.Other;

public sealed class AuditLogService : IAuditLogService
{
    private readonly IDbContextFactory<BackendDBContext> contextFactory;
    private readonly ILogger<AuditLogService> logger;
    private readonly AuditContentFileStore? contentFileStore;
    private readonly IClientIpProvider? clientIpProvider;

    /// <param name="contextFactory">
    /// 每筆稽核各用一個 context（0.9.131 起）。之前注入的 scoped context 在 Blazor 裡活得和整條 circuit 一樣久：
    /// 換頁稽核（fire-and-forget）會和頁面初始化同時用到它而撞上「second operation」，寫進去的實體也只增不減。
    /// </param>
    /// <param name="contentFileStore">長內容（修改差異、查詢條件）的檔案存放；未註冊（單元測試）時只寫資料列。</param>
    /// <param name="clientIpProvider">
    /// 來源 IP 在這裡集中取得，各呼叫點不必傳；未註冊（單元測試）時不記 IP。
    /// </param>
    public AuditLogService(
        IDbContextFactory<BackendDBContext> contextFactory,
        ILogger<AuditLogService> logger,
        AuditContentFileStore? contentFileStore = null,
        IClientIpProvider? clientIpProvider = null)
    {
        this.contextFactory = contextFactory;
        this.logger = logger;
        this.contentFileStore = contentFileStore;
        this.clientIpProvider = clientIpProvider;
    }

    public async Task WriteAsync(
        string action,
        bool success = true,
        int? actorUserId = null,
        string? actorAccount = null,
        string? targetType = null,
        string? targetId = null,
        string? detail = null,
        string? content = null)
    {
        string? contentFile = null;
        try
        {
            var occurredAt = DateTime.UtcNow;
            if (contentFileStore is not null && string.IsNullOrEmpty(content) == false)
            {
                contentFile = await contentFileStore.WriteAsync(occurredAt, content);
            }

            var entry = new AuditLog
            {
                OccurredAt = occurredAt,
                Action = action,
                Success = success,
                ActorUserId = actorUserId,
                ActorAccount = actorAccount,
                TargetType = targetType,
                TargetId = targetId,
                Detail = detail,
                ClientIp = clientIpProvider?.GetClientIp(),
                ContentFile = contentFile,
            };

            await using var context = await contextFactory.CreateDbContextAsync();
            context.AuditLog.Add(entry);
            await context.SaveChangesAsync();

            // 稽核同步到日誌（0.9.131 起）：只記資料庫裡的短摘要與檔名，內容檔本身（差異、搜尋原文）不進日誌。
            // 記 Debug：Information 已有各服務自己的操作日誌，這一行是排查時才打開的完整稽核軌跡。
            logger.LogDebug(
                "Audit recorded. Action={Action}, Success={Success}, ActorUserId={ActorUserId}, ActorAccount={ActorAccount}, "
                + "TargetType={TargetType}, TargetId={TargetId}, Detail={AuditDetail}, ContentFile={ContentFile}",
                action, success, actorUserId, actorAccount, targetType, targetId, detail, contentFile);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to write audit log. Action={Action}", action);

            // 資料列沒建起來，內容檔就成了孤兒。
            contentFileStore?.Delete(contentFile);
        }
    }
}

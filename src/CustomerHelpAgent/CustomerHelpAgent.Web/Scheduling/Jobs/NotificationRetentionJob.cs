using Microsoft.Extensions.Options;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Systems;

namespace CustomerHelpAgent.Web.Scheduling.Jobs;

/// <summary>
/// 站內通知的保存期限（0.9.100 起）：建立時間早於 <see cref="NotificationSettings.RetentionDays"/> 天前的通知（不論已讀未讀）分批刪除；
/// 0.9.125 起另外刪除標為已讀早於 <see cref="NotificationSettings.ReadRetentionDays"/> 天前的通知。兩者各自 0＝不清除。
/// 通知存 UTC，門檻用 UTC 時鐘算。
/// </summary>
public sealed class NotificationRetentionJob : IScheduledJob
{
    public const string JobName = "NotificationRetention";

    private readonly NotificationQueryService queryService;
    private readonly IAuditLogService auditLogService;
    private readonly IOptionsMonitor<NotificationSettings> options;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<NotificationRetentionJob> logger;

    public NotificationRetentionJob(
        NotificationQueryService queryService,
        IAuditLogService auditLogService,
        IOptionsMonitor<NotificationSettings> options,
        TimeProvider timeProvider,
        ILogger<NotificationRetentionJob> logger)
    {
        this.queryService = queryService;
        this.auditLogService = auditLogService;
        this.options = options;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    public async Task<ScheduledJobResult> ExecuteAsync(ScheduledJobContext context, CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue;
        var days = settings.RetentionDays;
        var readDays = settings.ReadRetentionDays;
        if (days <= 0 && readDays <= 0)
        {
            return ScheduledJobResult.Success("保留天數設為 0，不自動清除。");
        }

        var removed = days > 0 ? await queryService.PurgeBeforeAsync(RetentionCutoffs.Notification(days, timeProvider), cancellationToken) : 0;
        var readRemoved = readDays > 0 ? await queryService.PurgeReadBeforeAsync(RetentionCutoffs.Notification(readDays, timeProvider), cancellationToken) : 0;
        logger.LogInformation(
            "Notification retention completed. Rows={Rows}, RetentionDays={RetentionDays}, ReadRows={ReadRows}, ReadRetentionDays={ReadRetentionDays}",
            removed, days, readRemoved, readDays);

        if (removed + readRemoved > 0)
        {
            await auditLogService.WriteAsync(
                AuditActions.Notification.AutoPurge, success: true,
                targetType: "Notification", targetId: "*",
                detail: $"rows={removed}; days={days}; readRows={readRemoved}; readDays={readDays}; trigger={context.Trigger}");
        }

        var parts = new List<string>();
        if (days > 0)
        {
            parts.Add($"刪除 {removed} 則建立超過 {days} 天的站內通知");
        }

        if (readDays > 0)
        {
            parts.Add($"刪除 {readRemoved} 則已讀超過 {readDays} 天的站內通知");
        }

        return ScheduledJobResult.Success(string.Join("；", parts) + "。");
    }
}

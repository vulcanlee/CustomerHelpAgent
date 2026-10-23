using Microsoft.Extensions.Options;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Systems;

namespace CustomerHelpAgent.Web.Scheduling.Jobs;

/// <summary>
/// 稽核紀錄的保存期限（0.9.78 起 LOG-13，0.9.96 起改由排程執行）。天數讀 <see cref="LogRetentionSettings.AuditLogDays"/>，0＝不清理。
///
/// ⚠️ 稽核紀錄存 UTC，門檻用 UTC 時鐘算（系統例外紀錄是本地時間，兩者不可對調）。
/// 真的刪到資料才寫 <c>Audit.AutoPurge</c>，而且寫在刪除之後：稽核軌跡被清理這件事本身也必須查得到。
/// </summary>
public sealed class AuditLogRetentionJob : IScheduledJob
{
    public const string JobName = "AuditLogRetention";

    private readonly AuditLogQueryService auditLogQueryService;
    private readonly IAuditLogService auditLogService;
    private readonly IOptionsMonitor<LogRetentionSettings> options;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<AuditLogRetentionJob> logger;

    public AuditLogRetentionJob(
        AuditLogQueryService auditLogQueryService,
        IAuditLogService auditLogService,
        IOptionsMonitor<LogRetentionSettings> options,
        TimeProvider timeProvider,
        ILogger<AuditLogRetentionJob> logger)
    {
        this.auditLogQueryService = auditLogQueryService;
        this.auditLogService = auditLogService;
        this.options = options;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    public async Task<ScheduledJobResult> ExecuteAsync(ScheduledJobContext context, CancellationToken cancellationToken)
    {
        var days = options.CurrentValue.AuditLogDays;
        if (days <= 0)
        {
            return ScheduledJobResult.Success("保留天數設為 0，不自動清除。");
        }

        var threshold = RetentionCutoffs.AuditLog(days, timeProvider);
        var removed = await auditLogQueryService.PurgeBeforeAsync(threshold);
        logger.LogInformation("Audit log retention completed. Rows={Rows}, RetentionDays={RetentionDays}", removed, days);

        if (removed > 0)
        {
            await auditLogService.WriteAsync(
                AuditActions.Audit.AutoPurge, success: true,
                targetType: "AuditLog", targetId: "*", detail: $"rows={removed}; days={days}; trigger={context.Trigger}");
        }

        return ScheduledJobResult.Success($"刪除 {removed} 筆超過 {days} 天的稽核紀錄。");
    }
}

using Microsoft.Extensions.Options;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Systems;

namespace CustomerHelpAgent.Web.Scheduling.Jobs;

/// <summary>
/// 系統例外紀錄的保存期限（0.9.78 起 LOG-13，0.9.96 起改由排程執行）。天數讀 <see cref="LogRetentionSettings.ExceptionLogDays"/>，0＝不清理。
///
/// ⚠️ 例外紀錄存本地時間（<c>LastOccurredAt</c>），門檻用本地時鐘算（稽核紀錄是 UTC，兩者不可對調）。
/// 堆疊檔由 <c>ExceptionLogService.PurgeBeforeAsync</c> 經 <c>ExceptionStackFileStore</c> 一併刪除（速查表 §6.6 紅線）。
/// </summary>
public sealed class ExceptionLogRetentionJob : IScheduledJob
{
    public const string JobName = "ExceptionLogRetention";

    private readonly ExceptionLogService exceptionLogService;
    private readonly IAuditLogService auditLogService;
    private readonly IOptionsMonitor<LogRetentionSettings> options;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<ExceptionLogRetentionJob> logger;

    public ExceptionLogRetentionJob(
        ExceptionLogService exceptionLogService,
        IAuditLogService auditLogService,
        IOptionsMonitor<LogRetentionSettings> options,
        TimeProvider timeProvider,
        ILogger<ExceptionLogRetentionJob> logger)
    {
        this.exceptionLogService = exceptionLogService;
        this.auditLogService = auditLogService;
        this.options = options;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    public async Task<ScheduledJobResult> ExecuteAsync(ScheduledJobContext context, CancellationToken cancellationToken)
    {
        var days = options.CurrentValue.ExceptionLogDays;
        if (days <= 0)
        {
            return ScheduledJobResult.Success("保留天數設為 0，不自動清除。");
        }

        var threshold = RetentionCutoffs.ExceptionLog(days, timeProvider);
        var removed = await exceptionLogService.PurgeBeforeAsync(threshold);
        logger.LogInformation("Exception log retention completed. Rows={Rows}, RetentionDays={RetentionDays}", removed, days);

        if (removed > 0)
        {
            await auditLogService.WriteAsync(
                AuditActions.ExceptionLog.AutoPurge, success: true,
                targetType: "ExceptionLog", targetId: "*", detail: $"rows={removed}; days={days}; trigger={context.Trigger}");
        }

        return ScheduledJobResult.Success($"刪除 {removed} 筆超過 {days} 天的系統例外紀錄。");
    }
}

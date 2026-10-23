using Microsoft.Extensions.Options;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Systems;

namespace CustomerHelpAgent.Web.Scheduling.Jobs;

/// <summary>
/// Token 用量紀錄的保存期限（0.9.96 起；之前完全不會自動清除）。天數讀 <see cref="LogRetentionSettings.TokenUsageLogDays"/>（預設 365），0＝不清理。
///
/// 分批刪除、批次間可中斷；原始 JSON 檔經 <c>TokenUsageRawStore</c> 刪除（速查表 §6.7 紅線）。
/// 紀錄存本地時間，門檻用本地時鐘算。
/// </summary>
public sealed class TokenUsageLogRetentionJob : IScheduledJob
{
    public const string JobName = "TokenUsageLogRetention";

    private readonly TokenUsageLogService tokenUsageLogService;
    private readonly IAuditLogService auditLogService;
    private readonly IOptionsMonitor<LogRetentionSettings> options;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<TokenUsageLogRetentionJob> logger;

    public TokenUsageLogRetentionJob(
        TokenUsageLogService tokenUsageLogService,
        IAuditLogService auditLogService,
        IOptionsMonitor<LogRetentionSettings> options,
        TimeProvider timeProvider,
        ILogger<TokenUsageLogRetentionJob> logger)
    {
        this.tokenUsageLogService = tokenUsageLogService;
        this.auditLogService = auditLogService;
        this.options = options;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    public async Task<ScheduledJobResult> ExecuteAsync(ScheduledJobContext context, CancellationToken cancellationToken)
    {
        var days = options.CurrentValue.TokenUsageLogDays;
        if (days <= 0)
        {
            return ScheduledJobResult.Success("保留天數設為 0，不自動清除。");
        }

        var removed = await tokenUsageLogService.PurgeExpiredAsync(days, timeProvider.GetLocalNow().DateTime, cancellationToken);
        if (removed is null)
        {
            return ScheduledJobResult.Failure("清除 Token 用量紀錄失敗，詳細內容請看「系統例外紀錄」。");
        }

        logger.LogInformation("Usage record retention completed. Rows={Rows}, RetentionDays={RetentionDays}", removed, days);
        if (removed > 0)
        {
            await auditLogService.WriteAsync(
                AuditActions.TokenUsage.AutoPurge, success: true,
                targetType: "TokenUsageLog", targetId: "*", detail: $"rows={removed}; days={days}; trigger={context.Trigger}");
        }

        return ScheduledJobResult.Success($"刪除 {removed} 筆超過 {days} 天的 Token 用量紀錄。");
    }
}

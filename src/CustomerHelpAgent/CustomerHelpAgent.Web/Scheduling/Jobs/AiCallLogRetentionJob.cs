using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Systems;

namespace CustomerHelpAgent.Web.Scheduling.Jobs;

/// <summary>
/// AI 對話紀錄的自動過期（0.9.72 起，0.9.96 起改由排程執行）。天數讀 <see cref="AiCallLogSettings.RetentionDays"/>（1～3650，沒有「不清除」）。
///
/// ⚠️ <c>AiCallLogSettings.Enabled=false</c> 時照樣執行 —— 停用只代表「不再記」，已存在的內容仍必須過期。
/// 內容檔由 <c>AiCallLogService</c> 經 <c>AiCallLogFileStore</c> 刪除（速查表 §6.7 紅線）。
/// 服務失敗時會自己記錄錯誤並回 null，這裡回報失敗即可，不要再丟例外（否則同一個錯誤會記兩筆）。
/// </summary>
public sealed class AiCallLogRetentionJob : IScheduledJob
{
    public const string JobName = "AiCallLogRetention";

    private readonly AiCallLogService aiCallLogService;
    private readonly IAuditLogService auditLogService;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<AiCallLogRetentionJob> logger;

    public AiCallLogRetentionJob(
        AiCallLogService aiCallLogService,
        IAuditLogService auditLogService,
        TimeProvider timeProvider,
        ILogger<AiCallLogRetentionJob> logger)
    {
        this.aiCallLogService = aiCallLogService;
        this.auditLogService = auditLogService;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    public async Task<ScheduledJobResult> ExecuteAsync(ScheduledJobContext context, CancellationToken cancellationToken)
    {
        var days = aiCallLogService.RetentionDays;
        var removed = await aiCallLogService.PurgeExpiredAsync(timeProvider.GetLocalNow().DateTime);
        if (removed is null)
        {
            return ScheduledJobResult.Failure("清除 AI 對話紀錄失敗，詳細內容請看「系統例外紀錄」。");
        }

        logger.LogInformation("AI call log retention completed. Rows={Rows}, RetentionDays={RetentionDays}", removed, days);
        if (removed > 0)
        {
            await auditLogService.WriteAsync(
                AuditActions.AiCallLog.AutoPurge, success: true,
                targetType: "AiCallLog", targetId: "*", detail: $"rows={removed}; days={days}; trigger={context.Trigger}");
        }

        return ScheduledJobResult.Success($"刪除 {removed} 筆超過 {days} 天的 AI 對話紀錄。");
    }
}

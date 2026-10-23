using Microsoft.Extensions.Options;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Web.Backup;
using CustomerHelpAgent.Web.Configuration;

namespace CustomerHelpAgent.Web.Scheduling.Jobs;

/// <summary>
/// 系統備份（0.9.99 起，預設每天 02:00，排在 03:00 的清理作業之前）。備份內容見 <see cref="SystemBackupService"/>。
///
/// ⚠️ <b>備份成功之後才</b>依 <see cref="BackupSettings.KeepCount"/> 刪掉較舊的備份：失敗的那一次不可以把好的備份輪掉。
/// 管理頁的「立即備份」也是經排程框架的佇列執行，與排程共用同一把作業鎖，不會同時有兩份備份在跑。
/// </summary>
public sealed class SystemBackupJob : IScheduledJob
{
    public const string JobName = "SystemBackup";

    private readonly SystemBackupService backupService;
    private readonly BackupStore store;
    private readonly IAuditLogService auditLogService;
    private readonly IOptionsMonitor<BackupSettings> options;
    private readonly ILogger<SystemBackupJob> logger;

    public SystemBackupJob(
        SystemBackupService backupService,
        BackupStore store,
        IAuditLogService auditLogService,
        IOptionsMonitor<BackupSettings> options,
        ILogger<SystemBackupJob> logger)
    {
        this.backupService = backupService;
        this.store = store;
        this.auditLogService = auditLogService;
        this.options = options;
        this.logger = logger;
    }

    public async Task<ScheduledJobResult> ExecuteAsync(ScheduledJobContext context, CancellationToken cancellationToken)
    {
        var result = await backupService.CreateAsync(context.Trigger, cancellationToken);
        if (!result.Success)
        {
            logger.LogWarning("System backup was not created. Reason={Reason}", result.Message);
            return ScheduledJobResult.Failure(result.Message);
        }

        await auditLogService.WriteAsync(
            AuditActions.Backup.Create, success: true, actorAccount: context.TriggeredByAccount,
            targetType: "Backup", targetId: result.FileName,
            detail: $"file={result.FileName}; bytes={result.SizeBytes}; missing={result.MissingFileCount}; trigger={context.Trigger}");

        var message = result.Message;
        if (result.MissingFileCount > 0)
        {
            message += $"打包途中有 {result.MissingFileCount} 個檔案已被刪除，未納入。";
        }

        var keep = options.CurrentValue.KeepCount;
        var deleted = store.Prune(keep);
        if (deleted.Count > 0)
        {
            await auditLogService.WriteAsync(
                AuditActions.Backup.AutoPurge, success: true, targetType: "Backup", targetId: "*",
                detail: $"keep={keep}; deleted={string.Join(",", deleted)}");
            logger.LogInformation("Old backups deleted. Count={Count}, KeepCount={KeepCount}", deleted.Count, keep);
            message += $"刪除較舊的備份 {deleted.Count} 份（保留 {keep} 份）。";
        }

        return ScheduledJobResult.Success(message);
    }
}

using CustomerHelpAgent.Business.Factories;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Systems;

namespace CustomerHelpAgent.Web.Health;

/// <summary>
/// 系統健康監控頁的「發送測試通知」（0.9.122 起）：發一則站內通知給全體管理員，確認鈴鐺與即時更新正常。
/// 不寄信（寄信另有「寄出測試信」）、不設 SourceKey（可以重複測）。
/// </summary>
public sealed class NotificationTestService
{
    internal const string Title = "測試通知";

    private readonly INotificationSender notificationSender;
    private readonly IAuditLogService auditLogService;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<NotificationTestService> logger;

    public NotificationTestService(
        INotificationSender notificationSender,
        IAuditLogService auditLogService,
        TimeProvider timeProvider,
        ILogger<NotificationTestService> logger)
    {
        this.notificationSender = notificationSender;
        this.auditLogService = auditLogService;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    public async Task<VerifyRecordResult> SendAsync(int? actorUserId, string? actorAccount, CancellationToken cancellationToken = default)
    {
        var body = $"由 {actorAccount} 在「系統健康監控」於 {timeProvider.GetLocalNow():yyyy-MM-dd HH:mm:ss} 發送；收到表示鈴鐺與即時更新正常。";
        var result = await notificationSender.SendAsync(
            new NotificationRequest(NotificationCategories.Test, Title, body, "/system-health", NotificationTarget.AllAdmins()),
            cancellationToken);

        // 發送失敗時 NotificationSender 已記錯誤；這裡只記結果與人數。
        var success = !result.Failed && result.Recipients > 0;
        logger.LogInformation("Test notification sent. Recipients={Recipients}, Failed={Failed}, UserId={UserId}", result.Recipients, result.Failed, actorUserId);
        await auditLogService.WriteAsync(
            AuditActions.Notification.Test, success: success, actorUserId: actorUserId, actorAccount: actorAccount,
            detail: $"recipients={result.Recipients}" + (result.Failed ? "; failed" : string.Empty));

        if (result.Failed)
        {
            return VerifyRecordResultFactory.Build(false, "發送失敗，詳細原因請查看日誌。");
        }

        return result.Recipients > 0
            ? VerifyRecordResultFactory.Build(true, $"已發送給 {result.Recipients} 位管理員，請看右上角的鈴鐺。")
            : VerifyRecordResultFactory.Build(false, "沒有可以收到通知的管理員（需為啟用中、勾選「管理員」的帳號）。");
    }
}

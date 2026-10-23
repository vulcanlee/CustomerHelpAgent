using System.Net.Mail;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.Business.Factories;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Web.Configuration;

namespace CustomerHelpAgent.Web.Email;

/// <summary>
/// 系統健康監控頁的「寄出測試信」。
///
/// 刻意**同步**寄送（不走佇列）：管理員按下按鈕就是要當場知道設定對不對，
/// 丟進背景佇列只會讓他看到「已送出」卻不知道成敗。
/// </summary>
public sealed class EmailTestService
{
    private readonly IEmailSender emailSender;
    private readonly IAuditLogService auditLogService;
    private readonly IOptionsMonitor<EmailSettings> emailOptions;
    private readonly ISystemIdentity systemIdentity;
    private readonly ILogger<EmailTestService> logger;

    public EmailTestService(
        IEmailSender emailSender,
        IAuditLogService auditLogService,
        IOptionsMonitor<EmailSettings> emailOptions,
        ISystemIdentity systemIdentity,
        ILogger<EmailTestService> logger)
    {
        this.emailSender = emailSender;
        this.auditLogService = auditLogService;
        this.emailOptions = emailOptions;
        this.systemIdentity = systemIdentity;
        this.logger = logger;
    }

    public async Task<VerifyRecordResult> SendAsync(
        string? to, int? actorUserId, string? actorAccount, CancellationToken cancellationToken = default)
    {
        var provider = emailOptions.CurrentValue.GetProvider();
        if (provider == EmailProvider.None)
        {
            return VerifyRecordResultFactory.Build(false, "寄信功能未啟用（EmailSettings:Provider 為 None）。");
        }

        var recipient = to?.Trim() ?? string.Empty;
        if (!MailAddress.TryCreate(recipient, out _))
        {
            return VerifyRecordResultFactory.Build(false, "請輸入有效的收件者 Email。");
        }

        var message = EmailTemplates.BuildTest(
            recipient, systemIdentity.Name, DateTime.Now, provider.ToString());

        try
        {
            await emailSender.SendAsync(message, cancellationToken);
            logger.LogInformation("Test email sent. Provider={Provider}, UserId={UserId}", provider, actorUserId);

            // 稽核只記 provider 與成敗，不記收件者（個資）。
            await auditLogService.WriteAsync(
                AuditActions.Email.Test, success: true, actorUserId: actorUserId, actorAccount: actorAccount,
                detail: $"provider={provider}");

            return VerifyRecordResultFactory.Build(true, provider == EmailProvider.Pickup
                ? "測試信已寫入 Pickup 資料夾（不會真正寄出）。"
                : "測試信已寄出，請到收件匣確認。");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Test email failed. Provider={Provider}, UserId={UserId}", provider, actorUserId);
            await auditLogService.WriteAsync(
                AuditActions.Email.Test, success: false, actorUserId: actorUserId, actorAccount: actorAccount,
                detail: $"provider={provider}; error={ex.GetType().Name}");

            return VerifyRecordResultFactory.Build(false, $"寄送失敗：{ex.GetType().Name}。詳細原因請查看日誌。");
        }
    }
}

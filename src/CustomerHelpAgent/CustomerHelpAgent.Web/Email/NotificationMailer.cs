using System.Net.Mail;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Web.Configuration;

namespace CustomerHelpAgent.Web.Email;

/// <summary>
/// 站內通知的同步信（0.9.100 起）。寄信停用（<c>Provider = None</c>）時什麼都不做；格式不合的信箱略過（例如 support 預設的 <c>support</c>）。
/// 連結以 <c>EmailSettings:PublicBaseUrl</c> 組成完整網址，沒有設定就不附連結（背景工作沒有請求可以推網址）。
/// ⚠️ 寄信佇列滿時停止寄送其餘的信、只記一筆警告；日誌不記任何信箱。
/// </summary>
public sealed class NotificationMailer : INotificationMailer
{
    private readonly IOptionsMonitor<EmailSettings> emailOptions;
    private readonly IEmailQueue emailQueue;
    private readonly ISystemIdentity systemIdentity;
    private readonly ILogger<NotificationMailer> logger;

    public NotificationMailer(IOptionsMonitor<EmailSettings> emailOptions, IEmailQueue emailQueue, ISystemIdentity systemIdentity, ILogger<NotificationMailer> logger)
    {
        this.emailOptions = emailOptions;
        this.emailQueue = emailQueue;
        this.systemIdentity = systemIdentity;
        this.logger = logger;
    }

    public int Send(IReadOnlyList<string> emails, string title, string? body, string? link)
    {
        var settings = emailOptions.CurrentValue;
        if (settings.GetProvider() == EmailProvider.None)
        {
            return 0;
        }

        var baseUrl = settings.PublicBaseUrl?.Trim().TrimEnd('/');
        var absoluteLink = string.IsNullOrEmpty(baseUrl) || string.IsNullOrWhiteSpace(link) || !link.StartsWith('/') ? null : baseUrl + link;
        var queued = 0;
        var valid = emails.Select(x => x.Trim()).Where(x => MailAddress.TryCreate(x, out _)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var address in valid)
        {
            if (!emailQueue.TryEnqueue(EmailTemplates.BuildNotification(address, systemIdentity.Name, title, body, absoluteLink)))
            {
                logger.LogWarning("Email queue is full; remaining notification emails were skipped. Kind={Kind}, Skipped={Skipped}",
                    "Notification", valid.Count - queued);
                break;
            }

            queued++;
        }

        return queued;
    }
}

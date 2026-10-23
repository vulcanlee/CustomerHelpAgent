using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Web.Configuration;

namespace CustomerHelpAgent.Web.Email;

/// <summary>
/// Provider = Smtp：以 MailKit 寄出。
///
/// <para>不用 <c>System.Net.Mail.SmtpClient</c>：微軟已標示不建議新專案使用（不支援新式協定）。</para>
///
/// <para>⚠️ 每封信 new 一個 <see cref="SmtpClient"/>：它不是執行緒安全的，
/// 而本專案寄信量低，連線重用換來的效能不值得承擔共用狀態的風險。</para>
/// </summary>
public sealed class SmtpEmailSender : IEmailSender
{
    private readonly IOptionsMonitor<EmailSettings> emailOptions;
    private readonly ISystemIdentity systemIdentity;
    private readonly ILogger<SmtpEmailSender> logger;

    public SmtpEmailSender(
        IOptionsMonitor<EmailSettings> emailOptions,
        ISystemIdentity systemIdentity,
        ILogger<SmtpEmailSender> logger)
    {
        this.emailOptions = emailOptions;
        this.systemIdentity = systemIdentity;
        this.logger = logger;
    }

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        var settings = emailOptions.CurrentValue;
        using var mime = MimeMessageFactory.Create(message, settings, systemIdentity.Name);

        using var client = new SmtpClient { Timeout = settings.TimeoutSeconds * 1000 };
        await ConnectAsync(client, settings, cancellationToken);
        await client.SendAsync(mime, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);

        logger.LogInformation("Email sent through SMTP. Kind={Kind}, Host={Host}", message.Kind, settings.Host);
    }

    /// <summary>
    /// 連線、加密、登入。寄信與健康檢查共用，確保兩者走的是同一條路。
    ///
    /// ⚠️ 刻意**不**設定 <c>ServerCertificateValidationCallback</c>：憑證驗證失敗就該失敗，
    /// 「先關掉驗證讓它通」等於允許中間人攔截 SMTP 密碼與信件內容。
    /// </summary>
    internal static async Task ConnectAsync(SmtpClient client, EmailSettings settings, CancellationToken cancellationToken)
    {
        await client.ConnectAsync(settings.Host, settings.Port, ToSecureSocketOptions(settings.GetSecurity()), cancellationToken);

        if (!string.IsNullOrWhiteSpace(settings.UserName))
        {
            await client.AuthenticateAsync(settings.UserName, settings.Password, cancellationToken);
        }
    }

    internal static SecureSocketOptions ToSecureSocketOptions(EmailSecurityMode mode) => mode switch
    {
        EmailSecurityMode.None => SecureSocketOptions.None,
        EmailSecurityMode.StartTls => SecureSocketOptions.StartTls,
        EmailSecurityMode.SslOnConnect => SecureSocketOptions.SslOnConnect,
        _ => SecureSocketOptions.Auto,
    };
}

using System.Diagnostics;
using MailKit.Net.Smtp;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.Web.Configuration;

namespace CustomerHelpAgent.Web.Email;

public sealed record EmailProbeResult(bool Success, long ElapsedMilliseconds, string? Message);

public interface IEmailHealthProbe
{
    /// <summary>實際連線 SMTP（連線＋加密＋登入），不寄信。永不拋例外。</summary>
    Task<EmailProbeResult> ProbeAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 系統健康監控「寄信服務」項的實測。
///
/// 只看設定不夠 —— 主機名打錯、帳密過期、防火牆擋 587，設定看起來都完全正常。
/// 上限 5 秒，刻意不沿用 <see cref="EmailSettings.TimeoutSeconds"/>（預設 30 秒），
/// 否則 SMTP 掛掉時整個健康頁要等半分鐘才出得來。
/// </summary>
public sealed class EmailHealthProbe : IEmailHealthProbe
{
    public const int TimeoutSeconds = 5;

    private readonly IOptionsMonitor<EmailSettings> emailOptions;
    private readonly ILogger<EmailHealthProbe> logger;

    public EmailHealthProbe(IOptionsMonitor<EmailSettings> emailOptions, ILogger<EmailHealthProbe> logger)
    {
        this.emailOptions = emailOptions;
        this.logger = logger;
    }

    public async Task<EmailProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var settings = emailOptions.CurrentValue;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));

            using var client = new SmtpClient { Timeout = TimeoutSeconds * 1000 };
            await SmtpEmailSender.ConnectAsync(client, settings, timeout.Token);
            await client.DisconnectAsync(true, timeout.Token);

            return new EmailProbeResult(true, stopwatch.ElapsedMilliseconds, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SMTP health probe failed. Host={Host}, Port={Port}", settings.Host, settings.Port);
            return new EmailProbeResult(false, stopwatch.ElapsedMilliseconds, $"SMTP 連線或登入失敗：{ex.GetType().Name}。");
        }
    }
}

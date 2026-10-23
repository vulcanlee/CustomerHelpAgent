using Microsoft.Extensions.Options;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Web.Configuration;

namespace CustomerHelpAgent.Web.Email;

/// <summary>
/// Provider = Pickup：把信存成 .eml 檔（可直接用郵件程式開啟），不連任何郵件伺服器。
///
/// ⚠️ **只供開發使用**。檔案是明文、含完整內文（日後包含密碼重設連結），
/// 能讀到這個資料夾的人就能讀到所有信 —— Production 由 <c>StartupSafetyValidator</c> 拒絕啟動。
/// </summary>
public sealed class PickupEmailSender : IEmailSender
{
    private readonly IOptionsMonitor<EmailSettings> emailOptions;
    private readonly ISystemIdentity systemIdentity;
    private readonly ILogger<PickupEmailSender> logger;

    public PickupEmailSender(
        IOptionsMonitor<EmailSettings> emailOptions,
        ISystemIdentity systemIdentity,
        ILogger<PickupEmailSender> logger)
    {
        this.emailOptions = emailOptions;
        this.systemIdentity = systemIdentity;
        this.logger = logger;
    }

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        var settings = emailOptions.CurrentValue;

        // 在這裡才建立，而不是啟動時：沒用到 Pickup 的部署不該多出一個空資料夾。
        Directory.CreateDirectory(settings.PickupDirectory);

        var fileName = $"{DateTime.Now:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.eml";
        var path = Path.Combine(settings.PickupDirectory, fileName);

        using var mime = MimeMessageFactory.Create(message, settings, systemIdentity.Name);
        await mime.WriteToAsync(path, cancellationToken);

        logger.LogInformation("Email written to pickup directory. Kind={Kind}, FileName={FileName}", message.Kind, fileName);
    }
}

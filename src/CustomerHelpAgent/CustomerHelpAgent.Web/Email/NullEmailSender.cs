using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Systems;

namespace CustomerHelpAgent.Web.Email;

/// <summary>Provider = None：不寄信，只留一筆日誌。出貨預設值，整合測試也用它。</summary>
public sealed class NullEmailSender : IEmailSender
{
    private readonly ILogger<NullEmailSender> logger;

    public NullEmailSender(ILogger<NullEmailSender> logger)
    {
        this.logger = logger;
    }

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Email discarded because provider is None. Kind={Kind}", message.Kind);
        return Task.CompletedTask;
    }
}

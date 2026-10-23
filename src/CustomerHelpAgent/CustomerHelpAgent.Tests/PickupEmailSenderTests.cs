using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Web.Configuration;
using CustomerHelpAgent.Web.Email;
using CustomerHelpAgent.Business.Services.Other;

namespace CustomerHelpAgent.Tests;

public sealed class PickupEmailSenderTests
{
    [Fact]
    public async Task SendAsync_ShouldCreateDirectoryAndWriteOneMultipartMessage()
    {
        var root = Path.Combine(Path.GetTempPath(), "CustomerHelpAgentPickupTests", Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "Mails");

        try
        {
            var sender = CreateSender(new EmailSettings
            {
                Provider = "Pickup",
                PickupDirectory = directory,
                FromAddress = "noreply@example.com",
            });
            var message = EmailTemplates.BuildTest("alice@example.com", "測試系統", DateTime.Now, "Pickup");

            await sender.SendAsync(message);

            var file = Assert.Single(Directory.GetFiles(directory, "*.eml"));
            using var mime = await MimeMessage.LoadAsync(file);

            Assert.Equal("alice@example.com", mime.To.Mailboxes.Single().Address);
            Assert.Equal("noreply@example.com", mime.From.Mailboxes.Single().Address);
            Assert.Equal(message.Subject, mime.Subject);
            Assert.Equal("alternative", Assert.IsType<MultipartAlternative>(mime.Body).ContentType.MediaSubtype);
            Assert.Contains("測試系統", mime.TextBody);
            Assert.Contains("測試系統", mime.HtmlBody);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>沒填寄件者時，顯示名稱退回 SystemName、位址用 Pickup 專用的占位值。</summary>
    [Fact]
    public async Task SendAsync_WithoutFromAddress_ShouldUseFallbackSender()
    {
        var root = Path.Combine(Path.GetTempPath(), "CustomerHelpAgentPickupTests", Guid.NewGuid().ToString("N"));

        try
        {
            var sender = CreateSender(new EmailSettings { Provider = "Pickup", PickupDirectory = root });

            await sender.SendAsync(EmailTemplates.BuildTest("bob@example.com", "測試系統", DateTime.Now, "Pickup"));

            using var mime = await MimeMessage.LoadAsync(Assert.Single(Directory.GetFiles(root, "*.eml")));
            var from = mime.From.Mailboxes.Single();
            Assert.Equal(MimeMessageFactory.PickupFallbackFromAddress, from.Address);
            Assert.Equal("測試系統", from.Name);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static PickupEmailSender CreateSender(EmailSettings settings)
    {
        var systemSettings = new SystemSettings();
        systemSettings.SystemInformation.SystemName = "測試系統";

        return new PickupEmailSender(
            new StaticOptionsMonitor<EmailSettings>(settings),
            new SystemIdentity(new StaticOptionsMonitor<SystemSettings>(systemSettings)),
            NullLogger<PickupEmailSender>.Instance);
    }
}

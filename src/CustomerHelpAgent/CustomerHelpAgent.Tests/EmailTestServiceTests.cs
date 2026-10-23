using Microsoft.Extensions.Logging.Abstractions;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Web.Configuration;
using CustomerHelpAgent.Web.Email;
using CustomerHelpAgent.Business.Services.Other;

namespace CustomerHelpAgent.Tests;

public sealed class EmailTestServiceTests
{
    [Fact]
    public async Task SendAsync_WhenProviderIsNone_ShouldRefuseWithoutSending()
    {
        var sender = new CapturingEmailSender();
        var audit = new RecordingAuditLogService();
        var service = CreateService(sender, audit, "None");

        var result = await service.SendAsync("alice@example.com", 1, "admin");

        Assert.False(result.Success);
        Assert.Empty(sender.Sent);
        Assert.Empty(audit.Entries);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-address")]
    public async Task SendAsync_WithInvalidRecipient_ShouldRefuse(string? to)
    {
        var sender = new CapturingEmailSender();
        var service = CreateService(sender, new RecordingAuditLogService(), "Pickup");

        var result = await service.SendAsync(to, 1, "admin");

        Assert.False(result.Success);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task SendAsync_WhenSent_ShouldAuditWithoutRecipient()
    {
        var sender = new CapturingEmailSender();
        var audit = new RecordingAuditLogService();
        var service = CreateService(sender, audit, "Pickup");

        var result = await service.SendAsync(" alice@example.com ", 7, "admin");

        Assert.True(result.Success);
        var message = Assert.Single(sender.Sent);
        Assert.Equal("alice@example.com", message.To);
        Assert.Equal(EmailKinds.Test, message.Kind);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal("Email.Test", entry.Action);
        Assert.True(entry.Success);
        Assert.Equal(7, entry.ActorUserId);
        Assert.DoesNotContain("alice", entry.Detail);
    }

    /// <summary>失敗要讓管理員看到原因類型，並留下失敗的稽核紀錄 —— 但不可往外拋。</summary>
    [Fact]
    public async Task SendAsync_WhenSenderThrows_ShouldReturnFailureAndAudit()
    {
        var sender = new CapturingEmailSender(_ => new TimeoutException("smtp timeout"));
        var audit = new RecordingAuditLogService();
        var service = CreateService(sender, audit, "Smtp");

        var result = await service.SendAsync("alice@example.com", 7, "admin");

        Assert.False(result.Success);
        Assert.Contains(nameof(TimeoutException), result.Message);
        var entry = Assert.Single(audit.Entries);
        Assert.False(entry.Success);
        Assert.Contains(nameof(TimeoutException), entry.Detail);
    }

    private static EmailTestService CreateService(
        CapturingEmailSender sender, RecordingAuditLogService audit, string provider)
    {
        return new EmailTestService(
            sender,
            audit,
            new StaticOptionsMonitor<EmailSettings>(new EmailSettings
            {
                Provider = provider,
                Host = "smtp.example.com",
                FromAddress = "noreply@example.com",
            }),
            new SystemIdentity(new StaticOptionsMonitor<SystemSettings>(new SystemSettings())),
            NullLogger<EmailTestService>.Instance);
    }
}

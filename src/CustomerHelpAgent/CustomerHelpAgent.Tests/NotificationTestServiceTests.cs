using Microsoft.Extensions.Logging.Abstractions;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Web.Health;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 系統健康監控頁的「發送測試通知」（0.9.122 起）：發給全體管理員、不寄信、可重複測、寫稽核。
/// </summary>
public sealed class NotificationTestServiceTests
{
    [Fact]
    public async Task SendAsync_ShouldNotifyAllAdmins_WithoutEmail_AndAudit()
    {
        var sender = new StubNotificationSender(new NotificationSendResult(3, 0));
        var audit = new RecordingAuditLogService();

        var result = await CreateService(sender, audit).SendAsync(7, "alice");

        Assert.True(result.Success);
        Assert.Equal("已發送給 3 位管理員，請看右上角的鈴鐺。", result.Message);
        var request = Assert.Single(sender.Requests);
        Assert.Equal(NotificationCategories.Test, request.Category);
        Assert.Equal("測試通知", request.Title);
        Assert.Contains("alice", request.Body, StringComparison.Ordinal);
        Assert.Equal("/system-health", request.Link);
        Assert.True(request.Target.Admins);
        Assert.False(request.AlsoEmail);
        Assert.Null(request.SourceKey); // 可以重複測

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditActions.Notification.Test, entry.Action);
        Assert.True(entry.Success);
        Assert.Equal(7, entry.ActorUserId);
        Assert.Equal("recipients=3", entry.Detail);
    }

    [Fact]
    public async Task SendAsync_WhenThereIsNoAdmin_ShouldReportFailure()
    {
        var audit = new RecordingAuditLogService();

        var result = await CreateService(new StubNotificationSender(new NotificationSendResult(0, 0)), audit).SendAsync(7, "alice");

        Assert.False(result.Success);
        Assert.Contains("沒有可以收到通知的管理員", result.Message, StringComparison.Ordinal);
        Assert.False(Assert.Single(audit.Entries).Success);
    }

    [Fact]
    public async Task SendAsync_WhenTheSenderFails_ShouldReportFailure()
    {
        var audit = new RecordingAuditLogService();

        var result = await CreateService(new StubNotificationSender(new NotificationSendResult(0, 0, Failed: true)), audit).SendAsync(7, "alice");

        Assert.False(result.Success);
        Assert.Contains("發送失敗", result.Message, StringComparison.Ordinal);
        var entry = Assert.Single(audit.Entries);
        Assert.False(entry.Success);
        Assert.Equal("recipients=0; failed", entry.Detail);
    }

    private static NotificationTestService CreateService(INotificationSender sender, RecordingAuditLogService audit)
        => new(sender, audit, TimeProvider.System, NullLogger<NotificationTestService>.Instance);

    private sealed class StubNotificationSender(NotificationSendResult result) : INotificationSender
    {
        public List<NotificationRequest> Requests { get; } = [];

        public Task<NotificationSendResult> SendAsync(NotificationRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(result);
        }
    }
}

using CustomerHelpAgent.Business.Services.Other;

namespace CustomerHelpAgent.Tests;

/// <summary>測試用：記下送出的通知，不寫資料庫（0.9.100 起）。</summary>
internal sealed class RecordingNotificationSender : INotificationSender
{
    public List<NotificationRequest> Requests { get; } = [];

    public Task<NotificationSendResult> SendAsync(NotificationRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        return Task.FromResult(new NotificationSendResult(1, 0));
    }
}

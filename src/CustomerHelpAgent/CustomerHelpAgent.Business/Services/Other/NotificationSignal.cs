using Microsoft.Extensions.Logging;
using CustomerHelpAgent.Business.Helpers;

namespace CustomerHelpAgent.Business.Services.Other;

/// <summary>
/// 站內通知的即時訊號（0.9.100 起）：只告訴畫面「你的通知有變動」或「公告有變動」，畫面自己重新查詢（訊號不帶內容）。
/// </summary>
public interface INotificationSignal
{
    /// <summary>訂閱某位使用者的通知變動；回傳的物件 Dispose 時取消訂閱（元件 Dispose 時一定要呼叫）。</summary>
    IDisposable Subscribe(int userId, Func<Task> handler);

    /// <summary>訂閱公告變動（所有人）。</summary>
    IDisposable SubscribeAnnouncements(Func<Task> handler);

    void Publish(IEnumerable<int> userIds);

    void PublishAnnouncementsChanged();
}

/// <summary>
/// 單一行程內的訊號（singleton）。多個行程（IIS 重疊回收、多台主機）之間不互通 —— 畫面另以 60 秒輪詢補足。
/// 訂閱、派送與防洩漏的細節在 <see cref="KeyedSignalHub{TKey}"/>（與 AI 初審進度訊號共用）。
/// </summary>
public sealed class NotificationSignal : INotificationSignal
{
    private const int AnnouncementKey = -1;

    private readonly KeyedSignalHub<int> hub;

    public NotificationSignal(ILogger<NotificationSignal> logger)
    {
        hub = new KeyedSignalHub<int>(logger, "Notification");
    }

    /// <summary>目前的訂閱數（測試與診斷用）。</summary>
    public int SubscriberCount => hub.SubscriberCount;

    /// <summary>目前有訂閱的鍵數（測試用：取消訂閱後應歸零）。</summary>
    public int KeyCount => hub.KeyCount;

    public IDisposable Subscribe(int userId, Func<Task> handler) => hub.Subscribe(userId, handler);

    public IDisposable SubscribeAnnouncements(Func<Task> handler) => hub.Subscribe(AnnouncementKey, handler);

    public void Publish(IEnumerable<int> userIds)
    {
        foreach (var userId in userIds.Distinct())
        {
            hub.Publish(userId);
        }
    }

    public void PublishAnnouncementsChanged() => hub.Publish(AnnouncementKey);
}

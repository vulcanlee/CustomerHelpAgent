using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

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
///
/// ⚠️ 防洩漏：singleton 會讓元件的委派一直活著，元件 Dispose 時必須取消訂閱；取消時外層的空集合要原子移除
/// （<c>TryRemove(KeyValuePair)</c>，只在它仍是同一個空集合時才移除，不會誤刪剛加入的訂閱）。
/// ⚠️ 派送時不帶發送端的執行情境（<c>ExecutionContext.SuppressFlow</c>）：不可讓畫面的處理程序繼承發送端的例外情境與錯誤追蹤碼。
/// 每個處理程序各自 try/catch（例如連線已斷的畫面），一個失敗不影響其他人。
/// </summary>
public sealed class NotificationSignal : INotificationSignal
{
    private const int AnnouncementKey = -1;

    private readonly ConcurrentDictionary<int, ConcurrentDictionary<long, Func<Task>>> subscribers = new();
    private readonly ILogger<NotificationSignal> logger;
    private long nextId;

    public NotificationSignal(ILogger<NotificationSignal> logger)
    {
        this.logger = logger;
    }

    /// <summary>目前的訂閱數（測試與診斷用）。</summary>
    public int SubscriberCount => subscribers.Values.Sum(x => x.Count);

    /// <summary>目前有訂閱的鍵數（測試用：取消訂閱後應歸零）。</summary>
    public int KeyCount => subscribers.Count;

    public IDisposable Subscribe(int userId, Func<Task> handler) => Add(userId, handler);

    public IDisposable SubscribeAnnouncements(Func<Task> handler) => Add(AnnouncementKey, handler);

    public void Publish(IEnumerable<int> userIds)
    {
        foreach (var userId in userIds.Distinct())
        {
            Dispatch(userId);
        }
    }

    public void PublishAnnouncementsChanged() => Dispatch(AnnouncementKey);

    private IDisposable Add(int key, Func<Task> handler)
    {
        var id = Interlocked.Increment(ref nextId);
        while (true)
        {
            var bucket = subscribers.GetOrAdd(key, _ => new ConcurrentDictionary<long, Func<Task>>());
            bucket[id] = handler;

            // 剛好被另一個執行緒當成空集合移除了：換一個新的集合重來。
            if (subscribers.TryGetValue(key, out var current) && ReferenceEquals(current, bucket))
            {
                return new Subscription(this, key, id);
            }
        }
    }

    private void Remove(int key, long id)
    {
        if (!subscribers.TryGetValue(key, out var bucket))
        {
            return;
        }

        bucket.TryRemove(id, out _);
        if (bucket.IsEmpty)
        {
            subscribers.TryRemove(new KeyValuePair<int, ConcurrentDictionary<long, Func<Task>>>(key, bucket));
        }
    }

    private void Dispatch(int key)
    {
        if (!subscribers.TryGetValue(key, out var bucket))
        {
            return;
        }

        foreach (var handler in bucket.Values)
        {
            using (ExecutionContext.SuppressFlow())
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await handler();
                    }
                    catch (Exception ex)
                    {
                        // 畫面已關閉或連線已斷：那個畫面的訂閱會在 Dispose 時移除。常見且無害，只記 Debug。
                        logger.LogDebug(ex, "Notification signal handler failed; the subscriber is probably gone. Key={Key}", key);
                    }
                });
            }
        }
    }

    private sealed class Subscription(NotificationSignal owner, int key, long id) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                owner.Remove(key, id);
            }
        }
    }
}

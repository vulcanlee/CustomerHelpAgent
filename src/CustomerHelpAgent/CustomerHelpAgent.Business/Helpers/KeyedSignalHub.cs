using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace CustomerHelpAgent.Business.Helpers;

/// <summary>
/// 依鍵分組的即時訊號（單一行程內）：只告訴訂閱者「這個鍵有變動」，訊號不帶內容，畫面自己重新查詢。
/// 站內通知（<c>NotificationSignal</c>）與 AI 初審進度（<c>PreReviewSignal</c>）共用這一份實作。
///
/// ⚠️ 防洩漏：持有者是 singleton，元件的委派會一直活著，元件 Dispose 時必須取消訂閱；取消時外層的空集合要原子移除
/// （<c>TryRemove(KeyValuePair)</c>，只在它仍是同一個空集合時才移除，不會誤刪剛加入的訂閱）。
/// ⚠️ 派送時不帶發送端的執行情境（<c>ExecutionContext.SuppressFlow</c>）：不可讓畫面的處理程序繼承發送端的例外情境與錯誤追蹤碼。
/// 每個處理程序各自 try/catch（例如連線已斷的畫面），一個失敗不影響其他人。
/// </summary>
public sealed class KeyedSignalHub<TKey>
    where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, ConcurrentDictionary<long, Func<Task>>> subscribers = new();
    private readonly ILogger logger;
    private readonly string signalName;
    private long nextId;

    /// <param name="logger">持有者的 logger（處理程序失敗時記 Debug）。</param>
    /// <param name="signalName">日誌中辨識是哪一種訊號。</param>
    public KeyedSignalHub(ILogger logger, string signalName)
    {
        this.logger = logger;
        this.signalName = signalName;
    }

    /// <summary>目前的訂閱數（測試與診斷用）。</summary>
    public int SubscriberCount => subscribers.Values.Sum(x => x.Count);

    /// <summary>目前有訂閱的鍵數（測試用：取消訂閱後應歸零）。</summary>
    public int KeyCount => subscribers.Count;

    /// <summary>訂閱某個鍵；回傳的物件 Dispose 時取消訂閱。</summary>
    public IDisposable Subscribe(TKey key, Func<Task> handler)
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

    public void Publish(TKey key)
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
                        logger.LogDebug(ex, "Signal handler failed; the subscriber is probably gone. Signal={Signal}, Key={Key}", signalName, key);
                    }
                });
            }
        }
    }

    private void Remove(TKey key, long id)
    {
        if (!subscribers.TryGetValue(key, out var bucket))
        {
            return;
        }

        bucket.TryRemove(id, out _);
        if (bucket.IsEmpty)
        {
            subscribers.TryRemove(new KeyValuePair<TKey, ConcurrentDictionary<long, Func<Task>>>(key, bucket));
        }
    }

    private sealed class Subscription(KeyedSignalHub<TKey> owner, TKey key, long id) : IDisposable
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

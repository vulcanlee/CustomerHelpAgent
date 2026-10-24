using System.Threading.Channels;

namespace CustomerHelpAgent.Web.Agents.PreReview;

/// <summary>
/// AI 初審的待跑佇列（singleton，有界 Channel，內容是 <c>PreReviewRun.Id</c>）。消費端是 <see cref="PreReviewWorker"/>。
/// 送件掛勾與「重新初審」只把 Id 放進來，初審一律由 worker 在自己的 scope 執行。
/// ⚠️ 不可以在 Blazor 的事件處理裡直接 <c>Task.Run</c> 跑初審（理由同 <c>ScheduledJobTriggerQueue</c>）。
/// <para>
/// ⚠️ FullMode 用 <see cref="BoundedChannelFullMode.Wait"/> 搭配 <c>TryWrite</c>（同 <c>ChannelEmailQueue</c>）：佇列滿時 <c>TryWrite</c> 老實回 false。
/// 入列失敗不會遺失：那一輪仍是 Pending 存在資料庫，這裡記下「有溢出」，worker 清空佇列後重新掃描未完成的初審補入列。
/// </para>
/// </summary>
public sealed class PreReviewQueue
{
    public const int DefaultCapacity = 100;

    private readonly Channel<int> channel;
    private readonly int capacity;
    private readonly ILogger<PreReviewQueue> logger;
    private int overflowed;

    public PreReviewQueue(ILogger<PreReviewQueue> logger)
        : this(logger, DefaultCapacity)
    {
    }

    internal PreReviewQueue(ILogger<PreReviewQueue> logger, int capacity)
    {
        this.logger = logger;
        this.capacity = capacity;
        channel = Channel.CreateBounded<int>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public ChannelReader<int> Reader => channel.Reader;

    /// <summary>放入一輪初審；佇列滿時回 false（該輪留在資料庫，稍後由 worker 重新掃描補上）。</summary>
    public bool TryEnqueue(int runId)
    {
        if (channel.Writer.TryWrite(runId))
        {
            return true;
        }

        Interlocked.Exchange(ref overflowed, 1);
        logger.LogWarning("Pre-review queue is full; the run stays pending and will be rescanned. RunId={RunId}, Capacity={Capacity}", runId, capacity);
        return false;
    }

    /// <summary>上次呼叫後是否有入列失敗（讀取同時清除）。</summary>
    internal bool TakeOverflow() => Interlocked.Exchange(ref overflowed, 0) == 1;
}

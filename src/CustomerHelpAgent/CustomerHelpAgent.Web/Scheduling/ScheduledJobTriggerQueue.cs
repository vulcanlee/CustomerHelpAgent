using System.Collections.Concurrent;
using System.Threading.Channels;

namespace CustomerHelpAgent.Web.Scheduling;

/// <summary>
/// 「立即執行」的請求佇列（singleton，0.9.96 起）。管理頁只把請求放進來，由 <see cref="JobSchedulerWorker"/> 以自己的執行環境啟動。
///
/// ⚠️ 不可以在 Blazor 的事件處理裡直接 <c>Task.Run</c> 執行作業：會繼承畫面的 ExceptionContext 與錯誤追蹤碼，
/// 作業的錯誤會被記成「畫面操作」；而且主機不知道有這個工作，關機時不會等它。
/// </summary>
public sealed class ScheduledJobTriggerQueue
{
    private readonly Channel<ManualJobRequest> channel = Channel.CreateUnbounded<ManualJobRequest>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<string, byte> pending = new(StringComparer.OrdinalIgnoreCase);

    public ChannelReader<ManualJobRequest> Reader => channel.Reader;

    /// <summary>放入一個手動執行請求；同一個作業已在佇列或執行中（本行程）時回 false。</summary>
    public bool TryEnqueue(string jobName, string? account, int? userId = null)
    {
        if (!pending.TryAdd(jobName, 0))
        {
            return false;
        }

        if (!channel.Writer.TryWrite(new ManualJobRequest(jobName, account, userId)))
        {
            pending.TryRemove(jobName, out _);
            return false;
        }

        return true;
    }

    /// <summary>這個作業的手動執行是否還在佇列或執行中（本行程）。</summary>
    public bool IsPending(string jobName) => pending.ContainsKey(jobName);

    /// <summary>手動執行結束（不論結果）時由排程器呼叫。</summary>
    public void Complete(string jobName) => pending.TryRemove(jobName, out _);
}

/// <summary>一個「立即執行」請求。</summary>
/// <param name="UserId">觸發者的使用者 Id（0.9.100 起；作業失敗時通知他）。</param>
public sealed record ManualJobRequest(string JobName, string? Account, int? UserId = null);

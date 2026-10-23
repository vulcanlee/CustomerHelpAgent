namespace CustomerHelpAgent.Tests;

/// <summary>
/// 測試用：計時器不會自己觸發，由測試呼叫 <see cref="TriggerableTimer.Fire"/>（0.9.122 起，給會輪詢的元件用）。
/// 時間仍是系統時間；只接管 <see cref="CreateTimer"/>（<c>PeriodicTimer</c> 經由它建立計時器）。
/// 要推進「現在時間」請用 <see cref="ManualTimeProvider"/>。
/// </summary>
internal sealed class TriggerableTimeProvider : TimeProvider
{
    private readonly List<TriggerableTimer> timers = [];

    public IReadOnlyList<TriggerableTimer> Timers
    {
        get
        {
            lock (timers)
            {
                return [.. timers];
            }
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new TriggerableTimer(callback, state);
        lock (timers)
        {
            timers.Add(timer);
        }

        return timer;
    }

    internal sealed class TriggerableTimer(TimerCallback callback, object? state) : ITimer
    {
        public bool Disposed { get; private set; }

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period) => !Disposed;

        public void Dispose() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

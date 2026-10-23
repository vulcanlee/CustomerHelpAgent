using Microsoft.Extensions.Options;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 永遠回傳同一個設定實例的 <see cref="IOptionsMonitor{T}"/>。
///
/// 原本是 AiLogAnalysisServiceTests 的巢狀私有型別，AiUsageCostCalculator 的測試也要用，
/// 因此抽成共用檔，避免同一段替身在測試專案裡存在兩份。
/// </summary>
internal sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
{
    public StaticOptionsMonitor(T value)
    {
        CurrentValue = value;
    }

    public T CurrentValue { get; }

    public T Get(string? name) => CurrentValue;

    public IDisposable OnChange(Action<T, string?> listener) => new NoopDisposable();

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}

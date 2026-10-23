using CustomerHelpAgent.Business.Services.Other;

namespace CustomerHelpAgent.Tests;

/// <summary>AI 用量上限的測試替身：預設全部放行，可指定要擋下的上限，並記下被呼叫的使用者。</summary>
internal sealed class StubAiQuotaService : IAiQuotaService
{
    public AiQuotaLimitStatus? Block { get; set; }

    public List<int?> Checked { get; } = [];

    public List<int?> Notified { get; } = [];

    public Task<AiQuotaLimitStatus?> CheckAsync(int? userId, CancellationToken cancellationToken = default)
    {
        Checked.Add(userId);
        return Task.FromResult(Block);
    }

    public Task<IReadOnlyList<AiQuotaLimitStatus>> GetStatusAsync(int? userId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<AiQuotaLimitStatus>>([]);

    public Task NotifyIfReachedAsync(int? userId, CancellationToken cancellationToken = default)
    {
        Notified.Add(userId);
        return Task.CompletedTask;
    }
}

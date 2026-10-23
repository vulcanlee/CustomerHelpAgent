using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.Web.Configuration;

namespace CustomerHelpAgent.Web.Caching;

public sealed class DistributedCacheService : ICacheService
{
    private readonly IDistributedCache cache;
    private readonly ILogger<DistributedCacheService> logger;
    private readonly TimeSpan defaultExpiration;

    public DistributedCacheService(
        IDistributedCache cache,
        ILogger<DistributedCacheService> logger,
        IOptions<CacheSettings> options)
    {
        this.cache = cache;
        this.logger = logger;
        this.defaultExpiration = TimeSpan.FromMinutes(Math.Max(1, options.Value.DefaultExpirationMinutes));
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        var bytes = await cache.GetAsync(key, cancellationToken);
        if (bytes is null || bytes.Length == 0)
        {
            // 只記快取鍵，不記快取內容 —— 內容可能含業務資料。
            logger.LogDebug("Cache miss. Key={Key}", key);
            return default;
        }

        logger.LogDebug("Cache hit. Key={Key}, Bytes={Bytes}", key, bytes.Length);
        return JsonSerializer.Deserialize<T>(bytes);
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan? expiration = null, CancellationToken cancellationToken = default)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        var options = new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = expiration ?? defaultExpiration
        };

        await cache.SetAsync(key, bytes, options, cancellationToken);
    }

    public async Task<T> GetOrCreateAsync<T>(string key, Func<Task<T>> factory, TimeSpan? expiration = null, CancellationToken cancellationToken = default)
    {
        var cached = await GetAsync<T>(key, cancellationToken);
        if (cached is not null)
        {
            return cached;
        }

        var value = await factory();
        await SetAsync(key, value, expiration, cancellationToken);
        return value;
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        => cache.RemoveAsync(key, cancellationToken);
}

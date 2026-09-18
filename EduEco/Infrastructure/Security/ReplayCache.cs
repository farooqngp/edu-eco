using Microsoft.Extensions.Caching.Memory;
using StackExchange.Redis;

namespace EduEco.Infrastructure.Security;

/// <summary>One-time-use tracker for proof/token identifiers (DPoP <c>jti</c>, logout token <c>jti</c>).</summary>
public interface IReplayCache
{
    /// <summary>Atomically records <paramref name="key"/>; returns <c>false</c> when it was already seen.</summary>
    Task<bool> TryAddAsync(string key, TimeSpan lifetime, CancellationToken cancellationToken = default);
}

/// <summary>Single-instance replay cache.</summary>
public sealed class MemoryReplayCache(IMemoryCache cache) : IReplayCache
{
    private readonly Lock _gate = new();

    public Task<bool> TryAddAsync(string key, TimeSpan lifetime, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (cache.TryGetValue(key, out _))
            {
                return Task.FromResult(false);
            }

            cache.Set(key, true, lifetime);
            return Task.FromResult(true);
        }
    }
}

/// <summary>Cluster-wide replay cache (Redis <c>SET NX PX</c>): a proof accepted by one instance is rejected by all others.</summary>
public sealed class RedisReplayCache(IConnectionMultiplexer redis) : IReplayCache
{
    public async Task<bool> TryAddAsync(string key, TimeSpan lifetime, CancellationToken cancellationToken = default) =>
        await redis.GetDatabase().StringSetAsync("eduEco:replay:" + key, 1, lifetime, When.NotExists).ConfigureAwait(false);
}

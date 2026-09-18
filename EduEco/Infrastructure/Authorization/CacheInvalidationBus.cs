using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace EduEco.Infrastructure.Authorization;

/// <summary>Broadcasts cache-tag invalidations to every instance (scale-out).</summary>
public interface ICacheInvalidationBus
{
    Task PublishAsync(string tag, CancellationToken cancellationToken = default);
}

/// <summary>Single instance: local eviction is enough.</summary>
public sealed class LocalCacheInvalidationBus : ICacheInvalidationBus
{
    public Task PublishAsync(string tag, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>
/// Redis pub/sub: every instance evicts the tag from its in-process (L1) cache. Publishing is best effort: the change is
/// already committed, and the cache entry TTL bounds staleness on other instances while Redis is unavailable.
/// </summary>
public sealed partial class RedisCacheInvalidationBus(IConnectionMultiplexer redis, ILogger<RedisCacheInvalidationBus> logger) : ICacheInvalidationBus
{
    public static readonly RedisChannel Channel = RedisChannel.Literal("eduEco:cache-invalidation");

    public async Task PublishAsync(string tag, CancellationToken cancellationToken = default)
    {
        try
        {
            await redis.GetSubscriber().PublishAsync(Channel, tag).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            LogPublishFailed(logger, tag, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Cache invalidation for tag {Tag} could not be broadcast; other instances converge after the cache TTL")]
    private static partial void LogPublishFailed(ILogger logger, string tag, Exception exception);
}

/// <summary>Subscribes to <see cref="RedisCacheInvalidationBus.Channel"/> and evicts local cache entries by tag.</summary>
/// <remarks>Subscribes in the background and retries until Redis is reachable, so a Redis outage never blocks startup.</remarks>
public sealed partial class RedisCacheInvalidationListener(
    IConnectionMultiplexer redis,
    HybridCache cache,
    ILogger<RedisCacheInvalidationListener> logger) : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SubscribeAsync().ConfigureAwait(false);
                return; // StackExchange.Redis restores the subscription after reconnects.
            }
            catch (Exception ex) when (ex is RedisException or TimeoutException)
            {
                LogSubscribeFailed(logger, ex);
                await Task.Delay(RetryDelay, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        if (redis.IsConnected)
        {
            await redis.GetSubscriber().UnsubscribeAsync(RedisCacheInvalidationBus.Channel).ConfigureAwait(false);
        }
    }

    private async Task SubscribeAsync()
    {
        var queue = await redis.GetSubscriber().SubscribeAsync(RedisCacheInvalidationBus.Channel).ConfigureAwait(false);
        queue.OnMessage(async message =>
        {
            try
            {
                await cache.RemoveByTagAsync(message.Message.ToString()).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // A failed eviction must not kill the subscription; TTL bounds staleness.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogEvictionFailed(logger, ex);
            }
        });
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cache invalidation message could not be applied")]
    private static partial void LogEvictionFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Subscribing to cache invalidations failed; retrying")]
    private static partial void LogSubscribeFailed(ILogger logger, Exception exception);
}

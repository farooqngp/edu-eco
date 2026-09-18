using EduEco.Infrastructure.Authorization;
using EduEco.Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace EduEco.ServiceRegistry;

public static class SecurityServicesExtensions
{
    public const string RedisConnectionStringName = "Redis";

    /// <summary>
    /// Distributed cache for scale-out: with <c>ConnectionStrings:Redis</c>, Redis becomes the HybridCache L2 and backs
    /// cluster-wide replay protection. Without it everything stays in-process (single instance / development).
    /// </summary>
    public static IServiceCollection AddEduEcoDistributedCache(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddEduEcoReplayCache(configuration);
        if (AddRedisConnection(services, configuration) is not { } multiplexer)
        {
            services.TryAddSingleton<ICacheInvalidationBus, LocalCacheInvalidationBus>();
            return services;
        }

        services.TryAddSingleton<ICacheInvalidationBus, RedisCacheInvalidationBus>();
        services.AddHostedService<RedisCacheInvalidationListener>();
        services.AddStackExchangeRedisCache(options =>
        {
            options.InstanceName = "eduEco:";
            options.ConnectionMultiplexerFactory = () => multiplexer.Value;
        });
        return services;
    }

    /// <summary>
    /// One-time-use tracking (DPoP jti, client assertion jti, logout token jti): Redis when <c>ConnectionStrings:Redis</c>
    /// is set (cluster-wide), in-process otherwise.
    /// </summary>
    public static IServiceCollection AddEduEcoReplayCache(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddMemoryCache();
        if (AddRedisConnection(services, configuration) is null)
        {
            services.TryAddSingleton<IReplayCache, MemoryReplayCache>();
        }
        else
        {
            services.TryAddSingleton<IReplayCache, RedisReplayCache>();
        }

        return services;
    }

    /// <summary>One multiplexer per process, shared by the cache (L2), the invalidation bus and the replay cache.</summary>
    private static Lazy<Task<IConnectionMultiplexer>>? AddRedisConnection(IServiceCollection services, IConfiguration configuration)
    {
        var redis = configuration.GetConnectionString(RedisConnectionStringName);
        if (string.IsNullOrWhiteSpace(redis))
        {
            return null;
        }

        if (services.FirstOrDefault(d => d.ServiceType == typeof(RedisConnectionHolder))?.ImplementationInstance is RedisConnectionHolder existing)
        {
            return existing.Multiplexer;
        }

        // abortConnect=false: start even while Redis is down and reconnect in the background. Replay checks fail closed
        // (requests needing them are rejected) until Redis is back.
        var redisOptions = ConfigurationOptions.Parse(redis);
        redisOptions.AbortOnConnectFail = false;
        var holder = new RedisConnectionHolder(new Lazy<Task<IConnectionMultiplexer>>(async () =>
            await ConnectionMultiplexer.ConnectAsync(redisOptions).ConfigureAwait(false)));
        services.AddSingleton(holder);
        services.TryAddSingleton(_ => holder.Multiplexer.Value.GetAwaiter().GetResult());
        return holder.Multiplexer;
    }

    private sealed record RedisConnectionHolder(Lazy<Task<IConnectionMultiplexer>> Multiplexer);

    /// <summary>RFC 9449 DPoP proof validation (token endpoint and resource servers).</summary>
    public static IServiceCollection AddEduEcoDPoP(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<DPoPOptions>().Bind(configuration.GetSection(DPoPOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<DPoPProofValidator>();
        return services;
    }
}

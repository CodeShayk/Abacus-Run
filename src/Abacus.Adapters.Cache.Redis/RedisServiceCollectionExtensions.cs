using Abacus.Run.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Abacus.Adapters.Cache.Redis;

/// <summary>
/// Registration for the Redis cache adapter.
/// </summary>
/// <remarks>
/// One call, one substitution. Redis takes over every cache-shaped capability the framework has —
/// the general store and the SSE backplane — because the adapter, not the individual capability, is
/// the unit a deployment chooses. Domain messaging is a separate concern with its own adapters; see
/// <c>Abacus.Adapters.Messaging.*</c>.
/// </remarks>
public static class RedisServiceCollectionExtensions
{
    /// <summary>
    /// Replaces the framework's in-memory cache adapter with Redis.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The SSE backplane moves with it, which is what lets a subscriber attach to a replica that is
    /// not running the instance. Nothing here is a system of record: the durable event store remains
    /// the source of truth, so trimming a stream or evicting a key loses nothing a reader cannot
    /// recover.
    /// </para>
    /// <para>
    /// Without this call the in-memory adapter serves both jobs perfectly well for a single replica,
    /// which is why this is opt-in rather than required.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">Redis connection string, e.g. <c>localhost:6379</c>.</param>
    /// <param name="maxStreamLength">
    /// Approximate max entries per instance notification stream, auto-trimmed with <c>MAXLEN ~</c>.
    /// </param>
    /// <param name="keyPrefix">Namespace for general cache keys, so one Redis can serve several apps.</param>
    public static IServiceCollection AddRedisCache(
        this IServiceCollection services,
        string connectionString,
        int maxStreamLength = 10_000,
        string keyPrefix = "abacus:cache:")
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        // Thread-safe and internally multiplexed, so one connection is the right number.
        services.TryAddSingleton<IConnectionMultiplexer>(_ =>
            ConnectionMultiplexer.Connect(connectionString));

        // Replace rather than TryAdd: naming Redis is a decision, and it should win over the
        // in-memory default registered earlier. The framework resolves the backplane and the store
        // from whatever adapter is registered, so both move with this one line.
        services.RemoveAll<ICacheAdapter>();
        services.AddSingleton<ICacheAdapter>(sp => new RedisCacheAdapter(
            sp.GetRequiredService<IConnectionMultiplexer>(),
            maxStreamLength,
            keyPrefix,
            sp.GetService<ILoggerFactory>()));

        return services;
    }
}

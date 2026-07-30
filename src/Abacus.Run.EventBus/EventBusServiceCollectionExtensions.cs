using Abacus.Run.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Abacus.Run.EventBus;

public static class EventBusServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Redis Streams implementation of <see cref="IEventBus"/> and the Redis
    /// control channel. Call this from <c>Program.cs</c> to switch from the in-memory bus to Redis.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">Redis connection string (e.g. <c>localhost:6379</c>).</param>
    /// <param name="maxStreamLength">
    /// Approximate max length per Redis Stream. Streams are auto-trimmed with <c>MAXLEN ~</c>.
    /// The durable SQL event store is the source of truth; Redis provides live fan-out only.
    /// </param>
    public static IServiceCollection AddRedisEventBus(
        this IServiceCollection services,
        string connectionString,
        int maxStreamLength = 10_000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        // Register the ConnectionMultiplexer as a singleton; it is inherently thread-safe.
        services.TryAddSingleton<IConnectionMultiplexer>(_ =>
            ConnectionMultiplexer.Connect(connectionString));

        // Replace any existing IEventBus registration (e.g. InMemoryEventBus) with Redis.
        services.RemoveAll<IEventBus>();
        services.AddSingleton<IEventBus>(sp => new RedisEventBus(
            sp.GetRequiredService<IConnectionMultiplexer>(),
            maxStreamLength,
            sp.GetService<ILogger<RedisEventBus>>()));

        // Control channel for cancel/suspend signals across replicas.
        services.TryAddSingleton(sp => new RedisControlChannel(
            sp.GetRequiredService<IConnectionMultiplexer>(),
            sp.GetService<ILogger<RedisControlChannel>>()));

        return services;
    }
}

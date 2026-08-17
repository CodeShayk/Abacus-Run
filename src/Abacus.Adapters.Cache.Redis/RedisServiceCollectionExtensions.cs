using Abacus.Run.Abstractions;
using Abacus.Run.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Abacus.Adapters.Cache.Redis;

/// <summary>
/// Registration for the Redis adapters. Each method replaces one framework default, so a host takes
/// only the pieces it wants — the event bus without the broker, or the reverse.
/// </summary>
public static class RedisServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Redis Streams implementation of <see cref="IEventBus"/> and the Redis control
    /// channel, replacing the framework's in-process bus.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">Redis connection string, e.g. <c>localhost:6379</c>.</param>
    /// <param name="maxStreamLength">
    /// Approximate max length per Redis Stream, auto-trimmed with <c>MAXLEN ~</c>. The durable event
    /// store is the source of truth; Redis provides live fan-out only, so trimming loses nothing a
    /// reader cannot get from history.
    /// </param>
    public static IServiceCollection AddRedisEventBus(
        this IServiceCollection services,
        string connectionString,
        int maxStreamLength = 10_000)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        AddConnection(services, connectionString);

        // Replace rather than TryAdd: naming Redis is a decision, and it should win over whatever
        // default was registered first.
        services.RemoveAll<IEventBus>();
        services.AddSingleton<IEventBus>(sp => new RedisEventBus(
            sp.GetRequiredService<IConnectionMultiplexer>(),
            maxStreamLength,
            sp.GetService<ILogger<RedisEventBus>>()));

        // Control channel for cancel and suspend signals across replicas.
        services.TryAddSingleton(sp => new RedisControlChannel(
            sp.GetRequiredService<IConnectionMultiplexer>(),
            sp.GetService<ILogger<RedisControlChannel>>()));

        return services;
    }

    /// <summary>
    /// Replaces the in-process workflow broker with the Redis one, so workflows in separate services
    /// can publish and subscribe to each other.
    /// </summary>
    /// <remarks>
    /// Only messages published at <see cref="DeliveryScope.Distributed"/> cross the wire; local ones
    /// keep the in-process path this broker composes. Registering it therefore widens what a
    /// publisher <em>may</em> do without changing what any existing publisher does.
    /// </remarks>
    public static IServiceCollection AddRedisEventBroker(
        this IServiceCollection services,
        string connectionString,
        int maxStreamLength = 100_000)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        AddConnection(services, connectionString);

        services.RemoveAll<IEventBroker>();
        services.AddSingleton<IEventBroker>(sp => new RedisEventBroker(
            sp.GetRequiredService<IConnectionMultiplexer>(),
            maxStreamLength,
            loggerFactory: sp.GetService<ILoggerFactory>()));

        return services;
    }

    /// <summary>
    /// One multiplexer shared by every Redis adapter. It is thread-safe and multiplexes internally,
    /// so a second connection would cost sockets and buy nothing.
    /// </summary>
    private static void AddConnection(IServiceCollection services, string connectionString)
        => services.TryAddSingleton<IConnectionMultiplexer>(_ =>
            ConnectionMultiplexer.Connect(connectionString));
}

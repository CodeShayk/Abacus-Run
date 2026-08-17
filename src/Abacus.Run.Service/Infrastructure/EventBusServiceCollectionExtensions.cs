using Abacus.Run.Abstractions;
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
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.TryAddSingleton<IConnectionMultiplexer>(_ =>
            ConnectionMultiplexer.Connect(connectionString));

        services.RemoveAll<IEventBroker>();
        services.AddSingleton<IEventBroker>(sp => new RedisEventBroker(
            sp.GetRequiredService<IConnectionMultiplexer>(),
            maxStreamLength,
            loggerFactory: sp.GetService<ILoggerFactory>()));

        return services;
    }

    /// <summary>
    /// Replaces the in-process workflow broker with the RabbitMQ one. An alternative to
    /// <see cref="AddRedisEventBroker"/>, not a companion to it — register one or the other.
    /// </summary>
    /// <remarks>
    /// RabbitMQ filters by routing key at the exchange, so a subscriber is never woken for a message
    /// it would discard, and it dead-letters natively. It cannot replay: a queue holds what arrives
    /// after it is bound, which <see cref="BrokerCapabilities.SupportsReplay"/> reports honestly.
    /// </remarks>
    /// <param name="connectionString">AMQP URI, e.g. <c>amqp://guest:guest@localhost:5672</c>.</param>
    public static IServiceCollection AddRabbitMqEventBroker(
        this IServiceCollection services,
        string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.RemoveAll<IEventBroker>();
        services.AddSingleton<IEventBroker>(sp =>
            // Blocking once at composition is deliberate: a broker that cannot connect should fail
            // startup, not surface as a workflow that silently never triggers.
            RabbitMqEventBroker.CreateAsync(connectionString, sp.GetService<ILoggerFactory>())
                .GetAwaiter().GetResult());

        return services;
    }
}

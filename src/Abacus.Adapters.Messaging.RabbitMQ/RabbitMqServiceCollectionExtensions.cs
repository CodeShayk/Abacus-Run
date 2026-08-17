using Abacus.Run.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Abacus.Adapters.Messaging.RabbitMQ;

public static class RabbitMqServiceCollectionExtensions
{
    /// <summary>
    /// Replaces the in-process workflow broker with the RabbitMQ one. An alternative to the Redis
    /// broker, not a companion to it — register one or the other, since the second registration
    /// replaces the first.
    /// </summary>
    /// <remarks>
    /// RabbitMQ filters by routing key at the exchange, so a subscriber is never woken for a message
    /// it would discard, and it dead-letters natively. It cannot replay — a queue holds only what
    /// arrives after it is bound — and <see cref="BrokerCapabilities.SupportsReplay"/> reports that
    /// rather than pretending to a contract it cannot meet.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">AMQP URI, e.g. <c>amqp://guest:guest@localhost:5672</c>.</param>
    public static IServiceCollection AddRabbitMqEventBroker(
        this IServiceCollection services,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.RemoveAll<IEventBroker>();
        services.AddSingleton<IEventBroker>(sp =>
            // Blocking once at composition is deliberate: a broker that cannot connect should fail
            // startup, not surface later as a workflow that silently never triggers.
            RabbitMqEventBroker.CreateAsync(connectionString, sp.GetService<ILoggerFactory>())
                .GetAwaiter().GetResult());

        return services;
    }
}

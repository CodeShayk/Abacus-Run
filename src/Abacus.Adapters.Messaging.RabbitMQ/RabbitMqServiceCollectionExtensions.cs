using Abacus.Run.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Abacus.Adapters.Messaging.RabbitMQ;

/// <summary>
/// Registration for the RabbitMQ messaging adapter.
/// </summary>
/// <remarks>
/// One call, one substitution. RabbitMQ takes over every messaging capability — domain events and
/// control signalling — because the adapter, not the individual capability, is the unit a deployment
/// chooses. Caching is a separate concern with its own adapters; see <c>Abacus.Adapters.Cache.*</c>.
/// </remarks>
public static class RabbitMqServiceCollectionExtensions
{
    /// <summary>
    /// Replaces the framework's in-process messaging adapter with RabbitMQ, so workflows in separate
    /// services can publish and subscribe to each other and control signals reach every replica.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only messages published at <see cref="DeliveryScope.Distributed"/> cross the wire; local ones
    /// keep the in-process path the broker composes. Registering this therefore widens what a
    /// publisher <em>may</em> do without changing what any existing publisher does.
    /// </para>
    /// <para>
    /// Without this call the in-process adapter serves both jobs correctly for a single service,
    /// which is why it is opt-in rather than required.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">AMQP URI, e.g. <c>amqp://guest:guest@localhost:5672</c>.</param>
    public static IServiceCollection AddRabbitMqMessaging(
        this IServiceCollection services,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        // Replace rather than TryAdd: naming RabbitMQ is a decision, and it should win over the
        // in-process default. The framework resolves the broker and the control channel from
        // whatever adapter is registered, so both move with this one line.
        services.RemoveAll<IMessagingAdapter>();
        services.AddSingleton<IMessagingAdapter>(sp =>
            // Blocking once at composition is deliberate: a transport that cannot connect should
            // fail startup, not surface later as a workflow that silently never triggers.
            RabbitMqMessagingAdapter.CreateAsync(connectionString, sp.GetService<ILoggerFactory>())
                .GetAwaiter().GetResult());

        return services;
    }
}

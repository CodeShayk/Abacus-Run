namespace Abacus.Run.Abstractions;

/// <summary>An out-of-band instruction to whichever replica is running an instance.</summary>
public sealed record ControlSignal(string InstanceId, string Action, string? Reason, DateTimeOffset At);

public static class ControlActions
{
    public const string Cancel = "cancel";
    public const string Suspend = "suspend";
}

/// <summary>
/// Carries control instructions between replicas — "stop what you are doing", not "something
/// happened".
/// </summary>
/// <remarks>
/// Separate from the domain-event broker because the delivery semantics differ. A domain event is
/// routed to whoever declared interest and must not be lost; a control signal is broadcast to
/// everyone and is pure optimisation — the instance row is the authority, and a replica that misses
/// a signal discovers the same fact the next time it looks. Losing one costs latency, not
/// correctness.
/// </remarks>
public interface IControlChannel
{
    ValueTask PublishAsync(ControlSignal signal, CancellationToken cancellationToken);

    /// <summary>Registers a handler for one instance. Disposing the handle unsubscribes.</summary>
    Task<IAsyncDisposable> SubscribeAsync(
        string instanceId,
        Func<ControlSignal, Task> handler,
        CancellationToken cancellationToken);
}

/// <summary>
/// One adapter per messaging technology, supplying every messaging capability the framework needs.
/// </summary>
/// <remarks>
/// <para>
/// The mirror of <see cref="ICacheAdapter"/>, and for the same reason: the adapter is the unit a
/// deployment chooses, not the individual capability. Registering
/// <c>Abacus.Adapters.Messaging.RabbitMQ</c> — or a later <c>Abacus.Adapters.Messaging.AWS</c> —
/// moves domain events and control signalling together, so a deployment cannot end up publishing
/// domain events over one transport while signalling over another.
/// </para>
/// <para>
/// A new technology is one class implementing this interface plus its registration extension.
/// Nothing in the framework changes, because the framework only ever resolves the capabilities from
/// whichever adapter is registered.
/// </para>
/// </remarks>
public interface IMessagingAdapter
{
    /// <summary>The technology this adapter speaks, for diagnostics and startup logging.</summary>
    string Technology { get; }

    /// <summary>
    /// True when messages reach other services. False for the in-process default, which is correct
    /// for a single service and silently insufficient for several — so it is stated, not assumed.
    /// </summary>
    bool IsDistributed { get; }

    /// <summary>Topic pub/sub for domain events: what starts and resumes workflows.</summary>
    IDomainEventBroker DomainEventBroker { get; }

    /// <summary>Cross-replica control signalling: cancel and suspend.</summary>
    IControlChannel ControlChannel { get; }
}

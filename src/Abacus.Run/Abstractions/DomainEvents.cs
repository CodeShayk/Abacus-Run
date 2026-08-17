namespace Abacus.Run.Abstractions;

/// <summary>How far a message is allowed to travel.</summary>
/// <remarks>
/// <para>
/// <see cref="Local"/> is the default and the ordinary case: a workflow publishes, another workflow
/// in the same service subscribes, and nothing touches the network. Multi-service pub/sub is the
/// extension, not the baseline — it is opted into per message by publishing at
/// <see cref="Distributed"/> and registering a transport that can carry it.
/// </para>
/// <para>
/// Scope travels on the message rather than being fixed by the call site or by whichever transport
/// happens to be registered, so the same publishing code is correct in a single-service deployment
/// and in a fleet. A transport that cannot honour the scope it is handed fails loudly rather than
/// delivering locally and looking like it worked; see <see cref="DomainEventBrokerCapabilities"/>.
/// </para>
/// </remarks>
public enum DeliveryScope
{
    /// <summary>
    /// This service only. The default. Never leaves the host, even when a distributed transport is
    /// registered — a message scoped <see cref="Local"/> is a private implementation detail of the
    /// service that published it.
    /// </summary>
    Local,

    /// <summary>
    /// Crosses the service boundary, for pub/sub between separately deployed services. Requires a
    /// transport with <see cref="DomainEventBrokerCapabilities.SupportsDistributed"/>.
    /// </summary>
    Distributed
}

/// <summary>What a subscriber concluded about one delivery.</summary>
public enum DeliveryOutcome
{
    /// <summary>Handled. The transport may acknowledge and drop the message.</summary>
    Ack,

    /// <summary>Not handled, but worth another attempt. The transport redelivers subject to its own policy.</summary>
    Retry,

    /// <summary>Never going to succeed. Route to the dead-letter destination and stop redelivering.</summary>
    DeadLetter
}

/// <summary>
/// A domain message on a topic. Distinct from <c>EventEnvelope</c>, which is per-instance progress
/// reporting for the SSE stream: an envelope answers "what is this instance doing", a
/// <see cref="DomainEventMessage"/> answers "what happened in the domain".
/// </summary>
public sealed record DomainEventMessage
{
    /// <summary>
    /// Stable identity for the message, used as the deduplication key by subscribers.
    /// </summary>
    /// <remarks>
    /// Delivery is at-least-once across every distributed transport worth using, so this is not
    /// optional bookkeeping — it is the only thing standing between a redelivery and a second
    /// side effect.
    /// </remarks>
    public required string MessageId { get; init; }

    /// <summary>Dot-delimited topic, e.g. <c>orders.placed</c>. Matched against subscriber patterns.</summary>
    public required string Topic { get; init; }

    public required string PayloadJson { get; init; }

    /// <summary>
    /// Defaults to <see cref="DeliveryScope.Local"/>: a message stays inside the publishing service
    /// unless it is deliberately promoted. Leaking a message across the boundary should be a
    /// decision someone made, not something that happens because a transport was swapped in.
    /// </summary>
    public DeliveryScope Scope { get; init; } = DeliveryScope.Local;

    /// <summary>
    /// Routes the message to one specific waiting party rather than to everyone matching the topic —
    /// an order id, a case reference. Subscribers that declare a correlation key receive only
    /// messages carrying the same value.
    /// </summary>
    public string? CorrelationKey { get; init; }

    /// <summary>
    /// Owning tenant. Where multi-tenancy is enabled this is required, and a subscriber never sees a
    /// message from another tenant — FR-4.7 treats a cross-tenant read as a security defect.
    /// </summary>
    public string? TenantId { get; init; }

    /// <summary>Set when a workflow published this message, so a consumer can trace provenance.</summary>
    public string? SourceInstanceId { get; init; }

    /// <summary>
    /// Transport-level metadata carried across the boundary — <c>traceparent</c>, schema version,
    /// content type. Kept separate from the payload so infrastructure never has to parse domain data.
    /// </summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>One attempt to hand a message to one subscriber.</summary>
public sealed record DomainEventDelivery
{
    public required DomainEventMessage Message { get; init; }

    /// <summary>The subscription this delivery belongs to. Distinguishes fan-out copies of one message.</summary>
    public required string SubscriptionId { get; init; }

    /// <summary>1 on first delivery. Lets a handler give up rather than loop on a poison message.</summary>
    public int Attempt { get; init; } = 1;

    /// <summary>
    /// Transport-assigned position (a Redis stream id, a Service Bus sequence number). Opaque to
    /// callers; present so a handler can log something an operator can grep for in the broker.
    /// </summary>
    public string? TransportToken { get; init; }
}

/// <summary>What a handler concluded, and why.</summary>
public sealed record DeliveryResult
{
    private DeliveryResult(DeliveryOutcome outcome, string? reason)
    {
        Outcome = outcome;
        Reason = reason;
    }

    public DeliveryOutcome Outcome { get; }

    /// <summary>Required for <see cref="DeliveryOutcome.DeadLetter"/>: a dead letter with no stated cause is unactionable.</summary>
    public string? Reason { get; }

    public static DeliveryResult Ack { get; } = new(DeliveryOutcome.Ack, null);

    public static DeliveryResult Retry(string? reason = null) => new(DeliveryOutcome.Retry, reason);

    public static DeliveryResult DeadLetter(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new DeliveryResult(DeliveryOutcome.DeadLetter, reason);
    }
}

/// <summary>Where a new subscription begins reading.</summary>
public enum SubscriptionStart
{
    /// <summary>Only messages published after the subscription is established.</summary>
    Now,

    /// <summary>
    /// Everything the transport still retains. Requires <see cref="DomainEventBrokerCapabilities.SupportsReplay"/>.
    /// </summary>
    Earliest
}

/// <summary>Declares what a subscriber wants to receive and how it wants to receive it.</summary>
public sealed record DomainEventSubscriptionOptions
{
    /// <summary>
    /// Topic pattern: literal segments, <c>*</c> for exactly one segment, <c>#</c> for the trailing
    /// remainder. See <see cref="TopicPattern"/>.
    /// </summary>
    public required string TopicFilter { get; init; }

    /// <summary>
    /// Null means broadcast — every subscriber gets its own copy, which is what an observer wants.
    /// A non-null group means competing consumers: exactly one member of the group handles each
    /// message, which is what work routing wants.
    /// </summary>
    /// <remarks>
    /// These are genuinely different delivery semantics rather than a tuning knob, and picking the
    /// wrong one fails quietly — a work handler registered as a broadcast subscriber does the same
    /// job once per replica. Making the group name the switch keeps the choice explicit at every
    /// call site.
    /// </remarks>
    public string? ConsumerGroup { get; init; }

    /// <summary>Receive only messages published at this scope. Null receives both.</summary>
    public DeliveryScope? Scope { get; init; }

    /// <summary>Receive only messages for this tenant. Required where multi-tenancy is enabled.</summary>
    public string? TenantId { get; init; }

    /// <summary>Receive only messages carrying this correlation key.</summary>
    public string? CorrelationKey { get; init; }

    public SubscriptionStart Start { get; init; } = SubscriptionStart.Now;

    /// <summary>Deliveries handled concurrently by this subscriber. 1 preserves per-subscriber ordering.</summary>
    public int MaxConcurrency { get; init; } = 1;

    /// <summary>Diagnostic name, surfaced in logs and metrics. Defaults to the topic filter.</summary>
    public string? Name { get; init; }
}

/// <summary>
/// What a transport can actually do. Published so composition can reject an impossible subscription
/// at startup instead of accepting one that silently never fires.
/// </summary>
/// <remarks>
/// The in-process broker is not a degraded distributed broker — it genuinely cannot deliver to
/// another service. Because <see cref="DeliveryScope.Local"/> is the default, the common path needs
/// nothing more than that. But a publisher that elects <see cref="DeliveryScope.Distributed"/>
/// against an in-process broker has asked for something it cannot do, and the failure is invisible:
/// the message is delivered to local subscribers and simply never reaches the other service.
/// Surfacing capability as data lets composition reject that at startup rather than in production.
/// </remarks>
public sealed record DomainEventBrokerCapabilities
{
    public required bool SupportsDistributed { get; init; }

    /// <summary>Whether <see cref="DomainEventSubscriptionOptions.ConsumerGroup"/> is honoured.</summary>
    public required bool SupportsCompetingConsumers { get; init; }

    /// <summary>Whether <see cref="SubscriptionStart.Earliest"/> is honoured.</summary>
    public required bool SupportsReplay { get; init; }

    /// <summary>Whether <see cref="DeliveryOutcome.DeadLetter"/> has somewhere to go.</summary>
    public required bool SupportsDeadLetter { get; init; }

    /// <summary>Null when the transport imposes no practical limit.</summary>
    public int? MaxPayloadBytes { get; init; }

    /// <summary>
    /// The default single-service transport: local delivery only, no replay, no dead letter.
    /// Competing consumers are supported because that is just routing within one process.
    /// </summary>
    public static DomainEventBrokerCapabilities InProcess { get; } = new()
    {
        SupportsDistributed = false,
        SupportsCompetingConsumers = true,
        SupportsReplay = false,
        SupportsDeadLetter = false
    };
}

/// <summary>Publishes domain messages. Separated from subscription so a component can hold only the half it uses.</summary>
public interface IDomainEventPublisher
{
    ValueTask PublishAsync(DomainEventMessage message, CancellationToken cancellationToken);

    /// <summary>
    /// Publishes a batch as one transport round-trip where the transport allows it. Not atomic:
    /// partial success is possible, which is why <see cref="DomainEventMessage.MessageId"/> exists.
    /// </summary>
    ValueTask PublishBatchAsync(IReadOnlyList<DomainEventMessage> messages, CancellationToken cancellationToken);
}

/// <summary>Subscribes to domain messages.</summary>
public interface IDomainEventSubscriber
{
    /// <summary>
    /// Registers <paramref name="handler"/> and returns a handle that unsubscribes when disposed.
    /// </summary>
    /// <remarks>
    /// Push rather than pull: an <c>IAsyncEnumerable</c> surface would force every transport that is
    /// natively push-based to buffer, and would leave acknowledgement with no obvious home. The
    /// handler's <see cref="DeliveryResult"/> is what drives ack, redelivery, and dead-lettering.
    /// </remarks>
    /// <exception cref="NotSupportedException">
    /// The options ask for something <see cref="IDomainEventBroker.Capabilities"/> does not support.
    /// </exception>
    ValueTask<IAsyncDisposable> SubscribeAsync(
        DomainEventSubscriptionOptions options,
        Func<DomainEventDelivery, CancellationToken, ValueTask<DeliveryResult>> handler,
        CancellationToken cancellationToken);
}

/// <summary>
/// A message transport. The default implementation is in-process and serves a single service; a
/// distributed implementation is registered in its place when workflows in separate services need to
/// publish and subscribe to each other.
/// </summary>
/// <remarks>
/// Implementations are interchangeable, so the same workflow code runs against the in-memory broker
/// in tests and single-service deployments and against a distributed broker in a fleet. What changes
/// between them is <see cref="Capabilities"/> and the <see cref="DeliveryScope"/> a publisher elects —
/// never the author-facing call.
/// </remarks>
public interface IDomainEventBroker : IDomainEventPublisher, IDomainEventSubscriber
{
    DomainEventBrokerCapabilities Capabilities { get; }
}

/// <summary>
/// Declares that a message on <see cref="TopicFilter"/> starts a new instance of the workflow that
/// owns this trigger.
/// </summary>
public sealed record DomainEventTrigger
{
    public required string TopicFilter { get; init; }

    /// <summary>Receive only messages carrying this correlation key. Rarely useful for a trigger.</summary>
    public string? CorrelationKey { get; init; }

    /// <summary>
    /// Pins the trigger to one workflow version. Null resolves the newest at delivery time, which is
    /// what a long-lived topic subscription usually wants.
    /// </summary>
    public string? WorkflowVersion { get; init; }

    /// <summary>
    /// Maps the message to the workflow's context. Null passes the payload through unchanged, which
    /// is correct when the workflow's context type is the published contract.
    /// </summary>
    public Func<DomainEventMessage, string>? ContextSelector { get; init; }
}

/// <summary>
/// Opt-in on a workflow definition: this workflow starts when a matching message is published.
/// Opt-in rather than universal, so a workflow that is only ever started by API stays that way.
/// </summary>
public interface IDomainEventTriggeredWorkflow
{
    IReadOnlyList<DomainEventTrigger> Triggers { get; }
}

/// <summary>
/// Topic pattern matching. <c>*</c> matches exactly one segment; <c>#</c> matches the remainder and
/// may appear only as the final segment. Comparison is ordinal and case-sensitive — topics are
/// identifiers, and a transport that routes by byte equality would disagree with a matcher that did
/// not.
/// </summary>
public static class TopicPattern
{
    /// <summary>Matches <paramref name="topic"/> against <paramref name="pattern"/>. Neither is validated here.</summary>
    public static bool IsMatch(string pattern, string topic)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(topic);

        ReadOnlySpan<char> p = pattern;
        ReadOnlySpan<char> t = topic;

        while (true)
        {
            if (p.IsEmpty)
            {
                return t.IsEmpty;
            }

            int patternDot = p.IndexOf('.');
            ReadOnlySpan<char> patternSegment = patternDot < 0 ? p : p[..patternDot];

            // '#' is constrained to the final segment, so it absorbs whatever is left — including
            // nothing at all, which is what makes "orders.#" match "orders".
            if (patternSegment is "#")
            {
                return true;
            }

            if (t.IsEmpty)
            {
                return false;
            }

            int topicDot = t.IndexOf('.');
            ReadOnlySpan<char> topicSegment = topicDot < 0 ? t : t[..topicDot];

            if (patternSegment is not "*" && !patternSegment.SequenceEqual(topicSegment))
            {
                return false;
            }

            p = patternDot < 0 ? default : p[(patternDot + 1)..];
            t = topicDot < 0 ? default : t[(topicDot + 1)..];
        }
    }

    /// <summary>
    /// Validates a subscriber pattern. Wildcards must be whole segments: <c>order*</c> is rejected
    /// rather than quietly treated as a literal, because a filter that matches nothing looks
    /// identical to an upstream that published nothing.
    /// </summary>
    public static bool IsValidPattern(string? pattern, out string? error)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            error = "Topic filter must not be empty.";
            return false;
        }

        string[] segments = pattern.Split('.');
        for (int i = 0; i < segments.Length; i++)
        {
            string segment = segments[i];

            if (segment.Length == 0)
            {
                error = $"Topic filter '{pattern}' has an empty segment.";
                return false;
            }

            if (segment == "#" && i != segments.Length - 1)
            {
                error = $"Topic filter '{pattern}' uses '#' before the final segment; '#' matches the remainder and must come last.";
                return false;
            }

            if (segment.Length > 1 && (segment.Contains('*') || segment.Contains('#')))
            {
                error = $"Topic filter '{pattern}' mixes a wildcard with literal text in segment '{segment}'; wildcards must be whole segments.";
                return false;
            }
        }

        error = null;
        return true;
    }

    /// <summary>Validates a published topic: literal segments only, no wildcards.</summary>
    public static bool IsValidTopic(string? topic, out string? error)
    {
        if (string.IsNullOrWhiteSpace(topic))
        {
            error = "Topic must not be empty.";
            return false;
        }

        foreach (string segment in topic.Split('.'))
        {
            if (segment.Length == 0)
            {
                error = $"Topic '{topic}' has an empty segment.";
                return false;
            }

            if (segment.Contains('*') || segment.Contains('#'))
            {
                error = $"Topic '{topic}' contains a wildcard; wildcards belong to subscriber filters, not published topics.";
                return false;
            }
        }

        error = null;
        return true;
    }

    /// <summary>Throwing form, for composition-time validation where a bad filter should stop startup.</summary>
    public static void ValidatePattern(string? pattern)
    {
        if (!IsValidPattern(pattern, out string? error))
        {
            throw new ArgumentException(error, nameof(pattern));
        }
    }
}

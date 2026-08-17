using Abacus.Run.Abstractions;

namespace Abacus.Run.Core;

/// <summary>Why a subscription exists.</summary>
public enum DomainSubscriptionKind
{
    /// <summary>A matching message starts a new instance of a workflow.</summary>
    Trigger,

    /// <summary>A matching message resumes one instance parked on an executor.</summary>
    Wait
}

/// <summary>What happens to a parked instance when its wait window closes with nothing delivered.</summary>
public enum WaitExpiryAction
{
    /// <summary>Fail the instance. The default: a wait that timed out usually means an upstream broke.</summary>
    DeadStop,

    /// <summary>Resume with no payload, letting the workflow take its own timeout branch.</summary>
    Resume
}

/// <summary>
/// A durable record that something is listening. Triggers are rebuilt from the registry at startup;
/// waits are written when an instance parks and outlive any process, which is what lets an
/// event-driven pipeline survive a restart.
/// </summary>
public sealed record DomainEventSubscription
{
    public required string SubscriptionId { get; init; }
    public required DomainSubscriptionKind Kind { get; init; }
    public required string TopicFilter { get; init; }

    /// <summary>Null matches any correlation key; set matches only the identical value.</summary>
    public string? CorrelationKey { get; init; }

    /// <summary>Null matches any tenant; set matches only that tenant.</summary>
    public string? TenantId { get; init; }

    /// <summary>Only messages published at this scope match. Null matches both.</summary>
    public DeliveryScope? Scope { get; init; }

    // Wait subscriptions
    public string? InstanceId { get; init; }
    public string? ExecutorId { get; init; }
    public string? DeliveredPayloadJson { get; init; }
    public string? DeliveredMessageId { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public WaitExpiryAction OnExpiry { get; init; } = WaitExpiryAction.DeadStop;

    /// <summary>Set when the wait window closed with nothing delivered. Distinguishes a timeout from a delivery.</summary>
    public bool Expired { get; init; }

    // Trigger subscriptions
    public string? WorkflowName { get; init; }
    public string? WorkflowVersion { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>A wait is satisfied once something has been written to it, whether a payload or a timeout.</summary>
    public bool IsSatisfied => DeliveredMessageId is not null || Expired;
}

public sealed record DomainSubscriptionQuery
{
    public string? InstanceId { get; init; }
    public string? Topic { get; init; }
    public DomainSubscriptionKind? Kind { get; init; }
    public string? TenantId { get; init; }

    /// <summary>Excludes waits that have already been delivered or expired.</summary>
    public bool PendingOnly { get; init; }

    public int Limit { get; init; } = 100;
}

/// <summary>
/// Durable subscription registry. The broker transport decides how fast a message arrives; this store
/// decides what it means, and is the reason delivery survives a process restart.
/// </summary>
public interface IDomainEventSubscriptionStore
{
    ValueTask<DomainEventSubscription> RegisterAsync(DomainEventSubscription subscription, CancellationToken cancellationToken);

    /// <summary>
    /// Every subscription this message satisfies: topic pattern, tenant, correlation key and scope.
    /// Waits that are already satisfied are excluded — a parked instance is resumed once.
    /// </summary>
    ValueTask<IReadOnlyList<DomainEventSubscription>> MatchAsync(DomainEventMessage message, CancellationToken cancellationToken);

    /// <summary>
    /// Conditionally records a delivery against a wait. Returns false when another caller got there
    /// first.
    /// </summary>
    /// <remarks>
    /// This is the exactly-once boundary for resuming a parked instance. Every transport worth using
    /// delivers at least once and replicas race each other, so a compare-and-set here is what stops
    /// one message resuming one instance twice.
    /// </remarks>
    ValueTask<bool> TryDeliverAsync(string subscriptionId, DomainEventMessage message, CancellationToken cancellationToken);

    /// <summary>The wait belonging to one executor of one instance, delivered or not.</summary>
    ValueTask<DomainEventSubscription?> FindWaitAsync(string instanceId, string executorId, CancellationToken cancellationToken);

    /// <summary>Claims expired waits, marking them so a second sweeper does not claim them again.</summary>
    ValueTask<IReadOnlyList<DomainEventSubscription>> ClaimExpiredAsync(DateTimeOffset now, int max, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<DomainEventSubscription>> QueryAsync(DomainSubscriptionQuery query, CancellationToken cancellationToken);

    /// <summary>Drops every wait for an instance. Called when it terminates, so waits do not outlive it.</summary>
    ValueTask RemoveForInstanceAsync(string instanceId, CancellationToken cancellationToken);
}

/// <summary>Matching rules shared by every <see cref="IDomainEventSubscriptionStore"/> implementation.</summary>
/// <remarks>
/// Kept here rather than duplicated per store: a SQL implementation that pre-filters in the database
/// still ends its query with this predicate, so the in-memory and relational stores cannot disagree
/// about what a subscription means.
/// </remarks>
public static class DomainSubscriptionMatch
{
    public static bool Matches(DomainEventSubscription subscription, DomainEventMessage message)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(message);

        if (subscription.Kind == DomainSubscriptionKind.Wait && subscription.IsSatisfied)
        {
            return false;
        }

        if (subscription.Scope is { } scope && scope != message.Scope)
        {
            return false;
        }

        // A subscription with no tenant is a host-level listener and sees everything; one with a
        // tenant sees only that tenant. FR-4.7: a cross-tenant delivery is a security defect.
        if (subscription.TenantId is not null &&
            !string.Equals(subscription.TenantId, message.TenantId, StringComparison.Ordinal))
        {
            return false;
        }

        if (subscription.CorrelationKey is not null &&
            !string.Equals(subscription.CorrelationKey, message.CorrelationKey, StringComparison.Ordinal))
        {
            return false;
        }

        return TopicPattern.IsMatch(subscription.TopicFilter, message.Topic);
    }
}

/// <summary>Event types the broker adds to an instance's own progress stream.</summary>
public static class DomainEventNotifications
{
    public const string EventPublished = "event.published";
    public const string EventDelivered = "event.delivered";
    public const string EventWaitRegistered = "event.wait_registered";
    public const string EventWaitExpired = "event.wait_expired";
    public const string EventTriggered = "event.triggered";
}

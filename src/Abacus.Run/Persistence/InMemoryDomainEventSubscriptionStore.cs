using System.Collections.Concurrent;
using Abacus.Run.Abstractions;
using Abacus.Run.Core;

namespace Abacus.Run.Persistence;

/// <summary>
/// In-memory <see cref="IDomainEventSubscriptionStore"/>. Durable enough for a single-process deployment and
/// for tests; a relational implementation substitutes without touching the runtime.
/// </summary>
/// <remarks>
/// <see cref="TryDeliverAsync"/> is the interesting method. It is a compare-and-set under a lock,
/// standing in for the relational <c>UPDATE ... WHERE DeliveredMessageId IS NULL</c> that gives the
/// same guarantee across replicas. Everything else here is bookkeeping.
/// </remarks>
public sealed class InMemoryDomainEventSubscriptionStore : IDomainEventSubscriptionStore
{
    private readonly ConcurrentDictionary<string, DomainEventSubscription> _subscriptions = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public ValueTask<DomainEventSubscription> RegisterAsync(DomainEventSubscription subscription, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        TopicPattern.ValidatePattern(subscription.TopicFilter);

        if (subscription.Kind == DomainSubscriptionKind.Wait &&
            (subscription.InstanceId is null || subscription.ExecutorId is null))
        {
            throw new ArgumentException(
                "A Wait subscription must name the instance and executor it parks.", nameof(subscription));
        }

        if (subscription.Kind == DomainSubscriptionKind.Trigger && subscription.WorkflowName is null)
        {
            throw new ArgumentException(
                "A Trigger subscription must name the workflow it starts.", nameof(subscription));
        }

        lock (_sync)
        {
            // Re-registering the same wait is a resumed instance replaying its executor, not a new
            // subscription. Returning the existing row keeps any delivery already recorded against it.
            if (subscription.Kind == DomainSubscriptionKind.Wait &&
                FindWaitCore(subscription.InstanceId!, subscription.ExecutorId!) is { } existing)
            {
                return ValueTask.FromResult(existing);
            }

            _subscriptions[subscription.SubscriptionId] = subscription;
            return ValueTask.FromResult(subscription);
        }
    }

    public ValueTask<IReadOnlyList<DomainEventSubscription>> MatchAsync(DomainEventMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        lock (_sync)
        {
            DomainEventSubscription[] matches = _subscriptions.Values
                .Where(s => DomainSubscriptionMatch.Matches(s, message))
                .OrderBy(s => s.CreatedAt)
                .ToArray();

            return ValueTask.FromResult<IReadOnlyList<DomainEventSubscription>>(matches);
        }
    }

    public ValueTask<bool> TryDeliverAsync(string subscriptionId, DomainEventMessage message, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        ArgumentNullException.ThrowIfNull(message);

        lock (_sync)
        {
            if (!_subscriptions.TryGetValue(subscriptionId, out DomainEventSubscription? subscription))
            {
                return ValueTask.FromResult(false);
            }

            if (subscription.Kind != DomainSubscriptionKind.Wait || subscription.IsSatisfied)
            {
                return ValueTask.FromResult(false);
            }

            _subscriptions[subscriptionId] = subscription with
            {
                DeliveredMessageId = message.MessageId,
                DeliveredPayloadJson = message.PayloadJson
            };

            return ValueTask.FromResult(true);
        }
    }

    public ValueTask<DomainEventSubscription?> FindWaitAsync(string instanceId, string executorId, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            return ValueTask.FromResult(FindWaitCore(instanceId, executorId));
        }
    }

    public ValueTask<IReadOnlyList<DomainEventSubscription>> ClaimExpiredAsync(
        DateTimeOffset now, int max, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            DomainEventSubscription[] due = _subscriptions.Values
                .Where(s => s.Kind == DomainSubscriptionKind.Wait
                            && !s.IsSatisfied
                            && s.ExpiresAt is { } expiry
                            && expiry <= now)
                .OrderBy(s => s.ExpiresAt)
                .Take(Math.Max(1, max))
                .ToArray();

            // Mark under the same lock that selected them, so a second sweeper cannot claim the
            // same wait and expire one instance twice.
            var claimed = new List<DomainEventSubscription>(due.Length);
            foreach (DomainEventSubscription subscription in due)
            {
                DomainEventSubscription marked = subscription with { Expired = true };
                _subscriptions[subscription.SubscriptionId] = marked;
                claimed.Add(marked);
            }

            return ValueTask.FromResult<IReadOnlyList<DomainEventSubscription>>(claimed);
        }
    }

    public ValueTask<IReadOnlyList<DomainEventSubscription>> QueryAsync(DomainSubscriptionQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        lock (_sync)
        {
            IEnumerable<DomainEventSubscription> filtered = _subscriptions.Values;

            if (query.InstanceId is { } instanceId)
            {
                filtered = filtered.Where(s => string.Equals(s.InstanceId, instanceId, StringComparison.Ordinal));
            }

            if (query.Kind is { } kind)
            {
                filtered = filtered.Where(s => s.Kind == kind);
            }

            if (query.TenantId is { } tenantId)
            {
                filtered = filtered.Where(s => string.Equals(s.TenantId, tenantId, StringComparison.Ordinal));
            }

            if (query.Topic is { } topic)
            {
                filtered = filtered.Where(s => TopicPattern.IsMatch(s.TopicFilter, topic));
            }

            if (query.PendingOnly)
            {
                filtered = filtered.Where(s => s.Kind == DomainSubscriptionKind.Trigger || !s.IsSatisfied);
            }

            DomainEventSubscription[] page = filtered
                .OrderBy(s => s.CreatedAt)
                .Take(Math.Clamp(query.Limit, 1, 1000))
                .ToArray();

            return ValueTask.FromResult<IReadOnlyList<DomainEventSubscription>>(page);
        }
    }

    public ValueTask RemoveForInstanceAsync(string instanceId, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            foreach (DomainEventSubscription subscription in _subscriptions.Values
                .Where(s => string.Equals(s.InstanceId, instanceId, StringComparison.Ordinal))
                .ToArray())
            {
                _subscriptions.TryRemove(subscription.SubscriptionId, out _);
            }
        }

        return ValueTask.CompletedTask;
    }

    private DomainEventSubscription? FindWaitCore(string instanceId, string executorId)
        => _subscriptions.Values.FirstOrDefault(s =>
            s.Kind == DomainSubscriptionKind.Wait
            && string.Equals(s.InstanceId, instanceId, StringComparison.Ordinal)
            && string.Equals(s.ExecutorId, executorId, StringComparison.Ordinal));
}

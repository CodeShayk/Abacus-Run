using System.Collections.Concurrent;
using Abacus.Run.Abstractions;
using Abacus.Run.Core;

namespace Abacus.Run.Persistence;

/// <summary>
/// In-memory <see cref="IEventSubscriptionStore"/>. Durable enough for a single-process deployment and
/// for tests; a relational implementation substitutes without touching the runtime.
/// </summary>
/// <remarks>
/// <see cref="TryDeliverAsync"/> is the interesting method. It is a compare-and-set under a lock,
/// standing in for the relational <c>UPDATE ... WHERE DeliveredMessageId IS NULL</c> that gives the
/// same guarantee across replicas. Everything else here is bookkeeping.
/// </remarks>
public sealed class InMemoryEventSubscriptionStore : IEventSubscriptionStore
{
    private readonly ConcurrentDictionary<string, EventSubscription> _subscriptions = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public ValueTask<EventSubscription> RegisterAsync(EventSubscription subscription, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        TopicPattern.ValidatePattern(subscription.TopicFilter);

        if (subscription.Kind == SubscriptionKind.Wait &&
            (subscription.InstanceId is null || subscription.ExecutorId is null))
        {
            throw new ArgumentException(
                "A Wait subscription must name the instance and executor it parks.", nameof(subscription));
        }

        if (subscription.Kind == SubscriptionKind.Trigger && subscription.WorkflowName is null)
        {
            throw new ArgumentException(
                "A Trigger subscription must name the workflow it starts.", nameof(subscription));
        }

        lock (_sync)
        {
            // Re-registering the same wait is a resumed instance replaying its executor, not a new
            // subscription. Returning the existing row keeps any delivery already recorded against it.
            if (subscription.Kind == SubscriptionKind.Wait &&
                FindWaitCore(subscription.InstanceId!, subscription.ExecutorId!) is { } existing)
            {
                return ValueTask.FromResult(existing);
            }

            _subscriptions[subscription.SubscriptionId] = subscription;
            return ValueTask.FromResult(subscription);
        }
    }

    public ValueTask<IReadOnlyList<EventSubscription>> MatchAsync(BrokerMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        lock (_sync)
        {
            EventSubscription[] matches = _subscriptions.Values
                .Where(s => SubscriptionMatch.Matches(s, message))
                .OrderBy(s => s.CreatedAt)
                .ToArray();

            return ValueTask.FromResult<IReadOnlyList<EventSubscription>>(matches);
        }
    }

    public ValueTask<bool> TryDeliverAsync(string subscriptionId, BrokerMessage message, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        ArgumentNullException.ThrowIfNull(message);

        lock (_sync)
        {
            if (!_subscriptions.TryGetValue(subscriptionId, out EventSubscription? subscription))
            {
                return ValueTask.FromResult(false);
            }

            if (subscription.Kind != SubscriptionKind.Wait || subscription.IsSatisfied)
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

    public ValueTask<EventSubscription?> FindWaitAsync(string instanceId, string executorId, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            return ValueTask.FromResult(FindWaitCore(instanceId, executorId));
        }
    }

    public ValueTask<IReadOnlyList<EventSubscription>> ClaimExpiredAsync(
        DateTimeOffset now, int max, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            EventSubscription[] due = _subscriptions.Values
                .Where(s => s.Kind == SubscriptionKind.Wait
                            && !s.IsSatisfied
                            && s.ExpiresAt is { } expiry
                            && expiry <= now)
                .OrderBy(s => s.ExpiresAt)
                .Take(Math.Max(1, max))
                .ToArray();

            // Mark under the same lock that selected them, so a second sweeper cannot claim the
            // same wait and expire one instance twice.
            var claimed = new List<EventSubscription>(due.Length);
            foreach (EventSubscription subscription in due)
            {
                EventSubscription marked = subscription with { Expired = true };
                _subscriptions[subscription.SubscriptionId] = marked;
                claimed.Add(marked);
            }

            return ValueTask.FromResult<IReadOnlyList<EventSubscription>>(claimed);
        }
    }

    public ValueTask<IReadOnlyList<EventSubscription>> QueryAsync(SubscriptionQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        lock (_sync)
        {
            IEnumerable<EventSubscription> filtered = _subscriptions.Values;

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
                filtered = filtered.Where(s => s.Kind == SubscriptionKind.Trigger || !s.IsSatisfied);
            }

            EventSubscription[] page = filtered
                .OrderBy(s => s.CreatedAt)
                .Take(Math.Clamp(query.Limit, 1, 1000))
                .ToArray();

            return ValueTask.FromResult<IReadOnlyList<EventSubscription>>(page);
        }
    }

    public ValueTask RemoveForInstanceAsync(string instanceId, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            foreach (EventSubscription subscription in _subscriptions.Values
                .Where(s => string.Equals(s.InstanceId, instanceId, StringComparison.Ordinal))
                .ToArray())
            {
                _subscriptions.TryRemove(subscription.SubscriptionId, out _);
            }
        }

        return ValueTask.CompletedTask;
    }

    private EventSubscription? FindWaitCore(string instanceId, string executorId)
        => _subscriptions.Values.FirstOrDefault(s =>
            s.Kind == SubscriptionKind.Wait
            && string.Equals(s.InstanceId, instanceId, StringComparison.Ordinal)
            && string.Equals(s.ExecutorId, executorId, StringComparison.Ordinal));
}

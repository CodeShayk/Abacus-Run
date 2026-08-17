namespace Abacus.Run.Abstractions;

/// <summary>
/// Live fan-out of instance notifications to whoever is watching — the SSE backplane.
/// </summary>
/// <remarks>
/// A cache concern, not a record. It carries events to current subscribers and, on a distributed
/// implementation, holds them briefly so a reconnect inside that window is seamless. History always
/// comes from the event store, so losing the whole bus costs liveness and nothing else.
/// </remarks>
public interface INotificationBus
{
    ValueTask PublishBatchAsync(IReadOnlyList<EventEnvelope> events, CancellationToken cancellationToken);

    IAsyncEnumerable<EventEnvelope> SubscribeAsync(string instanceId, CancellationToken cancellationToken);
}

/// <summary>
/// General-purpose cache: values the framework can afford to lose and recompute.
/// </summary>
/// <remarks>
/// Never a system of record. Anything that must survive a cache eviction belongs in a store, and the
/// distinction is deliberate — a cache that quietly became the record is how data disappears without
/// anyone noticing it was supposed to be durable.
/// </remarks>
public interface ICacheStore
{
    ValueTask<T?> GetAsync<T>(string key, CancellationToken cancellationToken);

    /// <summary>Null <paramref name="ttl"/> means the entry lives until evicted or replaced.</summary>
    ValueTask SetAsync<T>(string key, T value, TimeSpan? ttl, CancellationToken cancellationToken);

    ValueTask<bool> RemoveAsync(string key, CancellationToken cancellationToken);

    /// <summary>
    /// Reads, or computes and stores. The factory may run more than once under concurrency — this is
    /// a cache, so a duplicated computation costs time and nothing else.
    /// </summary>
    ValueTask<T> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, ValueTask<T>> factory,
        TimeSpan? ttl,
        CancellationToken cancellationToken);
}

/// <summary>
/// One adapter per cache technology, supplying every cache-shaped capability the framework needs.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole substitution point. Redis, Memcached or the in-memory default are chosen once,
/// as an adapter, and everything cache-shaped follows — the general
/// <see cref="Store"/> and the SSE <see cref="NotificationBackplane"/> alike. A deployment cannot end
/// up caching in Redis while streaming through something else, because there is no seam between them
/// to get wrong.
/// </para>
/// <para>
/// The backplane is part of the adapter rather than a separate registration precisely because it is
/// a cache: it holds live events long enough to fan them out and to cover a brief reconnect, and the
/// durable record lives in the event store regardless.
/// </para>
/// <para>
/// A new technology is one class implementing this interface and one registration extension —
/// <c>Abacus.Adapters.Cache.Memcached</c> would supply both halves and nothing else would change.
/// </para>
/// </remarks>
public interface ICacheAdapter
{
    /// <summary>The technology this adapter speaks, for diagnostics and startup logging.</summary>
    string Technology { get; }

    /// <summary>True when the cache is shared between replicas rather than process-local.</summary>
    /// <remarks>
    /// The framework reads this to know whether SSE works across replicas. A process-local cache is
    /// correct for one replica and silently wrong for several, so it is stated rather than assumed.
    /// </remarks>
    bool IsDistributed { get; }

    ICacheStore Store { get; }

    /// <summary>
    /// The SSE fan-out backplane: how a live event reaches a subscriber, including one attached to a
    /// replica that is not running the instance.
    /// </summary>
    INotificationBus NotificationBackplane { get; }
}

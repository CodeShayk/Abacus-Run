using System.Collections.Concurrent;
using Abacus.Run.Abstractions;

namespace Abacus.Run.Notifications;

/// <summary>
/// The default <see cref="ICacheAdapter"/>: everything in this process, no infrastructure required.
/// </summary>
/// <remarks>
/// Correct for a single replica and for tests, which is why it is the default rather than a fallback
/// to apologise for. <see cref="IsDistributed"/> is false, and that is the honest signal a
/// multi-replica deployment needs — SSE will only reach subscribers attached to the replica running
/// the instance, so such a deployment must substitute a distributed adapter.
/// </remarks>
public sealed class InMemoryCacheAdapter : ICacheAdapter, IDisposable
{
    private readonly InMemoryNotificationBus _bus;

    public InMemoryCacheAdapter(InMemoryNotificationBus? bus = null, TimeProvider? clock = null)
    {
        _bus = bus ?? new InMemoryNotificationBus();
        Store = new InMemoryCacheStore(clock ?? TimeProvider.System);
    }

    public string Technology => "InMemory";

    public bool IsDistributed => false;

    public ICacheStore Store { get; }

    public INotificationBus NotificationBackplane => _bus;

    public void Dispose() => _bus.Dispose();
}

/// <summary>
/// Process-local <see cref="ICacheStore"/> with lazy expiry.
/// </summary>
/// <remarks>
/// Entries are evicted when next read rather than by a timer. A sweeper would cost a thread to
/// reclaim memory nobody is asking for, and an expired entry that is never read again is not a leak
/// worth paying to prevent — it is replaced the moment anyone wants that key.
/// </remarks>
public sealed class InMemoryCacheStore : ICacheStore
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;

    public InMemoryCacheStore(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    public ValueTask<T?> GetAsync<T>(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (!_entries.TryGetValue(key, out Entry? entry))
        {
            return ValueTask.FromResult<T?>(default);
        }

        if (entry.ExpiresAt is { } expiry && expiry <= _clock.GetUtcNow())
        {
            _entries.TryRemove(key, out _);
            return ValueTask.FromResult<T?>(default);
        }

        return ValueTask.FromResult(entry.Value is T typed ? typed : default);
    }

    public ValueTask SetAsync<T>(string key, T value, TimeSpan? ttl, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        _entries[key] = new Entry(value, ttl is { } t ? _clock.GetUtcNow() + t : null);
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> RemoveAsync(string key, CancellationToken cancellationToken)
        => ValueTask.FromResult(_entries.TryRemove(key, out _));

    public async ValueTask<T> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, ValueTask<T>> factory,
        TimeSpan? ttl,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(factory);

        if (await GetAsync<T>(key, cancellationToken).ConfigureAwait(false) is { } hit)
        {
            return hit;
        }

        T created = await factory(cancellationToken).ConfigureAwait(false);
        await SetAsync(key, created, ttl, cancellationToken).ConfigureAwait(false);
        return created;
    }

    private sealed record Entry(object? Value, DateTimeOffset? ExpiresAt);
}

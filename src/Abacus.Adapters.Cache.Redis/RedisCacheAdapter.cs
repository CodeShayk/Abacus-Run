using System.Text.Json;
using Abacus.Run.Abstractions;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Abacus.Adapters.Cache.Redis;

/// <summary>
/// The Redis <see cref="ICacheAdapter"/>: general caching and the SSE backplane, over one connection.
/// </summary>
/// <remarks>
/// Registering this moves every cache-shaped capability to Redis at once. That is the point of the
/// adapter being the substitution unit rather than each capability registering itself — a deployment
/// cannot end up half-migrated, caching in Redis while still streaming through process memory.
/// </remarks>
public sealed class RedisCacheAdapter : ICacheAdapter, IAsyncDisposable
{
    private readonly RedisNotificationBus _bus;

    public RedisCacheAdapter(
        IConnectionMultiplexer redis,
        int maxStreamLength = 10_000,
        string keyPrefix = "abacus:cache:",
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(redis);

        _bus = new RedisNotificationBus(redis, maxStreamLength, loggerFactory?.CreateLogger<RedisNotificationBus>());
        Store = new RedisCacheStore(redis, keyPrefix);
    }

    public string Technology => "Redis";

    /// <summary>Shared between replicas, which is the reason to reach for it.</summary>
    public bool IsDistributed => true;

    public ICacheStore Store { get; }

    public INotificationBus NotificationBackplane => _bus;

    public ValueTask DisposeAsync() => _bus.DisposeAsync();
}

/// <summary>Redis-backed <see cref="ICacheStore"/>. Values are stored as JSON strings.</summary>
public sealed class RedisCacheStore : ICacheStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IConnectionMultiplexer _redis;
    private readonly string _prefix;

    public RedisCacheStore(IConnectionMultiplexer redis, string keyPrefix = "abacus:cache:")
    {
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        _prefix = keyPrefix;
    }

    private string Key(string key) => _prefix + key;

    public async ValueTask<T?> GetAsync<T>(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        RedisValue value = await _redis.GetDatabase().StringGetAsync(Key(key)).ConfigureAwait(false);

        // RedisValue converts implicitly to both string and ReadOnlySpan<byte>, so the overload has
        // to be pinned or the call is ambiguous.
        return value.IsNullOrEmpty ? default : JsonSerializer.Deserialize<T>(value.ToString(), Json);
    }

    public async ValueTask SetAsync<T>(string key, T value, TimeSpan? ttl, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        await _redis.GetDatabase()
            .StringSetAsync(Key(key), JsonSerializer.Serialize(value, Json), ttl)
            .ConfigureAwait(false);
    }

    public async ValueTask<bool> RemoveAsync(string key, CancellationToken cancellationToken)
        => await _redis.GetDatabase().KeyDeleteAsync(Key(key)).ConfigureAwait(false);

    public async ValueTask<T> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, ValueTask<T>> factory,
        TimeSpan? ttl,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(factory);

        // No lock around the factory. Two replicas racing to compute the same value both write the
        // same answer, and paying for a distributed lock to prevent a duplicated computation would
        // cost more than the computation.
        if (await GetAsync<T>(key, cancellationToken).ConfigureAwait(false) is { } hit)
        {
            return hit;
        }

        T created = await factory(cancellationToken).ConfigureAwait(false);
        await SetAsync(key, created, ttl, cancellationToken).ConfigureAwait(false);
        return created;
    }
}

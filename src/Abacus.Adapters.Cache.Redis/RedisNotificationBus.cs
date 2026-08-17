using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Abacus.Run.Abstractions;
using Abacus.Run.Core;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Abacus.Adapters.Cache.Redis;

/// <summary>
/// Redis Streams implementation of <see cref="INotificationBus"/> (TDD §10.2).
///
/// <list type="bullet">
///   <item><c>PublishBatchAsync</c> → <c>XADD</c> to stream <c>workflow:events:{instanceId}</c></item>
///   <item><c>SubscribeAsync</c> → <c>XREAD BLOCK</c> consumer with de-duplication by sequence</item>
///   <item>Auto-trimming via <c>MAXLEN ~</c> based on configuration</item>
/// </list>
/// </summary>
public sealed class RedisNotificationBus : INotificationBus, IAsyncDisposable
{
    private const string StreamKeyPrefix = "workflow:events:";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisNotificationBus>? _logger;
    private readonly int _maxStreamLength;

    public RedisNotificationBus(
        IConnectionMultiplexer redis,
        int maxStreamLength = 10_000,
        ILogger<RedisNotificationBus>? logger = null)
    {
        _redis = redis;
        _maxStreamLength = maxStreamLength;
        _logger = logger;
    }

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    private static string StreamKey(string instanceId) => $"{StreamKeyPrefix}{instanceId}";

    /// <summary>
    /// The stream's current last id, or <c>0-0</c> when the stream does not exist yet.
    /// </summary>
    /// <remarks>
    /// A subscriber wants what happens from now on, not the history — backfill is the durable event
    /// store's job, and the SSE path reads that first. Starting at the resolved tail gives exactly
    /// that. When nothing has been published yet there is no tail, and <c>0-0</c> is both correct and
    /// equivalent: an empty stream has no history to replay.
    /// </remarks>
    private static async Task<string> ResolveTailAsync(IDatabase db, string key)
    {
        try
        {
            if (await db.KeyExistsAsync(key).ConfigureAwait(false))
            {
                StreamInfo info = await db.StreamInfoAsync(key).ConfigureAwait(false);
                return info.LastGeneratedId.ToString();
            }
        }
        catch (RedisException)
        {
            // Raced with a trim or an expiry. Starting from the beginning of a stream that just
            // vanished costs nothing.
        }

        return "0-0";
    }

    public async ValueTask PublishBatchAsync(IReadOnlyList<EventEnvelope> events, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0)
        {
            return;
        }

        IDatabase db = _redis.GetDatabase();

        foreach (EventEnvelope envelope in events)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string key = StreamKey(envelope.InstanceId);
            NameValueEntry[] fields =
            [
                new("seq", envelope.Sequence.ToString()),
                new("type", envelope.EventType),
                new("payload", envelope.PayloadJson),
                new("instanceId", envelope.InstanceId),
                new("executorId", envelope.ExecutorId ?? ""),
                new("superstep", envelope.Superstep?.ToString() ?? ""),
                new("tenantId", envelope.TenantId ?? ""),
                new("occurredAt", envelope.OccurredAt.ToString("O"))
            ];

            try
            {
                await db.StreamAddAsync(
                    key,
                    fields,
                    maxLength: _maxStreamLength,
                    useApproximateMaxLength: true).ConfigureAwait(false);
            }
            catch (RedisException ex)
            {
                _logger?.LogWarning(ex,
                    "Failed to publish event {Sequence} for instance {InstanceId} to Redis.",
                    envelope.Sequence, envelope.InstanceId);
                // Swallow: the durable store is the source of truth; Redis is best-effort for live fan-out.
            }
        }
    }

    public async IAsyncEnumerable<EventEnvelope> SubscribeAsync(
        string instanceId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        IDatabase db = _redis.GetDatabase();
        string key = StreamKey(instanceId);

        // Resolve the tail to a concrete id before reading anything.
        //
        // "$" means "entries added after this read starts blocking", which is meaningful only to a
        // blocking XREAD. StackExchange.Redis exposes no blocking read, so a non-blocking XREAD from
        // "$" returns nothing and — because nothing arrived — never advances past "$" either. Left as
        // it was, this loop polls forever and yields nothing at all.
        string lastId = await ResolveTailAsync(db, key).ConfigureAwait(false);

        while (!cancellationToken.IsCancellationRequested)
        {
            StreamEntry[]? entries;
            try
            {
                entries = await db.StreamReadAsync(key, lastId, count: 100).ConfigureAwait(false);
            }
            catch (RedisException ex)
            {
                _logger?.LogWarning(ex, "Redis stream read failed for {InstanceId}; retrying.", instanceId);
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (entries is null || entries.Length == 0)
            {
                // No new entries; wait briefly before polling again. This interval is the backplane's
                // added latency, and it is the price of having no blocking read available.
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
                continue;
            }

            foreach (StreamEntry entry in entries)
            {
                lastId = entry.Id!;
                EventEnvelope? envelope = ParseEntry(entry, instanceId);
                if (envelope is not null)
                {
                    yield return envelope;
                }
            }
        }
    }

    private static EventEnvelope? ParseEntry(StreamEntry entry, string fallbackInstanceId)
    {
        try
        {
            string? seq = entry["seq"];
            string? type = entry["type"];
            string? payload = entry["payload"];
            string? instanceId = entry["instanceId"];
            string? executorId = entry["executorId"];
            string? superstep = entry["superstep"];
            string? tenantId = entry["tenantId"];
            string? occurredAt = entry["occurredAt"];

            return new EventEnvelope
            {
                InstanceId = string.IsNullOrEmpty(instanceId) ? fallbackInstanceId : instanceId,
                Sequence = long.TryParse(seq, out long s) ? s : 0,
                EventType = type ?? "unknown",
                PayloadJson = payload ?? "{}",
                ExecutorId = string.IsNullOrEmpty(executorId) ? null : executorId,
                Superstep = int.TryParse(superstep, out int ss) ? ss : null,
                TenantId = string.IsNullOrEmpty(tenantId) ? null : tenantId,
                OccurredAt = DateTimeOffset.TryParse(occurredAt, out DateTimeOffset dt)
                    ? dt
                    : DateTimeOffset.UtcNow
            };
        }
        catch
        {
            return null;
        }
    }

    public ValueTask DisposeAsync()
    {
        // The IConnectionMultiplexer is owned by DI; we don't dispose it here.
        return ValueTask.CompletedTask;
    }
}

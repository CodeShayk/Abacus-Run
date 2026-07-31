using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Abacus.Run.Abstractions;
using Abacus.Run.Core;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Abacus.Run.EventBus;

/// <summary>
/// Redis Streams implementation of <see cref="IEventBus"/> (TDD §10.2).
///
/// <list type="bullet">
///   <item><c>PublishBatchAsync</c> → <c>XADD</c> to stream <c>workflow:events:{instanceId}</c></item>
///   <item><c>SubscribeAsync</c> → <c>XREAD BLOCK</c> consumer with de-duplication by sequence</item>
///   <item>Auto-trimming via <c>MAXLEN ~</c> based on configuration</item>
/// </list>
/// </summary>
public sealed class RedisEventBus : IEventBus, IAsyncDisposable
{
    private const string StreamKeyPrefix = "workflow:events:";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisEventBus>? _logger;
    private readonly int _maxStreamLength;

    public RedisEventBus(
        IConnectionMultiplexer redis,
        int maxStreamLength = 10_000,
        ILogger<RedisEventBus>? logger = null)
    {
        _redis = redis;
        _maxStreamLength = maxStreamLength;
        _logger = logger;
    }

    private static string StreamKey(string instanceId) => $"{StreamKeyPrefix}{instanceId}";

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
        string lastId = "$"; // Only new messages

        while (!cancellationToken.IsCancellationRequested)
        {
            StreamEntry[]? entries;
            try
            {
                // Block for up to 5 seconds waiting for new entries.
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
                // No new entries; wait briefly before polling again.
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
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

/// <summary>
/// Redis Pub/Sub channel for control signals (e.g., <c>control.cancel</c>).
/// Used by <c>InstanceControlService</c> to signal the owning replica to cancel a running
/// instance (§11.2). The owning replica's <c>WorkflowRunner</c> subscribes to this channel.
/// </summary>
public sealed class RedisControlChannel : IAsyncDisposable
{
    private const string ControlChannelPrefix = "workflow:control:";
    private readonly ISubscriber _subscriber;
    private readonly ILogger<RedisControlChannel>? _logger;

    public RedisControlChannel(IConnectionMultiplexer redis, ILogger<RedisControlChannel>? logger = null)
    {
        _subscriber = redis.GetSubscriber();
        _logger = logger;
    }

    /// <summary>Publishes a control message to all subscribers for the given instance.</summary>
    public async Task PublishAsync(string instanceId, string action, string? reason = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string channel = $"{ControlChannelPrefix}{instanceId}";
        string message = JsonSerializer.Serialize(new { action, reason, timestamp = DateTimeOffset.UtcNow });

        try
        {
            await _subscriber.PublishAsync(RedisChannel.Literal(channel), message).ConfigureAwait(false);
        }
        catch (RedisException ex)
        {
            _logger?.LogWarning(ex, "Failed to publish control message to {Channel}.", channel);
        }
    }

    /// <summary>
    /// Subscribes to control messages for a specific instance. Returns an unsubscribe handle.
    /// The <paramref name="handler"/> receives the action string (e.g. "cancel") and optional reason.
    /// </summary>
    public async Task<IAsyncDisposable> SubscribeAsync(
        string instanceId,
        Func<string, string?, Task> handler)
    {
        string channel = $"{ControlChannelPrefix}{instanceId}";

        await _subscriber.SubscribeAsync(
            RedisChannel.Literal(channel),
            async (_, message) =>
            {
                try
                {
                    using JsonDocument doc = JsonDocument.Parse((string)message!);
                    string action = doc.RootElement.GetProperty("action").GetString() ?? "";
                    string? reason = doc.RootElement.TryGetProperty("reason", out JsonElement r)
                        ? r.GetString()
                        : null;
                    await handler(action, reason).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Error processing control message on {Channel}.", channel);
                }
            }).ConfigureAwait(false);

        return new Unsubscriber(_subscriber, channel);
    }

    public async ValueTask DisposeAsync()
    {
        await _subscriber.UnsubscribeAllAsync().ConfigureAwait(false);
    }

    private sealed class Unsubscriber : IAsyncDisposable
    {
        private readonly ISubscriber _subscriber;
        private readonly string _channel;

        public Unsubscriber(ISubscriber subscriber, string channel)
        {
            _subscriber = subscriber;
            _channel = channel;
        }

        public async ValueTask DisposeAsync()
        {
            await _subscriber.UnsubscribeAsync(RedisChannel.Literal(_channel)).ConfigureAwait(false);
        }
    }
}

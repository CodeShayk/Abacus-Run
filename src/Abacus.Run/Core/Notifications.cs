using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Abacus.Run.Abstractions;

namespace Abacus.Run.Core;

public interface INotificationSink
{
    ValueTask PublishAsync(EventEnvelope envelope, CancellationToken cancellationToken);
}

/// <summary>
/// Assigns gapless per-instance sequence numbers. The SSE <c>id:</c> and the stored sequence are the
/// same value, which is what makes history-then-subscribe catch-up exact.
/// </summary>
public sealed class NotificationSequencer
{
    private readonly ConcurrentDictionary<string, StrongBox> _counters = new();

    public long Next(string instanceId)
    {
        StrongBox box = _counters.GetOrAdd(instanceId, _ => new StrongBox());
        return Interlocked.Increment(ref box.Value);
    }

    public long Peek(string instanceId)
        => _counters.TryGetValue(instanceId, out StrongBox? box) ? Interlocked.Read(ref box.Value) : 0;

    /// <summary>Seeds from the store so a resumed instance continues its sequence instead of restarting at 1.</summary>
    public void Seed(string instanceId, long lastSequence)
        => _counters[instanceId] = new StrongBox { Value = lastSequence };

    public void Forget(string instanceId) => _counters.TryRemove(instanceId, out _);

    private sealed class StrongBox
    {
        public long Value;
    }
}

/// <summary>
/// Single write path for events: redact, persist durably, fan out to the bus. Bounded with wait —
/// dropping events would break the gapless-sequence contract that catch-up depends on.
/// </summary>
public sealed class NotificationPublisher : INotificationSink, IAsyncDisposable
{
    private readonly IEventStore _store;
    private readonly INotificationBus? _bus;
    private readonly IRedactionPolicy _redaction;
    private readonly Channel<EventEnvelope> _channel;
    private readonly Task _drain;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly int _batchSize;

    public NotificationPublisher(
        IEventStore store,
        INotificationBus? bus = null,
        IRedactionPolicy? redaction = null,
        int channelCapacity = 10_000,
        int batchSize = 200)
    {
        _store = store;
        _bus = bus;
        _redaction = redaction ?? RedactionPolicy.Default;
        _batchSize = batchSize;
        _channel = Channel.CreateBounded<EventEnvelope>(new BoundedChannelOptions(channelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true
        });
        _drain = Task.Run(() => DrainAsync(_shutdown.Token));
    }

    public ValueTask PublishAsync(EventEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        EventEnvelope redacted = envelope with { PayloadJson = _redaction.RedactBody(envelope.PayloadJson) };
        return _channel.Writer.WriteAsync(redacted, cancellationToken);
    }

    /// <summary>Flushes everything currently queued. Used by tests and by drain.</summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        while (_channel.Reader.Count > 0 && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(5, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DrainAsync(CancellationToken cancellationToken)
    {
        var batch = new List<EventEnvelope>(_batchSize);
        try
        {
            while (await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                batch.Clear();
                while (batch.Count < _batchSize && _channel.Reader.TryRead(out EventEnvelope? envelope))
                {
                    batch.Add(envelope);
                }

                if (batch.Count == 0)
                {
                    continue;
                }

                // Stream-only events reach live subscribers and leave no record; log-only events do
                // the reverse. Each destination takes the subset that named it.
                EventEnvelope[] durable = [.. batch.Where(e => e.Delivery != EventDeliveryMode.StreamOnly)];
                if (durable.Length > 0)
                {
                    await _store.AppendBatchAsync(durable, cancellationToken).ConfigureAwait(false);
                }

                EventEnvelope[] streamed = [.. batch.Where(e => e.Delivery != EventDeliveryMode.LogOnly)];
                if (_bus is not null && streamed.Length > 0)
                {
                    await _bus.PublishBatchAsync(streamed, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        try
        {
            await _drain.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // fall through to cancellation
        }
        await _shutdown.CancelAsync().ConfigureAwait(false);
        _shutdown.Dispose();
    }
}

/// <summary>Synchronous sink used in tests and by callers that need write-through ordering.</summary>
public sealed class DirectNotificationSink : INotificationSink
{
    private readonly IEventStore _store;
    private readonly INotificationBus? _bus;
    private readonly IRedactionPolicy _redaction;

    public DirectNotificationSink(IEventStore store, INotificationBus? bus = null, IRedactionPolicy? redaction = null)
    {
        _store = store;
        _bus = bus;
        _redaction = redaction ?? RedactionPolicy.Default;
    }

    public async ValueTask PublishAsync(EventEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        EventEnvelope redacted = envelope with { PayloadJson = _redaction.RedactBody(envelope.PayloadJson) };
        EventEnvelope[] batch = [redacted];

        if (redacted.Delivery != EventDeliveryMode.StreamOnly)
        {
            await _store.AppendBatchAsync(batch, cancellationToken).ConfigureAwait(false);
        }

        if (_bus is not null && redacted.Delivery != EventDeliveryMode.LogOnly)
        {
            await _bus.PublishBatchAsync(batch, cancellationToken).ConfigureAwait(false);
        }
    }
}

public static class NotificationFactory
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static EventEnvelope Create(
        string instanceId, long sequence, string eventType, object payload,
        string? executorId = null, int? superstep = null, string? tenantId = null, DateTimeOffset? at = null,
        string? workflowName = null, EventDeliveryMode delivery = EventDeliveryMode.StreamAndLog)
        => new()
        {
            InstanceId = instanceId,
            Sequence = sequence,
            EventType = eventType,
            ExecutorId = executorId,
            Superstep = superstep,
            TenantId = tenantId,
            WorkflowName = workflowName,
            Delivery = delivery,
            PayloadJson = JsonSerializer.Serialize(payload, Json),
            OccurredAt = at ?? DateTimeOffset.UtcNow
        };

    public static EventEnvelope ApprovalRequested(ApprovalRequest approval)
        => Create(approval.InstanceId, 0, WorkflowEventTypes.ApprovalRequested, new
        {
            approvalId = approval.ApprovalId,
            executorId = approval.ExecutorId,
            reason = approval.Reason,
            proposedInput = approval.ProposedInputJson,
            assignees = approval.Assignees,
            requiredApprovers = approval.RequiredApprovers,
            allowModification = approval.AllowModification,
            expiresAt = approval.ExpiresAt,
            decisionUrl = $"/approvals/{approval.ApprovalId}/decision"
        }, approval.ExecutorId, approval.Superstep, approval.TenantId, approval.CreatedAt);

    public static EventEnvelope ApprovalDecided(ApprovalRequest approval, ApprovalDecision decision, ApprovalState? newState)
        => Create(approval.InstanceId, 0, WorkflowEventTypes.ApprovalDecided, new
        {
            approvalId = approval.ApprovalId,
            executorId = approval.ExecutorId,
            outcome = decision.Outcome.ToString(),
            deciderId = decision.DeciderId,
            comment = decision.Comment,
            state = (newState ?? ApprovalState.Pending).ToString(),
            autoApproved = decision.AutoApproved
        }, approval.ExecutorId, approval.Superstep, approval.TenantId, decision.DecidedAt);

    public static EventEnvelope ApprovalExpired(ApprovalRequest approval)
        => Create(approval.InstanceId, 0, WorkflowEventTypes.ApprovalExpired, new
        {
            approvalId = approval.ApprovalId,
            executorId = approval.ExecutorId,
            onExpiry = approval.OnExpiry.ToString()
        }, approval.ExecutorId, approval.Superstep, approval.TenantId);

    public static EventEnvelope Terminated(WorkflowInstance instance, string reason)
        => Create(instance.InstanceId, 0, WorkflowEventTypes.WorkflowTerminated, new
        {
            status = instance.Status.ToString(),
            reason,
            attempt = instance.AttemptCount,
            correlationId = instance.CorrelationId
        }, tenantId: instance.TenantId);
}

public static class IdGenerator
{
    private static readonly Lock Sync = new();
    private static long _lastTimestamp;
    private static readonly byte[] LastRandom = new byte[10];

    /// <summary>
    /// Lexicographically sortable id: 48-bit millisecond timestamp + 80 bits of randomness, base32.
    /// </summary>
    /// <remarks>
    /// Within a single millisecond the random component is <em>incremented</em> rather than redrawn,
    /// so ids remain strictly increasing even when the clock does not advance. The checkpoint index
    /// relies on this: it breaks commit-timestamp ties by id, and a redrawn random suffix would make
    /// the resume point non-deterministic under load.
    /// </remarks>
    public static string NewId(string? prefix = null)
    {
        Span<byte> bytes = stackalloc byte[16];
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        lock (Sync)
        {
            if (timestamp == _lastTimestamp)
            {
                IncrementRandom();
            }
            else
            {
                // A backwards clock step must not produce a smaller id than one already issued.
                _lastTimestamp = Math.Max(timestamp, _lastTimestamp);
                Random.Shared.NextBytes(LastRandom);
            }

            timestamp = _lastTimestamp;
            LastRandom.CopyTo(bytes[6..]);
        }

        bytes[0] = (byte)(timestamp >> 40);
        bytes[1] = (byte)(timestamp >> 32);
        bytes[2] = (byte)(timestamp >> 24);
        bytes[3] = (byte)(timestamp >> 16);
        bytes[4] = (byte)(timestamp >> 8);
        bytes[5] = (byte)timestamp;

        string encoded = Base32.Encode(bytes);
        return prefix is null ? encoded : $"{prefix}_{encoded}";
    }

    /// <summary>Big-endian increment with carry; overflow wraps, which needs 2^80 ids in one ms.</summary>
    private static void IncrementRandom()
    {
        for (int i = LastRandom.Length - 1; i >= 0; i--)
        {
            if (++LastRandom[i] != 0)
            {
                return;
            }
        }
    }
}

internal static class Base32
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var sb = new System.Text.StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0;
        int bitsLeft = 0;

        foreach (byte b in data)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                sb.Append(Alphabet[(buffer >> (bitsLeft - 5)) & 31]);
                bitsLeft -= 5;
            }
        }

        if (bitsLeft > 0)
        {
            sb.Append(Alphabet[(buffer << (5 - bitsLeft)) & 31]);
        }

        return sb.ToString();
    }
}

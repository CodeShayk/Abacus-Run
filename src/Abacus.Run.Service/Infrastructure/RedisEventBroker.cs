using System.Text.Json;
using Abacus.Run.Abstractions;
using Abacus.Run.Core;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Abacus.Run.EventBus;

/// <summary>
/// Cross-service <see cref="IEventBroker"/> over Redis Streams.
/// </summary>
/// <remarks>
/// <para>
/// This is the in-process broker <em>plus a wire</em>, not a replacement for it. A
/// <see cref="DeliveryScope.Local"/> message never touches Redis — it is the publishing service's
/// private business, and the abstraction promises it stays that way. A
/// <see cref="DeliveryScope.Distributed"/> message goes to the stream and comes back to every
/// service through its own consumer, including the one that published it. Publishing does not also
/// deliver locally, because that would deliver twice.
/// </para>
/// <para>
/// Consumer groups carry the broadcast/competing distinction:
/// <see cref="EventSubscriptionOptions.ConsumerGroup"/> names a shared group, so exactly one member
/// across the fleet handles each message; a null group gets a private group, so every subscriber
/// sees its own copy. This is the difference between routing work and observing it, and Redis
/// enforces it rather than the application hoping for it.
/// </para>
/// <para>
/// Wake-ups come from pub/sub rather than polling. StackExchange.Redis exposes no BLOCK argument on
/// <c>XREADGROUP</c>, and issuing one through <c>ExecuteAsync</c> would stall the shared multiplexer
/// for every other caller, so a lightweight notification drives the read instead. A slow fallback
/// poll covers the at-most-once notification.
/// </para>
/// </remarks>
public sealed class RedisEventBroker : IEventBroker, IAsyncDisposable
{
    private const string StreamKey = "workflow:broker";
    private const string NotifyChannel = "workflow:broker:notify";
    private const string DeadLetterKey = "workflow:broker:dead";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan FallbackPoll = TimeSpan.FromSeconds(1);

    private readonly IConnectionMultiplexer _redis;
    private readonly InProcessEventBroker _local;
    private readonly ILogger<RedisEventBroker>? _logger;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly int _maxStreamLength;
    private readonly string _consumerName;

    public RedisEventBroker(
        IConnectionMultiplexer redis,
        int maxStreamLength = 100_000,
        string? consumerName = null,
        ILoggerFactory? loggerFactory = null)
    {
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        _maxStreamLength = maxStreamLength;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory?.CreateLogger<RedisEventBroker>();
        _local = new InProcessEventBroker(loggerFactory?.CreateLogger<InProcessEventBroker>());

        // Identifies this replica within a consumer group so XAUTOCLAIM can tell whose work was
        // stranded when a pod dies.
        _consumerName = consumerName
            ?? Environment.GetEnvironmentVariable("POD_NAME")
            ?? $"{Environment.MachineName}:{Environment.ProcessId}";
    }

    public BrokerCapabilities Capabilities { get; } = new()
    {
        SupportsDistributed = true,
        SupportsCompetingConsumers = true,
        SupportsReplay = true,
        SupportsDeadLetter = true,

        // Redis itself allows far more, but a message this large is a payload that should have been
        // a blob reference, and letting it through would move the failure to the consumer.
        MaxPayloadBytes = 1024 * 1024
    };

    public ValueTask PublishAsync(BrokerMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return PublishBatchAsync([message], cancellationToken);
    }

    public async ValueTask PublishBatchAsync(IReadOnlyList<BrokerMessage> messages, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count == 0)
        {
            return;
        }

        var distributed = new List<BrokerMessage>(messages.Count);

        foreach (BrokerMessage message in messages)
        {
            if (!TopicPattern.IsValidTopic(message.Topic, out string? error))
            {
                throw new ArgumentException(error, nameof(messages));
            }

            if (message.PayloadJson.Length > Capabilities.MaxPayloadBytes)
            {
                throw new ArgumentException(
                    $"Message '{message.MessageId}' on '{message.Topic}' is {message.PayloadJson.Length} bytes, " +
                    $"over the {Capabilities.MaxPayloadBytes}-byte limit. Publish a reference rather than the body.",
                    nameof(messages));
            }

            if (message.Scope == DeliveryScope.Distributed)
            {
                distributed.Add(message);
            }
        }

        // Local messages stay in this process entirely.
        BrokerMessage[] local = [.. messages.Where(m => m.Scope == DeliveryScope.Local)];
        if (local.Length > 0)
        {
            await _local.PublishBatchAsync(local, cancellationToken).ConfigureAwait(false);
        }

        if (distributed.Count == 0)
        {
            return;
        }

        IDatabase db = _redis.GetDatabase();

        // One round-trip for the batch rather than one per message.
        IBatch batch = db.CreateBatch();
        var writes = new List<Task>(distributed.Count);

        foreach (BrokerMessage message in distributed)
        {
            writes.Add(batch.StreamAddAsync(
                StreamKey, ToFields(message), maxLength: _maxStreamLength, useApproximateMaxLength: true));
        }

        batch.Execute();
        await Task.WhenAll(writes).ConfigureAwait(false);

        // Nudge every waiting consumer; the notification carries nothing, because the stream is the
        // record and a lost nudge only costs one fallback-poll interval.
        await db.PublishAsync(RedisChannel.Literal(NotifyChannel), RedisValue.EmptyString)
            .ConfigureAwait(false);
    }

    public async ValueTask<IAsyncDisposable> SubscribeAsync(
        EventSubscriptionOptions options,
        Func<EventDelivery, CancellationToken, ValueTask<DeliveryResult>> handler,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(handler);
        TopicPattern.ValidatePattern(options.TopicFilter);

        var handles = new List<IAsyncDisposable>(2);

        // Unless the subscriber explicitly asked for distributed-only, it also wants local traffic —
        // the same subscription serves both, which is what makes scope a publisher's decision.
        if (options.Scope != DeliveryScope.Distributed)
        {
            handles.Add(await _local
                .SubscribeAsync(options with { Scope = DeliveryScope.Local }, handler, cancellationToken)
                .ConfigureAwait(false));
        }

        if (options.Scope != DeliveryScope.Local)
        {
            handles.Add(await SubscribeToStreamAsync(options, handler).ConfigureAwait(false));
        }

        return new CompositeHandle(handles);
    }

    private async Task<IAsyncDisposable> SubscribeToStreamAsync(
        EventSubscriptionOptions options,
        Func<EventDelivery, CancellationToken, ValueTask<DeliveryResult>> handler)
    {
        IDatabase db = _redis.GetDatabase();

        // A named group is shared across the fleet — one member handles each message. An unnamed one
        // gets a private group so this subscriber sees everything.
        string group = options.ConsumerGroup ?? $"broadcast:{IdGenerator.NewId()}";
        bool ephemeral = options.ConsumerGroup is null;

        RedisValue start = options.Start == SubscriptionStart.Earliest ? StreamPosition.Beginning : StreamPosition.NewMessages;

        try
        {
            await db.StreamCreateConsumerGroupAsync(StreamKey, group, start, createStream: true).ConfigureAwait(false);
        }
        catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP", StringComparison.Ordinal))
        {
            // Another replica created it first, which is the normal case for a named group.
        }

        var consumer = new StreamConsumer(
            _redis, db, group, _consumerName, options, handler, ephemeral,
            _loggerFactory?.CreateLogger<RedisEventBroker>() ?? _logger);

        await consumer.StartAsync().ConfigureAwait(false);
        return consumer;
    }

    private static NameValueEntry[] ToFields(BrokerMessage message) =>
    [
        new("messageId", message.MessageId),
        new("topic", message.Topic),
        new("payload", message.PayloadJson),
        new("scope", message.Scope.ToString()),
        new("correlationKey", message.CorrelationKey ?? ""),
        new("tenantId", message.TenantId ?? ""),
        new("sourceInstanceId", message.SourceInstanceId ?? ""),
        new("headers", message.Headers.Count == 0 ? "" : JsonSerializer.Serialize(message.Headers, Json)),
        new("occurredAt", message.OccurredAt.ToString("O"))
    ];

    private static BrokerMessage? FromEntry(StreamEntry entry)
    {
        try
        {
            string? headers = entry["headers"];

            return new BrokerMessage
            {
                MessageId = entry["messageId"].ToString() ?? "",
                Topic = entry["topic"].ToString() ?? "",
                PayloadJson = entry["payload"].ToString() ?? "{}",
                Scope = Enum.TryParse(entry["scope"].ToString(), out DeliveryScope scope) ? scope : DeliveryScope.Distributed,
                CorrelationKey = Empty(entry["correlationKey"]),
                TenantId = Empty(entry["tenantId"]),
                SourceInstanceId = Empty(entry["sourceInstanceId"]),
                Headers = string.IsNullOrEmpty(headers)
                    ? new Dictionary<string, string>(StringComparer.Ordinal)
                    : JsonSerializer.Deserialize<Dictionary<string, string>>(headers, Json)
                      ?? new Dictionary<string, string>(StringComparer.Ordinal),
                OccurredAt = DateTimeOffset.TryParse(entry["occurredAt"].ToString(), out DateTimeOffset at)
                    ? at
                    : DateTimeOffset.UtcNow
            };
        }
        catch
        {
            // A malformed entry is poison; the caller dead-letters it rather than looping on it.
            return null;
        }

        static string? Empty(RedisValue value)
        {
            string? text = value.ToString();
            return string.IsNullOrEmpty(text) ? null : text;
        }
    }

    public async ValueTask DisposeAsync() => await _local.DisposeAsync().ConfigureAwait(false);

    private sealed class CompositeHandle : IAsyncDisposable
    {
        private readonly IReadOnlyList<IAsyncDisposable> _handles;

        public CompositeHandle(IReadOnlyList<IAsyncDisposable> handles) => _handles = handles;

        public async ValueTask DisposeAsync()
        {
            foreach (IAsyncDisposable handle in _handles)
            {
                await handle.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>One consumer-group reader, woken by pub/sub and backstopped by a slow poll.</summary>
    private sealed class StreamConsumer : IAsyncDisposable
    {
        private readonly IConnectionMultiplexer _redis;
        private readonly IDatabase _db;
        private readonly string _group;
        private readonly string _consumer;
        private readonly EventSubscriptionOptions _options;
        private readonly Func<EventDelivery, CancellationToken, ValueTask<DeliveryResult>> _handler;
        private readonly bool _ephemeral;
        private readonly ILogger? _logger;
        private readonly CancellationTokenSource _shutdown = new();
        private readonly SemaphoreSlim _wake = new(0);
        private ChannelMessageQueue? _notifications;
        private Task? _pump;

        public StreamConsumer(
            IConnectionMultiplexer redis,
            IDatabase db,
            string group,
            string consumer,
            EventSubscriptionOptions options,
            Func<EventDelivery, CancellationToken, ValueTask<DeliveryResult>> handler,
            bool ephemeral,
            ILogger? logger)
        {
            _redis = redis;
            _db = db;
            _group = group;
            _consumer = consumer;
            _options = options;
            _handler = handler;
            _ephemeral = ephemeral;
            _logger = logger;
        }

        public async Task StartAsync()
        {
            _notifications = await _redis.GetSubscriber()
                .SubscribeAsync(RedisChannel.Literal(NotifyChannel)).ConfigureAwait(false);

            _notifications.OnMessage(_ => _wake.Release());
            _pump = Task.Run(() => PumpAsync(_shutdown.Token));
        }

        private async Task PumpAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // Read first, then wait: anything added between subscribing and here is already
                    // pending in the group, so a wake-up is not required to see it.
                    int handled = await ReadOnceAsync(cancellationToken).ConfigureAwait(false);

                    if (handled == 0)
                    {
                        await _wake.WaitAsync(FallbackPoll, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Broker consumer {Group} failed a read; retrying.", _group);
                    try
                    {
                        await Task.Delay(FallbackPoll, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }

        private async Task<int> ReadOnceAsync(CancellationToken cancellationToken)
        {
            StreamEntry[] entries = await _db
                .StreamReadGroupAsync(StreamKey, _group, _consumer, StreamPosition.NewMessages, count: 100)
                .ConfigureAwait(false);

            if (entries.Length == 0)
            {
                return 0;
            }

            foreach (StreamEntry entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                BrokerMessage? message = FromEntry(entry);

                if (message is null)
                {
                    await DeadLetterAsync(entry, "unparseable entry").ConfigureAwait(false);
                    continue;
                }

                if (!Matches(message))
                {
                    // Not ours, but this group owns the entry, so it must be acknowledged or it
                    // would sit pending forever and be reclaimed on every sweep.
                    await _db.StreamAcknowledgeAsync(StreamKey, _group, entry.Id).ConfigureAwait(false);
                    continue;
                }

                await DispatchAsync(entry, message, cancellationToken).ConfigureAwait(false);
            }

            return entries.Length;
        }

        private async Task DispatchAsync(StreamEntry entry, BrokerMessage message, CancellationToken cancellationToken)
        {
            try
            {
                DeliveryResult result = await _handler(new EventDelivery
                {
                    Message = message,
                    SubscriptionId = _group,
                    TransportToken = entry.Id.ToString()
                }, cancellationToken).ConfigureAwait(false);

                switch (result.Outcome)
                {
                    case DeliveryOutcome.Ack:
                        await _db.StreamAcknowledgeAsync(StreamKey, _group, entry.Id).ConfigureAwait(false);
                        break;

                    case DeliveryOutcome.DeadLetter:
                        await DeadLetterAsync(entry, result.Reason ?? "no reason given").ConfigureAwait(false);
                        break;

                    case DeliveryOutcome.Retry:
                        // Left unacknowledged on purpose: it stays pending for this group and is
                        // redelivered, which is exactly what Retry means.
                        _logger?.LogWarning(
                            "Broker consumer {Group} deferred message {MessageId}: {Reason}",
                            _group, message.MessageId, result.Reason ?? "no reason given");
                        break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Unacknowledged, so it will be redelivered; the pump must survive to do that.
                _logger?.LogError(ex,
                    "Broker consumer {Group} threw handling message {MessageId} on {Topic}.",
                    _group, message.MessageId, message.Topic);
            }
        }

        private async Task DeadLetterAsync(StreamEntry entry, string reason)
        {
            NameValueEntry[] fields =
            [
                .. entry.Values,
                new NameValueEntry("deadLetterReason", reason),
                new NameValueEntry("deadLetteredAt", DateTimeOffset.UtcNow.ToString("O"))
            ];

            await _db.StreamAddAsync(DeadLetterKey, fields, maxLength: 10_000, useApproximateMaxLength: true)
                .ConfigureAwait(false);

            await _db.StreamAcknowledgeAsync(StreamKey, _group, entry.Id).ConfigureAwait(false);

            _logger?.LogError("Dead-lettered broker entry {EntryId}: {Reason}", entry.Id, reason);
        }

        private bool Matches(BrokerMessage message)
        {
            if (_options.TenantId is not null &&
                !string.Equals(_options.TenantId, message.TenantId, StringComparison.Ordinal))
            {
                return false;
            }

            if (_options.CorrelationKey is not null &&
                !string.Equals(_options.CorrelationKey, message.CorrelationKey, StringComparison.Ordinal))
            {
                return false;
            }

            return TopicPattern.IsMatch(_options.TopicFilter, message.Topic);
        }

        public async ValueTask DisposeAsync()
        {
            await _shutdown.CancelAsync().ConfigureAwait(false);

            if (_notifications is { } notifications)
            {
                await notifications.UnsubscribeAsync().ConfigureAwait(false);
            }

            if (_pump is { } pump)
            {
                try { await pump.ConfigureAwait(false); } catch (OperationCanceledException) { }
            }

            // A private group has no other members and would otherwise accumulate in Redis forever.
            if (_ephemeral)
            {
                try
                {
                    await _db.StreamDeleteConsumerGroupAsync(StreamKey, _group).ConfigureAwait(false);
                }
                catch (RedisException)
                {
                    // Best effort: a leaked empty group costs a little memory, not correctness.
                }
            }

            _shutdown.Dispose();
            _wake.Dispose();
        }
    }
}

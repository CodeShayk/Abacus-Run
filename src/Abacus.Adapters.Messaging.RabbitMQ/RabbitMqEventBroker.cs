using System.Text;
using System.Text.Json;
using Abacus.Run.Abstractions;
using Abacus.Run.EventBus;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Abacus.Adapters.Messaging.RabbitMQ;

/// <summary>
/// Cross-service <see cref="IEventBroker"/> over RabbitMQ.
/// </summary>
/// <remarks>
/// <para>
/// Like the Redis broker, this is the in-process broker <em>plus a wire</em>. A
/// <see cref="DeliveryScope.Local"/> message never touches the network; a
/// <see cref="DeliveryScope.Distributed"/> one goes to the exchange and comes back through each
/// service's own queue, including the publisher's. Publishing does not also deliver locally, because
/// that would deliver twice.
/// </para>
/// <para>
/// RabbitMQ maps onto the abstraction more directly than Redis does. A topic exchange does the
/// filtering server-side, so a subscriber is not woken for messages it would discard, and the
/// broadcast/competing distinction is just queue ownership: a named
/// <see cref="EventSubscriptionOptions.ConsumerGroup"/> becomes one durable queue that every replica
/// consumes from, so exactly one member handles each message; an unnamed group becomes an exclusive
/// auto-delete queue private to this subscriber.
/// </para>
/// <para>
/// The topic vocabularies differ in one place and it is worth being precise about it: AMQP uses
/// <c>*</c> for one word and <c>#</c> for zero or more, which is what
/// <see cref="TopicPattern"/> already means by them, so filters pass through unchanged.
/// </para>
/// </remarks>
public sealed class RabbitMqEventBroker : IEventBroker, IAsyncDisposable
{
    private const string Exchange = "abacus.events";
    private const string DeadLetterExchange = "abacus.events.dead";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IConnection _connection;
    private readonly InProcessEventBroker _local;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ILogger<RabbitMqEventBroker>? _logger;
    private readonly SemaphoreSlim _publishLock = new(1, 1);
    private IChannel? _publishChannel;

    private RabbitMqEventBroker(IConnection connection, ILoggerFactory? loggerFactory)
    {
        _connection = connection;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory?.CreateLogger<RabbitMqEventBroker>();
        _local = new InProcessEventBroker(loggerFactory?.CreateLogger<InProcessEventBroker>());
    }

    /// <summary>
    /// Connects and declares the exchanges. Async because the RabbitMQ client's connection and
    /// topology calls are async, and doing them lazily on first publish would hide a broken
    /// configuration until the first message rather than at startup.
    /// </summary>
    public static async Task<RabbitMqEventBroker> CreateAsync(
        string connectionString,
        ILoggerFactory? loggerFactory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var factory = new ConnectionFactory
        {
            Uri = new Uri(connectionString),
            ClientProvidedName = "abacus-run"
        };

        IConnection connection = await factory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using (IChannel setup = await connection.CreateChannelAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            await setup.ExchangeDeclareAsync(
                Exchange, ExchangeType.Topic, durable: true, autoDelete: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            await setup.ExchangeDeclareAsync(
                DeadLetterExchange, ExchangeType.Fanout, durable: true, autoDelete: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        return new RabbitMqEventBroker(connection, loggerFactory);
    }

    public BrokerCapabilities Capabilities { get; } = new()
    {
        SupportsDistributed = true,
        SupportsCompetingConsumers = true,

        // A queue holds what arrives after it is bound. There is no reading back through history, so
        // claiming replay would be a lie that only shows up when someone relies on it.
        SupportsReplay = false,

        SupportsDeadLetter = true,
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
        }

        BrokerMessage[] local = [.. messages.Where(m => m.Scope == DeliveryScope.Local)];
        if (local.Length > 0)
        {
            await _local.PublishBatchAsync(local, cancellationToken).ConfigureAwait(false);
        }

        BrokerMessage[] distributed = [.. messages.Where(m => m.Scope == DeliveryScope.Distributed)];
        if (distributed.Length == 0)
        {
            return;
        }

        IChannel channel = await PublishChannelAsync(cancellationToken).ConfigureAwait(false);

        foreach (BrokerMessage message in distributed)
        {
            var properties = new BasicProperties
            {
                MessageId = message.MessageId,
                CorrelationId = message.CorrelationKey,
                Timestamp = new AmqpTimestamp(message.OccurredAt.ToUnixTimeSeconds()),
                ContentType = "application/json",
                DeliveryMode = DeliveryModes.Persistent,
                Headers = Headers(message)
            };

            await channel.BasicPublishAsync(
                Exchange, message.Topic, mandatory: false, properties,
                Encoding.UTF8.GetBytes(message.PayloadJson), cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask<IAsyncDisposable> SubscribeAsync(
        EventSubscriptionOptions options,
        Func<EventDelivery, CancellationToken, ValueTask<DeliveryResult>> handler,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(handler);
        TopicPattern.ValidatePattern(options.TopicFilter);

        if (options.Start == SubscriptionStart.Earliest)
        {
            throw new NotSupportedException(
                $"{nameof(RabbitMqEventBroker)} binds a queue at subscribe time and cannot read back through " +
                "history, so Earliest would silently behave as Now.");
        }

        var handles = new List<IAsyncDisposable>(2);

        if (options.Scope != DeliveryScope.Distributed)
        {
            handles.Add(await _local
                .SubscribeAsync(options with { Scope = DeliveryScope.Local }, handler, cancellationToken)
                .ConfigureAwait(false));
        }

        if (options.Scope != DeliveryScope.Local)
        {
            handles.Add(await SubscribeToQueueAsync(options, handler, cancellationToken).ConfigureAwait(false));
        }

        return new CompositeHandle(handles);
    }

    private async Task<IAsyncDisposable> SubscribeToQueueAsync(
        EventSubscriptionOptions options,
        Func<EventDelivery, CancellationToken, ValueTask<DeliveryResult>> handler,
        CancellationToken cancellationToken)
    {
        IChannel channel = await _connection.CreateChannelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        // Bound prefetch, or one subscriber would pull the whole queue into memory and defeat the
        // point of competing consumers.
        await channel.BasicQosAsync(0, (ushort)Math.Max(1, options.MaxConcurrency), global: false, cancellationToken)
            .ConfigureAwait(false);

        bool shared = options.ConsumerGroup is { Length: > 0 };

        // Shared group: one durable queue every replica consumes from, so exactly one handles each
        // message. No group: an exclusive auto-delete queue, so this subscriber gets its own copy
        // and the queue disappears with it.
        string queueName = shared ? $"abacus.{options.ConsumerGroup}" : string.Empty;

        QueueDeclareOk queue = await channel.QueueDeclareAsync(
            queue: queueName,
            durable: shared,
            exclusive: !shared,
            autoDelete: !shared,
            arguments: shared
                ? new Dictionary<string, object?> { ["x-dead-letter-exchange"] = DeadLetterExchange }
                : null,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // The exchange is a topic exchange, so filtering happens server-side and this subscriber is
        // never woken for a message it would only discard.
        await channel.QueueBindAsync(queue.QueueName, Exchange, options.TopicFilter,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var consumer = new AsyncEventingBasicConsumer(channel);
        string subscriptionId = shared ? options.ConsumerGroup! : queue.QueueName;

        consumer.ReceivedAsync += async (_, args) =>
        {
            BrokerMessage? message = FromDelivery(args);

            if (message is null)
            {
                // Poison: nothing downstream can do anything with it, and redelivering would loop.
                await channel.BasicNackAsync(args.DeliveryTag, multiple: false, requeue: false)
                    .ConfigureAwait(false);
                _logger?.LogError("Dead-lettered an unparseable message on {Topic}.", args.RoutingKey);
                return;
            }

            // Tenant and correlation are not expressible as routing keys, so they are filtered here.
            if (!Matches(options, message))
            {
                await channel.BasicAckAsync(args.DeliveryTag, multiple: false).ConfigureAwait(false);
                return;
            }

            try
            {
                DeliveryResult result = await handler(new EventDelivery
                {
                    Message = message,
                    SubscriptionId = subscriptionId,
                    Attempt = args.Redelivered ? 2 : 1,
                    TransportToken = args.DeliveryTag.ToString()
                }, cancellationToken).ConfigureAwait(false);

                switch (result.Outcome)
                {
                    case DeliveryOutcome.Ack:
                        await channel.BasicAckAsync(args.DeliveryTag, multiple: false).ConfigureAwait(false);
                        break;

                    case DeliveryOutcome.Retry:
                        // Requeued rather than dropped: Retry means try again.
                        await channel.BasicNackAsync(args.DeliveryTag, multiple: false, requeue: true)
                            .ConfigureAwait(false);
                        _logger?.LogWarning(
                            "Requeued message {MessageId} on {Topic}: {Reason}",
                            message.MessageId, message.Topic, result.Reason ?? "no reason given");
                        break;

                    case DeliveryOutcome.DeadLetter:
                        await channel.BasicNackAsync(args.DeliveryTag, multiple: false, requeue: false)
                            .ConfigureAwait(false);
                        _logger?.LogError(
                            "Dead-lettered message {MessageId} on {Topic}: {Reason}",
                            message.MessageId, message.Topic, result.Reason);
                        break;
                }
            }
            catch (Exception ex)
            {
                // Requeue and keep the consumer alive: killing it would silently stop every later
                // message on this subscription.
                _logger?.LogError(ex,
                    "Handler threw for message {MessageId} on {Topic}; requeuing.",
                    message.MessageId, message.Topic);

                await channel.BasicNackAsync(args.DeliveryTag, multiple: false, requeue: true)
                    .ConfigureAwait(false);
            }
        };

        string consumerTag = await channel.BasicConsumeAsync(
            queue.QueueName, autoAck: false, consumer, cancellationToken).ConfigureAwait(false);

        return new QueueSubscription(channel, consumerTag, _logger);
    }

    private async Task<IChannel> PublishChannelAsync(CancellationToken cancellationToken)
    {
        if (_publishChannel is { IsOpen: true } open)
        {
            return open;
        }

        await _publishLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_publishChannel is { IsOpen: true } existing)
            {
                return existing;
            }

            _publishChannel = await _connection
                .CreateChannelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

            return _publishChannel;
        }
        finally
        {
            _publishLock.Release();
        }
    }

    private static Dictionary<string, object?> Headers(BrokerMessage message)
    {
        var headers = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["x-abacus-scope"] = message.Scope.ToString(),
            ["x-abacus-tenant"] = message.TenantId ?? "",
            ["x-abacus-source"] = message.SourceInstanceId ?? "",
            ["x-abacus-occurred"] = message.OccurredAt.ToString("O")
        };

        if (message.Headers.Count > 0)
        {
            headers["x-abacus-headers"] = JsonSerializer.Serialize(message.Headers, Json);
        }

        return headers;
    }

    private static BrokerMessage? FromDelivery(BasicDeliverEventArgs args)
    {
        try
        {
            IDictionary<string, object?>? headers = args.BasicProperties.Headers;

            return new BrokerMessage
            {
                MessageId = args.BasicProperties.MessageId ?? "",
                Topic = args.RoutingKey,
                PayloadJson = Encoding.UTF8.GetString(args.Body.Span),
                Scope = DeliveryScope.Distributed,
                CorrelationKey = Empty(args.BasicProperties.CorrelationId),
                TenantId = Empty(Header(headers, "x-abacus-tenant")),
                SourceInstanceId = Empty(Header(headers, "x-abacus-source")),
                Headers = Header(headers, "x-abacus-headers") is { Length: > 0 } custom
                    ? JsonSerializer.Deserialize<Dictionary<string, string>>(custom, Json)
                      ?? new Dictionary<string, string>(StringComparer.Ordinal)
                    : new Dictionary<string, string>(StringComparer.Ordinal),
                OccurredAt = DateTimeOffset.TryParse(Header(headers, "x-abacus-occurred"), out DateTimeOffset at)
                    ? at
                    : DateTimeOffset.UtcNow
            };
        }
        catch
        {
            return null;
        }

        static string? Empty(string? value) => string.IsNullOrEmpty(value) ? null : value;

        static string? Header(IDictionary<string, object?>? headers, string key)
            => headers is not null && headers.TryGetValue(key, out object? value) && value is byte[] bytes
                ? Encoding.UTF8.GetString(bytes)
                : null;
    }

    private static bool Matches(EventSubscriptionOptions options, BrokerMessage message)
    {
        if (options.TenantId is not null &&
            !string.Equals(options.TenantId, message.TenantId, StringComparison.Ordinal))
        {
            return false;
        }

        return options.CorrelationKey is null
            || string.Equals(options.CorrelationKey, message.CorrelationKey, StringComparison.Ordinal);
    }

    public async ValueTask DisposeAsync()
    {
        await _local.DisposeAsync().ConfigureAwait(false);

        if (_publishChannel is { } channel)
        {
            await channel.DisposeAsync().ConfigureAwait(false);
        }

        await _connection.DisposeAsync().ConfigureAwait(false);
        _publishLock.Dispose();
    }

    private sealed class QueueSubscription : IAsyncDisposable
    {
        private readonly IChannel _channel;
        private readonly string _consumerTag;
        private readonly ILogger? _logger;

        public QueueSubscription(IChannel channel, string consumerTag, ILogger? logger)
        {
            _channel = channel;
            _consumerTag = consumerTag;
            _logger = logger;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (_channel.IsOpen)
                {
                    await _channel.BasicCancelAsync(_consumerTag).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Cancelling consumer {ConsumerTag} failed during shutdown.", _consumerTag);
            }

            await _channel.DisposeAsync().ConfigureAwait(false);
        }
    }

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
}

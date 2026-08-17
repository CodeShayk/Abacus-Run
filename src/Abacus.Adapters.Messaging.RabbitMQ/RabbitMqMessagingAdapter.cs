using System.Text;
using System.Text.Json;
using Abacus.Run.Abstractions;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Abacus.Adapters.Messaging.RabbitMQ;

/// <summary>
/// The RabbitMQ <see cref="IMessagingAdapter"/>: domain events and control signalling, over one
/// connection.
/// </summary>
/// <remarks>
/// Registering this moves every messaging capability to RabbitMQ at once. A future
/// <c>Abacus.Adapters.Messaging.AWS</c> implements the same interface and substitutes the same way —
/// the framework resolves both capabilities from whichever adapter is registered, so nothing in the
/// core changes to accommodate a new transport.
/// </remarks>
public sealed class RabbitMqMessagingAdapter : IMessagingAdapter, IAsyncDisposable
{
    private readonly RabbitMqDomainEventBroker _broker;
    private readonly RabbitMqControlChannel _control;

    private RabbitMqMessagingAdapter(RabbitMqDomainEventBroker broker, RabbitMqControlChannel control)
    {
        _broker = broker;
        _control = control;
    }

    /// <summary>
    /// Connects and declares topology. Async because the client's connection and exchange calls are,
    /// and doing it lazily would hide a broken configuration until the first message instead of at
    /// startup.
    /// </summary>
    public static async Task<RabbitMqMessagingAdapter> CreateAsync(
        string connectionString,
        ILoggerFactory? loggerFactory = null,
        CancellationToken cancellationToken = default)
    {
        RabbitMqDomainEventBroker broker = await RabbitMqDomainEventBroker
            .CreateAsync(connectionString, loggerFactory, cancellationToken).ConfigureAwait(false);

        RabbitMqControlChannel control = await RabbitMqControlChannel
            .CreateAsync(connectionString, loggerFactory, cancellationToken).ConfigureAwait(false);

        return new RabbitMqMessagingAdapter(broker, control);
    }

    public string Technology => "RabbitMQ";

    public bool IsDistributed => true;

    public IDomainEventBroker DomainEventBroker => _broker;

    public IControlChannel ControlChannel => _control;

    public async ValueTask DisposeAsync()
    {
        await _broker.DisposeAsync().ConfigureAwait(false);
        await _control.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// Cross-replica control signalling over a topic exchange, routed by instance id.
/// </summary>
/// <remarks>
/// Broadcast, not competing: every replica hears the signal and only the one holding the instance
/// acts on it. Queues are exclusive and auto-delete, and messages are transient — a signal that
/// arrives after the replica has gone is worthless, and the instance row already carries the
/// instruction for whoever picks the work up next.
/// </remarks>
public sealed class RabbitMqControlChannel : IControlChannel, IAsyncDisposable
{
    private const string Exchange = "abacus.control";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IConnection _connection;
    private readonly ILogger<RabbitMqControlChannel>? _logger;
    private readonly SemaphoreSlim _publishLock = new(1, 1);
    private IChannel? _publishChannel;

    private RabbitMqControlChannel(IConnection connection, ILogger<RabbitMqControlChannel>? logger)
    {
        _connection = connection;
        _logger = logger;
    }

    public static async Task<RabbitMqControlChannel> CreateAsync(
        string connectionString,
        ILoggerFactory? loggerFactory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var factory = new ConnectionFactory
        {
            Uri = new Uri(connectionString),
            ClientProvidedName = "abacus-control"
        };

        IConnection connection = await factory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using (IChannel setup = await connection.CreateChannelAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            await setup.ExchangeDeclareAsync(
                Exchange, ExchangeType.Topic, durable: true, autoDelete: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        return new RabbitMqControlChannel(connection, loggerFactory?.CreateLogger<RabbitMqControlChannel>());
    }

    public async ValueTask PublishAsync(ControlSignal signal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(signal);

        try
        {
            IChannel channel = await PublishChannelAsync(cancellationToken).ConfigureAwait(false);

            var properties = new BasicProperties
            {
                ContentType = "application/json",
                DeliveryMode = DeliveryModes.Transient
            };

            await channel.BasicPublishAsync(
                Exchange, signal.InstanceId, mandatory: false, properties,
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(signal, Json)),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Never fail the control action for a failed signal: the caller has already written the
            // authoritative row, and the owning replica will see it on its next look.
            _logger?.LogWarning(ex,
                "Could not signal {Action} for instance {InstanceId}; the instance row still carries it.",
                signal.Action, signal.InstanceId);
        }
    }

    public async Task<IAsyncDisposable> SubscribeAsync(
        string instanceId, Func<ControlSignal, Task> handler, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(handler);

        IChannel channel = await _connection.CreateChannelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        QueueDeclareOk queue = await channel.QueueDeclareAsync(
            queue: string.Empty, durable: false, exclusive: true, autoDelete: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await channel.QueueBindAsync(queue.QueueName, Exchange, instanceId,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var consumer = new AsyncEventingBasicConsumer(channel);

        consumer.ReceivedAsync += async (_, args) =>
        {
            try
            {
                ControlSignal? signal = JsonSerializer.Deserialize<ControlSignal>(
                    Encoding.UTF8.GetString(args.Body.Span), Json);

                if (signal is not null)
                {
                    await handler(signal).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Error handling a control signal for {InstanceId}.", instanceId);
            }
        };

        string tag = await channel.BasicConsumeAsync(queue.QueueName, autoAck: true, consumer, cancellationToken)
            .ConfigureAwait(false);

        return new Subscription(channel, tag);
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
            return _publishChannel is { IsOpen: true } existing
                ? existing
                : _publishChannel = await _connection
                    .CreateChannelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _publishLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_publishChannel is { } channel)
        {
            await channel.DisposeAsync().ConfigureAwait(false);
        }

        await _connection.DisposeAsync().ConfigureAwait(false);
        _publishLock.Dispose();
    }

    private sealed class Subscription : IAsyncDisposable
    {
        private readonly IChannel _channel;
        private readonly string _tag;

        public Subscription(IChannel channel, string tag)
        {
            _channel = channel;
            _tag = tag;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (_channel.IsOpen)
                {
                    await _channel.BasicCancelAsync(_tag).ConfigureAwait(false);
                }
            }
            catch
            {
                // Shutting down; the exclusive queue disappears with the channel regardless.
            }

            await _channel.DisposeAsync().ConfigureAwait(false);
        }
    }
}

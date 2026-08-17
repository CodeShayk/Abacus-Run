using System.Collections.Concurrent;
using System.Threading.Channels;
using Abacus.Run.Abstractions;
using Abacus.Run.Core;
using Microsoft.Extensions.Logging;

namespace Abacus.Run.Messaging;

/// <summary>
/// Single-service <see cref="IDomainEventBroker"/>. The default: messages are handed to subscribers in this
/// process and go no further.
/// </summary>
/// <remarks>
/// <para>
/// Each subscription owns an unbounded channel and a pump task, so one slow handler delays only its
/// own subscription. Publishing never blocks on a handler — a workflow that publishes an event should
/// not be paced by whoever is listening.
/// </para>
/// <para>
/// A <see cref="DeliveryScope.Distributed"/> message is rejected rather than delivered locally: the
/// publisher asked for something this transport cannot do, and quietly downgrading it would mean the
/// other service simply never hears about it.
/// </para>
/// </remarks>
public sealed class InProcessDomainEventBroker : IDomainEventBroker, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Subscription> _subscriptions = new(StringComparer.Ordinal);
    private readonly ILogger<InProcessDomainEventBroker>? _logger;
    private readonly TimeProvider _clock;
    private volatile bool _disposed;

    public InProcessDomainEventBroker(ILogger<InProcessDomainEventBroker>? logger = null, TimeProvider? clock = null)
    {
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public DomainEventBrokerCapabilities Capabilities => DomainEventBrokerCapabilities.InProcess;

    /// <summary>Test and diagnostic hook: how many subscriptions are currently registered.</summary>
    public int SubscriptionCount => _subscriptions.Count;

    public ValueTask PublishAsync(DomainEventMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return PublishBatchAsync([message], cancellationToken);
    }

    public ValueTask PublishBatchAsync(IReadOnlyList<DomainEventMessage> messages, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ObjectDisposedException.ThrowIf(_disposed, this);

        foreach (DomainEventMessage message in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Validate(message);

            // Snapshotting the values is enough: a subscription registered after this point had not
            // yet asked for the message, and one disposed after this point drains what it was given.
            foreach (Subscription subscription in _subscriptions.Values)
            {
                if (!SubscriptionMatches(subscription.Options, message))
                {
                    continue;
                }

                if (!subscription.Channel.Writer.TryWrite(message))
                {
                    _logger?.LogWarning(
                        "Subscription {SubscriptionId} refused message {MessageId} on {Topic}; it is shutting down.",
                        subscription.Id, message.MessageId, message.Topic);
                }
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<IAsyncDisposable> SubscribeAsync(
        DomainEventSubscriptionOptions options,
        Func<DomainEventDelivery, CancellationToken, ValueTask<DeliveryResult>> handler,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(handler);
        ObjectDisposedException.ThrowIf(_disposed, this);

        TopicPattern.ValidatePattern(options.TopicFilter);

        if (options.Scope == DeliveryScope.Distributed)
        {
            throw new NotSupportedException(
                $"Subscription '{options.Name ?? options.TopicFilter}' asks for distributed delivery, which " +
                $"{nameof(InProcessDomainEventBroker)} cannot provide. Register a distributed broker or drop the scope filter.");
        }

        if (options.Start == SubscriptionStart.Earliest)
        {
            throw new NotSupportedException(
                $"{nameof(InProcessDomainEventBroker)} retains nothing, so {nameof(SubscriptionStart.Earliest)} would " +
                "silently behave as Now. Use a broker with replay support.");
        }

        var subscription = new Subscription(
            IdGenerator.NewId("sub"), options, handler, _logger, _clock);

        _subscriptions[subscription.Id] = subscription;
        subscription.Start(() => _subscriptions.TryRemove(subscription.Id, out _));

        return ValueTask.FromResult<IAsyncDisposable>(subscription);
    }

    /// <summary>
    /// Competing consumers within one process still need every group member to receive the message so
    /// exactly one can take it; that arbitration is the store's compare-and-set, not the transport's.
    /// The transport therefore filters only on what it can see: topic, scope, tenant, correlation.
    /// </summary>
    private static bool SubscriptionMatches(DomainEventSubscriptionOptions options, DomainEventMessage message)
    {
        if (options.Scope is { } scope && scope != message.Scope)
        {
            return false;
        }

        if (options.TenantId is not null &&
            !string.Equals(options.TenantId, message.TenantId, StringComparison.Ordinal))
        {
            return false;
        }

        if (options.CorrelationKey is not null &&
            !string.Equals(options.CorrelationKey, message.CorrelationKey, StringComparison.Ordinal))
        {
            return false;
        }

        return TopicPattern.IsMatch(options.TopicFilter, message.Topic);
    }

    private static void Validate(DomainEventMessage message)
    {
        if (!TopicPattern.IsValidTopic(message.Topic, out string? error))
        {
            throw new ArgumentException(error, nameof(message));
        }

        if (message.Scope == DeliveryScope.Distributed)
        {
            throw new NotSupportedException(
                $"Message '{message.MessageId}' on topic '{message.Topic}' is scoped " +
                $"{nameof(DeliveryScope.Distributed)}, which {nameof(InProcessDomainEventBroker)} cannot deliver. " +
                "Register a distributed broker, or publish it as Local.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (Subscription subscription in _subscriptions.Values)
        {
            await subscription.DisposeAsync().ConfigureAwait(false);
        }

        _subscriptions.Clear();
    }

    private sealed class Subscription : IAsyncDisposable
    {
        private readonly Func<DomainEventDelivery, CancellationToken, ValueTask<DeliveryResult>> _handler;
        private readonly ILogger? _logger;
        private readonly TimeProvider _clock;
        private readonly CancellationTokenSource _shutdown = new();
        private Task? _pump;
        private Action? _onDisposed;

        public Subscription(
            string id,
            DomainEventSubscriptionOptions options,
            Func<DomainEventDelivery, CancellationToken, ValueTask<DeliveryResult>> handler,
            ILogger? logger,
            TimeProvider clock)
        {
            Id = id;
            Options = options;
            _handler = handler;
            _logger = logger;
            _clock = clock;
            Channel = System.Threading.Channels.Channel.CreateUnbounded<DomainEventMessage>(
                new UnboundedChannelOptions { SingleReader = options.MaxConcurrency <= 1 });
        }

        public string Id { get; }
        public DomainEventSubscriptionOptions Options { get; }
        public Channel<DomainEventMessage> Channel { get; }

        public void Start(Action onDisposed)
        {
            _onDisposed = onDisposed;
            _pump = Task.Run(() => PumpAsync(_shutdown.Token));
        }

        private async Task PumpAsync(CancellationToken cancellationToken)
        {
            int concurrency = Math.Max(1, Options.MaxConcurrency);

            if (concurrency == 1)
            {
                await DrainAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            Task[] workers = Enumerable.Range(0, concurrency)
                .Select(_ => Task.Run(() => DrainAsync(cancellationToken), cancellationToken))
                .ToArray();

            await Task.WhenAll(workers).ConfigureAwait(false);
        }

        private async Task DrainAsync(CancellationToken cancellationToken)
        {
            try
            {
                await foreach (DomainEventMessage message in
                    Channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    await HandleAsync(message, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // unsubscribed
            }
        }

        private async Task HandleAsync(DomainEventMessage message, CancellationToken cancellationToken)
        {
            var delivery = new DomainEventDelivery
            {
                Message = message,
                SubscriptionId = Id,
                Attempt = 1,
                TransportToken = _clock.GetUtcNow().ToUnixTimeMilliseconds().ToString()
            };

            try
            {
                DeliveryResult result = await _handler(delivery, cancellationToken).ConfigureAwait(false);

                // In-process delivery has nowhere to redeliver from and no dead-letter destination,
                // so a non-Ack is a logged fact rather than a retry. A handler that needs redelivery
                // needs a transport that retains, and DomainEventBrokerCapabilities says which ones do.
                if (result.Outcome != DeliveryOutcome.Ack)
                {
                    _logger?.LogWarning(
                        "Subscription {SubscriptionId} returned {Outcome} for message {MessageId} on {Topic}: {Reason}. " +
                        "This transport does not redeliver.",
                        Id, result.Outcome, message.MessageId, message.Topic, result.Reason ?? "no reason given");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A throwing handler must not kill the pump: every later message on this
                // subscription would be lost, and the loss would be silent.
                _logger?.LogError(ex,
                    "Subscription {SubscriptionId} threw handling message {MessageId} on {Topic}.",
                    Id, message.MessageId, message.Topic);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _onDisposed?.Invoke();
            Channel.Writer.TryComplete();

            if (_pump is { } pump)
            {
                // Let what is already queued drain before cancelling, so unsubscribing does not
                // discard messages the subscriber was already given.
                try
                {
                    await pump.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    await _shutdown.CancelAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // already stopping
                }
            }

            if (!_shutdown.IsCancellationRequested)
            {
                await _shutdown.CancelAsync().ConfigureAwait(false);
            }

            _shutdown.Dispose();
        }
    }
}

using System.Collections.Concurrent;
using Abacus.Run.Abstractions;
using Microsoft.Extensions.Logging;

namespace Abacus.Run.Messaging;

/// <summary>
/// The default <see cref="IMessagingAdapter"/>: everything in this process, no infrastructure.
/// </summary>
/// <remarks>
/// Correct for a single-service deployment, which is why it is the default rather than a stub.
/// <see cref="IsDistributed"/> is false, and that is the signal a fleet needs: domain events will
/// not reach another service, and a control signal will not reach another replica.
/// </remarks>
public sealed class InProcessMessagingAdapter : IMessagingAdapter, IAsyncDisposable
{
    private readonly InProcessDomainEventBroker _broker;

    public InProcessMessagingAdapter(
        InProcessDomainEventBroker? broker = null,
        ILoggerFactory? loggerFactory = null,
        TimeProvider? clock = null)
    {
        _broker = broker ?? new InProcessDomainEventBroker(
            loggerFactory?.CreateLogger<InProcessDomainEventBroker>(), clock);

        ControlChannel = new InProcessControlChannel();
    }

    public string Technology => "InProcess";

    public bool IsDistributed => false;

    public IDomainEventBroker DomainEventBroker => _broker;

    public IControlChannel ControlChannel { get; }

    public ValueTask DisposeAsync() => _broker.DisposeAsync();
}

/// <summary>
/// Process-local <see cref="IControlChannel"/>. Delivers to every subscriber for the instance.
/// </summary>
/// <remarks>
/// Handlers are invoked without awaiting the publisher's completion, because a control signal is an
/// instruction rather than a transaction: the caller has already written the authoritative row and
/// should not be blocked by whoever is reacting to it.
/// </remarks>
public sealed class InProcessControlChannel : IControlChannel
{
    private readonly ConcurrentDictionary<string, List<Subscription>> _subscribers = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public ValueTask PublishAsync(ControlSignal signal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(signal);

        Subscription[] targets;
        lock (_sync)
        {
            targets = _subscribers.TryGetValue(signal.InstanceId, out List<Subscription>? list)
                ? [.. list]
                : [];
        }

        foreach (Subscription subscription in targets)
        {
            // Fire and forget by design; a throwing handler must not fail the control action that
            // has already been recorded.
            _ = subscription.InvokeAsync(signal);
        }

        return ValueTask.CompletedTask;
    }

    public Task<IAsyncDisposable> SubscribeAsync(
        string instanceId, Func<ControlSignal, Task> handler, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(handler);

        var subscription = new Subscription(handler, () => Remove(instanceId, handler));

        lock (_sync)
        {
            _subscribers.GetOrAdd(instanceId, _ => []).Add(subscription);
        }

        return Task.FromResult<IAsyncDisposable>(subscription);
    }

    private void Remove(string instanceId, Func<ControlSignal, Task> handler)
    {
        lock (_sync)
        {
            if (!_subscribers.TryGetValue(instanceId, out List<Subscription>? list))
            {
                return;
            }

            list.RemoveAll(s => s.Handler == handler);
            if (list.Count == 0)
            {
                _subscribers.TryRemove(instanceId, out _);
            }
        }
    }

    private sealed class Subscription : IAsyncDisposable
    {
        private readonly Action _remove;

        public Subscription(Func<ControlSignal, Task> handler, Action remove)
        {
            Handler = handler;
            _remove = remove;
        }

        public Func<ControlSignal, Task> Handler { get; }

        public async Task InvokeAsync(ControlSignal signal)
        {
            try
            {
                await Handler(signal).ConfigureAwait(false);
            }
            catch
            {
                // The row is the authority; a failed reaction does not undo the instruction.
            }
        }

        public ValueTask DisposeAsync()
        {
            _remove();
            return ValueTask.CompletedTask;
        }
    }
}

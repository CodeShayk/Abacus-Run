using System.Threading.Channels;
using Abacus.Run.Abstractions;
using Abacus.Run.Core;

namespace Abacus.Run.Notifications;

/// <summary>In-process event bus. Substitutable with Redis Streams without touching the SSE path.</summary>
public sealed class InMemoryNotificationBus : INotificationBus, IDisposable
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<Channel<EventEnvelope>>> _subscribers =
        new(StringComparer.Ordinal);

    private readonly object _sync = new();

    public ValueTask PublishBatchAsync(IReadOnlyList<EventEnvelope> events, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(events);

        foreach (EventEnvelope envelope in events)
        {
            lock (_sync)
            {
                if (!_subscribers.TryGetValue(envelope.InstanceId, out List<Channel<EventEnvelope>>? channels))
                {
                    continue;
                }

                foreach (Channel<EventEnvelope> channel in channels)
                {
                    channel.Writer.TryWrite(envelope);
                }
            }
        }

        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<EventEnvelope> SubscribeAsync(
        string instanceId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Channel<EventEnvelope> channel = Channel.CreateUnbounded<EventEnvelope>();

        lock (_sync)
        {
            _subscribers.GetOrAdd(instanceId, _ => []).Add(channel);
        }

        try
        {
            await foreach (EventEnvelope envelope in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return envelope;
            }
        }
        finally
        {
            lock (_sync)
            {
                if (_subscribers.TryGetValue(instanceId, out List<Channel<EventEnvelope>>? channels))
                {
                    channels.Remove(channel);
                    if (channels.Count == 0)
                    {
                        _subscribers.TryRemove(instanceId, out _);
                    }
                }
            }
        }
    }

    public int SubscriberCount(string instanceId)
    {
        lock (_sync)
        {
            return _subscribers.TryGetValue(instanceId, out List<Channel<EventEnvelope>>? channels) ? channels.Count : 0;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            foreach (List<Channel<EventEnvelope>> channels in _subscribers.Values)
            {
                foreach (Channel<EventEnvelope> channel in channels)
                {
                    channel.Writer.TryComplete();
                }
            }
            _subscribers.Clear();
        }
    }
}

using Abacus.Adapters.Cache.Redis;
using Abacus.Run.Abstractions;
using Abacus.Run.Core;
using FluentAssertions;
using StackExchange.Redis;
using Xunit;

namespace Abacus.Run.BrokerTests;

/// <summary>
/// The Redis cache adapter serves one purpose: it is the SSE backplane, so a subscriber can connect
/// to a replica that is not running the instance (PRD FR-4.6). These tests exercise exactly that —
/// two bus instances standing in for two replicas over one Redis.
/// </summary>
[Collection(RedisCollection.Name)]
public class RedisEventBusTests : IAsyncLifetime
{
    private readonly RedisFixture _redis;
    private readonly List<RedisNotificationBus> _buses = [];
    private readonly CancellationTokenSource _shutdown = new();
    private IConnectionMultiplexer? _connection;

    public RedisEventBusTests(RedisFixture redis) => _redis = redis;

    public async Task InitializeAsync()
    {
        if (_redis.Available)
        {
            _connection = await ConnectionMultiplexer.ConnectAsync(_redis.ConnectionString);
        }
    }

    public async Task DisposeAsync()
    {
        await _shutdown.CancelAsync();

        foreach (RedisNotificationBus bus in _buses)
        {
            await bus.DisposeAsync();
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        _shutdown.Dispose();
    }

    /// <summary>One bus per call — each stands in for a separate replica sharing the same Redis.</summary>
    private RedisNotificationBus Replica()
    {
        var bus = new RedisNotificationBus(_connection!, maxStreamLength: 1000);
        _buses.Add(bus);
        return bus;
    }

    private static EventEnvelope Event(string instanceId, long sequence, string type = "executor.completed")
        => NotificationFactory.Create(
            instanceId, sequence, type, new { step = sequence },
            executorId: "settle", superstep: 2, tenantId: "acme",
            workflowName: "order");

    /// <summary>
    /// Collects from a subscription. The subscription is established before the caller publishes,
    /// because the bus reads only new entries — a subscriber is not a reader of history, and
    /// backfill is the event store's job.
    /// </summary>
    private async Task<(Task Pump, List<EventEnvelope> Received, SemaphoreSlim Signal)> SubscribeAsync(
        RedisNotificationBus bus, string instanceId)
    {
        var received = new List<EventEnvelope>();
        var signal = new SemaphoreSlim(0);

        Task pump = Task.Run(async () =>
        {
            try
            {
                await foreach (EventEnvelope envelope in bus.SubscribeAsync(instanceId, _shutdown.Token))
                {
                    lock (received)
                    {
                        received.Add(envelope);
                    }
                    signal.Release();
                }
            }
            catch (OperationCanceledException)
            {
                // test teardown
            }
        });

        // The reader polls; give it a cycle to establish before anything is published.
        await Task.Delay(300);
        return (pump, received, signal);
    }

    private static async Task<bool> WaitAsync(SemaphoreSlim signal, int count, int seconds = 15)
    {
        for (int i = 0; i < count; i++)
        {
            if (!await signal.WaitAsync(TimeSpan.FromSeconds(seconds)))
            {
                return false;
            }
        }
        return true;
    }

    [RequiresDockerFact]
    public async Task A_subscriber_on_one_replica_sees_an_event_published_by_another()
    {
        RedisNotificationBus publisher = Replica();
        RedisNotificationBus subscriber = Replica();
        string instanceId = $"i-{Guid.NewGuid():N}";

        (Task pump, List<EventEnvelope> received, SemaphoreSlim signal) =
            await SubscribeAsync(subscriber, instanceId);

        await publisher.PublishBatchAsync([Event(instanceId, 1)], default);

        (await WaitAsync(signal, 1)).Should()
            .BeTrue("SSE must work when the client reaches a replica that does not own the instance");

        lock (received)
        {
            received[0].InstanceId.Should().Be(instanceId);
            received[0].Sequence.Should().Be(1);
        }
    }

    [RequiresDockerFact]
    public async Task The_envelope_survives_the_round_trip_intact()
    {
        RedisNotificationBus publisher = Replica();
        RedisNotificationBus subscriber = Replica();
        string instanceId = $"i-{Guid.NewGuid():N}";

        (Task pump, List<EventEnvelope> received, SemaphoreSlim signal) =
            await SubscribeAsync(subscriber, instanceId);

        await publisher.PublishBatchAsync([Event(instanceId, 7, "approval.requested")], default);
        (await WaitAsync(signal, 1)).Should().BeTrue();

        lock (received)
        {
            EventEnvelope e = received[0];
            e.Sequence.Should().Be(7, "the sequence is the SSE id and Last-Event-ID depends on it");
            e.EventType.Should().Be("approval.requested");
            e.ExecutorId.Should().Be("settle");
            e.Superstep.Should().Be(2);
            e.TenantId.Should().Be("acme");
            e.PayloadJson.Should().Contain("7");
        }
    }

    [RequiresDockerFact]
    public async Task Streams_are_isolated_per_instance()
    {
        RedisNotificationBus publisher = Replica();
        RedisNotificationBus subscriber = Replica();
        string mine = $"i-{Guid.NewGuid():N}";
        string theirs = $"i-{Guid.NewGuid():N}";

        (Task pump, List<EventEnvelope> received, SemaphoreSlim signal) =
            await SubscribeAsync(subscriber, mine);

        await publisher.PublishBatchAsync([Event(theirs, 1)], default);
        await publisher.PublishBatchAsync([Event(mine, 1)], default);

        (await WaitAsync(signal, 1)).Should().BeTrue();
        await Task.Delay(1000);

        lock (received)
        {
            received.Should().ContainSingle("a subscriber must never see another instance's events")
                .Which.InstanceId.Should().Be(mine);
        }
    }

    [RequiresDockerFact]
    public async Task Every_subscriber_to_an_instance_gets_its_own_copy()
    {
        RedisNotificationBus publisher = Replica();
        RedisNotificationBus watcherA = Replica();
        RedisNotificationBus watcherB = Replica();
        string instanceId = $"i-{Guid.NewGuid():N}";

        (Task pumpA, List<EventEnvelope> a, SemaphoreSlim signalA) = await SubscribeAsync(watcherA, instanceId);
        (Task pumpB, List<EventEnvelope> b, SemaphoreSlim signalB) = await SubscribeAsync(watcherB, instanceId);

        await publisher.PublishBatchAsync([Event(instanceId, 1)], default);

        (await WaitAsync(signalA, 1)).Should().BeTrue();
        (await WaitAsync(signalB, 1)).Should()
            .BeTrue("SSE is broadcast — two operators watching one run must both see it");
    }

    [RequiresDockerFact]
    public async Task A_batch_arrives_in_sequence_order()
    {
        RedisNotificationBus publisher = Replica();
        RedisNotificationBus subscriber = Replica();
        string instanceId = $"i-{Guid.NewGuid():N}";

        (Task pump, List<EventEnvelope> received, SemaphoreSlim signal) =
            await SubscribeAsync(subscriber, instanceId);

        await publisher.PublishBatchAsync(
            [.. Enumerable.Range(1, 20).Select(i => Event(instanceId, i))], default);

        (await WaitAsync(signal, 20)).Should().BeTrue();

        lock (received)
        {
            received.Select(e => e.Sequence).Should()
                .Equal(Enumerable.Range(1, 20).Select(i => (long)i),
                    "a stream that reordered events would break catch-up de-duplication");
        }
    }
}

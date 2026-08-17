using System.Text;
using Abacus.Adapters.Cache.Redis;
using Abacus.Run.Abstractions;
using Abacus.Run.Api;
using Abacus.Run.Core;
using Abacus.Run.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using StackExchange.Redis;
using Xunit;

namespace Abacus.Run.BrokerTests;

/// <summary>
/// The two halves of the events story, and the line between them.
///
/// <para>
/// <b>Service persistence</b> holds the record. Every logged event is written to
/// <see cref="IEventStore"/> — SQL Server in a real deployment — and that is what a reader gets back
/// afterwards.
/// </para>
/// <para>
/// <b>The cache</b> carries live streaming only. Redis exists so an SSE subscriber can attach to a
/// replica that is not running the instance, and to give that live window some durability across a
/// brief reconnect. It is never the record, and nothing reads history from it.
/// </para>
/// </summary>
[Collection(RedisCollection.Name)]
public class SseBackplaneTests : IAsyncLifetime
{
    private readonly RedisFixture _redis;
    private readonly List<RedisNotificationBus> _buses = [];
    private IConnectionMultiplexer? _connection;

    public SseBackplaneTests(RedisFixture redis) => _redis = redis;

    public async Task InitializeAsync()
    {
        if (_redis.Available)
        {
            _connection = await ConnectionMultiplexer.ConnectAsync(_redis.ConnectionString);
        }
    }

    public async Task DisposeAsync()
    {
        foreach (RedisNotificationBus bus in _buses)
        {
            await bus.DisposeAsync();
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }
    }

    private RedisNotificationBus Replica()
    {
        var bus = new RedisNotificationBus(_connection!, maxStreamLength: 1000);
        _buses.Add(bus);
        return bus;
    }

    private static EventEnvelope Event(
        string instanceId, long sequence, string type,
        EventDeliveryMode delivery = EventDeliveryMode.StreamAndLog)
        => NotificationFactory.Create(
            instanceId, sequence, type, new { step = sequence },
            executorId: "settle", superstep: 1, tenantId: "acme",
            workflowName: "order", delivery: delivery);

    /// <summary>
    /// PRD FR-4.6: a subscriber may connect to a replica that does not own the instance. This drives
    /// the real <see cref="Sse.StreamAsync"/> path, with the store supplying history and Redis
    /// supplying everything live — the two halves doing their separate jobs in one request.
    /// </summary>
    [RequiresDockerFact]
    public async Task An_sse_stream_on_one_replica_delivers_events_published_by_another()
    {
        string instanceId = $"i-{Guid.NewGuid():N}";

        RedisNotificationBus owningReplica = Replica();     // runs the instance
        RedisNotificationBus servingReplica = Replica();    // the client happens to reach this one

        // History lives in service persistence, not in the cache.
        var store = new InMemoryEventStore();
        await store.AppendBatchAsync([Event(instanceId, 1, WorkflowEventTypes.WorkflowStarted)], default);

        var http = new DefaultHttpContext();
        var body = new MemoryStream();
        http.Response.Body = body;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        Task stream = Sse.StreamAsync(
            http, instanceId, fromExclusive: 0, isTerminal: false,
            store, servingReplica, timeout.Token, heartbeat: TimeSpan.FromSeconds(5));

        // Publish only after the stream has subscribed: a subscriber starts at the tail, because
        // backfill is the store's job and replaying the cache would double-deliver.
        await Task.Delay(1000, timeout.Token);

        await owningReplica.PublishBatchAsync([Event(instanceId, 2, WorkflowEventTypes.ExecutorCompleted)], default);
        await Task.Delay(400, timeout.Token);
        await owningReplica.PublishBatchAsync([Event(instanceId, 3, WorkflowEventTypes.WorkflowTerminated)], default);

        // The terminal event closes the stream, so this returns rather than running to the timeout.
        await stream;

        string sse = Encoding.UTF8.GetString(body.ToArray());

        sse.Should().Contain($"event: {WorkflowEventTypes.WorkflowStarted}", "history comes from persistence");
        sse.Should().Contain($"event: {WorkflowEventTypes.ExecutorCompleted}", "live events come from the cache");
        sse.Should().Contain($"event: {WorkflowEventTypes.WorkflowTerminated}");

        sse.Should().Contain("id: 1").And.Contain("id: 2").And.Contain("id: 3");
    }

    [RequiresDockerFact]
    public async Task A_reconnect_backfills_from_persistence_not_from_the_cache()
    {
        string instanceId = $"i-{Guid.NewGuid():N}";
        RedisNotificationBus bus = Replica();

        // Persistence holds everything; the cache holds nothing at all for this instance.
        var store = new InMemoryEventStore();
        await store.AppendBatchAsync(
        [
            Event(instanceId, 1, WorkflowEventTypes.WorkflowStarted),
            Event(instanceId, 2, WorkflowEventTypes.ExecutorCompleted),
            Event(instanceId, 3, WorkflowEventTypes.WorkflowTerminated)
        ], default);

        var http = new DefaultHttpContext();
        var body = new MemoryStream();
        http.Response.Body = body;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        // Terminal: replay history and close, which is what a late reconnect gets.
        await Sse.StreamAsync(
            http, instanceId, fromExclusive: 0, isTerminal: true, store, bus, timeout.Token);

        string sse = Encoding.UTF8.GetString(body.ToArray());

        sse.Should().Contain("id: 1").And.Contain("id: 2").And.Contain("id: 3",
            "the record is in persistence, so a reconnect is served even with an empty cache");

        (await _connection!.GetDatabase().KeyExistsAsync($"workflow:events:{instanceId}")).Should()
            .BeFalse("nothing was ever streamed, and history is not written to the cache");
    }

    [RequiresDockerFact]
    public async Task A_log_only_event_is_persisted_and_never_touches_the_cache()
    {
        string instanceId = $"i-{Guid.NewGuid():N}";
        RedisNotificationBus bus = Replica();
        var store = new InMemoryEventStore();
        var sink = new DirectNotificationSink(store, bus);

        await sink.PublishAsync(
            Event(instanceId, 1, WorkflowEventTypes.ExecutorCompleted, EventDeliveryMode.LogOnly), default);

        var persisted = new List<EventEnvelope>();
        await foreach (EventEnvelope e in store.ReadAsync(instanceId, 0, default))
        {
            persisted.Add(e);
        }

        persisted.Should().ContainSingle("a workflow that does not stream is still fully recorded");

        (await _connection!.GetDatabase().KeyExistsAsync($"workflow:events:{instanceId}")).Should()
            .BeFalse("the cache is for streaming; a log-only event has nothing to stream");
    }

    /// <summary>
    /// The adapter is the substitution unit: registering Redis must move general caching and the SSE
    /// backplane together, not one of them.
    /// </summary>
    [RequiresDockerFact]
    public async Task The_redis_adapter_supplies_both_the_cache_and_the_backplane()
    {
        await using var adapter = new RedisCacheAdapter(_connection!);

        adapter.Technology.Should().Be("Redis");
        adapter.IsDistributed.Should().BeTrue("that is the reason to reach for it");

        string key = $"k-{Guid.NewGuid():N}";
        await adapter.Store.SetAsync(key, new { name = "abacus", count = 3 }, null, default);

        var read = await adapter.Store.GetAsync<Dictionary<string, object>>(key, default);
        read.Should().NotBeNull("the general cache round-trips through the same connection");

        adapter.NotificationBackplane.Should().BeAssignableTo<INotificationBus>();
    }

    [RequiresDockerFact]
    public async Task A_cached_value_expires_on_its_ttl()
    {
        await using var adapter = new RedisCacheAdapter(_connection!);
        string key = $"k-{Guid.NewGuid():N}";

        await adapter.Store.SetAsync(key, "v", TimeSpan.FromSeconds(1), default);
        (await adapter.Store.GetAsync<string>(key, default)).Should().Be("v");

        await Task.Delay(1500);

        (await adapter.Store.GetAsync<string>(key, default)).Should()
            .BeNull("Redis applies the ttl, so the framework does not have to sweep");
    }

    [RequiresDockerFact]
    public async Task A_stream_only_event_reaches_the_cache_and_is_never_persisted()
    {
        string instanceId = $"i-{Guid.NewGuid():N}";
        RedisNotificationBus bus = Replica();
        var store = new InMemoryEventStore();
        var sink = new DirectNotificationSink(store, bus);

        await sink.PublishAsync(
            Event(instanceId, 0, WorkflowEventTypes.LlmDelta, EventDeliveryMode.StreamOnly), default);

        var persisted = new List<EventEnvelope>();
        await foreach (EventEnvelope e in store.ReadAsync(instanceId, 0, default))
        {
            persisted.Add(e);
        }

        persisted.Should().BeEmpty("a rendered token has no replay value and is not part of the record");

        (await _connection!.GetDatabase().KeyExistsAsync($"workflow:events:{instanceId}")).Should()
            .BeTrue("it still has to reach whoever is watching right now");
    }
}

using Abacus.Run.Abstractions;
using Abacus.Run.EventBus;
using FluentAssertions;
using StackExchange.Redis;
using Xunit;

namespace Abacus.Run.BrokerTests;

[Collection(RedisCollection.Name)]
public class RedisEventBrokerTests : IAsyncLifetime
{
    private readonly RedisFixture _redis;
    private readonly List<IAsyncDisposable> _disposables = [];
    private IConnectionMultiplexer? _connection;

    public RedisEventBrokerTests(RedisFixture redis) => _redis = redis;

    public async Task InitializeAsync()
    {
        if (_redis.Available)
        {
            _connection = await ConnectionMultiplexer.ConnectAsync(_redis.ConnectionString);
        }
    }

    public async Task DisposeAsync()
    {
        foreach (IAsyncDisposable disposable in _disposables)
        {
            await disposable.DisposeAsync();
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }
    }

    /// <summary>
    /// A separate broker per call, each with its own in-process half — which is what lets a test
    /// stand in for two services sharing one Redis.
    /// </summary>
    private RedisEventBroker Broker()
    {
        var broker = new RedisEventBroker(_connection!, maxStreamLength: 1000);
        _disposables.Add(broker);
        return broker;
    }

    [RequiresDockerFact]
    public async Task Delivers_a_distributed_message_across_two_brokers()
    {
        RedisEventBroker publisher = Broker();
        RedisEventBroker consumer = Broker();
        var recorder = new DeliveryRecorder();

        _disposables.Add(await consumer.SubscribeAsync(
            new EventSubscriptionOptions { TopicFilter = "orders.#" }, recorder.Handler, default));

        await publisher.PublishAsync(Msg.Distributed("orders.placed"), default);

        (await recorder.WaitAsync(1)).Should().BeTrue("a distributed message must cross between services");
        recorder.Messages[0].Topic.Should().Be("orders.placed");
    }

    [RequiresDockerFact]
    public async Task A_local_message_never_reaches_another_broker()
    {
        RedisEventBroker publisher = Broker();
        RedisEventBroker consumer = Broker();
        var recorder = new DeliveryRecorder();

        _disposables.Add(await consumer.SubscribeAsync(
            new EventSubscriptionOptions { TopicFilter = "#" }, recorder.Handler, default));

        await publisher.PublishAsync(Msg.Local("orders.placed"), default);

        (await recorder.StaysAtAsync(0)).Should()
            .BeTrue("Local is a private implementation detail of the publishing service");
    }

    [RequiresDockerFact]
    public async Task A_local_message_still_reaches_subscribers_in_the_publishing_service()
    {
        RedisEventBroker broker = Broker();
        var recorder = new DeliveryRecorder();

        _disposables.Add(await broker.SubscribeAsync(
            new EventSubscriptionOptions { TopicFilter = "#" }, recorder.Handler, default));

        await broker.PublishAsync(Msg.Local("orders.placed"), default);

        (await recorder.WaitAsync(1)).Should().BeTrue("local delivery is the ordinary case, not a disabled one");
    }

    [RequiresDockerFact]
    public async Task A_named_consumer_group_delivers_to_exactly_one_member()
    {
        RedisEventBroker publisher = Broker();
        RedisEventBroker replicaA = Broker();
        RedisEventBroker replicaB = Broker();

        var a = new DeliveryRecorder();
        var b = new DeliveryRecorder();
        var options = new EventSubscriptionOptions { TopicFilter = "work.#", ConsumerGroup = "workers" };

        _disposables.Add(await replicaA.SubscribeAsync(options, a.Handler, default));
        _disposables.Add(await replicaB.SubscribeAsync(options, b.Handler, default));

        await publisher.PublishAsync(Msg.Distributed("work.item"), default);
        await Task.Delay(3000);

        (a.Count + b.Count).Should()
            .Be(1, "a shared group is work routing: doing the job twice is the failure this prevents");
    }

    [RequiresDockerFact]
    public async Task An_unnamed_group_gives_every_subscriber_its_own_copy()
    {
        RedisEventBroker publisher = Broker();
        RedisEventBroker watcherA = Broker();
        RedisEventBroker watcherB = Broker();

        var a = new DeliveryRecorder();
        var b = new DeliveryRecorder();
        var options = new EventSubscriptionOptions { TopicFilter = "audit.#" };

        _disposables.Add(await watcherA.SubscribeAsync(options, a.Handler, default));
        _disposables.Add(await watcherB.SubscribeAsync(options, b.Handler, default));

        await publisher.PublishAsync(Msg.Distributed("audit.viewed"), default);

        (await a.WaitAsync(1)).Should().BeTrue();
        (await b.WaitAsync(1)).Should().BeTrue("an observer must not be starved by another observer");
    }

    [RequiresDockerFact]
    public async Task Topic_filters_are_honoured_over_the_wire()
    {
        RedisEventBroker publisher = Broker();
        RedisEventBroker consumer = Broker();
        var recorder = new DeliveryRecorder();

        _disposables.Add(await consumer.SubscribeAsync(
            new EventSubscriptionOptions { TopicFilter = "orders.*.shipped" }, recorder.Handler, default));

        await publisher.PublishAsync(Msg.Distributed("orders.eu.west.shipped"), default);   // too deep for *
        await publisher.PublishAsync(Msg.Distributed("payments.settled"), default);         // wrong prefix
        await publisher.PublishAsync(Msg.Distributed("orders.eu.shipped"), default);        // matches

        (await recorder.WaitAsync(1)).Should().BeTrue();
        (await recorder.StaysAtAsync(1)).Should().BeTrue();
        recorder.Messages[0].Topic.Should().Be("orders.eu.shipped");
    }

    [RequiresDockerFact]
    public async Task Tenant_and_correlation_narrow_delivery()
    {
        RedisEventBroker publisher = Broker();
        RedisEventBroker consumer = Broker();
        var recorder = new DeliveryRecorder();

        _disposables.Add(await consumer.SubscribeAsync(
            new EventSubscriptionOptions { TopicFilter = "#", TenantId = "acme", CorrelationKey = "ORD-1" },
            recorder.Handler, default));

        await publisher.PublishAsync(Msg.Distributed("orders.placed", "ORD-2", "acme"), default);
        await publisher.PublishAsync(Msg.Distributed("orders.placed", "ORD-1", "globex"), default);
        await publisher.PublishAsync(Msg.Distributed("orders.placed", "ORD-1", "acme"), default);

        (await recorder.WaitAsync(1)).Should().BeTrue();
        (await recorder.StaysAtAsync(1)).Should().BeTrue("a cross-tenant delivery is a security defect");

        recorder.Messages[0].TenantId.Should().Be("acme");
        recorder.Messages[0].CorrelationKey.Should().Be("ORD-1");
    }

    [RequiresDockerFact]
    public async Task A_batch_publishes_in_one_round_trip_and_arrives_whole()
    {
        RedisEventBroker publisher = Broker();
        RedisEventBroker consumer = Broker();
        var recorder = new DeliveryRecorder();

        _disposables.Add(await consumer.SubscribeAsync(
            new EventSubscriptionOptions { TopicFilter = "bulk.#" }, recorder.Handler, default));

        BrokerMessage[] batch = [.. Enumerable.Range(0, 25).Select(i => Msg.Distributed($"bulk.item.{i}"))];
        await publisher.PublishBatchAsync(batch, default);

        (await recorder.WaitAsync(25)).Should().BeTrue("pipelining must not lose messages");
    }

    [RequiresDockerFact]
    public async Task Message_provenance_survives_the_wire()
    {
        RedisEventBroker publisher = Broker();
        RedisEventBroker consumer = Broker();
        var recorder = new DeliveryRecorder();

        _disposables.Add(await consumer.SubscribeAsync(
            new EventSubscriptionOptions { TopicFilter = "#" }, recorder.Handler, default));

        var sent = new BrokerMessage
        {
            MessageId = "msg_fixed_1",
            Topic = "orders.placed",
            PayloadJson = """{"orderId":"ORD-9"}""",
            Scope = DeliveryScope.Distributed,
            CorrelationKey = "ORD-9",
            TenantId = "acme",
            SourceInstanceId = "inst_1",
            Headers = new Dictionary<string, string>(StringComparer.Ordinal) { ["traceparent"] = "00-abc-def-01" }
        };

        await publisher.PublishAsync(sent, default);
        (await recorder.WaitAsync(1)).Should().BeTrue();

        BrokerMessage received = recorder.Messages[0];
        received.MessageId.Should().Be("msg_fixed_1", "de-duplication downstream depends on it");
        received.SourceInstanceId.Should().Be("inst_1");
        received.PayloadJson.Should().Be("""{"orderId":"ORD-9"}""");
        received.Headers.Should().ContainKey("traceparent");
    }

    [RequiresDockerFact]
    public async Task Capabilities_report_what_this_transport_can_do()
    {
        RedisEventBroker broker = Broker();

        broker.Capabilities.SupportsDistributed.Should().BeTrue();
        broker.Capabilities.SupportsCompetingConsumers.Should().BeTrue();
        broker.Capabilities.SupportsReplay.Should().BeTrue();

        await Task.CompletedTask;
    }
}

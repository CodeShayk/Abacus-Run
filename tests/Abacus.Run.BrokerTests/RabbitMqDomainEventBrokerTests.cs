using Abacus.Run.Abstractions;
using Abacus.Adapters.Messaging.RabbitMQ;
using FluentAssertions;
using Xunit;

namespace Abacus.Run.BrokerTests;

[Collection(RabbitMqCollection.Name)]
public class RabbitMqEventBrokerTests : IAsyncLifetime
{
    private readonly RabbitMqFixture _rabbit;
    private readonly List<IAsyncDisposable> _disposables = [];

    public RabbitMqEventBrokerTests(RabbitMqFixture rabbit) => _rabbit = rabbit;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (IAsyncDisposable disposable in _disposables)
        {
            await disposable.DisposeAsync();
        }
    }

    /// <summary>One broker stands in for one service; two of them share the container.</summary>
    private async Task<RabbitMqDomainEventBroker> BrokerAsync()
    {
        RabbitMqDomainEventBroker broker = await RabbitMqDomainEventBroker.CreateAsync(_rabbit.ConnectionString);
        _disposables.Add(broker);
        return broker;
    }

    [RequiresDockerFact]
    public async Task Delivers_a_distributed_message_across_two_brokers()
    {
        RabbitMqDomainEventBroker publisher = await BrokerAsync();
        RabbitMqDomainEventBroker consumer = await BrokerAsync();
        var recorder = new DeliveryRecorder();

        _disposables.Add(await consumer.SubscribeAsync(
            new DomainEventSubscriptionOptions { TopicFilter = "orders.#" }, recorder.Handler, default));

        await publisher.PublishAsync(Msg.Distributed("orders.placed"), default);

        (await recorder.WaitAsync(1)).Should().BeTrue();
        recorder.Messages[0].Topic.Should().Be("orders.placed");
    }

    [RequiresDockerFact]
    public async Task A_local_message_never_reaches_another_broker()
    {
        RabbitMqDomainEventBroker publisher = await BrokerAsync();
        RabbitMqDomainEventBroker consumer = await BrokerAsync();
        var recorder = new DeliveryRecorder();

        _disposables.Add(await consumer.SubscribeAsync(
            new DomainEventSubscriptionOptions { TopicFilter = "#" }, recorder.Handler, default));

        await publisher.PublishAsync(Msg.Local("orders.placed"), default);

        (await recorder.StaysAtAsync(0)).Should()
            .BeTrue("Local is a private implementation detail of the publishing service");
    }

    [RequiresDockerFact]
    public async Task A_local_message_still_reaches_subscribers_in_the_publishing_service()
    {
        RabbitMqDomainEventBroker broker = await BrokerAsync();
        var recorder = new DeliveryRecorder();

        _disposables.Add(await broker.SubscribeAsync(
            new DomainEventSubscriptionOptions { TopicFilter = "#" }, recorder.Handler, default));

        await broker.PublishAsync(Msg.Local("orders.placed"), default);

        (await recorder.WaitAsync(1)).Should().BeTrue();
    }

    [RequiresDockerFact]
    public async Task A_named_consumer_group_delivers_to_exactly_one_member()
    {
        RabbitMqDomainEventBroker publisher = await BrokerAsync();
        RabbitMqDomainEventBroker replicaA = await BrokerAsync();
        RabbitMqDomainEventBroker replicaB = await BrokerAsync();

        var a = new DeliveryRecorder();
        var b = new DeliveryRecorder();
        var options = new DomainEventSubscriptionOptions
        {
            TopicFilter = "work.#",
            ConsumerGroup = $"workers-{Guid.NewGuid():N}"
        };

        _disposables.Add(await replicaA.SubscribeAsync(options, a.Handler, default));
        _disposables.Add(await replicaB.SubscribeAsync(options, b.Handler, default));

        await publisher.PublishAsync(Msg.Distributed("work.item"), default);
        await Task.Delay(2500);

        (a.Count + b.Count).Should()
            .Be(1, "a shared group is work routing: doing the job twice is the failure this prevents");
    }

    [RequiresDockerFact]
    public async Task An_unnamed_group_gives_every_subscriber_its_own_copy()
    {
        RabbitMqDomainEventBroker publisher = await BrokerAsync();
        RabbitMqDomainEventBroker watcherA = await BrokerAsync();
        RabbitMqDomainEventBroker watcherB = await BrokerAsync();

        var a = new DeliveryRecorder();
        var b = new DeliveryRecorder();
        var options = new DomainEventSubscriptionOptions { TopicFilter = "audit.#" };

        _disposables.Add(await watcherA.SubscribeAsync(options, a.Handler, default));
        _disposables.Add(await watcherB.SubscribeAsync(options, b.Handler, default));

        await publisher.PublishAsync(Msg.Distributed("audit.viewed"), default);

        (await a.WaitAsync(1)).Should().BeTrue();
        (await b.WaitAsync(1)).Should().BeTrue("an observer must not be starved by another observer");
    }

    /// <summary>
    /// The interesting one for RabbitMQ: filtering happens at the exchange, so this proves the
    /// abstraction's wildcards and AMQP's routing keys agree rather than merely coexist.
    /// </summary>
    [RequiresDockerFact]
    public async Task Topic_filters_are_honoured_by_the_exchange()
    {
        RabbitMqDomainEventBroker publisher = await BrokerAsync();
        RabbitMqDomainEventBroker consumer = await BrokerAsync();
        var recorder = new DeliveryRecorder();

        _disposables.Add(await consumer.SubscribeAsync(
            new DomainEventSubscriptionOptions { TopicFilter = "orders.*.shipped" }, recorder.Handler, default));

        await publisher.PublishAsync(Msg.Distributed("orders.eu.west.shipped"), default);   // too deep for *
        await publisher.PublishAsync(Msg.Distributed("payments.settled"), default);         // wrong prefix
        await publisher.PublishAsync(Msg.Distributed("orders.eu.shipped"), default);        // matches

        (await recorder.WaitAsync(1)).Should().BeTrue();
        (await recorder.StaysAtAsync(1)).Should().BeTrue();
        recorder.Messages[0].Topic.Should().Be("orders.eu.shipped");
    }

    [RequiresDockerFact]
    public async Task Hash_matches_the_remainder_including_nothing()
    {
        RabbitMqDomainEventBroker publisher = await BrokerAsync();
        RabbitMqDomainEventBroker consumer = await BrokerAsync();
        var recorder = new DeliveryRecorder();

        _disposables.Add(await consumer.SubscribeAsync(
            new DomainEventSubscriptionOptions { TopicFilter = "orders.#" }, recorder.Handler, default));

        await publisher.PublishAsync(Msg.Distributed("orders"), default);
        await publisher.PublishAsync(Msg.Distributed("orders.eu.west.placed"), default);

        (await recorder.WaitAsync(2)).Should()
            .BeTrue("'#' means the remainder, and the remainder may be empty");
    }

    [RequiresDockerFact]
    public async Task Tenant_and_correlation_narrow_delivery()
    {
        RabbitMqDomainEventBroker publisher = await BrokerAsync();
        RabbitMqDomainEventBroker consumer = await BrokerAsync();
        var recorder = new DeliveryRecorder();

        _disposables.Add(await consumer.SubscribeAsync(
            new DomainEventSubscriptionOptions { TopicFilter = "#", TenantId = "acme", CorrelationKey = "ORD-1" },
            recorder.Handler, default));

        await publisher.PublishAsync(Msg.Distributed("orders.placed", "ORD-2", "acme"), default);
        await publisher.PublishAsync(Msg.Distributed("orders.placed", "ORD-1", "globex"), default);
        await publisher.PublishAsync(Msg.Distributed("orders.placed", "ORD-1", "acme"), default);

        (await recorder.WaitAsync(1)).Should().BeTrue();
        (await recorder.StaysAtAsync(1)).Should().BeTrue("a cross-tenant delivery is a security defect");
    }

    [RequiresDockerFact]
    public async Task Message_provenance_survives_the_wire()
    {
        RabbitMqDomainEventBroker publisher = await BrokerAsync();
        RabbitMqDomainEventBroker consumer = await BrokerAsync();
        var recorder = new DeliveryRecorder();

        _disposables.Add(await consumer.SubscribeAsync(
            new DomainEventSubscriptionOptions { TopicFilter = "#" }, recorder.Handler, default));

        var sent = new DomainEventMessage
        {
            MessageId = "msg_fixed_2",
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

        DomainEventMessage received = recorder.Messages[0];
        received.MessageId.Should().Be("msg_fixed_2", "de-duplication downstream depends on it");
        received.TenantId.Should().Be("acme");
        received.SourceInstanceId.Should().Be("inst_1");
        received.PayloadJson.Should().Be("""{"orderId":"ORD-9"}""");
        received.Headers.Should().ContainKey("traceparent");
    }

    [RequiresDockerFact]
    public async Task A_batch_arrives_whole()
    {
        RabbitMqDomainEventBroker publisher = await BrokerAsync();
        RabbitMqDomainEventBroker consumer = await BrokerAsync();
        var recorder = new DeliveryRecorder();

        _disposables.Add(await consumer.SubscribeAsync(
            new DomainEventSubscriptionOptions { TopicFilter = "bulk.#" }, recorder.Handler, default));

        DomainEventMessage[] batch = [.. Enumerable.Range(0, 25).Select(i => Msg.Distributed($"bulk.item.{i}"))];
        await publisher.PublishBatchAsync(batch, default);

        (await recorder.WaitAsync(25)).Should().BeTrue();
    }

    [RequiresDockerFact]
    public async Task Replay_is_refused_rather_than_silently_behaving_as_now()
    {
        RabbitMqDomainEventBroker broker = await BrokerAsync();

        broker.Capabilities.SupportsReplay.Should()
            .BeFalse("a queue holds what arrives after it is bound; claiming replay would be a lie");

        Func<Task> subscribe = () => broker.SubscribeAsync(
            new DomainEventSubscriptionOptions { TopicFilter = "#", Start = SubscriptionStart.Earliest },
            (_, _) => ValueTask.FromResult(DeliveryResult.Ack), default).AsTask();

        await subscribe.Should().ThrowAsync<NotSupportedException>();
    }
}

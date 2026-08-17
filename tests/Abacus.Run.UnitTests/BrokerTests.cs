using Abacus.Run.Abstractions;
using Abacus.Run.Core;
using Abacus.Run.Messaging;
using Abacus.Run.Notifications;
using Abacus.Run.Persistence;
using FluentAssertions;
using Xunit;

namespace Abacus.Run.UnitTests;

public class TopicPatternTests
{
    [Theory]
    [InlineData("orders.placed", "orders.placed", true)]
    [InlineData("orders.placed", "orders.shipped", false)]
    [InlineData("orders", "orders", true)]
    [InlineData("orders.placed", "orders", false)]
    [InlineData("orders", "orders.placed", false)]
    [InlineData("orders.placed", "Orders.Placed", false)]
    public void Matches_literal_topics(string pattern, string topic, bool expected)
        => TopicPattern.IsMatch(pattern, topic).Should().Be(expected);

    [Theory]
    [InlineData("orders.*.shipped", "orders.eu.shipped", true)]
    [InlineData("orders.*.shipped", "orders.eu.west.shipped", false)]
    [InlineData("orders.*.shipped", "orders.shipped", false)]
    [InlineData("*", "orders", true)]
    [InlineData("*", "orders.placed", false)]
    [InlineData("orders.*", "orders.placed", true)]
    [InlineData("orders.*", "orders", false)]
    public void Star_matches_exactly_one_segment(string pattern, string topic, bool expected)
        => TopicPattern.IsMatch(pattern, topic).Should().Be(expected);

    [Theory]
    [InlineData("orders.#", "orders.placed", true)]
    [InlineData("orders.#", "orders.eu.west.placed", true)]
    [InlineData("orders.#", "orders", true)]
    [InlineData("orders.#", "payments.placed", false)]
    [InlineData("#", "anything.at.all", true)]
    [InlineData("orders.*.#", "orders.eu.west.placed", true)]
    [InlineData("orders.*.#", "orders", false)]
    public void Hash_matches_the_remainder(string pattern, string topic, bool expected)
        => TopicPattern.IsMatch(pattern, topic).Should().Be(expected);

    [Theory]
    [InlineData("orders.placed")]
    [InlineData("orders.*.shipped")]
    [InlineData("orders.#")]
    [InlineData("#")]
    public void Accepts_valid_patterns(string pattern)
        => TopicPattern.IsValidPattern(pattern, out _).Should().BeTrue();

    [Theory]
    [InlineData("orders.#.shipped", "'#' before the final segment")]
    [InlineData("order*", "a wildcard glued to literal text")]
    [InlineData("orders..placed", "an empty segment")]
    [InlineData("", "an empty filter")]
    public void Rejects_malformed_patterns(string pattern, string because)
        => TopicPattern.IsValidPattern(pattern, out _).Should().BeFalse(because);

    [Fact]
    public void A_published_topic_may_not_contain_wildcards()
    {
        TopicPattern.IsValidTopic("orders.placed", out _).Should().BeTrue();
        TopicPattern.IsValidTopic("orders.*", out _).Should()
            .BeFalse("wildcards belong to subscriber filters, not published topics");
    }
}

public class InProcessEventBrokerTests
{
    private static DomainEventMessage Message(string topic, string? correlationKey = null, string? tenantId = null)
        => new()
        {
            MessageId = IdGenerator.NewId("msg"),
            Topic = topic,
            PayloadJson = """{"value":1}""",
            CorrelationKey = correlationKey,
            TenantId = tenantId
        };

    private static async Task<IReadOnlyList<DomainEventMessage>> CollectAsync(
        InProcessDomainEventBroker broker, DomainEventSubscriptionOptions options, Func<Task> act, int expected)
    {
        var received = new List<DomainEventMessage>();
        var signal = new SemaphoreSlim(0);

        await using IAsyncDisposable handle = await broker.SubscribeAsync(options, (delivery, _) =>
        {
            lock (received)
            {
                received.Add(delivery.Message);
            }
            signal.Release();
            return ValueTask.FromResult(DeliveryResult.Ack);
        }, CancellationToken.None);

        await act();

        for (int i = 0; i < expected; i++)
        {
            (await signal.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue("the broker should deliver promptly");
        }

        lock (received)
        {
            return [.. received];
        }
    }

    [Fact]
    public async Task Delivers_a_matching_message()
    {
        await using var broker = new InProcessDomainEventBroker();

        IReadOnlyList<DomainEventMessage> received = await CollectAsync(
            broker,
            new DomainEventSubscriptionOptions { TopicFilter = "orders.#" },
            () => broker.PublishAsync(Message("orders.placed"), CancellationToken.None).AsTask(),
            expected: 1);

        received.Should().ContainSingle().Which.Topic.Should().Be("orders.placed");
    }

    [Fact]
    public async Task Does_not_deliver_a_non_matching_topic()
    {
        await using var broker = new InProcessDomainEventBroker();
        var received = new List<DomainEventMessage>();

        await using IAsyncDisposable handle = await broker.SubscribeAsync(
            new DomainEventSubscriptionOptions { TopicFilter = "orders.#" },
            (delivery, _) =>
            {
                lock (received) { received.Add(delivery.Message); }
                return ValueTask.FromResult(DeliveryResult.Ack);
            }, CancellationToken.None);

        await broker.PublishAsync(Message("payments.settled"), CancellationToken.None);
        await Task.Delay(200);

        lock (received) { received.Should().BeEmpty(); }
    }

    [Fact]
    public async Task Filters_by_tenant_so_a_subscriber_never_sees_another_tenants_message()
    {
        await using var broker = new InProcessDomainEventBroker();
        var received = new List<DomainEventMessage>();

        await using IAsyncDisposable handle = await broker.SubscribeAsync(
            new DomainEventSubscriptionOptions { TopicFilter = "#", TenantId = "acme" },
            (delivery, _) =>
            {
                lock (received) { received.Add(delivery.Message); }
                return ValueTask.FromResult(DeliveryResult.Ack);
            }, CancellationToken.None);

        await broker.PublishAsync(Message("orders.placed", tenantId: "globex"), CancellationToken.None);
        await Task.Delay(200);

        lock (received) { received.Should().BeEmpty(); }
    }

    [Fact]
    public async Task Filters_by_correlation_key()
    {
        await using var broker = new InProcessDomainEventBroker();

        IReadOnlyList<DomainEventMessage> received = await CollectAsync(
            broker,
            new DomainEventSubscriptionOptions { TopicFilter = "#", CorrelationKey = "ORD-1" },
            async () =>
            {
                await broker.PublishAsync(Message("orders.placed", correlationKey: "ORD-2"), CancellationToken.None);
                await broker.PublishAsync(Message("orders.placed", correlationKey: "ORD-1"), CancellationToken.None);
            },
            expected: 1);

        received.Should().ContainSingle().Which.CorrelationKey.Should().Be("ORD-1");
    }

    [Fact]
    public async Task Every_matching_subscription_receives_its_own_copy()
    {
        await using var broker = new InProcessDomainEventBroker();
        var first = new List<string>();
        var second = new List<string>();
        var signal = new SemaphoreSlim(0);

        await using IAsyncDisposable a = await broker.SubscribeAsync(
            new DomainEventSubscriptionOptions { TopicFilter = "orders.#" },
            (d, _) => { lock (first) { first.Add(d.Message.MessageId); } signal.Release(); return ValueTask.FromResult(DeliveryResult.Ack); },
            CancellationToken.None);

        await using IAsyncDisposable b = await broker.SubscribeAsync(
            new DomainEventSubscriptionOptions { TopicFilter = "#" },
            (d, _) => { lock (second) { second.Add(d.Message.MessageId); } signal.Release(); return ValueTask.FromResult(DeliveryResult.Ack); },
            CancellationToken.None);

        await broker.PublishAsync(Message("orders.placed"), CancellationToken.None);

        await signal.WaitAsync(TimeSpan.FromSeconds(5));
        await signal.WaitAsync(TimeSpan.FromSeconds(5));

        lock (first) { first.Should().HaveCount(1); }
        lock (second) { second.Should().HaveCount(1); }
    }

    [Fact]
    public async Task A_throwing_handler_does_not_kill_the_subscription()
    {
        await using var broker = new InProcessDomainEventBroker();
        var received = new List<string>();
        var signal = new SemaphoreSlim(0);
        bool thrown = false;

        await using IAsyncDisposable handle = await broker.SubscribeAsync(
            new DomainEventSubscriptionOptions { TopicFilter = "#" },
            (d, _) =>
            {
                if (!thrown)
                {
                    thrown = true;
                    throw new InvalidOperationException("boom");
                }

                lock (received) { received.Add(d.Message.MessageId); }
                signal.Release();
                return ValueTask.FromResult(DeliveryResult.Ack);
            }, CancellationToken.None);

        await broker.PublishAsync(Message("orders.first"), CancellationToken.None);
        await broker.PublishAsync(Message("orders.second"), CancellationToken.None);

        (await signal.WaitAsync(TimeSpan.FromSeconds(5))).Should()
            .BeTrue("a handler that throws must not silently swallow every later message");

        lock (received) { received.Should().HaveCount(1); }
    }

    [Fact]
    public async Task Unsubscribing_stops_delivery()
    {
        await using var broker = new InProcessDomainEventBroker();
        var received = new List<string>();

        IAsyncDisposable handle = await broker.SubscribeAsync(
            new DomainEventSubscriptionOptions { TopicFilter = "#" },
            (d, _) => { lock (received) { received.Add(d.Message.MessageId); } return ValueTask.FromResult(DeliveryResult.Ack); },
            CancellationToken.None);

        await handle.DisposeAsync();
        broker.SubscriptionCount.Should().Be(0);

        await broker.PublishAsync(Message("orders.placed"), CancellationToken.None);
        await Task.Delay(200);

        lock (received) { received.Should().BeEmpty(); }
    }

    [Fact]
    public async Task Refuses_to_publish_a_distributed_message_rather_than_pretending()
    {
        await using var broker = new InProcessDomainEventBroker();

        DomainEventMessage message = Message("orders.placed") with { Scope = DeliveryScope.Distributed };

        Func<Task> publish = () => broker.PublishAsync(message, CancellationToken.None).AsTask();

        await publish.Should().ThrowAsync<NotSupportedException>(
            "delivering locally would look like success while the other service never hears about it");
    }

    [Fact]
    public async Task Refuses_a_distributed_subscription()
    {
        await using var broker = new InProcessDomainEventBroker();

        Func<Task> subscribe = () => broker.SubscribeAsync(
            new DomainEventSubscriptionOptions { TopicFilter = "#", Scope = DeliveryScope.Distributed },
            (_, _) => ValueTask.FromResult(DeliveryResult.Ack),
            CancellationToken.None).AsTask();

        await subscribe.Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public async Task Rejects_a_malformed_topic_filter_at_subscribe_time()
    {
        await using var broker = new InProcessDomainEventBroker();

        Func<Task> subscribe = () => broker.SubscribeAsync(
            new DomainEventSubscriptionOptions { TopicFilter = "orders.#.shipped" },
            (_, _) => ValueTask.FromResult(DeliveryResult.Ack),
            CancellationToken.None).AsTask();

        await subscribe.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Reports_in_process_capabilities()
    {
        await using var broker = new InProcessDomainEventBroker();
        broker.Capabilities.SupportsDistributed.Should().BeFalse();
        broker.Capabilities.SupportsReplay.Should().BeFalse();
    }
}

public class EventSubscriptionStoreTests
{
    private static DomainEventMessage Message(string topic, string? correlationKey = null, string? tenantId = null)
        => new()
        {
            MessageId = IdGenerator.NewId("msg"),
            Topic = topic,
            PayloadJson = """{"orderId":"ORD-1"}""",
            CorrelationKey = correlationKey,
            TenantId = tenantId
        };

    private static DomainEventSubscription Wait(string topicFilter, string instanceId = "i1", string executorId = "wait",
        string? correlationKey = null, DateTimeOffset? expiresAt = null)
        => new()
        {
            SubscriptionId = IdGenerator.NewId("sub"),
            Kind = DomainSubscriptionKind.Wait,
            TopicFilter = topicFilter,
            InstanceId = instanceId,
            ExecutorId = executorId,
            CorrelationKey = correlationKey,
            ExpiresAt = expiresAt
        };

    [Fact]
    public async Task Matches_on_topic_tenant_and_correlation()
    {
        var store = new InMemoryDomainEventSubscriptionStore();
        DomainEventSubscription registered = await store.RegisterAsync(
            Wait("orders.#", correlationKey: "ORD-1") with { TenantId = "acme" }, default);

        (await store.MatchAsync(Message("orders.placed", "ORD-1", "acme"), default))
            .Should().ContainSingle().Which.SubscriptionId.Should().Be(registered.SubscriptionId);

        (await store.MatchAsync(Message("orders.placed", "ORD-2", "acme"), default)).Should().BeEmpty();
        (await store.MatchAsync(Message("orders.placed", "ORD-1", "globex"), default)).Should().BeEmpty();
        (await store.MatchAsync(Message("payments.settled", "ORD-1", "acme"), default)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_subscription_without_a_tenant_sees_every_tenant()
    {
        var store = new InMemoryDomainEventSubscriptionStore();
        await store.RegisterAsync(Wait("orders.#"), default);

        (await store.MatchAsync(Message("orders.placed", tenantId: "acme"), default)).Should().HaveCount(1);
    }

    [Fact]
    public async Task Only_one_caller_wins_a_delivery()
    {
        var store = new InMemoryDomainEventSubscriptionStore();
        DomainEventSubscription subscription = await store.RegisterAsync(Wait("orders.#"), default);
        DomainEventMessage message = Message("orders.placed");

        bool[] results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
            Task.Run(async () => await store.TryDeliverAsync(subscription.SubscriptionId, message, default))));

        results.Count(won => won).Should()
            .Be(1, "a compare-and-set is what stops one message resuming one instance twice");
    }

    [Fact]
    public async Task A_delivered_wait_stops_matching()
    {
        var store = new InMemoryDomainEventSubscriptionStore();
        DomainEventSubscription subscription = await store.RegisterAsync(Wait("orders.#"), default);

        await store.TryDeliverAsync(subscription.SubscriptionId, Message("orders.placed"), default);

        (await store.MatchAsync(Message("orders.placed"), default)).Should().BeEmpty();
    }

    [Fact]
    public async Task Re_registering_the_same_wait_preserves_a_recorded_delivery()
    {
        var store = new InMemoryDomainEventSubscriptionStore();
        DomainEventSubscription first = await store.RegisterAsync(Wait("orders.#"), default);
        await store.TryDeliverAsync(first.SubscriptionId, Message("orders.placed"), default);

        // A resumed instance replays its executor, which re-registers. It must find its payload.
        DomainEventSubscription second = await store.RegisterAsync(Wait("orders.#"), default);

        second.SubscriptionId.Should().Be(first.SubscriptionId);
        second.DeliveredPayloadJson.Should().NotBeNull();
    }

    [Fact]
    public async Task Claims_expired_waits_once()
    {
        var store = new InMemoryDomainEventSubscriptionStore();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await store.RegisterAsync(Wait("orders.#", expiresAt: now.AddMinutes(-1)), default);

        (await store.ClaimExpiredAsync(now, 10, default)).Should().HaveCount(1);
        (await store.ClaimExpiredAsync(now, 10, default)).Should()
            .BeEmpty("a second sweeper must not expire the same instance again");
    }

    [Fact]
    public async Task Does_not_claim_a_wait_that_has_not_expired()
    {
        var store = new InMemoryDomainEventSubscriptionStore();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await store.RegisterAsync(Wait("orders.#", expiresAt: now.AddMinutes(5)), default);

        (await store.ClaimExpiredAsync(now, 10, default)).Should().BeEmpty();
    }

    [Fact]
    public async Task Removes_every_wait_belonging_to_an_instance()
    {
        var store = new InMemoryDomainEventSubscriptionStore();
        await store.RegisterAsync(Wait("orders.#", "i1", "a"), default);
        await store.RegisterAsync(Wait("orders.#", "i1", "b"), default);
        await store.RegisterAsync(Wait("orders.#", "i2", "a"), default);

        await store.RemoveForInstanceAsync("i1", default);

        (await store.QueryAsync(new DomainSubscriptionQuery(), default)).Should().ContainSingle()
            .Which.InstanceId.Should().Be("i2");
    }

    [Fact]
    public async Task A_wait_must_name_its_instance_and_executor()
    {
        var store = new InMemoryDomainEventSubscriptionStore();

        Func<Task> register = () => store.RegisterAsync(new DomainEventSubscription
        {
            SubscriptionId = "s1",
            Kind = DomainSubscriptionKind.Wait,
            TopicFilter = "orders.#"
        }, default).AsTask();

        await register.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Scope_narrows_matching()
    {
        var store = new InMemoryDomainEventSubscriptionStore();
        await store.RegisterAsync(Wait("orders.#") with { Scope = DeliveryScope.Distributed }, default);

        (await store.MatchAsync(Message("orders.placed"), default)).Should()
            .BeEmpty("the message is Local and the subscription asked for Distributed");
    }
}

using Abacus.Run.Abstractions;
using Abacus.Run.Core;
using Abacus.Run.Middlewares;
using Abacus.Run.Persistence;
using FluentAssertions;
using Xunit;

namespace Abacus.Run.UnitTests;

public class NotificationPolicyTests
{
    private static NotificationPolicy At(NotificationLevel level) => new() { Level = level };

    [Theory]
    // Never suppressible at any level: the stream must be able to close, and control-plane and
    // broker facts are not the workflow's to hide.
    [InlineData(WorkflowEventTypes.WorkflowStarted)]
    [InlineData(WorkflowEventTypes.WorkflowOutput)]
    [InlineData(WorkflowEventTypes.WorkflowWarning)]
    [InlineData(WorkflowEventTypes.WorkflowTerminated)]
    [InlineData(WorkflowEventTypes.RequestPending)]
    [InlineData(WorkflowEventTypes.ApprovalRequested)]
    [InlineData(WorkflowEventTypes.InstanceCancelled)]
    [InlineData("event.delivered")]
    public void Minimal_still_emits_everything_that_must_never_be_suppressed(string eventType)
        => At(NotificationLevel.Minimal).ShouldEmit(eventType, null).Should().BeTrue();

    [Theory]
    [InlineData(WorkflowEventTypes.SuperstepStarted)]
    [InlineData(WorkflowEventTypes.SuperstepCompleted)]
    [InlineData(WorkflowEventTypes.ExecutorInvoked)]
    [InlineData(WorkflowEventTypes.ExecutorCompleted)]
    [InlineData(WorkflowEventTypes.LlmDelta)]
    [InlineData(WorkflowEventTypes.LlmCompleted)]
    [InlineData("custom.anything")]
    public void Minimal_suppresses_progress_and_node_traffic(string eventType)
        => At(NotificationLevel.Minimal).ShouldEmit(eventType, "node").Should().BeFalse();

    [Theory]
    [InlineData(WorkflowEventTypes.SuperstepStarted, true)]
    [InlineData(WorkflowEventTypes.SuperstepCompleted, true)]
    [InlineData(WorkflowEventTypes.ExecutorInvoked, false)]
    [InlineData(WorkflowEventTypes.LlmDelta, false)]
    [InlineData("custom.anything", false)]
    public void Lifecycle_keeps_superstep_boundaries_and_drops_node_chatter(string eventType, bool expected)
        => At(NotificationLevel.Lifecycle).ShouldEmit(eventType, "node").Should().Be(expected);

    [Theory]
    [InlineData(WorkflowEventTypes.ExecutorInvoked)]
    [InlineData(WorkflowEventTypes.LlmDelta)]
    [InlineData(WorkflowEventTypes.LlmCompleted)]
    [InlineData("custom.anything")]
    public void Standard_emits_everything(string eventType)
        => At(NotificationLevel.Standard).ShouldEmit(eventType, "node").Should().BeTrue();

    [Fact]
    public void The_default_policy_emits_everything()
        => NotificationPolicy.Default.ShouldEmit(WorkflowEventTypes.ExecutorInvoked, "node").Should().BeTrue();

    [Fact]
    public void ByNode_narrows_one_executor_without_touching_its_siblings()
    {
        var policy = new NotificationPolicy
        {
            Level = NotificationLevel.Standard,
            ByNode = new Dictionary<string, NotificationLevel>(StringComparer.Ordinal)
            {
                ["chatty"] = NotificationLevel.Minimal
            }
        };

        policy.ShouldEmit(WorkflowEventTypes.ExecutorInvoked, "chatty").Should().BeFalse();
        policy.ShouldEmit(WorkflowEventTypes.ExecutorInvoked, "interesting").Should().BeTrue();
    }

    [Fact]
    public void ByNode_can_raise_a_node_above_a_quiet_workflow()
    {
        var policy = new NotificationPolicy
        {
            Level = NotificationLevel.Minimal,
            ByNode = new Dictionary<string, NotificationLevel>(StringComparer.Ordinal)
            {
                ["interesting"] = NotificationLevel.Standard
            }
        };

        policy.ShouldEmit(WorkflowEventTypes.ExecutorCompleted, "interesting").Should().BeTrue();
        policy.ShouldEmit(WorkflowEventTypes.ExecutorCompleted, "other").Should().BeFalse();
    }

    [Fact]
    public void Streaming_is_on_by_default()
    {
        NotificationPolicy.Default.StreamEvents.Should().BeTrue();
        NotificationPolicy.Default.IsLogOnly.Should().BeFalse();
    }

    [Fact]
    public void Turning_streaming_off_leaves_the_event_logged()
    {
        var policy = new NotificationPolicy { StreamEvents = false };

        policy.DeliveryFor(EventDeliveryMode.StreamAndLog).Should()
            .Be(EventDeliveryMode.LogOnly, "the log is unconditional; only the stream is switchable");

        policy.IsLogOnly.Should().BeTrue();
    }

    /// <summary>
    /// The invariant the whole feature rests on: there is no combination of policy settings that
    /// stops an ordinary event being written to the durable log.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void No_policy_can_stop_an_ordinary_event_being_logged(bool streamEvents)
    {
        var policy = new NotificationPolicy
        {
            StreamEvents = streamEvents,
            Level = NotificationLevel.Minimal
        };

        policy.DeliveryFor(EventDeliveryMode.StreamAndLog).Should()
            .NotBe(EventDeliveryMode.StreamOnly, "a workflow cannot opt out of its own event record");
    }

    [Fact]
    public void A_stream_only_event_stays_stream_only_when_streaming_is_off()
    {
        var policy = new NotificationPolicy { StreamEvents = false };

        policy.DeliveryFor(EventDeliveryMode.StreamOnly).Should()
            .Be(EventDeliveryMode.StreamOnly,
                "a stream-only event never had a durable record to lose; it simply goes nowhere");
    }

    [Fact]
    public void A_streaming_workflow_leaves_delivery_alone()
    {
        var policy = new NotificationPolicy { StreamEvents = true };

        policy.DeliveryFor(EventDeliveryMode.StreamAndLog).Should().Be(EventDeliveryMode.StreamAndLog);
        policy.DeliveryFor(EventDeliveryMode.StreamOnly).Should().Be(EventDeliveryMode.StreamOnly);
    }

    [Fact]
    public void Declared_names_are_validated_at_composition_time()
    {
        var policy = new NotificationPolicy { Emits = ["ok.name", "bad name"] };

        Action validate = () => policy.Validate("my-workflow");

        validate.Should().Throw<InvalidOperationException>().WithMessage("*my-workflow*");
    }
}

public class NodeNotifierTests
{
    private static (NodeNotifier Notifier, InMemoryEventStore Store, EventSequencer Sequencer) Build(
        NotificationPolicy? policy = null)
    {
        var store = new InMemoryEventStore();
        var sequencer = new EventSequencer();
        var notifier = new NodeNotifier(
            "i1", "acme", "node-a", new DirectEventSink(store), sequencer,
            policy ?? NotificationPolicy.Default, () => 3, TimeProvider.System);

        return (notifier, store, sequencer);
    }

    private static async Task<IReadOnlyList<EventEnvelope>> ReadAsync(InMemoryEventStore store)
    {
        var events = new List<EventEnvelope>();
        await foreach (EventEnvelope e in store.ReadAsync("i1", 0, default))
        {
            events.Add(e);
        }
        return events;
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("has space")]
    [InlineData(".leading")]
    [InlineData("trailing.")]
    [InlineData("double..dot")]
    public void A_malformed_name_throws_rather_than_emitting_something_unmatchable(string name)
    {
        (NodeNotifier notifier, _, _) = Build();

        Func<Task> notify = () => notifier.NotifyAsync(name, new { }, default).AsTask();

        notify.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task A_custom_event_is_always_prefixed()
    {
        (NodeNotifier notifier, InMemoryEventStore store, _) = Build();

        await notifier.NotifyAsync("documents.scanned", new { count = 3 }, default);

        IReadOnlyList<EventEnvelope> events = await ReadAsync(store);
        events.Should().ContainSingle().Which.EventType.Should()
            .Be("custom.documents.scanned", "the prefix is what makes collision with a framework event impossible");
    }

    [Fact]
    public async Task A_custom_event_carries_the_executor_tenant_and_superstep()
    {
        (NodeNotifier notifier, InMemoryEventStore store, _) = Build();

        await notifier.NotifyAsync("thing.happened", new { }, default);

        EventEnvelope envelope = (await ReadAsync(store)).Single();
        envelope.ExecutorId.Should().Be("node-a");
        envelope.TenantId.Should().Be("acme");
        envelope.Superstep.Should().Be(3);
    }

    [Fact]
    public async Task Custom_events_draw_from_the_same_sequence_as_lifecycle_events()
    {
        (NodeNotifier notifier, InMemoryEventStore store, EventSequencer sequencer) = Build();
        var sink = new DirectEventSink(store);

        // Interleave: a lifecycle publish, a notify, another lifecycle publish.
        await sink.PublishAsync(EventFactory.Create("i1", sequencer.Next("i1"), "workflow.started", new { }), default);
        await notifier.NotifyAsync("midway", new { }, default);
        await sink.PublishAsync(EventFactory.Create("i1", sequencer.Next("i1"), "workflow.output", new { }), default);

        long[] sequences = (await ReadAsync(store)).Select(e => e.Sequence).ToArray();

        sequences.Should().Equal([1, 2, 3], "a parallel numbering would leave a consumer to reconcile two streams");
    }

    [Fact]
    public async Task A_suppressed_event_consumes_no_sequence_number()
    {
        (NodeNotifier notifier, InMemoryEventStore store, EventSequencer sequencer) =
            Build(new NotificationPolicy { Level = NotificationLevel.Minimal });

        await notifier.NotifyAsync("suppressed", new { }, default);

        sequencer.Peek("i1").Should()
            .Be(0, "a hole in the gapless sequence would make Last-Event-ID catch-up wait forever");
        (await ReadAsync(store)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_log_only_workflow_still_writes_a_full_durable_record()
    {
        var store = new InMemoryEventStore();
        using var bus = new Abacus.Run.Api.InMemoryEventBus();
        var sequencer = new EventSequencer();
        var notifier = new NodeNotifier(
            "i1", null, "node-a", new DirectEventSink(store, bus), sequencer,
            new NotificationPolicy { StreamEvents = false },
            () => 1, TimeProvider.System, "my-workflow");

        await notifier.NotifyAsync("thing.happened", new { }, default);

        var events = new List<EventEnvelope>();
        await foreach (EventEnvelope e in store.ReadAsync("i1", 0, default))
        {
            events.Add(e);
        }

        EventEnvelope logged = events.Should().ContainSingle().Subject;
        logged.Delivery.Should().Be(EventDeliveryMode.LogOnly);
        logged.Sequence.Should().Be(1, "log-only events are still sequenced; only the stream is skipped");
        logged.WorkflowName.Should().Be("my-workflow");
    }

    [Fact]
    public async Task A_reserved_type_can_only_be_emitted_through_the_framework_path()
    {
        (NodeNotifier notifier, InMemoryEventStore store, _) = Build();

        await notifier.EmitReservedAsync(WorkflowEventTypes.LlmCompleted, new { model = "x" }, false, default);

        (await ReadAsync(store)).Single().EventType.Should().Be(WorkflowEventTypes.LlmCompleted);
    }

    [Fact]
    public async Task An_unknown_reserved_type_is_refused()
    {
        (NodeNotifier notifier, _, _) = Build();

        Func<Task> emit = () => notifier.EmitReservedAsync("workflow.forged", new { }, false, default).AsTask();

        await emit.Should().ThrowAsync<ArgumentException>(
            "a forged framework event must not reach a subscriber looking authentic");
    }

    [Fact]
    public async Task A_stream_only_event_reaches_the_bus_but_not_the_store()
    {
        var store = new InMemoryEventStore();
        using var bus = new Abacus.Run.Api.InMemoryEventBus();
        var sequencer = new EventSequencer();
        var notifier = new NodeNotifier(
            "i1", null, "node-a", new DirectEventSink(store, bus), sequencer,
            NotificationPolicy.Default, () => 1, TimeProvider.System);

        var received = new List<EventEnvelope>();
        using var subscribed = new CancellationTokenSource();
        Task pump = Task.Run(async () =>
        {
            await foreach (EventEnvelope e in bus.SubscribeAsync("i1", subscribed.Token))
            {
                lock (received) { received.Add(e); }
            }
        });

        while (bus.SubscriberCount("i1") == 0)
        {
            await Task.Delay(10);
        }

        await notifier.EmitReservedAsync(WorkflowEventTypes.LlmDelta, new { delta = "tok" }, true, default);
        await Task.Delay(150);
        await subscribed.CancelAsync();
        try { await pump; } catch (OperationCanceledException) { }

        lock (received)
        {
            received.Should().ContainSingle().Which.IsStreamOnly.Should().BeTrue();
        }

        (await ReadAsync(store)).Should()
            .BeEmpty("a token already rendered has no replay value and must not be stored");

        sequencer.Peek("i1").Should().Be(0, "a transient event takes no sequence number");
    }
}

public class ModelPricingTests
{
    private static readonly Dictionary<string, ModelPrice> Prices = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude-sonnet-5"] = new ModelPrice(InputPerMillion: 3m, OutputPerMillion: 15m)
    };

    [Fact]
    public void Computes_cost_from_per_million_rates()
    {
        var pricing = new ModelPricing(Prices);

        decimal? cost = pricing.CostOf("claude-sonnet-5", 1_000_000, 1_000_000);

        cost.Should().Be(18m);
    }

    [Fact]
    public void Model_lookup_is_case_insensitive()
        => new ModelPricing(Prices).CostOf("CLAUDE-SONNET-5", 0, 0).Should().Be(0m);

    [Fact]
    public void An_unpriced_model_yields_null_not_zero()
        => new ModelPricing(Prices).CostOf("some-other-model", 1000, 1000).Should()
            .BeNull("a zero would average into the drift baseline as a real observation");

    [Fact]
    public void A_host_with_no_price_table_prices_nothing()
        => new ModelPricing().CostOf("claude-sonnet-5", 1000, 1000).Should().BeNull();
}

public class CostDriftTests
{
    private static DriftBaseline Baseline(double? costMean, double? costStdDev) => new()
    {
        Key = new DriftKey("w", "e", "m", null),
        SampleCount = 500,
        LatencyMean = 100, LatencyStdDev = 10,
        OutputTokenMean = 100, OutputTokenStdDev = 10,
        RefusalRate = 0, SchemaFailureRate = 0,
        CostMean = costMean, CostStdDev = costStdDev
    };

    private static DriftSample Sample(decimal? cost) => new()
    {
        LatencyMs = 100,
        OutputTokens = 100,
        CostUsd = cost,
        At = DateTimeOffset.UtcNow
    };

    [Fact]
    public void A_cost_spike_breaches()
    {
        IReadOnlyList<DriftBreach> breaches =
            DriftDetector.Evaluate(Baseline(0.01, 0.001), Sample(0.05m), sigma: 3);

        breaches.Should().Contain(b => b.Signal == "cost");
    }

    [Fact]
    public void A_normal_cost_does_not_breach()
        => DriftDetector.Evaluate(Baseline(0.01, 0.001), Sample(0.0105m), sigma: 3)
            .Should().NotContain(b => b.Signal == "cost");

    [Fact]
    public void An_unpriced_sample_cannot_breach()
        => DriftDetector.Evaluate(Baseline(0.01, 0.001), Sample(null), sigma: 3)
            .Should().NotContain(b => b.Signal == "cost");

    [Fact]
    public void An_unpriced_baseline_cannot_be_breached()
        => DriftDetector.Evaluate(Baseline(null, null), Sample(0.05m), sigma: 3)
            .Should().NotContain(b => b.Signal == "cost",
                "comparing a priced call against an unpriced baseline says nothing about the model");
    }

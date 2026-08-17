using Abacus.Run.Notifications;
using Abacus.Run.Abstractions;
using Abacus.Run.Api;
using Abacus.Run.Core;
using Abacus.Run.Dispatch;
using Abacus.Run.Persistence;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Abacus.Run.UnitTests;

public class ConcurrencyLimiterTests
{
    [Fact]
    public void Grants_slots_up_to_the_ceiling()
    {
        var limiter = new ConcurrencyLimiter(2);

        limiter.TryTake("wf", out IDisposable? a).Should().BeTrue();
        limiter.TryTake("wf", out IDisposable? b).Should().BeTrue();
        limiter.TryTake("wf", out IDisposable? c).Should().BeFalse();

        limiter.InFlight.Should().Be(2);
        limiter.AvailableCapacity.Should().Be(0);

        a!.Dispose();
        limiter.AvailableCapacity.Should().Be(1);
        limiter.TryTake("wf", out _).Should().BeTrue();

        b!.Dispose();
        c.Should().BeNull();
    }

    [Fact]
    public void A_saturated_workflow_does_not_starve_others()
    {
        var limiter = new ConcurrencyLimiter(10, new Dictionary<string, int> { ["hot"] = 1 });

        limiter.TryTake("hot", out IDisposable? first).Should().BeTrue();
        limiter.TryTake("hot", out _).Should().BeFalse();

        limiter.TryTake("cool", out _).Should().BeTrue("a per-workflow ceiling must not consume global capacity");
        limiter.InFlight.Should().Be(2);

        first!.Dispose();
        limiter.TryTake("hot", out _).Should().BeTrue();
    }

    [Fact]
    public void Rejecting_a_per_workflow_take_releases_the_global_slot()
    {
        var limiter = new ConcurrencyLimiter(5, new Dictionary<string, int> { ["hot"] = 1 });

        limiter.TryTake("hot", out IDisposable? held).Should().BeTrue();
        limiter.TryTake("hot", out _).Should().BeFalse("the per-workflow ceiling is reached");

        // The rejected take must hand back the global slot it provisionally acquired.
        limiter.InFlight.Should().Be(1);
        limiter.AvailableCapacity.Should().Be(4);

        held!.Dispose();
        limiter.AvailableCapacity.Should().Be(5);
    }

    [Fact]
    public void Double_dispose_is_harmless()
    {
        var limiter = new ConcurrencyLimiter(1);
        limiter.TryTake("wf", out IDisposable? slot);

        slot!.Dispose();
        slot.Dispose();

        limiter.InFlight.Should().Be(0);
        limiter.AvailableCapacity.Should().Be(1);
    }

    [Fact]
    public async Task Wait_for_idle_returns_once_slots_are_released()
    {
        var limiter = new ConcurrencyLimiter(1);
        limiter.TryTake("wf", out IDisposable? slot);

        Task idle = limiter.WaitForIdleAsync(default);
        idle.IsCompleted.Should().BeFalse();

        slot!.Dispose();
        await idle.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Concurrent_takes_never_exceed_the_ceiling()
    {
        var limiter = new ConcurrencyLimiter(5);
        var slots = new System.Collections.Concurrent.ConcurrentBag<IDisposable>();

        await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(() =>
        {
            if (limiter.TryTake("wf", out IDisposable? slot))
            {
                slots.Add(slot!);
            }
        })));

        slots.Should().HaveCount(5);
        limiter.InFlight.Should().Be(5);
    }
}

public class DispatcherServiceTests
{
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.Parse("2026-07-30T10:00:00Z"));
    private readonly InMemoryInstanceStore _instances;

    public DispatcherServiceTests() => _instances = new InMemoryInstanceStore(_clock);

    private DispatcherService Build(
        IWorkflowRunnerFactory runners, ConcurrencyLimiter? limiter = null, WorkflowHostOptions? options = null)
        => new(
            _instances, runners, limiter ?? new ConcurrencyLimiter(10),
            Options.Create(options ?? new WorkflowHostOptions { ReplicaId = "replica-1" }),
            NullLogger<DispatcherService>.Instance, _clock);

    [Fact]
    public async Task Claims_and_dispatches_a_pending_instance()
    {
        await _instances.CreateAsync(TestFactory.CreateRequest("i1"), default);
        var runners = new RecordingRunnerFactory();

        DispatcherService dispatcher = Build(runners);
        int claimed = await dispatcher.PollOnceAsync(default);

        claimed.Should().Be(1);
        (await _instances.GetAsync("i1", default))!.LeaseOwner.Should().Be("replica-1");
    }

    [Fact]
    public async Task Claims_nothing_when_the_queue_is_empty()
        => (await Build(new RecordingRunnerFactory()).PollOnceAsync(default)).Should().Be(0);

    [Fact]
    public async Task Claims_nothing_once_capacity_is_exhausted()
    {
        await _instances.CreateAsync(TestFactory.CreateRequest("i1"), default);

        var limiter = new ConcurrencyLimiter(1);
        limiter.TryTake("wf", out _);

        (await Build(new RecordingRunnerFactory(), limiter).PollOnceAsync(default)).Should().Be(0);
    }

    [Fact]
    public async Task Claim_batches_are_bounded_by_configuration()
    {
        for (int i = 0; i < 20; i++)
        {
            await _instances.CreateAsync(TestFactory.CreateRequest($"i{i}"), default);
        }

        DispatcherService dispatcher = Build(new RecordingRunnerFactory(), new ConcurrencyLimiter(100),
            new WorkflowHostOptions { ReplicaId = "r1", ClaimBatchSize = 3 });

        (await dispatcher.PollOnceAsync(default)).Should().Be(3);
    }

    [Fact]
    public async Task Stopping_claiming_halts_dispatch_for_drain()
    {
        await _instances.CreateAsync(TestFactory.CreateRequest("i1"), default);

        DispatcherService dispatcher = Build(new RecordingRunnerFactory());
        dispatcher.StopClaiming();

        (await dispatcher.PollOnceAsync(default)).Should().Be(0);
        (await _instances.GetAsync("i1", default))!.LeaseOwner.Should().BeNull();
    }

    [Fact]
    public void Replica_id_falls_back_to_the_machine_name()
    {
        DispatcherService dispatcher = new(
            _instances, new RecordingRunnerFactory(), new ConcurrencyLimiter(1),
            Options.Create(new WorkflowHostOptions()), NullLogger<DispatcherService>.Instance, _clock);

        dispatcher.ReplicaId.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_single_poll_dispatches_each_claimed_instance_exactly_once()
    {
        for (int i = 0; i < 50; i++)
        {
            await _instances.CreateAsync(TestFactory.CreateRequest($"i{i:D2}"), default);
        }

        var factory = new RecordingRunnerFactory();
        DispatcherService dispatcher = Build(factory, new ConcurrencyLimiter(100),
            new WorkflowHostOptions { ReplicaId = "replica-1", ClaimBatchSize = 50 });

        int claimed = await dispatcher.PollOnceAsync(default);

        claimed.Should().Be(50);
        factory.Seen.Should().OnlyHaveUniqueItems("a claimed instance is handed to exactly one runner");
    }

    [Fact]
    public async Task An_instance_leased_by_another_replica_is_not_claimed()
    {
        await _instances.CreateAsync(TestFactory.CreateRequest("i1"), default);

        // Simulate a peer holding the lease. Disjointness of concurrent claims themselves is a store
        // invariant, covered by InstanceStoreTests.Concurrent_claimers_receive_disjoint_sets.
        await _instances.ClaimAsync("replica-2", 1, TimeSpan.FromMinutes(5), _clock.GetUtcNow(), default);

        var factory = new RecordingRunnerFactory();
        DispatcherService dispatcher = Build(factory);

        (await dispatcher.PollOnceAsync(default)).Should().Be(0);
        factory.Seen.Should().BeEmpty();
        (await _instances.GetAsync("i1", default))!.LeaseOwner.Should().Be("replica-2");
    }

    [Fact]
    public async Task The_lease_is_released_after_the_run_completes()
    {
        WorkflowInstance instance = await _instances.CreateAsync(TestFactory.CreateRequest("i1"), default);
        var limiter = new ConcurrencyLimiter(1);
        limiter.TryTake("wf", out IDisposable? slot);

        DispatcherService dispatcher = Build(new RecordingRunnerFactory(), limiter);
        await dispatcher.RunLeasedAsync(instance, slot!, default);

        (await _instances.GetAsync("i1", default))!.LeaseOwner.Should().BeNull();
        limiter.InFlight.Should().Be(0);
    }

    [Fact]
    public async Task A_failing_run_still_releases_the_lease_and_the_slot()
    {
        WorkflowInstance instance = await _instances.CreateAsync(TestFactory.CreateRequest("i1"), default);
        var limiter = new ConcurrencyLimiter(1);
        limiter.TryTake("wf", out IDisposable? slot);

        DispatcherService dispatcher = Build(new ThrowingRunnerFactory(), limiter);

        Func<Task> act = async () => await dispatcher.RunLeasedAsync(instance, slot!, default);

        await act.Should().NotThrowAsync("an unhandled run failure must not take the dispatcher down");
        limiter.InFlight.Should().Be(0);
        (await _instances.GetAsync("i1", default))!.LeaseOwner.Should().BeNull();
    }

    private sealed class RecordingRunnerFactory : IWorkflowRunnerFactory
    {
        public System.Collections.Concurrent.ConcurrentBag<string> Seen { get; } = [];

        public WorkflowRunner Create(WorkflowInstance instance)
        {
            Seen.Add(instance.InstanceId);
            return BuildInert();
        }

        private static WorkflowRunner BuildInert() => new(new WorkflowRunnerDependencies
        {
            Registry = new WorkflowRegistry([]),
            Instances = new InMemoryInstanceStore(),
            Events = new DirectNotificationSink(new InMemoryEventStore()),
            Sequencer = new NotificationSequencer(),
            Pipelines = new MiddlewarePipelineFactory(),
            Checkpoints = new OverflowCheckpointStore()
        });
    }

    private sealed class ThrowingRunnerFactory : IWorkflowRunnerFactory
    {
        public WorkflowRunner Create(WorkflowInstance instance) => throw new InvalidOperationException("factory blew up");
    }
}

public class InMemoryEventBusTests
{
    [Fact]
    public async Task Subscribers_receive_published_events()
    {
        using var bus = new InMemoryNotificationBus();
        var received = new List<EventEnvelope>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        Task consumer = Task.Run(async () =>
        {
            await foreach (EventEnvelope envelope in bus.SubscribeAsync("i1", cts.Token))
            {
                received.Add(envelope);
                if (received.Count == 2)
                {
                    return;
                }
            }
        }, cts.Token);

        await WaitForSubscriberAsync(bus, "i1");

        await bus.PublishBatchAsync([TestFactory.Event("i1", 1, "a"), TestFactory.Event("i1", 2, "b")], default);
        await consumer.WaitAsync(TimeSpan.FromSeconds(5));

        received.Select(e => e.Sequence).Should().Equal(1, 2);
    }

    [Fact]
    public async Task Events_are_routed_only_to_the_matching_instance()
    {
        using var bus = new InMemoryNotificationBus();
        var received = new List<EventEnvelope>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        Task consumer = Task.Run(async () =>
        {
            await foreach (EventEnvelope envelope in bus.SubscribeAsync("i1", cts.Token))
            {
                received.Add(envelope);
                return;
            }
        }, cts.Token);

        await WaitForSubscriberAsync(bus, "i1");

        await bus.PublishBatchAsync([TestFactory.Event("other", 1, "a")], default);
        await Task.Delay(50);
        received.Should().BeEmpty();

        await bus.PublishBatchAsync([TestFactory.Event("i1", 1, "a")], default);
        await consumer.WaitAsync(TimeSpan.FromSeconds(5));
        received.Should().ContainSingle();
    }

    [Fact]
    public async Task Publishing_with_no_subscribers_is_a_no_op()
    {
        using var bus = new InMemoryNotificationBus();
        Func<Task> act = async () => await bus.PublishBatchAsync([TestFactory.Event("i1", 1, "a")], default);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Unsubscribing_removes_the_subscriber()
    {
        using var bus = new InMemoryNotificationBus();
        using var cts = new CancellationTokenSource();

        Task consumer = Task.Run(async () =>
        {
            try
            {
                await foreach (EventEnvelope _ in bus.SubscribeAsync("i1", cts.Token)) { }
            }
            catch (OperationCanceledException) { }
        });

        await WaitForSubscriberAsync(bus, "i1");
        bus.SubscriberCount("i1").Should().Be(1);

        await cts.CancelAsync();
        try { await consumer.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }

        bus.SubscriberCount("i1").Should().Be(0);
    }

    [Fact]
    public async Task Publish_rejects_null()
    {
        using var bus = new InMemoryNotificationBus();
        await bus.Invoking(b => b.PublishBatchAsync(null!, default).AsTask()).Should().ThrowAsync<ArgumentNullException>();
    }

    private static async Task WaitForSubscriberAsync(InMemoryNotificationBus bus, string instanceId)
    {
        for (int i = 0; i < 100 && bus.SubscriberCount(instanceId) == 0; i++)
        {
            await Task.Delay(10);
        }
    }
}

public class WorkflowHostOptionsTests
{
    [Fact]
    public void Defaults_match_the_documented_configuration()
    {
        var options = new WorkflowHostOptions();

        options.MaxConcurrentInstances.Should().Be(100);
        options.Lease.DurationSeconds.Should().Be(60);
        options.Lease.RenewalSeconds.Should().Be(20);
        options.Drain.GraceSeconds.Should().Be(45);
        options.Checkpoint.Cadence.Should().Be(CheckpointCadence.SuperStep);
        options.Checkpoint.InlineThresholdBytes.Should().Be(256 * 1024);
        options.Retry.MaxAttempts.Should().Be(5);
        options.Sse.HeartbeatSeconds.Should().Be(15);
        options.Retention.CheckpointDays.Should().Be(7);
        options.Retention.InstanceDays.Should().Be(90);
    }

    [Fact]
    public void Lease_renewal_is_well_inside_the_lease_window()
    {
        var lease = new LeaseOptions();
        lease.Renewal.Should().BeLessThan(lease.Duration / 2,
            "renewal must have time to retry before the lease expires");
    }

    [Fact]
    public void Event_retention_outlives_checkpoint_retention()
    {
        var options = new WorkflowHostOptions();
        options.Events.RetentionDays.Should().BeGreaterThan(options.Retention.CheckpointDays,
            "an instance should stay explainable after it stops being resumable");
    }

    [Fact]
    public void Sensitive_headers_are_denied_by_default()
    {
        var logging = new LoggingOptions();
        logging.HeaderDenyList.Should().Contain(["Authorization", "Cookie", "x-api-key"]);
    }

    [Fact]
    public void Timespan_projections_are_derived_from_the_numeric_settings()
    {
        var retry = new RetryOptions { BackoffBaseSeconds = 3, BackoffCapSeconds = 90, MaxLifetimeHours = 2 };

        retry.BackoffBase.Should().Be(TimeSpan.FromSeconds(3));
        retry.BackoffCap.Should().Be(TimeSpan.FromSeconds(90));
        retry.MaxLifetime.Should().Be(TimeSpan.FromHours(2));
    }
}

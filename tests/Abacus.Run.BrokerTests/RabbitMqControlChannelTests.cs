using Abacus.Adapters.Messaging.RabbitMQ;
using Abacus.Run.Abstractions;
using FluentAssertions;
using Xunit;

namespace Abacus.Run.BrokerTests;

/// <summary>
/// Control signalling over a real RabbitMQ. The semantics differ from domain events in ways worth
/// proving rather than assuming: signals are broadcast so every replica hears them, they are scoped
/// to one instance, and a failure to send never becomes the caller's problem.
/// </summary>
[Collection(RabbitMqCollection.Name)]
public class RabbitMqControlChannelTests : IAsyncLifetime
{
    private readonly RabbitMqFixture _rabbit;
    private readonly List<IAsyncDisposable> _disposables = [];

    public RabbitMqControlChannelTests(RabbitMqFixture rabbit) => _rabbit = rabbit;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (IAsyncDisposable disposable in _disposables)
        {
            await disposable.DisposeAsync();
        }
    }

    /// <summary>One channel stands in for one replica; they share the container.</summary>
    private async Task<RabbitMqControlChannel> ReplicaAsync()
    {
        RabbitMqControlChannel channel = await RabbitMqControlChannel.CreateAsync(_rabbit.ConnectionString);
        _disposables.Add(channel);
        return channel;
    }

    private static ControlSignal Cancel(string instanceId, string? reason = "operator asked")
        => new(instanceId, ControlActions.Cancel, reason, DateTimeOffset.UtcNow);

    /// <summary>
    /// Records signals and lets a test wait for them rather than sleep a fixed amount.
    /// </summary>
    private sealed class Recorder
    {
        private readonly List<ControlSignal> _signals = [];
        private readonly SemaphoreSlim _signal = new(0);

        public Func<ControlSignal, Task> Handler => s =>
        {
            lock (_signals) { _signals.Add(s); }
            _signal.Release();
            return Task.CompletedTask;
        };

        public int Count { get { lock (_signals) { return _signals.Count; } } }

        public IReadOnlyList<ControlSignal> Signals
        {
            get { lock (_signals) { return [.. _signals]; } }
        }

        public async Task<bool> WaitAsync(int count = 1, int seconds = 15)
        {
            for (int i = 0; i < count; i++)
            {
                if (!await _signal.WaitAsync(TimeSpan.FromSeconds(seconds)))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>The absence of a signal takes as long to observe as you are willing to wait.</summary>
        public async Task<bool> StaysAtAsync(int expected, int milliseconds = 2000)
        {
            await Task.Delay(milliseconds);
            return Count == expected;
        }
    }

    /// <summary>The defect this exists to close: a cancel must reach the replica running the run.</summary>
    [RequiresDockerFact]
    public async Task A_signal_crosses_from_one_replica_to_another()
    {
        RabbitMqControlChannel publisher = await ReplicaAsync();
        RabbitMqControlChannel owner = await ReplicaAsync();
        string instanceId = $"i-{Guid.NewGuid():N}";
        var recorder = new Recorder();

        _disposables.Add(await owner.SubscribeAsync(instanceId, recorder.Handler, default));
        await Task.Delay(300);

        await publisher.PublishAsync(Cancel(instanceId), default);

        (await recorder.WaitAsync()).Should()
            .BeTrue("cancelling on one replica must stop the run on whichever replica owns it");

        ControlSignal received = recorder.Signals[0];
        received.InstanceId.Should().Be(instanceId);
        received.Action.Should().Be(ControlActions.Cancel);
        received.Reason.Should().Be("operator asked");
    }

    /// <summary>
    /// Broadcast, not competing. Every replica hears it and only the one holding the instance acts —
    /// a shared queue would deliver to exactly one and could easily pick the wrong one.
    /// </summary>
    [RequiresDockerFact]
    public async Task Every_replica_hears_the_signal()
    {
        RabbitMqControlChannel publisher = await ReplicaAsync();
        RabbitMqControlChannel replicaA = await ReplicaAsync();
        RabbitMqControlChannel replicaB = await ReplicaAsync();
        string instanceId = $"i-{Guid.NewGuid():N}";

        var a = new Recorder();
        var b = new Recorder();

        _disposables.Add(await replicaA.SubscribeAsync(instanceId, a.Handler, default));
        _disposables.Add(await replicaB.SubscribeAsync(instanceId, b.Handler, default));
        await Task.Delay(300);

        await publisher.PublishAsync(Cancel(instanceId), default);

        (await a.WaitAsync()).Should().BeTrue();
        (await b.WaitAsync()).Should()
            .BeTrue("delivering to only one replica could deliver to the one that is not running it");
    }

    [RequiresDockerFact]
    public async Task A_signal_for_another_instance_is_not_delivered()
    {
        RabbitMqControlChannel publisher = await ReplicaAsync();
        RabbitMqControlChannel owner = await ReplicaAsync();
        string mine = $"i-{Guid.NewGuid():N}";
        string theirs = $"i-{Guid.NewGuid():N}";
        var recorder = new Recorder();

        _disposables.Add(await owner.SubscribeAsync(mine, recorder.Handler, default));
        await Task.Delay(300);

        await publisher.PublishAsync(Cancel(theirs), default);

        (await recorder.StaysAtAsync(0)).Should()
            .BeTrue("a signal is routed by instance; cancelling one run must not stop another");
    }

    [RequiresDockerFact]
    public async Task Suspend_is_carried_as_faithfully_as_cancel()
    {
        RabbitMqControlChannel publisher = await ReplicaAsync();
        RabbitMqControlChannel owner = await ReplicaAsync();
        string instanceId = $"i-{Guid.NewGuid():N}";
        var recorder = new Recorder();

        _disposables.Add(await owner.SubscribeAsync(instanceId, recorder.Handler, default));
        await Task.Delay(300);

        await publisher.PublishAsync(
            new ControlSignal(instanceId, ControlActions.Suspend, "pause", DateTimeOffset.UtcNow), default);

        (await recorder.WaitAsync()).Should().BeTrue();
        recorder.Signals[0].Action.Should().Be(ControlActions.Suspend);
    }

    [RequiresDockerFact]
    public async Task Unsubscribing_stops_delivery()
    {
        RabbitMqControlChannel publisher = await ReplicaAsync();
        RabbitMqControlChannel owner = await ReplicaAsync();
        string instanceId = $"i-{Guid.NewGuid():N}";
        var recorder = new Recorder();

        IAsyncDisposable handle = await owner.SubscribeAsync(instanceId, recorder.Handler, default);
        await Task.Delay(300);
        await handle.DisposeAsync();

        await publisher.PublishAsync(Cancel(instanceId), default);

        (await recorder.StaysAtAsync(0)).Should()
            .BeTrue("a replica that finished the run should not still be receiving instructions for it");
    }

    /// <summary>
    /// A signal sent with nobody listening is dropped rather than queued. That is deliberate: the
    /// instance row already carries the instruction, and a signal delivered to a replica that picks
    /// the work up an hour later would be acting on stale intent.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_signal_with_no_listener_is_discarded()
    {
        RabbitMqControlChannel publisher = await ReplicaAsync();
        string instanceId = $"i-{Guid.NewGuid():N}";

        await publisher.PublishAsync(Cancel(instanceId), default);

        // Subscribing afterwards must not receive the earlier signal.
        RabbitMqControlChannel latecomer = await ReplicaAsync();
        var recorder = new Recorder();
        _disposables.Add(await latecomer.SubscribeAsync(instanceId, recorder.Handler, default));

        (await recorder.StaysAtAsync(0)).Should()
            .BeTrue("the row is the authority; a replayed signal would be acting on stale intent");
    }

    /// <summary>
    /// A throwing handler must not take the subscription down with it, or every later instruction
    /// for that instance is lost silently.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_throwing_handler_does_not_kill_the_subscription()
    {
        RabbitMqControlChannel publisher = await ReplicaAsync();
        RabbitMqControlChannel owner = await ReplicaAsync();
        string instanceId = $"i-{Guid.NewGuid():N}";

        var received = new List<ControlSignal>();
        var signal = new SemaphoreSlim(0);
        bool thrown = false;

        _disposables.Add(await owner.SubscribeAsync(instanceId, s =>
        {
            if (!thrown)
            {
                thrown = true;
                throw new InvalidOperationException("boom");
            }

            lock (received) { received.Add(s); }
            signal.Release();
            return Task.CompletedTask;
        }, default));

        await Task.Delay(300);

        await publisher.PublishAsync(Cancel(instanceId, "first"), default);
        await Task.Delay(300);
        await publisher.PublishAsync(Cancel(instanceId, "second"), default);

        (await signal.WaitAsync(TimeSpan.FromSeconds(15))).Should()
            .BeTrue("the second signal must still arrive after the first handler threw");
    }

    [RequiresDockerFact]
    public async Task The_messaging_adapter_supplies_both_capabilities()
    {
        await using RabbitMqMessagingAdapter adapter =
            await RabbitMqMessagingAdapter.CreateAsync(_rabbit.ConnectionString);

        adapter.Technology.Should().Be("RabbitMQ");
        adapter.IsDistributed.Should().BeTrue();

        adapter.DomainEventBroker.Should().BeAssignableTo<IDomainEventBroker>();
        adapter.ControlChannel.Should().BeAssignableTo<IControlChannel>(
            "registering the adapter must move domain events and control signalling together");
    }
}

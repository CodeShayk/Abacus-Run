using Abacus.Run.Abstractions;
using Abacus.Run.Api;
using Abacus.Run.Core;
using Abacus.Run.Dispatch;
using Abacus.Run.Messaging;
using Abacus.Run.Persistence;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Abacus.Run.UnitTests;

/// <summary>
/// Cancelling an instance owned by another replica used to flip the row to Cancelled while the run
/// carried on to completion, committing side effects. These pin the two halves that close it: the
/// control service tells someone, and the dispatcher listens.
/// </summary>
public class CrossReplicaCancelTests
{
    private static InstanceControlService Control(
        InMemoryInstanceStore instances, IControlChannel? channel) =>
        new(instances,
            new InMemoryApprovalStore(),
            new DirectNotificationSink(new InMemoryEventStore()),
            new NotificationSequencer(),
            new InMemoryAuditStore(),
            new WorkflowRegistry([]),
            checkpoints: null,
            clock: null,
            control: channel);

    private static async Task<WorkflowInstance> RunningInstanceAsync(InMemoryInstanceStore instances)
    {
        WorkflowInstance instance = await instances.CreateAsync(new CreateInstanceRequest
        {
            InstanceId = IdGenerator.NewId(),
            TenantId = "default",
            WorkflowName = "w",
            WorkflowVersion = "1.0.0",
            ContextJson = """{"value":"x"}"""
        }, default);

        await instances.UpdateAsync(instance.InstanceId, m => m.Status = InstanceStatus.Running, default);
        return instance;
    }

    [Fact]
    public async Task Cancelling_signals_the_replica_that_owns_the_instance()
    {
        var instances = new InMemoryInstanceStore();
        var channel = new InProcessControlChannel();
        WorkflowInstance instance = await RunningInstanceAsync(instances);

        var received = new TaskCompletionSource<ControlSignal>();
        await using IAsyncDisposable handle = await channel.SubscribeAsync(
            instance.InstanceId, s => { received.TrySetResult(s); return Task.CompletedTask; }, default);

        await Control(instances, channel).CancelAsync(instance.InstanceId, "stop it", null, default);

        ControlSignal signal = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        signal.Action.Should().Be(ControlActions.Cancel);
        signal.InstanceId.Should().Be(instance.InstanceId);
    }

    [Fact]
    public async Task Suspending_signals_too()
    {
        var instances = new InMemoryInstanceStore();
        var channel = new InProcessControlChannel();
        WorkflowInstance instance = await RunningInstanceAsync(instances);

        var received = new TaskCompletionSource<ControlSignal>();
        await using IAsyncDisposable handle = await channel.SubscribeAsync(
            instance.InstanceId, s => { received.TrySetResult(s); return Task.CompletedTask; }, default);

        await Control(instances, channel).SuspendAsync(instance.InstanceId, "pause", null, default);

        (await received.Task.WaitAsync(TimeSpan.FromSeconds(5))).Action.Should().Be(ControlActions.Suspend);
    }

    [Fact]
    public async Task A_failing_channel_does_not_fail_the_cancel()
    {
        var instances = new InMemoryInstanceStore();
        WorkflowInstance instance = await RunningInstanceAsync(instances);

        ControlResult result = await Control(instances, new ThrowingControlChannel())
            .CancelAsync(instance.InstanceId, "stop", null, default);

        result.IsSuccess.Should()
            .BeTrue("the row is already written; reporting failure would misdescribe what happened");

        (await instances.GetAsync(instance.InstanceId, default))!.Status.Should().Be(InstanceStatus.Cancelled);
    }

    [Fact]
    public async Task Cancelling_without_a_channel_still_works()
    {
        var instances = new InMemoryInstanceStore();
        WorkflowInstance instance = await RunningInstanceAsync(instances);

        ControlResult result = await Control(instances, channel: null)
            .CancelAsync(instance.InstanceId, "stop", null, default);

        result.IsSuccess.Should().BeTrue("a single-replica host needs no signalling at all");
    }

    /// <summary>
    /// The defect itself: a signal must stop a run that is already in progress on this replica.
    /// </summary>
    [Fact]
    public async Task A_signal_stops_a_run_already_in_flight()
    {
        var instances = new InMemoryInstanceStore();
        var channel = new InProcessControlChannel();
        WorkflowInstance instance = await RunningInstanceAsync(instances);

        var started = new TaskCompletionSource();
        var runner = new BlockingRunnerFactory(started, instances);

        DispatcherService dispatcher = Dispatcher(instances, runner, channel);

        Task run = dispatcher.RunLeasedAsync(instance, new NoopSlot(), CancellationToken.None);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        run.IsCompleted.Should().BeFalse("the node blocks, so the run is genuinely in flight");

        // An operator cancels — on this replica or another; the signal is what carries it.
        await Control(instances, channel).CancelAsync(instance.InstanceId, "stop", null, default);

        // Without the signal reaching the dispatcher this waits forever: the node blocks on a token
        // nothing else cancels, which is precisely the defect.
        Func<Task> awaitRun = () => run.WaitAsync(TimeSpan.FromSeconds(10));

        await awaitRun.Should().NotThrowAsync(
            "the run must unwind, not carry on committing side effects behind a Cancelled row");
    }

    [Fact]
    public async Task An_instance_cancelled_before_the_run_starts_is_never_started()
    {
        var instances = new InMemoryInstanceStore();
        WorkflowInstance instance = await RunningInstanceAsync(instances);

        // Cancelled in the window between claiming and subscribing.
        await instances.UpdateAsync(instance.InstanceId, m =>
        {
            m.Status = InstanceStatus.Cancelled;
            m.CancellationRequested = true;
        }, default);

        var runner = new BlockingRunnerFactory(new TaskCompletionSource(), instances);

        await Dispatcher(instances, runner, new InProcessControlChannel())
            .RunLeasedAsync(instance, new NoopSlot(), CancellationToken.None);

        runner.WasInvoked.Should()
            .BeFalse("a cancel that landed before the subscription must still be honoured");
    }

    private static DispatcherService Dispatcher(
        InMemoryInstanceStore instances, IWorkflowRunnerFactory runners, IControlChannel channel)
        => new(instances, runners,
            new ConcurrencyLimiter(10, new Dictionary<string, int>()),
            Options.Create(new WorkflowHostOptions()),
            NullLogger<DispatcherService>.Instance,
            clock: null,
            control: channel);

    private sealed class NoopSlot : IDisposable
    {
        public void Dispose() { }
    }

    private sealed class ThrowingControlChannel : IControlChannel
    {
        public ValueTask PublishAsync(ControlSignal signal, CancellationToken cancellationToken)
            => throw new InvalidOperationException("transport down");

        public Task<IAsyncDisposable> SubscribeAsync(
            string instanceId, Func<ControlSignal, Task> handler, CancellationToken cancellationToken)
            => throw new InvalidOperationException("transport down");
    }

    /// <summary>
    /// Drives the real <see cref="WorkflowRunner"/> over a workflow whose only node blocks until its
    /// token is cancelled. Using the real runner is the point: a stub would prove the test's own
    /// wiring rather than that cancellation actually reaches a running executor.
    /// </summary>
    private sealed class BlockingRunnerFactory : IWorkflowRunnerFactory
    {
        private readonly BlockingWorkflow _workflow;
        private readonly InMemoryInstanceStore _instances;

        public BlockingRunnerFactory(TaskCompletionSource started, InMemoryInstanceStore? instances = null)
        {
            _workflow = new BlockingWorkflow(started);
            _instances = instances ?? new InMemoryInstanceStore();
        }

        public bool WasInvoked => _workflow.WasInvoked;
        public bool WasCancelled => _workflow.WasCancelled;

        public WorkflowRunner Create(WorkflowInstance instance) => new(new WorkflowRunnerDependencies
        {
            Registry = new WorkflowRegistry([_workflow]),
            Instances = _instances,
            Events = new DirectNotificationSink(new InMemoryEventStore()),
            Sequencer = new NotificationSequencer(),
            Pipelines = new MiddlewarePipelineFactory([], []),
            Checkpoints = new OverflowCheckpointStore(new InMemoryBlobStore(), 1024, TimeProvider.System)
        });
    }

    private sealed record BlockingContext(string Value = "x");

    private sealed record BlockingResult(string Value);

    private sealed class BlockingWorkflow : IWorkflowDefinition<BlockingContext, BlockingResult>
    {
        private readonly TaskCompletionSource _started;

        public BlockingWorkflow(TaskCompletionSource started) => _started = started;

        public string Name => "w";
        public string Version => "1.0.0";

        public bool WasInvoked { get; private set; }
        public bool WasCancelled { get; private set; }

        public ValueTask<Microsoft.Agents.AI.Workflows.Workflow> BuildAsync(
            WorkflowBuildContext context, CancellationToken cancellationToken)
        {
            WasInvoked = true;

            Microsoft.Agents.AI.Workflows.ExecutorBinding block = context.Node(
                new Abacus.Run.Executors.DelegateExecutor<BlockingContext, BlockingResult>(
                    "block",
                    async (input, _, ct) =>
                    {
                        _started.TrySetResult();

                        try
                        {
                            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            WasCancelled = true;
                            throw;
                        }

                        return new BlockingResult(input.Value);
                    }));

            return new ValueTask<Microsoft.Agents.AI.Workflows.Workflow>(
                new Microsoft.Agents.AI.Workflows.WorkflowBuilder(block)
                    .WithOutputFrom(block)
                    .WithName(Name)
                    .Build());
        }
    }
}

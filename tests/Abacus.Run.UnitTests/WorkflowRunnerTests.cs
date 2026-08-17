using System.Security.Claims;
using Abacus.Run.Abstractions;
using Abacus.Run.Abstractions.Middleware;
using Abacus.Run.Core;
using Abacus.Run.Persistence;
using FluentAssertions;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Abacus.Run.UnitTests;

/// <summary>
/// Drives the real Agent Framework engine end to end through <see cref="WorkflowRunner"/>: graph
/// build, event translation, gate parking, and failure disposition.
/// </summary>
public class WorkflowRunnerTests
{
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.Parse("2026-07-30T10:00:00Z"));
    private readonly InMemoryInstanceStore _instances;
    private readonly InMemoryEventStore _events = new();
    private readonly InMemoryApprovalStore _approvals = new();
    private readonly InMemoryLogStore _logs = new();
    private readonly NotificationSequencer _sequencer = new();

    public WorkflowRunnerTests() => _instances = new InMemoryInstanceStore(_clock);

    private WorkflowRunner Build(
        IWorkflowDefinition definition,
        MiddlewarePipelineFactory? pipelines = null,
        IApprovalService? approvals = null,
        WorkflowHostOptions? options = null)
        => new(new WorkflowRunnerDependencies
        {
            Registry = new WorkflowRegistry([definition]),
            Instances = _instances,
            Events = new DirectNotificationSink(_events),
            Sequencer = _sequencer,
            Pipelines = pipelines ?? new MiddlewarePipelineFactory(),
            Checkpoints = new OverflowCheckpointStore(clock: _clock),
            Approvals = _approvals,
            ApprovalService = approvals,
            Logs = _logs,
            Options = options ?? new WorkflowHostOptions(),
            Clock = _clock
        });

    private async Task<WorkflowInstance> SeedAsync(string workflow = "linear", string version = "1.0.0")
    {
        WorkflowInstance instance = await _instances.CreateAsync(new CreateInstanceRequest
        {
            InstanceId = "i1",
            TenantId = "t1",
            WorkflowName = workflow,
            WorkflowVersion = version,
            ContextJson = """{"value":"seed"}"""
        }, default);

        await _instances.UpdateAsync("i1", m => m.Status = InstanceStatus.Running, default);
        return (await _instances.GetAsync("i1", default))!;
    }

    /// <summary>The gated workflow assigns to "group:approvers", so the decider must hold that role.</summary>
    private static ClaimsPrincipal Approver(string id)
        => new(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, id), new Claim(ClaimTypes.Role, "approvers")], "test"));

    private async Task<IReadOnlyList<EventEnvelope>> EventsAsync()
        => (await _events.QueryAsync(new EventQuery("i1", Limit: 200), default)).Items;

    [Fact]
    public async Task Runs_a_linear_workflow_to_completion()
    {
        WorkflowInstance instance = await SeedAsync();
        RunOutcome outcome = await Build(new LinearWorkflow()).RunAsync(instance, default);

        outcome.Status.Should().Be(InstanceStatus.Completed);
        (await _instances.GetAsync("i1", default))!.Status.Should().Be(InstanceStatus.Completed);
    }

    [Fact]
    public async Task Emits_the_documented_event_sequence()
    {
        WorkflowInstance instance = await SeedAsync();
        await Build(new LinearWorkflow()).RunAsync(instance, default);

        IReadOnlyList<EventEnvelope> events = await EventsAsync();

        events.Select(e => e.EventType).Should().ContainInOrder(
            WorkflowEventTypes.WorkflowStarted,
            WorkflowEventTypes.ExecutorInvoked,
            WorkflowEventTypes.WorkflowTerminated);

        events.Should().Contain(e => e.EventType == WorkflowEventTypes.ExecutorCompleted);
        events.Should().Contain(e => e.EventType == WorkflowEventTypes.WorkflowOutput);
    }

    [Fact]
    public async Task Event_sequences_are_gapless_and_ascending()
    {
        WorkflowInstance instance = await SeedAsync();
        await Build(new LinearWorkflow()).RunAsync(instance, default);

        long[] sequences = (await EventsAsync()).Select(e => e.Sequence).ToArray();

        sequences.Should().OnlyHaveUniqueItems();
        sequences.Should().BeInAscendingOrder();
        sequences.Should().Equal(Enumerable.Range(1, sequences.Length).Select(i => (long)i));
    }

    [Fact]
    public async Task Executor_middleware_wraps_every_node()
    {
        var log = new List<string>();
        var pipelines = new MiddlewarePipelineFactory([new RecordingExecutorMiddleware(log, "mw")]);

        WorkflowInstance instance = await SeedAsync();
        await Build(new LinearWorkflow(), pipelines).RunAsync(instance, default);

        // Two nodes in the graph, so two full traversals of the pipeline.
        log.Count(entry => entry == "mw:before").Should().Be(2);
        log.Count(entry => entry == "mw:after").Should().Be(2);
    }

    [Fact]
    public async Task Workflow_middleware_wraps_the_whole_run()
    {
        var log = new List<string>();
        var pipelines = new MiddlewarePipelineFactory(workflowMiddleware: [new RecordingWorkflowMiddleware(log, "run")]);

        WorkflowInstance instance = await SeedAsync();
        await Build(new LinearWorkflow(), pipelines).RunAsync(instance, default);

        log.Should().Equal("run:before", "run:after");
    }

    [Fact]
    public async Task A_dead_stop_exception_terminates_without_retrying()
    {
        WorkflowInstance instance = await SeedAsync("failing");

        RunOutcome outcome = await Build(new FailingWorkflow(new WorkflowDeadStopException("fraud")))
            .RunAsync(instance, default);

        outcome.Status.Should().Be(InstanceStatus.DeadStopped);

        WorkflowInstance? stored = await _instances.GetAsync("i1", default);
        stored!.Status.Should().Be(InstanceStatus.DeadStopped);
        stored.TerminalReason.Should().Contain("fraud");
        stored.AttemptCount.Should().Be(0, "a dead stop consumes no retry budget");
    }

    [Fact]
    public async Task A_transient_failure_schedules_a_retry_with_backoff()
    {
        WorkflowInstance instance = await SeedAsync("failing");

        RunOutcome outcome = await Build(new FailingWorkflow(new TimeoutException("slow")))
            .RunAsync(instance, default);

        outcome.Status.Should().Be(InstanceStatus.RetryScheduled);

        WorkflowInstance? stored = await _instances.GetAsync("i1", default);
        stored!.Status.Should().Be(InstanceStatus.RetryScheduled);
        stored.AttemptCount.Should().Be(1);
        stored.NextRetryAt.Should().NotBeNull();
        stored.LeaseOwner.Should().BeNull("a retrying instance must be claimable by any replica");
    }

    [Fact]
    public async Task Retries_stop_once_the_attempt_budget_is_spent()
    {
        await SeedAsync("failing");
        await _instances.UpdateAsync("i1", m => m.AttemptCount = 4, default);   // MaxAttempts defaults to 5
        WorkflowInstance instance = (await _instances.GetAsync("i1", default))!;

        RunOutcome outcome = await Build(new FailingWorkflow(new TimeoutException()))
            .RunAsync(instance, default);

        outcome.Status.Should().Be(InstanceStatus.Failed);
    }

    [Fact]
    public async Task Retries_stop_once_the_lifetime_budget_is_spent()
    {
        WorkflowInstance instance = await SeedAsync("failing");
        _clock.Advance(TimeSpan.FromDays(2));   // MaxLifetime defaults to 24h

        RunOutcome outcome = await Build(new FailingWorkflow(new TimeoutException()))
            .RunAsync(instance, default);

        outcome.Status.Should().Be(InstanceStatus.Failed);
    }

    [Fact]
    public async Task An_escalating_classifier_parks_for_input()
    {
        WorkflowInstance instance = await SeedAsync("escalating");

        RunOutcome outcome = await Build(new EscalatingWorkflow()).RunAsync(instance, default);

        outcome.Status.Should().Be(InstanceStatus.AwaitingInput);
        (await _instances.GetAsync("i1", default))!.Status.Should().Be(InstanceStatus.AwaitingInput);
    }

    [Fact]
    public async Task A_throwing_classifier_dead_stops_rather_than_failing_open()
    {
        WorkflowInstance instance = await SeedAsync("classifier-throws");

        RunOutcome outcome = await Build(new ThrowingClassifierWorkflow()).RunAsync(instance, default);

        outcome.Status.Should().Be(InstanceStatus.DeadStopped);
    }

    [Fact]
    public async Task Failures_are_written_to_the_instance_log_with_a_stack_trace()
    {
        WorkflowInstance instance = await SeedAsync("failing");
        await Build(new FailingWorkflow(new InvalidOperationException("kaboom"))).RunAsync(instance, default);

        IReadOnlyList<InstanceLogEntry> logs = await _logs.QueryAsync("i1", "Error", null, 10, default);

        logs.Should().ContainSingle();
        logs[0].Message.Should().Be("kaboom");
        logs[0].ExceptionType.Should().Contain("InvalidOperationException");
    }

    [Fact]
    public async Task A_failure_event_names_the_executor_and_exception()
    {
        WorkflowInstance instance = await SeedAsync("failing");
        await Build(new FailingWorkflow(new TimeoutException("slow"))).RunAsync(instance, default);

        EventEnvelope failure = (await EventsAsync())
            .Should().ContainSingle(e => e.EventType == WorkflowEventTypes.ExecutorFailed).Subject;

        failure.ExecutorId.Should().Be("boom");
        failure.PayloadJson.Should().Contain("TimeoutException").And.Contain("slow");
    }

    [Fact]
    public async Task An_unregistered_version_dead_stops_with_a_clear_reason()
    {
        WorkflowInstance instance = await SeedAsync("linear", "9.9.9");

        RunOutcome outcome = await Build(new LinearWorkflow()).RunAsync(instance, default);

        outcome.Status.Should().Be(InstanceStatus.DeadStopped);
        outcome.TerminalReason.Should().Be("WorkflowVersionUnavailable");
    }

    [Fact]
    public async Task A_tripped_gate_parks_the_instance_without_running_the_node()
    {
        var gated = new GatedWorkflow();
        var coordinator = new ApprovalCoordinator(
            _approvals, _instances, new DirectNotificationSink(_events), _sequencer, null, _clock);

        WorkflowInstance instance = await SeedAsync("gated");
        RunOutcome outcome = await Build(gated, approvals: coordinator).RunAsync(instance, default);

        outcome.Status.Should().Be(InstanceStatus.AwaitingApproval);
        gated.GuardedInvocations.Should().Be(0, "no side effect may occur before a decision exists");

        IReadOnlyList<ApprovalRequest> raised = await _approvals.ListForInstanceAsync("i1", default);
        raised.Should().ContainSingle();
        raised[0].ExecutorId.Should().Be("guarded");
        raised[0].State.Should().Be(ApprovalState.Pending);
    }

    [Fact]
    public async Task The_approval_request_arrives_on_the_same_event_stream_as_progress()
    {
        var coordinator = new ApprovalCoordinator(
            _approvals, _instances, new DirectNotificationSink(_events), _sequencer, null, _clock);

        WorkflowInstance instance = await SeedAsync("gated");
        await Build(new GatedWorkflow(), approvals: coordinator).RunAsync(instance, default);

        IReadOnlyList<EventEnvelope> events = await EventsAsync();

        events.Select(e => e.EventType).Should().ContainInOrder(
            WorkflowEventTypes.WorkflowStarted,
            WorkflowEventTypes.ExecutorInvoked,
            WorkflowEventTypes.ApprovalRequested);

        events.Select(e => e.Sequence).Should().OnlyHaveUniqueItems(
            "approvals share the instance sequence space with progress events");
    }

    [Fact]
    public async Task An_approved_gate_lets_the_node_run_on_resume()
    {
        var gated = new GatedWorkflow();
        var coordinator = new ApprovalCoordinator(
            _approvals, _instances, new DirectNotificationSink(_events), _sequencer, null, _clock);

        WorkflowInstance instance = await SeedAsync("gated");
        await Build(gated, approvals: coordinator).RunAsync(instance, default);

        ApprovalRequest approval = (await _approvals.ListForInstanceAsync("i1", default)).Single();
        DecisionResult decision = await coordinator.ApplyDecisionAsync(approval.ApprovalId,
            new ApprovalDecisionInput { Decision = ApprovalOutcomeKind.Approve },
            Approver("alice"), default);

        decision.Kind.Should().Be(DecisionResultKind.Accepted);

        // Resume: the engine re-delivers the parked message and the gate now sees a decision.
        await _instances.UpdateAsync("i1", m => m.Status = InstanceStatus.Running, default);
        WorkflowInstance resumed = (await _instances.GetAsync("i1", default))!;

        var freshWorkflow = new GatedWorkflow();
        RunOutcome outcome = await Build(freshWorkflow, approvals: coordinator).RunAsync(resumed, default);

        outcome.Status.Should().Be(InstanceStatus.Completed);
        freshWorkflow.GuardedInvocations.Should().Be(1);
    }

    [Fact]
    public async Task A_rejected_gate_dead_stops_through_the_workflow_classifier()
    {
        var coordinator = new ApprovalCoordinator(
            _approvals, _instances, new DirectNotificationSink(_events), _sequencer, null, _clock);

        WorkflowInstance instance = await SeedAsync("gated");
        await Build(new GatedWorkflow(), approvals: coordinator).RunAsync(instance, default);

        ApprovalRequest approval = (await _approvals.ListForInstanceAsync("i1", default)).Single();
        DecisionResult decision = await coordinator.ApplyDecisionAsync(approval.ApprovalId,
            new ApprovalDecisionInput { Decision = ApprovalOutcomeKind.Reject },
            Approver("bob"), default);

        decision.Kind.Should().Be(DecisionResultKind.Accepted);

        await _instances.UpdateAsync("i1", m => m.Status = InstanceStatus.Running, default);
        WorkflowInstance resumed = (await _instances.GetAsync("i1", default))!;

        var freshWorkflow = new GatedWorkflow();
        RunOutcome outcome = await Build(freshWorkflow, approvals: coordinator).RunAsync(resumed, default);

        outcome.Status.Should().Be(InstanceStatus.DeadStopped);
        freshWorkflow.GuardedInvocations.Should().Be(0);
    }

    [Fact]
    public async Task Run_rejects_a_null_instance()
        => await Build(new LinearWorkflow()).Invoking(r => r.RunAsync(null!, default))
            .Should().ThrowAsync<ArgumentNullException>();

    // ---- workflow definitions under test -------------------------------------------------

    private sealed class LinearWorkflow : IWorkflowDefinition<SampleContext, SampleResult>
    {
        public string Name => "linear";
        public string Version => "1.0.0";

        public ValueTask<Workflow> BuildAsync(WorkflowBuildContext context, CancellationToken cancellationToken)
        {
            ExecutorBinding first = context.Node(new PassThrough("first"));
            ExecutorBinding second = context.Node(new PassThrough("second"));

            return new ValueTask<Workflow>(new WorkflowBuilder(first)
                .AddEdge(first, second)
                .WithOutputFrom(second)
                .Build());
        }

        private sealed class PassThrough : HostExecutor<SampleContext, SampleContext>
        {
            public PassThrough(string id) : base(id) { }

            protected override ValueTask<SampleContext> ExecuteCoreAsync(
                SampleContext input, IWorkflowContext context, CancellationToken cancellationToken)
                => ValueTask.FromResult(input);
        }
    }

    private sealed class FailingWorkflow : IWorkflowDefinition<SampleContext, SampleResult>
    {
        private readonly Exception _error;

        public FailingWorkflow(Exception error) => _error = error;

        public string Name => "failing";
        public string Version => "1.0.0";

        public ValueTask<Workflow> BuildAsync(WorkflowBuildContext context, CancellationToken cancellationToken)
        {
            ExecutorBinding boom = context.Node(new Boom("boom", _error));
            return new ValueTask<Workflow>(new WorkflowBuilder(boom).WithOutputFrom(boom).Build());
        }

        private sealed class Boom : HostExecutor<SampleContext, SampleResult>
        {
            private readonly Exception _error;

            public Boom(string id, Exception error) : base(id) => _error = error;

            protected override ValueTask<SampleResult> ExecuteCoreAsync(
                SampleContext input, IWorkflowContext context, CancellationToken cancellationToken)
                => throw _error;
        }
    }

    private sealed class EscalatingWorkflow : IWorkflowDefinition<SampleContext, SampleResult>
    {
        public string Name => "escalating";
        public string Version => "1.0.0";

        public FailureDisposition Classify(WorkflowFailure failure) => FailureDisposition.Escalate;

        public ValueTask<Workflow> BuildAsync(WorkflowBuildContext context, CancellationToken cancellationToken)
        {
            ExecutorBinding boom = context.Node(new Thrower("boom"));
            return new ValueTask<Workflow>(new WorkflowBuilder(boom).WithOutputFrom(boom).Build());
        }

        private sealed class Thrower : HostExecutor<SampleContext, SampleResult>
        {
            public Thrower(string id) : base(id) { }

            protected override ValueTask<SampleResult> ExecuteCoreAsync(
                SampleContext input, IWorkflowContext context, CancellationToken cancellationToken)
                => throw new InvalidOperationException("needs a human");
        }
    }

    private sealed class ThrowingClassifierWorkflow : IWorkflowDefinition<SampleContext, SampleResult>
    {
        public string Name => "classifier-throws";
        public string Version => "1.0.0";

        public FailureDisposition Classify(WorkflowFailure failure)
            => throw new InvalidOperationException("classifier is broken");

        public ValueTask<Workflow> BuildAsync(WorkflowBuildContext context, CancellationToken cancellationToken)
        {
            ExecutorBinding boom = context.Node(new Thrower("boom"));
            return new ValueTask<Workflow>(new WorkflowBuilder(boom).WithOutputFrom(boom).Build());
        }

        private sealed class Thrower : HostExecutor<SampleContext, SampleResult>
        {
            public Thrower(string id) : base(id) { }

            protected override ValueTask<SampleResult> ExecuteCoreAsync(
                SampleContext input, IWorkflowContext context, CancellationToken cancellationToken)
                => throw new TimeoutException();
        }
    }

    private sealed class GatedWorkflow : IWorkflowDefinition<SampleContext, SampleResult>
    {
        private readonly Guarded _guarded = new("guarded");

        public string Name => "gated";
        public string Version => "1.0.0";

        public int GuardedInvocations => _guarded.Invocations;

        public ValueTask<Workflow> BuildAsync(WorkflowBuildContext context, CancellationToken cancellationToken)
        {
            ExecutorBinding start = context.Node(new PassThrough("start"));
            ExecutorBinding guarded = context.Node(_guarded, gate => gate
                .Mode(ExecutionMode.RequireApproval)
                .Reason("AlwaysGated")
                .AssignTo("group:approvers"));

            return new ValueTask<Workflow>(new WorkflowBuilder(start)
                .AddEdge(start, guarded)
                .WithOutputFrom(guarded)
                .Build());
        }

        private sealed class PassThrough : HostExecutor<SampleContext, SampleContext>
        {
            public PassThrough(string id) : base(id) { }

            protected override ValueTask<SampleContext> ExecuteCoreAsync(
                SampleContext input, IWorkflowContext context, CancellationToken cancellationToken)
                => ValueTask.FromResult(input);
        }

        private sealed class Guarded : HostExecutor<SampleContext, SampleResult>
        {
            public Guarded(string id) : base(id) { }

            public int Invocations { get; private set; }

            protected override ValueTask<SampleResult> ExecuteCoreAsync(
                SampleContext input, IWorkflowContext context, CancellationToken cancellationToken)
            {
                Invocations++;
                return ValueTask.FromResult(new SampleResult(input.Value));
            }
        }
    }
}

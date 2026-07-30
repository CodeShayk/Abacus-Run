using Abacus.Run.Abstractions;
using Abacus.Run.Abstractions.Middleware;
using Abacus.Run.Core;
using FluentAssertions;
using Xunit;

namespace Abacus.Run.UnitTests;

public class HostExecutorTests
{
    private static HostExecutorRuntime Runtime(
        IGateEvaluator? gates = null,
        IApprovalCoordinator? approvals = null,
        ExecutorDelegate? pipeline = null,
        int superstep = 3)
        => new()
        {
            InstanceId = "inst-1",
            Descriptor = TestFactory.Descriptor(),
            Attempt = 2,
            Gates = gates,
            Approvals = approvals,
            Pipeline = pipeline,
            SuperstepAccessor = () => superstep
        };

    [Fact]
    public async Task Unattached_executor_runs_core_directly()
    {
        var executor = new ProbeExecutor();

        Outcome result = await executor.HandleAsync(new Payload("x"), new FakeWorkflowContext(), default);

        result.Value.Should().Be("handled:x");
        executor.CoreInvocations.Should().Be(1);
    }

    [Fact]
    public async Task Gate_pause_never_invokes_the_executor_core()
    {
        // The whole point of gating: no side effect may occur before a decision exists.
        var approvals = new RecordingApprovalCoordinator();
        var executor = new ProbeExecutor();
        var context = new FakeWorkflowContext();

        var gate = new ApprovalGate { Mode = ExecutionMode.RequireApproval, Reason = "AboveThreshold" };
        executor.Runtime = Runtime(new ScriptedGateEvaluator(new GateOutcome(GateOutcomeKind.Pause, gate)), approvals);

        Outcome result = await executor.HandleAsync(new Payload("x"), context, default);

        executor.CoreInvocations.Should().Be(0);
        result.Should().BeNull("a paused executor must emit nothing downstream");
        context.HaltRequests.Should().Be(1);
        approvals.Raised.Should().ContainSingle();
        approvals.Raised[0].Gate.Reason.Should().Be("AboveThreshold");
        approvals.Raised[0].ExecutorId.Should().Be("probe");
    }

    [Fact]
    public async Task Gate_pause_without_a_coordinator_still_halts()
    {
        var executor = new ProbeExecutor();
        var context = new FakeWorkflowContext();
        executor.Runtime = Runtime(new ScriptedGateEvaluator(new GateOutcome(GateOutcomeKind.Pause)));

        await executor.HandleAsync(new Payload(), context, default);

        context.HaltRequests.Should().Be(1);
        executor.CoreInvocations.Should().Be(0);
    }

    [Fact]
    public async Task Gate_rejection_throws_approval_rejected()
    {
        var executor = new ProbeExecutor();
        executor.Runtime = Runtime(new ScriptedGateEvaluator(
            new GateOutcome(GateOutcomeKind.Rejected, ApprovalId: "apr_7", Comment: "not authorised")));

        Func<Task> act = async () => await executor.HandleAsync(new Payload(), new FakeWorkflowContext(), default);

        (await act.Should().ThrowAsync<ApprovalRejectedException>())
            .Which.ApprovalId.Should().Be("apr_7");
        executor.CoreInvocations.Should().Be(0);
    }

    [Fact]
    public async Task Approved_gate_runs_the_core_with_the_original_input()
    {
        var executor = new ProbeExecutor();
        executor.Runtime = Runtime(new ScriptedGateEvaluator(new GateOutcome(GateOutcomeKind.ProceedApproved)));

        Outcome result = await executor.HandleAsync(new Payload("orig"), new FakeWorkflowContext(), default);

        executor.CoreInvocations.Should().Be(1);
        executor.LastInput!.Value.Should().Be("orig");
        result.Value.Should().Be("handled:orig");
    }

    [Fact]
    public async Task Approved_with_modification_substitutes_the_input()
    {
        var executor = new ProbeExecutor();
        executor.Runtime = Runtime(new ScriptedGateEvaluator(
            new GateOutcome(GateOutcomeKind.ProceedApproved, ModifiedInput: new Payload("modified"))));

        Outcome result = await executor.HandleAsync(new Payload("orig"), new FakeWorkflowContext(), default);

        executor.LastInput!.Value.Should().Be("modified");
        result.Value.Should().Be("handled:modified");
    }

    [Fact]
    public async Task Pipeline_can_mutate_input_before_the_core_sees_it()
    {
        var log = new List<string>();
        var executor = new ProbeExecutor();
        var factory = new MiddlewarePipelineFactory(
        [
            new RecordingExecutorMiddleware(log, "mutate", before: ctx => ctx.Input = new Payload("rewritten"))
        ]);

        executor.Runtime = Runtime(pipeline: factory.BuildExecutorPipeline(
            TestFactory.Descriptor(), executor.ExecuteTerminalAsync));

        Outcome result = await executor.HandleAsync(new Payload("orig"), new FakeWorkflowContext(), default);

        executor.LastInput!.Value.Should().Be("rewritten");
        result.Value.Should().Be("handled:rewritten");
    }

    [Fact]
    public async Task Pipeline_can_replace_output_after_the_core_returns()
    {
        var log = new List<string>();
        var executor = new ProbeExecutor();
        var factory = new MiddlewarePipelineFactory(
        [
            new RecordingExecutorMiddleware(log, "replace", after: ctx => ctx.Output = new Outcome("replaced"))
        ]);

        executor.Runtime = Runtime(pipeline: factory.BuildExecutorPipeline(
            TestFactory.Descriptor(), executor.ExecuteTerminalAsync));

        Outcome result = await executor.HandleAsync(new Payload(), new FakeWorkflowContext(), default);

        result.Value.Should().Be("replaced");
    }

    [Fact]
    public async Task Middleware_can_swallow_an_exception_and_supply_output()
    {
        var log = new List<string>();
        var executor = new ProbeExecutor(throws: new TimeoutException("boom"));
        var factory = new MiddlewarePipelineFactory(
        [
            new RecordingExecutorMiddleware(log, "recover", after: ctx =>
            {
                ctx.Exception = null;
                ctx.Output = new Outcome("recovered");
            })
        ]);

        executor.Runtime = Runtime(pipeline: factory.BuildExecutorPipeline(
            TestFactory.Descriptor(), executor.ExecuteTerminalAsync));

        Outcome result = await executor.HandleAsync(new Payload(), new FakeWorkflowContext(), default);

        result.Value.Should().Be("recovered");
    }

    [Fact]
    public async Task Exception_surviving_the_pipeline_is_rethrown_with_its_original_stack()
    {
        var executor = new ProbeExecutor(throws: new WorkflowDeadStopException("hard stop"));
        var factory = new MiddlewarePipelineFactory([]);

        executor.Runtime = Runtime(pipeline: factory.BuildExecutorPipeline(
            TestFactory.Descriptor(), executor.ExecuteTerminalAsync));

        Func<Task> act = async () => await executor.HandleAsync(new Payload(), new FakeWorkflowContext(), default);

        var thrown = (await act.Should().ThrowAsync<WorkflowDeadStopException>()).Which;
        thrown.Message.Should().Be("hard stop");
        thrown.StackTrace.Should().NotBeNullOrEmpty("classification depends on the original stack");
    }

    [Fact]
    public async Task Invocation_context_carries_instance_descriptor_superstep_and_attempt()
    {
        ExecutorInvocationContext? captured = null;
        var log = new List<string>();
        var executor = new ProbeExecutor();
        var factory = new MiddlewarePipelineFactory(
        [
            new RecordingExecutorMiddleware(log, "capture", before: ctx => captured = ctx)
        ]);

        executor.Runtime = Runtime(pipeline: factory.BuildExecutorPipeline(
            TestFactory.Descriptor(), executor.ExecuteTerminalAsync), superstep: 7);

        await executor.HandleAsync(new Payload(), new FakeWorkflowContext(), default);

        captured.Should().NotBeNull();
        captured!.InstanceId.Should().Be("inst-1");
        captured.Superstep.Should().Be(7);
        captured.Attempt.Should().Be(2);
        captured.Descriptor.ExecutorId.Should().Be("e1");
    }

    [Fact]
    public async Task Elapsed_is_recorded_even_when_the_core_throws()
    {
        ExecutorInvocationContext? captured = null;
        var log = new List<string>();
        var executor = new ProbeExecutor(throws: new TimeoutException());
        var factory = new MiddlewarePipelineFactory(
        [
            new RecordingExecutorMiddleware(log, "capture", after: ctx =>
            {
                captured = ctx;
                ctx.Exception = null;
                ctx.Output = new Outcome("x");
            })
        ]);

        executor.Runtime = Runtime(pipeline: factory.BuildExecutorPipeline(
            TestFactory.Descriptor(), executor.ExecuteTerminalAsync));

        await executor.HandleAsync(new Payload(), new FakeWorkflowContext(), default);

        captured.Should().NotBeNull();
    }

    [Fact]
    public async Task Terminal_captures_the_exception_rather_than_throwing()
    {
        var executor = new ProbeExecutor(throws: new InvalidOperationException("inner"));
        ExecutorInvocationContext context = TestFactory.Invocation();

        await executor.ExecuteTerminalAsync(context, default);

        context.Exception.Should().BeOfType<InvalidOperationException>();
        context.Output.Should().BeNull();
        context.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task Terminal_clears_a_previous_exception_on_success()
    {
        var executor = new ProbeExecutor();
        ExecutorInvocationContext context = TestFactory.Invocation();
        context.Exception = new InvalidOperationException("stale");

        await executor.ExecuteTerminalAsync(context, default);

        context.Exception.Should().BeNull();
        context.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_rejects_a_null_workflow_context()
    {
        var executor = new ProbeExecutor();
        Func<Task> act = async () => await executor.HandleAsync(new Payload(), null!, default);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public void Executor_exposes_its_input_and_output_types()
    {
        var executor = new ProbeExecutor();
        executor.InputType.Should().Be<Payload>();
        executor.OutputType.Should().Be<Outcome>();
    }

    [Fact]
    public void Unattached_runtime_reports_zero_superstep()
        => HostExecutorRuntime.Unattached.CurrentSuperstep.Should().Be(0);
}

using Abacus.Run.Abstractions;
using Abacus.Run.Core;
using Abacus.Run.Persistence;
using FluentAssertions;
using Xunit;

namespace Abacus.Run.UnitTests;

public class GateEvaluatorTests
{
    private static GateEvaluator Build(
        ApprovalGate? definitionGate = null,
        IGatePolicyStore? policies = null,
        IApprovalStore? approvals = null)
        => new("wf", "1.0.0",
            definitionGate is null
                ? new Dictionary<string, ApprovalGate>()
                : new Dictionary<string, ApprovalGate> { ["pay"] = definitionGate },
            policies, approvals);

    [Fact]
    public async Task Unknown_executor_proceeds()
    {
        GateOutcome outcome = await Build().EvaluateAsync("i1", "anything", new Payload(), default);
        outcome.Kind.Should().Be(GateOutcomeKind.Proceed);
    }

    [Fact]
    public async Task Autonomous_gate_proceeds()
    {
        GateOutcome outcome = await Build(ApprovalGate.Autonomous).EvaluateAsync("i1", "pay", new Payload(), default);
        outcome.Kind.Should().Be(GateOutcomeKind.Proceed);
    }

    [Fact]
    public async Task RequireApproval_always_pauses()
    {
        var gate = new ApprovalGate { Mode = ExecutionMode.RequireApproval, Reason = "always" };
        GateOutcome outcome = await Build(gate).EvaluateAsync("i1", "pay", new Payload(), default);

        outcome.Kind.Should().Be(GateOutcomeKind.Pause);
        outcome.Gate!.Reason.Should().Be("always");
    }

    [Fact]
    public async Task Conditional_gate_pauses_only_when_the_predicate_trips()
    {
        ApprovalGate gate = new ApprovalGateBuilder()
            .When<Payload>(p => p.Amount > 25_000m)
            .Reason("AmountAboveThreshold")
            .Build();

        GateEvaluator evaluator = Build(gate);

        (await evaluator.EvaluateAsync("i1", "pay", new Payload(Amount: 100m), default))
            .Kind.Should().Be(GateOutcomeKind.Proceed);

        (await evaluator.EvaluateAsync("i1", "pay", new Payload(Amount: 50_000m), default))
            .Kind.Should().Be(GateOutcomeKind.Pause);
    }

    [Fact]
    public async Task Conditional_predicate_ignores_unexpected_input_types()
    {
        ApprovalGate gate = new ApprovalGateBuilder().When<Payload>(p => p.Amount > 0).Build();

        GateOutcome outcome = await Build(gate).EvaluateAsync("i1", "pay", "a string", default);

        outcome.Kind.Should().Be(GateOutcomeKind.Proceed, "a type mismatch must not trip the gate");
    }

    [Fact]
    public async Task Conditional_without_a_predicate_proceeds()
    {
        var gate = new ApprovalGate { Mode = ExecutionMode.Conditional, Predicate = null };
        (await Build(gate).EvaluateAsync("i1", "pay", new Payload(), default))
            .Kind.Should().Be(GateOutcomeKind.Proceed);
    }

    [Fact]
    public async Task Policy_store_overrides_the_definition_gate()
    {
        var policies = new InMemoryGatePolicyStore();
        await policies.SetAsync("wf", "1.0.0", "pay",
            new ApprovalGate { Mode = ExecutionMode.RequireApproval, Reason = "promoted at runtime" }, default);

        // An executor the definition left autonomous becomes gated with no redeploy.
        GateOutcome outcome = await Build(ApprovalGate.Autonomous, policies)
            .EvaluateAsync("i1", "pay", new Payload(), default);

        outcome.Kind.Should().Be(GateOutcomeKind.Pause);
        outcome.Gate!.Reason.Should().Be("promoted at runtime");
    }

    [Fact]
    public async Task Instance_override_outranks_the_workflow_policy()
    {
        var policies = new InMemoryGatePolicyStore();
        await policies.SetAsync("wf", "1.0.0", "pay", new ApprovalGate { Mode = ExecutionMode.RequireApproval }, default);
        await policies.SetInstanceOverrideAsync("i1", "pay", ApprovalGate.Autonomous, default);

        (await Build(ApprovalGate.Autonomous, policies).EvaluateAsync("i1", "pay", new Payload(), default))
            .Kind.Should().Be(GateOutcomeKind.Proceed);

        (await Build(ApprovalGate.Autonomous, policies).EvaluateAsync("i2", "pay", new Payload(), default))
            .Kind.Should().Be(GateOutcomeKind.Pause, "a different instance still sees the workflow policy");
    }

    [Fact]
    public async Task Policy_store_failure_falls_back_to_the_definition_not_to_autonomous()
    {
        var gate = new ApprovalGate { Mode = ExecutionMode.RequireApproval, Reason = "protected" };

        GateOutcome outcome = await Build(gate, new ThrowingPolicyStore())
            .EvaluateAsync("i1", "pay", new Payload(), default);

        outcome.Kind.Should().Be(GateOutcomeKind.Pause, "failing open would silently un-gate a protected executor");
    }

    [Fact]
    public async Task Existing_approved_decision_lets_the_invocation_through()
    {
        var approvals = new InMemoryApprovalStore();
        ApprovalRequest approval = await CreateApproval(approvals, ApprovalState.Approved);

        GateOutcome outcome = await Build(new ApprovalGate { Mode = ExecutionMode.RequireApproval }, null, approvals)
            .EvaluateAsync(approval.InstanceId, "pay", new Payload(), default);

        outcome.Kind.Should().Be(GateOutcomeKind.ProceedApproved);
        outcome.ApprovalId.Should().Be(approval.ApprovalId);
    }

    [Fact]
    public async Task Approved_with_modification_surfaces_the_modified_input()
    {
        var approvals = new InMemoryApprovalStore();
        ApprovalRequest approval = await CreateApproval(approvals, ApprovalState.Pending, proposedInput: """{"amount":100}""");

        await approvals.TryRecordDecisionAsync(approval.ApprovalId, new ApprovalDecision
        {
            ApprovalId = approval.ApprovalId,
            DeciderId = "alice",
            Outcome = ApprovalOutcomeKind.ApproveWithModification,
            ModifiedInputJson = """{"amount":250}""",
            DecidedAt = DateTimeOffset.UnixEpoch
        }, ApprovalState.Approved, default);

        GateOutcome outcome = await Build(new ApprovalGate { Mode = ExecutionMode.RequireApproval }, null, approvals)
            .EvaluateAsync(approval.InstanceId, "pay", new Payload(), default);

        outcome.Kind.Should().Be(GateOutcomeKind.ProceedApproved);
        outcome.ModifiedInput.Should().NotBeNull();
    }

    [Fact]
    public async Task Rejected_decision_yields_rejection_with_the_comment()
    {
        var approvals = new InMemoryApprovalStore();
        ApprovalRequest approval = await CreateApproval(approvals, ApprovalState.Pending);

        await approvals.TryRecordDecisionAsync(approval.ApprovalId, new ApprovalDecision
        {
            ApprovalId = approval.ApprovalId,
            DeciderId = "bob",
            Outcome = ApprovalOutcomeKind.Reject,
            Comment = "duplicate payment",
            DecidedAt = DateTimeOffset.UnixEpoch
        }, ApprovalState.Rejected, default);

        GateOutcome outcome = await Build(new ApprovalGate { Mode = ExecutionMode.RequireApproval }, null, approvals)
            .EvaluateAsync(approval.InstanceId, "pay", new Payload(), default);

        outcome.Kind.Should().Be(GateOutcomeKind.Rejected);
        outcome.Comment.Should().Be("duplicate payment");
    }

    [Fact]
    public async Task Still_pending_approval_pauses_again_without_raising_a_duplicate()
    {
        var approvals = new InMemoryApprovalStore();
        ApprovalRequest approval = await CreateApproval(approvals, ApprovalState.Pending);

        GateOutcome outcome = await Build(new ApprovalGate { Mode = ExecutionMode.RequireApproval }, null, approvals)
            .EvaluateAsync(approval.InstanceId, "pay", new Payload(), default);

        outcome.Kind.Should().Be(GateOutcomeKind.Pause);
        outcome.ApprovalId.Should().Be(approval.ApprovalId);
        outcome.Gate.Should().BeNull("re-raising would create a second approval record");
    }

    [Theory]
    [InlineData(ExpiryAction.AutoApprove, GateOutcomeKind.ProceedApproved)]
    [InlineData(ExpiryAction.DeadStop, GateOutcomeKind.Rejected)]
    [InlineData(ExpiryAction.Reject, GateOutcomeKind.Rejected)]
    public async Task Expired_approval_follows_its_expiry_action(ExpiryAction action, GateOutcomeKind expected)
    {
        var approvals = new InMemoryApprovalStore();
        ApprovalRequest approval = await CreateApproval(approvals, ApprovalState.Expired, onExpiry: action);

        GateOutcome outcome = await Build(new ApprovalGate { Mode = ExecutionMode.RequireApproval }, null, approvals)
            .EvaluateAsync(approval.InstanceId, "pay", new Payload(), default);

        outcome.Kind.Should().Be(expected);
    }

    [Fact]
    public async Task Cancelled_approval_is_ignored_and_the_gate_re_evaluates()
    {
        var approvals = new InMemoryApprovalStore();
        ApprovalRequest approval = await CreateApproval(approvals, ApprovalState.Cancelled);

        GateOutcome outcome = await Build(ApprovalGate.Autonomous, null, approvals)
            .EvaluateAsync(approval.InstanceId, "pay", new Payload(), default);

        outcome.Kind.Should().Be(GateOutcomeKind.Proceed);
    }

    [Fact]
    public async Task Approval_for_a_different_executor_does_not_leak()
    {
        var approvals = new InMemoryApprovalStore();
        await CreateApproval(approvals, ApprovalState.Approved, executorId: "other");

        GateOutcome outcome = await Build(new ApprovalGate { Mode = ExecutionMode.RequireApproval }, null, approvals)
            .EvaluateAsync("i1", "pay", new Payload(), default);

        outcome.Kind.Should().Be(GateOutcomeKind.Pause);
    }

    private static async Task<ApprovalRequest> CreateApproval(
        IApprovalStore store,
        ApprovalState state,
        string executorId = "pay",
        string? proposedInput = null,
        ExpiryAction onExpiry = ExpiryAction.DeadStop)
        => await store.CreateAsync(new ApprovalRequest
        {
            ApprovalId = IdGenerator.NewId("apr"),
            InstanceId = "i1",
            TenantId = "t1",
            ExecutorId = executorId,
            ProposedInputJson = proposedInput,
            State = state,
            OnExpiry = onExpiry,
            CreatedAt = DateTimeOffset.UnixEpoch,
            ExpiresAt = DateTimeOffset.UnixEpoch.AddHours(1)
        }, default);

    private sealed class ThrowingPolicyStore : IGatePolicyStore
    {
        public ValueTask<ApprovalGate?> FindAsync(
            string workflowName, string workflowVersion, string executorId, string? instanceId, CancellationToken cancellationToken)
            => throw new InvalidOperationException("policy store unavailable");

        public ValueTask SetAsync(
            string workflowName, string workflowVersion, string executorId, ApprovalGate gate, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public ValueTask SetInstanceOverrideAsync(
            string instanceId, string executorId, ApprovalGate gate, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }
}

public class ApprovalGateBuilderTests
{
    [Fact]
    public void Defaults_to_require_approval_when_a_gate_block_is_supplied()
        => new ApprovalGateBuilder().Build().Mode.Should().Be(ExecutionMode.RequireApproval);

    [Fact]
    public void Fluent_configuration_round_trips()
    {
        ApprovalGate gate = new ApprovalGateBuilder()
            .When<Payload>(p => p.Amount > 10)
            .Reason("AboveLimit")
            .AssignTo("group:finance", "alice")
            .RequireApprovers(2)
            .ExpiresAfter(TimeSpan.FromHours(8))
            .OnExpiry(ExpiryAction.Escalate, "group:cfo")
            .AllowModification()
            .RequireSegregationOfDuties()
            .Build();

        gate.Mode.Should().Be(ExecutionMode.Conditional);
        gate.Reason.Should().Be("AboveLimit");
        gate.Assignees.Should().Equal("group:finance", "alice");
        gate.RequiredApprovers.Should().Be(2);
        gate.Expiry.Should().Be(TimeSpan.FromHours(8));
        gate.OnExpiry.Should().Be(ExpiryAction.Escalate);
        gate.EscalationAssignees.Should().Equal("group:cfo");
        gate.AllowModification.Should().BeTrue();
        gate.RequireSegregationOfDuties.Should().BeTrue();
    }

    [Fact]
    public void Rejects_invalid_approver_counts()
        => new ApprovalGateBuilder().Invoking(b => b.RequireApprovers(0))
            .Should().Throw<ArgumentOutOfRangeException>();

    [Fact]
    public void Rejects_non_positive_expiry()
        => new ApprovalGateBuilder().Invoking(b => b.ExpiresAfter(TimeSpan.Zero))
            .Should().Throw<ArgumentOutOfRangeException>();

    [Fact]
    public void Rejects_null_predicates()
    {
        new ApprovalGateBuilder().Invoking(b => b.When<Payload>(null!)).Should().Throw<ArgumentNullException>();
        new ApprovalGateBuilder().Invoking(b => b.WhenAsync(null!)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task Async_predicate_is_honoured()
    {
        ApprovalGate gate = new ApprovalGateBuilder()
            .WhenAsync(input => ValueTask.FromResult(input is Payload { Amount: > 5 }))
            .Build();

        (await gate.Predicate!(new Payload(Amount: 10))).Should().BeTrue();
        (await gate.Predicate!(new Payload(Amount: 1))).Should().BeFalse();
    }

    [Fact]
    public void Autonomous_singleton_has_safe_defaults()
    {
        ApprovalGate gate = ApprovalGate.Autonomous;
        gate.Mode.Should().Be(ExecutionMode.Autonomous);
        gate.RequiredApprovers.Should().Be(1);
        gate.OnExpiry.Should().Be(ExpiryAction.DeadStop, "the safe default is to stop, not to proceed");
        gate.AllowModification.Should().BeFalse();
    }
}

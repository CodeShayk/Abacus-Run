using System.Security.Claims;
using Abacus.Run.Abstractions;
using Abacus.Run.Core;
using Abacus.Run.Persistence;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Abacus.Run.UnitTests;

public class ApprovalCoordinatorTests
{
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.Parse("2026-07-30T10:00:00Z"));
    private readonly InMemoryApprovalStore _approvals = new();
    private readonly InMemoryEventStore _events = new();
    private readonly InMemoryAuditStore _audit = new();
    private readonly InMemoryInstanceStore _instances;
    private readonly ApprovalCoordinator _coordinator;

    public ApprovalCoordinatorTests()
    {
        _instances = new InMemoryInstanceStore(_clock);
        _coordinator = new ApprovalCoordinator(
            _approvals, _instances, new DirectNotificationSink(_events), new NotificationSequencer(), _audit, _clock);
    }

    private async Task<WorkflowInstance> SeedInstanceAsync(string id = "i1")
        => await _instances.CreateAsync(TestFactory.CreateRequest(instanceId: id), default);

    private static ClaimsPrincipal User(string id, params string[] roles)
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id)], "test");
        foreach (string role in roles)
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, role));
        }
        return new ClaimsPrincipal(identity);
    }

    private async Task<ApprovalRequest> RaiseAsync(ApprovalGate? gate = null, string instanceId = "i1")
    {
        await SeedInstanceAsync(instanceId);
        return await _coordinator.RaiseAsync(
            instanceId, "pay",
            gate ?? new ApprovalGate { Mode = ExecutionMode.RequireApproval, Reason = "AboveLimit" },
            new Payload(Amount: 50_000m), superstep: 2, default);
    }

    [Fact]
    public async Task Raise_persists_a_pending_approval_and_emits_an_event()
    {
        ApprovalRequest approval = await RaiseAsync();

        approval.State.Should().Be(ApprovalState.Pending);
        approval.ExecutorId.Should().Be("pay");
        approval.Reason.Should().Be("AboveLimit");
        approval.ProposedInputJson.Should().Contain("50000");
        approval.ExpiresAt.Should().Be(_clock.GetUtcNow().AddHours(24));

        Page<EventEnvelope> page = await _events.QueryAsync(new EventQuery("i1"), default);
        page.Items.Should().ContainSingle(e => e.EventType == WorkflowEventTypes.ApprovalRequested);
    }

    [Fact]
    public async Task Approve_marks_approved_and_makes_the_instance_dispatchable()
    {
        ApprovalRequest approval = await RaiseAsync();

        DecisionResult result = await _coordinator.ApplyDecisionAsync(approval.ApprovalId,
            new ApprovalDecisionInput { Decision = ApprovalOutcomeKind.Approve, Comment = "ok" }, User("alice"), default);

        result.Kind.Should().Be(DecisionResultKind.Accepted);

        ApprovalRequest? stored = await _approvals.GetAsync(approval.ApprovalId, default);
        stored!.State.Should().Be(ApprovalState.Approved);

        WorkflowInstance? instance = await _instances.GetAsync("i1", default);
        instance!.Status.Should().Be(InstanceStatus.Dispatchable, "any replica may now lease and resume it");
        instance.LeaseOwner.Should().BeNull();
    }

    [Fact]
    public async Task Reject_marks_rejected_and_still_wakes_the_instance()
    {
        ApprovalRequest approval = await RaiseAsync();

        await _coordinator.ApplyDecisionAsync(approval.ApprovalId,
            new ApprovalDecisionInput { Decision = ApprovalOutcomeKind.Reject, Comment = "duplicate" }, User("bob"), default);

        (await _approvals.GetAsync(approval.ApprovalId, default))!.State.Should().Be(ApprovalState.Rejected);

        // Rejection must resume so the workflow's classifier gets to decide what it means.
        (await _instances.GetAsync("i1", default))!.Status.Should().Be(InstanceStatus.Dispatchable);
    }

    [Fact]
    public async Task Unknown_approval_returns_not_found()
    {
        DecisionResult result = await _coordinator.ApplyDecisionAsync("missing",
            new ApprovalDecisionInput { Decision = ApprovalOutcomeKind.Approve }, User("alice"), default);

        result.Kind.Should().Be(DecisionResultKind.NotFound);
    }

    [Fact]
    public async Task Second_decision_is_rejected_as_already_decided()
    {
        ApprovalRequest approval = await RaiseAsync();

        await _coordinator.ApplyDecisionAsync(approval.ApprovalId,
            new ApprovalDecisionInput { Decision = ApprovalOutcomeKind.Approve }, User("alice"), default);

        DecisionResult second = await _coordinator.ApplyDecisionAsync(approval.ApprovalId,
            new ApprovalDecisionInput { Decision = ApprovalOutcomeKind.Reject }, User("bob"), default);

        second.Kind.Should().Be(DecisionResultKind.AlreadyDecided);
    }

    [Fact]
    public async Task Same_decider_cannot_vote_twice()
    {
        ApprovalRequest approval = await RaiseAsync(new ApprovalGate
        {
            Mode = ExecutionMode.RequireApproval,
            RequiredApprovers = 3
        });

        await _coordinator.ApplyDecisionAsync(approval.ApprovalId,
            new ApprovalDecisionInput { Decision = ApprovalOutcomeKind.Approve }, User("alice"), default);

        DecisionResult repeat = await _coordinator.ApplyDecisionAsync(approval.ApprovalId,
            new ApprovalDecisionInput { Decision = ApprovalOutcomeKind.Approve }, User("alice"), default);

        repeat.Kind.Should().Be(DecisionResultKind.AlreadyDecided);
    }

    [Fact]
    public async Task Concurrent_decisions_produce_exactly_one_winner()
    {
        ApprovalRequest approval = await RaiseAsync();

        DecisionResult[] results = await Task.WhenAll(
            Enumerable.Range(0, 10).Select(i => Task.Run(async () =>
                await _coordinator.ApplyDecisionAsync(approval.ApprovalId,
                    new ApprovalDecisionInput { Decision = ApprovalOutcomeKind.Approve }, User($"user{i}"), default))));

        results.Count(r => r.Kind == DecisionResultKind.Accepted).Should().Be(1);
        results.Count(r => r.Kind == DecisionResultKind.AlreadyDecided).Should().Be(9);

        (await _approvals.GetDecisionsAsync(approval.ApprovalId, default)).Should().ContainSingle();
    }

    [Fact]
    public async Task Quorum_holds_the_instance_until_enough_approvers_vote()
    {
        ApprovalRequest approval = await RaiseAsync(new ApprovalGate
        {
            Mode = ExecutionMode.RequireApproval,
            RequiredApprovers = 2
        });

        DecisionResult first = await _coordinator.ApplyDecisionAsync(approval.ApprovalId,
            new ApprovalDecisionInput { Decision = ApprovalOutcomeKind.Approve }, User("alice"), default);

        first.Kind.Should().Be(DecisionResultKind.QuorumPending);
        (await _approvals.GetAsync(approval.ApprovalId, default))!.State.Should().Be(ApprovalState.Pending);
        (await _instances.GetAsync("i1", default))!.Status.Should().NotBe(InstanceStatus.Dispatchable);

        DecisionResult second = await _coordinator.ApplyDecisionAsync(approval.ApprovalId,
            new ApprovalDecisionInput { Decision = ApprovalOutcomeKind.Approve }, User("bob"), default);

        second.Kind.Should().Be(DecisionResultKind.Accepted);
        (await _approvals.GetAsync(approval.ApprovalId, default))!.State.Should().Be(ApprovalState.Approved);
    }

    [Fact]
    public async Task A_single_rejection_defeats_a_pending_quorum()
    {
        ApprovalRequest approval = await RaiseAsync(new ApprovalGate
        {
            Mode = ExecutionMode.RequireApproval,
            RequiredApprovers = 3
        });

        await _coordinator.ApplyDecisionAsync(approval.ApprovalId,
            new ApprovalDecisionInput { Decision = ApprovalOutcomeKind.Approve }, User("alice"), default);

        await _coordinator.ApplyDecisionAsync(approval.ApprovalId,
            new ApprovalDecisionInput { Decision = ApprovalOutcomeKind.Reject }, User("bob"), default);

        (await _approvals.GetAsync(approval.ApprovalId, default))!.State.Should().Be(ApprovalState.Rejected);
    }

    [Fact]
    public async Task Non_assignee_is_forbidden()
    {
        ApprovalRequest approval = await RaiseAsync(new ApprovalGate
        {
            Mode = ExecutionMode.RequireApproval,
            Assignees = ["alice"]
        });

        DecisionResult result = await _coordinator.ApplyDecisionAsync(approval.ApprovalId,
            new ApprovalDecisionInput { Decision = ApprovalOutcomeKind.Approve }, User("mallory"), default);

        result.Kind.Should().Be(DecisionResultKind.Forbidden);
    }

    [Fact]
    public async Task Group_assignee_matches_via_role()
    {
        ApprovalRequest approval = await RaiseAsync(new ApprovalGate
        {
            Mode = ExecutionMode.RequireApproval,
            Assignees = ["group:finance"]
        });

        DecisionResult result = await _coordinator.ApplyDecisionAsync(approval.ApprovalId,
            new ApprovalDecisionInput { Decision = ApprovalOutcomeKind.Approve }, User("carol", "finance"), default);

        result.Kind.Should().Be(DecisionResultKind.Accepted);
    }

    [Fact]
    public async Task Empty_assignee_list_permits_any_principal()
    {
        ApprovalRequest approval = await RaiseAsync();

        DecisionResult result = await _coordinator.ApplyDecisionAsync(approval.ApprovalId,
            new ApprovalDecisionInput { Decision = ApprovalOutcomeKind.Approve }, User("anyone"), default);

        result.Kind.Should().Be(DecisionResultKind.Accepted);
    }

    [Fact]
    public async Task Segregation_of_duties_blocks_the_initiator()
    {
        await SeedInstanceAsync();
        ApprovalRequest approval = await _approvals.CreateAsync(new ApprovalRequest
        {
            ApprovalId = "apr_sod",
            InstanceId = "i1",
            TenantId = "t1",
            ExecutorId = "pay",
            RequireSegregationOfDuties = true,
            InitiatorId = "alice",
            CreatedAt = _clock.GetUtcNow(),
            ExpiresAt = _clock.GetUtcNow().AddHours(1)
        }, default);

        DecisionResult self = await _coordinator.ApplyDecisionAsync(approval.ApprovalId,
            new ApprovalDecisionInput { Decision = ApprovalOutcomeKind.Approve }, User("alice"), default);
        self.Kind.Should().Be(DecisionResultKind.Forbidden);

        DecisionResult other = await _coordinator.ApplyDecisionAsync(approval.ApprovalId,
            new ApprovalDecisionInput { Decision = ApprovalOutcomeKind.Approve }, User("bob"), default);
        other.Kind.Should().Be(DecisionResultKind.Accepted);
    }

    [Fact]
    public async Task Modification_is_refused_when_the_gate_disallows_it()
    {
        ApprovalRequest approval = await RaiseAsync(new ApprovalGate
        {
            Mode = ExecutionMode.RequireApproval,
            AllowModification = false
        });

        DecisionResult result = await _coordinator.ApplyDecisionAsync(approval.ApprovalId, new ApprovalDecisionInput
        {
            Decision = ApprovalOutcomeKind.ApproveWithModification,
            ModifiedInput = TestFactory.Json("""{"amount":1}""")
        }, User("alice"), default);

        result.Kind.Should().Be(DecisionResultKind.InvalidModification);
    }

    [Fact]
    public async Task Modification_is_accepted_when_permitted()
    {
        ApprovalRequest approval = await RaiseAsync(new ApprovalGate
        {
            Mode = ExecutionMode.RequireApproval,
            AllowModification = true
        });

        DecisionResult result = await _coordinator.ApplyDecisionAsync(approval.ApprovalId, new ApprovalDecisionInput
        {
            Decision = ApprovalOutcomeKind.ApproveWithModification,
            ModifiedInput = TestFactory.Json("""{"amount":250}""")
        }, User("alice"), default);

        result.Kind.Should().Be(DecisionResultKind.Accepted);

        IReadOnlyList<ApprovalDecision> decisions = await _approvals.GetDecisionsAsync(approval.ApprovalId, default);
        decisions.Should().ContainSingle().Which.ModifiedInputJson.Should().Contain("250");
    }

    [Fact]
    public async Task Decision_is_audited_with_actor_and_comment()
    {
        ApprovalRequest approval = await RaiseAsync();

        await _coordinator.ApplyDecisionAsync(approval.ApprovalId,
            new ApprovalDecisionInput { Decision = ApprovalOutcomeKind.Approve, Comment = "verified" }, User("alice"), default);

        IReadOnlyList<AuditEntry> entries = await _audit.QueryAsync("i1", 10, default);
        entries.Should().ContainSingle();
        entries[0].ActorId.Should().Be("alice");
        entries[0].Reason.Should().Be("verified");
    }

    [Fact]
    public async Task Anonymous_principal_is_recorded_as_anonymous()
    {
        ApprovalRequest approval = await RaiseAsync();

        await _coordinator.ApplyDecisionAsync(approval.ApprovalId,
            new ApprovalDecisionInput { Decision = ApprovalOutcomeKind.Approve }, null, default);

        (await _approvals.GetDecisionsAsync(approval.ApprovalId, default))[0].DeciderId.Should().Be("anonymous");
    }

    [Fact]
    public async Task Expiry_dead_stops_the_instance_by_default()
    {
        ApprovalRequest approval = await RaiseAsync(new ApprovalGate
        {
            Mode = ExecutionMode.RequireApproval,
            Expiry = TimeSpan.FromHours(1),
            OnExpiry = ExpiryAction.DeadStop
        });

        _clock.Advance(TimeSpan.FromHours(2));
        int swept = await _coordinator.SweepExpiredAsync(default);

        swept.Should().Be(1);
        (await _approvals.GetAsync(approval.ApprovalId, default))!.State.Should().Be(ApprovalState.Expired);

        WorkflowInstance? instance = await _instances.GetAsync("i1", default);
        instance!.Status.Should().Be(InstanceStatus.DeadStopped);
        instance.TerminalReason.Should().Be("ApprovalExpired");
    }

    [Fact]
    public async Task Expiry_auto_approve_resumes_and_flags_the_decision()
    {
        ApprovalRequest approval = await RaiseAsync(new ApprovalGate
        {
            Mode = ExecutionMode.RequireApproval,
            Expiry = TimeSpan.FromHours(1),
            OnExpiry = ExpiryAction.AutoApprove
        });

        _clock.Advance(TimeSpan.FromHours(2));
        await _coordinator.SweepExpiredAsync(default);

        (await _approvals.GetAsync(approval.ApprovalId, default))!.State.Should().Be(ApprovalState.Approved);
        (await _instances.GetAsync("i1", default))!.Status.Should().Be(InstanceStatus.Dispatchable);

        IReadOnlyList<ApprovalDecision> decisions = await _approvals.GetDecisionsAsync(approval.ApprovalId, default);
        decisions.Should().ContainSingle().Which.AutoApproved.Should().BeTrue("auto-approval must be visible in the audit trail");
    }

    [Fact]
    public async Task Expiry_reject_routes_through_the_workflow()
    {
        ApprovalRequest approval = await RaiseAsync(new ApprovalGate
        {
            Mode = ExecutionMode.RequireApproval,
            Expiry = TimeSpan.FromHours(1),
            OnExpiry = ExpiryAction.Reject
        });

        _clock.Advance(TimeSpan.FromHours(2));
        await _coordinator.SweepExpiredAsync(default);

        (await _approvals.GetAsync(approval.ApprovalId, default))!.State.Should().Be(ApprovalState.Rejected);
        (await _instances.GetAsync("i1", default))!.Status.Should().Be(InstanceStatus.Dispatchable);
    }

    [Fact]
    public async Task Escalation_extends_once_then_dead_stops()
    {
        ApprovalRequest approval = await RaiseAsync(new ApprovalGate
        {
            Mode = ExecutionMode.RequireApproval,
            Expiry = TimeSpan.FromHours(1),
            OnExpiry = ExpiryAction.Escalate
        });

        _clock.Advance(TimeSpan.FromHours(2));
        await _coordinator.SweepExpiredAsync(default);

        ApprovalRequest? afterFirst = await _approvals.GetAsync(approval.ApprovalId, default);
        afterFirst!.State.Should().Be(ApprovalState.Pending);
        afterFirst.EscalatedOnce.Should().BeTrue();

        await _coordinator.SweepExpiredAsync(default);

        (await _approvals.GetAsync(approval.ApprovalId, default))!.State.Should().Be(ApprovalState.Expired);
        (await _instances.GetAsync("i1", default))!.Status.Should().Be(InstanceStatus.DeadStopped);
    }

    [Fact]
    public async Task Sweep_ignores_approvals_that_are_not_yet_due()
    {
        await RaiseAsync(new ApprovalGate { Mode = ExecutionMode.RequireApproval, Expiry = TimeSpan.FromHours(8) });

        _clock.Advance(TimeSpan.FromHours(1));

        (await _coordinator.SweepExpiredAsync(default)).Should().Be(0);
    }

    [Fact]
    public async Task Expiry_emits_an_approval_expired_event()
    {
        await RaiseAsync(new ApprovalGate
        {
            Mode = ExecutionMode.RequireApproval,
            Expiry = TimeSpan.FromMinutes(1),
            OnExpiry = ExpiryAction.DeadStop
        });

        _clock.Advance(TimeSpan.FromHours(1));
        await _coordinator.SweepExpiredAsync(default);

        Page<EventEnvelope> page = await _events.QueryAsync(new EventQuery("i1", Limit: 100), default);
        page.Items.Should().Contain(e => e.EventType == WorkflowEventTypes.ApprovalExpired);
    }

    [Fact]
    public async Task Decision_emits_an_approval_decided_event()
    {
        ApprovalRequest approval = await RaiseAsync();

        await _coordinator.ApplyDecisionAsync(approval.ApprovalId,
            new ApprovalDecisionInput { Decision = ApprovalOutcomeKind.Approve }, User("alice"), default);

        Page<EventEnvelope> page = await _events.QueryAsync(new EventQuery("i1", Limit: 100), default);
        page.Items.Should().Contain(e => e.EventType == WorkflowEventTypes.ApprovalDecided);
    }

    [Fact]
    public async Task Explicit_decider_override_is_honoured()
    {
        ApprovalRequest approval = await RaiseAsync();

        await _coordinator.ApplyDecisionAsync(approval.ApprovalId, new ApprovalDecisionInput
        {
            Decision = ApprovalOutcomeKind.Approve,
            DeciderIdOverride = "service-account"
        }, null, default);

        (await _approvals.GetDecisionsAsync(approval.ApprovalId, default))[0].DeciderId.Should().Be("service-account");
    }
}

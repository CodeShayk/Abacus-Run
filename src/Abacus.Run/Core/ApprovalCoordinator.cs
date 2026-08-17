using System.Security.Claims;
using System.Text.Json;
using Abacus.Run.Abstractions;

namespace Abacus.Run.Core;

public interface IApprovalService : IApprovalCoordinator
{
    ValueTask<DecisionResult> ApplyDecisionAsync(
        string approvalId, ApprovalDecisionInput input, ClaimsPrincipal? user, CancellationToken cancellationToken);

    ValueTask<int> SweepExpiredAsync(CancellationToken cancellationToken);
}

public sealed record ApprovalDecisionInput
{
    public required ApprovalOutcomeKind Decision { get; init; }
    public string? Comment { get; init; }
    public JsonElement? ModifiedInput { get; init; }
    public string? DeciderIdOverride { get; init; }
}

/// <summary>
/// Owns the approval lifecycle: raising a request when a gate trips, applying decisions under
/// single-winner concurrency, and applying expiry policy.
/// </summary>
public sealed class ApprovalCoordinator : IApprovalService
{
    private readonly IApprovalStore _approvals;
    private readonly IInstanceStore _instances;
    private readonly INotificationSink _events;
    private readonly NotificationSequencer _sequencer;
    private readonly IAuditStore? _audit;
    private readonly TimeProvider _clock;

    public ApprovalCoordinator(
        IApprovalStore approvals,
        IInstanceStore instances,
        INotificationSink events,
        NotificationSequencer sequencer,
        IAuditStore? audit = null,
        TimeProvider? clock = null)
    {
        _approvals = approvals;
        _instances = instances;
        _events = events;
        _sequencer = sequencer ?? throw new ArgumentNullException(nameof(sequencer));
        _audit = audit;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Approval events must share the instance's sequence space with progress events — that is what
    /// puts them on the same SSE stream and inside the same Last-Event-ID replay guarantee.
    /// </summary>
    private ValueTask PublishAsync(EventEnvelope envelope, CancellationToken cancellationToken)
        => _events.PublishAsync(
            envelope with { Sequence = _sequencer.Next(envelope.InstanceId) }, cancellationToken);

    public async ValueTask<ApprovalRequest> RaiseAsync(
        string instanceId, string executorId, ApprovalGate gate, object? input, int superstep, CancellationToken cancellationToken)
    {
        WorkflowInstance? instance = await _instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = _clock.GetUtcNow();

        var request = new ApprovalRequest
        {
            ApprovalId = IdGenerator.NewId("apr"),
            InstanceId = instanceId,
            TenantId = instance?.TenantId ?? "unknown",
            ExecutorId = executorId,
            Superstep = superstep,
            CheckpointId = instance?.LatestCheckpointId,
            Reason = gate.Reason,
            ProposedInputJson = input is null ? null : JsonSerializer.Serialize(input),
            Assignees = gate.Assignees,
            RequiredApprovers = gate.RequiredApprovers,
            AllowModification = gate.AllowModification,
            RequireSegregationOfDuties = gate.RequireSegregationOfDuties,
            State = ApprovalState.Pending,
            CreatedAt = now,
            ExpiresAt = now + gate.Expiry,
            OnExpiry = gate.OnExpiry
        };

        ApprovalRequest created = await _approvals.CreateAsync(request, cancellationToken).ConfigureAwait(false);

        await PublishAsync(NotificationFactory.ApprovalRequested(created), cancellationToken).ConfigureAwait(false);

        return created;
    }

    public async ValueTask<DecisionResult> ApplyDecisionAsync(
        string approvalId, ApprovalDecisionInput input, ClaimsPrincipal? user, CancellationToken cancellationToken)
    {
        ApprovalRequest? approval = await _approvals.GetAsync(approvalId, cancellationToken).ConfigureAwait(false);
        if (approval is null)
        {
            return new DecisionResult(DecisionResultKind.NotFound);
        }

        if (approval.State != ApprovalState.Pending)
        {
            return new DecisionResult(DecisionResultKind.AlreadyDecided, approval, $"Already {approval.State}.");
        }

        string deciderId = input.DeciderIdOverride
            ?? user?.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? user?.FindFirst("sub")?.Value
            ?? "anonymous";

        if (!IsAuthorized(approval, deciderId, user, out string? denial))
        {
            return new DecisionResult(DecisionResultKind.Forbidden, approval, denial);
        }

        if (input.Decision == ApprovalOutcomeKind.ApproveWithModification && !approval.AllowModification)
        {
            return new DecisionResult(DecisionResultKind.InvalidModification, approval,
                "This gate does not permit input modification.");
        }

        var decision = new ApprovalDecision
        {
            ApprovalId = approvalId,
            DeciderId = deciderId,
            Outcome = input.Decision,
            Comment = input.Comment,
            ModifiedInputJson = input.ModifiedInput?.GetRawText(),
            DecidedAt = _clock.GetUtcNow()
        };

        IReadOnlyList<ApprovalDecision> existing =
            await _approvals.GetDecisionsAsync(approvalId, cancellationToken).ConfigureAwait(false);

        if (existing.Any(d => d.DeciderId == deciderId))
        {
            return new DecisionResult(DecisionResultKind.AlreadyDecided, approval, "This principal already voted.");
        }

        int approvals = existing.Count(d => d.Outcome != ApprovalOutcomeKind.Reject)
            + (input.Decision == ApprovalOutcomeKind.Reject ? 0 : 1);

        ApprovalState? newState = input.Decision == ApprovalOutcomeKind.Reject
            ? ApprovalState.Rejected
            : approvals >= approval.RequiredApprovers
                ? ApprovalState.Approved
                : null;   // quorum not yet met — stay Pending

        bool recorded = await _approvals
            .TryRecordDecisionAsync(approvalId, decision, newState, cancellationToken)
            .ConfigureAwait(false);

        if (!recorded)
        {
            ApprovalRequest? current = await _approvals.GetAsync(approvalId, cancellationToken).ConfigureAwait(false);
            return new DecisionResult(DecisionResultKind.AlreadyDecided, current ?? approval, "Lost a concurrent decision.");
        }

        await _audit.SafeWriteAsync(new AuditEntry
        {
            Action = $"approval.{input.Decision}".ToLowerInvariant(),
            InstanceId = approval.InstanceId,
            ApprovalId = approvalId,
            ActorId = deciderId,
            Reason = input.Comment,
            OccurredAt = decision.DecidedAt
        }, cancellationToken).ConfigureAwait(false);

        await PublishAsync(NotificationFactory.ApprovalDecided(approval, decision, newState), cancellationToken).ConfigureAwait(false);

        if (newState is null)
        {
            return new DecisionResult(DecisionResultKind.QuorumPending, approval,
                $"{approvals} of {approval.RequiredApprovers} approvals recorded.");
        }

        // Wake the instance: any replica may now lease and resume it.
        await _instances.UpdateAsync(approval.InstanceId, m =>
        {
            m.Status = InstanceStatus.Dispatchable;
            m.ClearLease = true;
        }, cancellationToken).ConfigureAwait(false);

        return new DecisionResult(DecisionResultKind.Accepted, approval with { State = newState.Value });
    }

    public async ValueTask<int> SweepExpiredAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock.GetUtcNow();
        IReadOnlyList<ApprovalRequest> expired =
            await _approvals.ClaimExpiredAsync(now, 100, cancellationToken).ConfigureAwait(false);

        foreach (ApprovalRequest approval in expired)
        {
            await ApplyExpiryAsync(approval, now, cancellationToken).ConfigureAwait(false);
        }

        return expired.Count;
    }

    private async ValueTask ApplyExpiryAsync(ApprovalRequest approval, DateTimeOffset now, CancellationToken cancellationToken)
    {
        switch (approval.OnExpiry)
        {
            case ExpiryAction.AutoApprove:
                await _approvals.TryRecordDecisionAsync(approval.ApprovalId, new ApprovalDecision
                {
                    ApprovalId = approval.ApprovalId,
                    DeciderId = "system:expiry",
                    Outcome = ApprovalOutcomeKind.Approve,
                    Comment = "Auto-approved on expiry.",
                    DecidedAt = now,
                    AutoApproved = true
                }, ApprovalState.Approved, cancellationToken).ConfigureAwait(false);

                await _instances.UpdateAsync(approval.InstanceId, m =>
                {
                    m.Status = InstanceStatus.Dispatchable;
                    m.ClearLease = true;
                }, cancellationToken).ConfigureAwait(false);
                break;

            case ExpiryAction.Reject:
                await _approvals.TrySetStateAsync(approval.ApprovalId, ApprovalState.Rejected, cancellationToken)
                    .ConfigureAwait(false);
                await _instances.UpdateAsync(approval.InstanceId, m =>
                {
                    m.Status = InstanceStatus.Dispatchable;
                    m.ClearLease = true;
                }, cancellationToken).ConfigureAwait(false);
                break;

            case ExpiryAction.Escalate when !approval.EscalatedOnce:
                // Extend once; a second expiry falls through to DeadStop.
                await _approvals.TrySetStateAsync(approval.ApprovalId, ApprovalState.Pending, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case ExpiryAction.Escalate:
            case ExpiryAction.DeadStop:
            default:
                await _approvals.TrySetStateAsync(approval.ApprovalId, ApprovalState.Expired, cancellationToken)
                    .ConfigureAwait(false);
                await _instances.UpdateAsync(approval.InstanceId, m =>
                {
                    m.Status = InstanceStatus.DeadStopped;
                    m.TerminalReason = "ApprovalExpired";
                    m.CompletedAt = now;
                    m.ClearLease = true;
                }, cancellationToken).ConfigureAwait(false);
                break;
        }

        await PublishAsync(NotificationFactory.ApprovalExpired(approval), cancellationToken).ConfigureAwait(false);
    }

    private static bool IsAuthorized(ApprovalRequest approval, string deciderId, ClaimsPrincipal? user, out string? denial)
    {
        denial = null;

        if (approval.Assignees.Count > 0)
        {
            bool matches = approval.Assignees.Any(a =>
                string.Equals(a, deciderId, StringComparison.OrdinalIgnoreCase) ||
                (a.StartsWith("group:", StringComparison.OrdinalIgnoreCase) &&
                 user?.IsInRole(a["group:".Length..]) == true));

            if (!matches)
            {
                denial = "Principal is not an assignee for this approval.";
                return false;
            }
        }

        if (approval.RequireSegregationOfDuties &&
            approval.InitiatorId is { Length: > 0 } initiator &&
            string.Equals(initiator, deciderId, StringComparison.OrdinalIgnoreCase))
        {
            denial = "Segregation of duties: the initiator cannot approve their own instance.";
            return false;
        }

        return true;
    }
}

internal static class AuditExtensions
{
    public static ValueTask SafeWriteAsync(this IAuditStore? store, AuditEntry entry, CancellationToken cancellationToken)
        => store is null ? ValueTask.CompletedTask : store.WriteAsync(entry, cancellationToken);
}

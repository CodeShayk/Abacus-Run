using System.Text.Json;
using Abacus.Run.Abstractions;

namespace Abacus.Run.Core;

/// <summary>
/// Resolves the effective gate for an executor and decides whether an invocation proceeds, pauses,
/// or fails.
/// </summary>
/// <remarks>
/// Precedence (highest first): per-instance override, runtime policy store, workflow definition,
/// host default (<see cref="ExecutionMode.Autonomous"/>). A policy-store failure falls back to the
/// definition's gate — never to Autonomous, which would silently un-gate a protected executor.
/// </remarks>
public sealed class GateEvaluator : IGateEvaluator
{
    private readonly IReadOnlyDictionary<string, ApprovalGate> _definitionGates;
    private readonly IGatePolicyStore? _policyStore;
    private readonly IApprovalStore? _approvalStore;
    private readonly string _workflowName;
    private readonly string _workflowVersion;

    public GateEvaluator(
        string workflowName,
        string workflowVersion,
        IReadOnlyDictionary<string, ApprovalGate> definitionGates,
        IGatePolicyStore? policyStore = null,
        IApprovalStore? approvalStore = null)
    {
        _workflowName = workflowName;
        _workflowVersion = workflowVersion;
        _definitionGates = definitionGates ?? new Dictionary<string, ApprovalGate>();
        _policyStore = policyStore;
        _approvalStore = approvalStore;
    }

    public async ValueTask<GateOutcome> EvaluateAsync(
        string instanceId, string executorId, object? input, CancellationToken cancellationToken)
    {
        // A decision already recorded for this executor wins over any gate evaluation: the instance
        // is being resumed and the engine is re-delivering the message it parked on.
        if (_approvalStore is not null)
        {
            GateOutcome? resolved = await ResolveExistingDecisionAsync(instanceId, executorId, cancellationToken)
                .ConfigureAwait(false);
            if (resolved is not null)
            {
                return resolved;
            }
        }

        ApprovalGate gate = await ResolveGateAsync(instanceId, executorId, cancellationToken).ConfigureAwait(false);

        return gate.Mode switch
        {
            ExecutionMode.Autonomous => GateOutcome.Proceed,
            ExecutionMode.RequireApproval => new GateOutcome(GateOutcomeKind.Pause, gate),
            ExecutionMode.Conditional when gate.Predicate is null => GateOutcome.Proceed,
            ExecutionMode.Conditional =>
                await gate.Predicate!(input!).ConfigureAwait(false)
                    ? new GateOutcome(GateOutcomeKind.Pause, gate)
                    : GateOutcome.Proceed,
            _ => GateOutcome.Proceed
        };
    }

    private async ValueTask<GateOutcome?> ResolveExistingDecisionAsync(
        string instanceId, string executorId, CancellationToken cancellationToken)
    {
        IReadOnlyList<ApprovalRequest> approvals =
            await _approvalStore!.ListForInstanceAsync(instanceId, cancellationToken).ConfigureAwait(false);

        ApprovalRequest? latest = approvals
            .Where(a => a.ExecutorId == executorId)
            .OrderByDescending(a => a.CreatedAt)
            .ThenByDescending(a => a.ApprovalId, StringComparer.Ordinal)
            .FirstOrDefault();

        if (latest is null)
        {
            return null;
        }

        switch (latest.State)
        {
            case ApprovalState.Approved:
            {
                IReadOnlyList<ApprovalDecision> decisions =
                    await _approvalStore.GetDecisionsAsync(latest.ApprovalId, cancellationToken).ConfigureAwait(false);

                ApprovalDecision? modification = decisions
                    .Where(d => d.Outcome == ApprovalOutcomeKind.ApproveWithModification && d.ModifiedInputJson is not null)
                    .OrderByDescending(d => d.DecidedAt)
                    .FirstOrDefault();

                object? modified = null;
                if (modification is not null && latest.ProposedInputJson is not null)
                {
                    modified = JsonSerializer.Deserialize<JsonElement>(modification.ModifiedInputJson!);
                }

                return new GateOutcome(GateOutcomeKind.ProceedApproved, ModifiedInput: modified, ApprovalId: latest.ApprovalId);
            }

            case ApprovalState.Rejected:
            {
                IReadOnlyList<ApprovalDecision> decisions =
                    await _approvalStore.GetDecisionsAsync(latest.ApprovalId, cancellationToken).ConfigureAwait(false);
                string? comment = decisions.LastOrDefault(d => d.Outcome == ApprovalOutcomeKind.Reject)?.Comment;
                return new GateOutcome(GateOutcomeKind.Rejected, ApprovalId: latest.ApprovalId, Comment: comment);
            }

            case ApprovalState.Expired:
                return latest.OnExpiry switch
                {
                    ExpiryAction.AutoApprove => new GateOutcome(GateOutcomeKind.ProceedApproved, ApprovalId: latest.ApprovalId),
                    _ => new GateOutcome(GateOutcomeKind.Rejected, ApprovalId: latest.ApprovalId, Comment: "Approval expired.")
                };

            case ApprovalState.Pending:
                // Already raised and still undecided: park again without creating a duplicate request.
                return new GateOutcome(GateOutcomeKind.Pause, null, ApprovalId: latest.ApprovalId);

            case ApprovalState.Cancelled:
            default:
                return null;
        }
    }

    private async ValueTask<ApprovalGate> ResolveGateAsync(
        string instanceId, string executorId, CancellationToken cancellationToken)
    {
        ApprovalGate definitionGate = _definitionGates.TryGetValue(executorId, out ApprovalGate? declared)
            ? declared
            : ApprovalGate.Autonomous;

        if (_policyStore is null)
        {
            return definitionGate;
        }

        try
        {
            ApprovalGate? policy = await _policyStore
                .FindAsync(_workflowName, _workflowVersion, executorId, instanceId, cancellationToken)
                .ConfigureAwait(false);

            return policy ?? definitionGate;
        }
        catch
        {
            // Fail closed: a policy lookup failure must not un-gate a protected executor.
            return definitionGate;
        }
    }
}

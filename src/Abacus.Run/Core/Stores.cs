using Abacus.Run.Abstractions;

namespace Abacus.Run.Core;

/// <summary>Control-plane store for instance records.</summary>
public interface IInstanceStore
{
    ValueTask<WorkflowInstance> CreateAsync(CreateInstanceRequest request, CancellationToken cancellationToken);

    ValueTask<WorkflowInstance?> GetAsync(string instanceId, CancellationToken cancellationToken);

    ValueTask<WorkflowInstance?> FindByIdempotencyKeyAsync(string tenantId, string idempotencyKey, CancellationToken cancellationToken);

    ValueTask<Page<WorkflowInstance>> QueryAsync(InstanceQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Optimistic transition. Returns false when the instance is not in <paramref name="from"/>,
    /// is already terminal, or lost a concurrent write.
    /// </summary>
    ValueTask<bool> TryTransitionAsync(
        string instanceId,
        InstanceStatus from,
        InstanceStatus to,
        Action<InstanceMutation>? mutate,
        CancellationToken cancellationToken);

    /// <summary>Unconditional update used by the owning replica for bookkeeping (checkpoint pointer, lease).</summary>
    ValueTask<bool> UpdateAsync(string instanceId, Action<InstanceMutation> mutate, CancellationToken cancellationToken);

    /// <summary>Atomically claims up to <paramref name="max"/> claimable instances for this replica.</summary>
    ValueTask<IReadOnlyList<WorkflowInstance>> ClaimAsync(
        string replicaId, int max, TimeSpan leaseDuration, DateTimeOffset now, CancellationToken cancellationToken);

    ValueTask<bool> RenewLeaseAsync(string instanceId, string replicaId, TimeSpan leaseDuration, CancellationToken cancellationToken);

    ValueTask ReleaseLeaseAsync(string instanceId, string replicaId, CancellationToken cancellationToken);
}

/// <summary>Mutable projection handed to store callers; the store applies it under concurrency control.</summary>
public sealed class InstanceMutation
{
    public InstanceStatus? Status { get; set; }
    public string? ResultJson { get; set; }
    public string? TerminalReason { get; set; }
    public int? AttemptCount { get; set; }
    public DateTimeOffset? NextRetryAt { get; set; }
    public bool ClearNextRetryAt { get; set; }
    public string? LatestCheckpointId { get; set; }
    public string? LeaseOwner { get; set; }
    public bool ClearLease { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public bool ClearCompletedAt { get; set; }
    public bool? CancellationRequested { get; set; }

    /// <summary>
    /// Permits a transition out of a terminal state. Terminal states are otherwise final so a late
    /// writer (a stale runner finishing after termination) cannot resurrect or reclassify an
    /// instance. Set only by the deliberate, audited operator revive path.
    /// </summary>
    public bool AllowTerminalTransition { get; set; }
}

public interface IEventStore
{
    ValueTask AppendBatchAsync(IReadOnlyList<EventEnvelope> events, CancellationToken cancellationToken);

    ValueTask<Page<EventEnvelope>> QueryAsync(EventQuery query, CancellationToken cancellationToken);

    IAsyncEnumerable<EventEnvelope> ReadAsync(string instanceId, long fromExclusive, CancellationToken cancellationToken);

    ValueTask<long> MaxSequenceAsync(string instanceId, CancellationToken cancellationToken);
}

public sealed record EventQuery(
    string InstanceId,
    long FromExclusive = 0,
    long? ToInclusive = null,
    int Limit = 100,
    IReadOnlyList<string>? Types = null);

public interface ILogStore
{
    ValueTask AppendAsync(InstanceLogEntry entry, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<InstanceLogEntry>> QueryAsync(
        string instanceId, string? level, string? executorId, int limit, CancellationToken cancellationToken);
}

public interface IApprovalStore
{
    ValueTask<ApprovalRequest> CreateAsync(ApprovalRequest request, CancellationToken cancellationToken);

    ValueTask<ApprovalRequest?> GetAsync(string approvalId, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<ApprovalRequest>> ListForInstanceAsync(string instanceId, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<ApprovalRequest>> QueryPendingAsync(
        string? tenantId, IReadOnlyList<string>? assignees, int limit, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<ApprovalRequest>> ClaimExpiredAsync(DateTimeOffset now, int max, CancellationToken cancellationToken);

    /// <summary>Records a vote. Returns false if the approval already left the Pending state.</summary>
    ValueTask<bool> TryRecordDecisionAsync(
        string approvalId, ApprovalDecision decision, ApprovalState? newState, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<ApprovalDecision>> GetDecisionsAsync(string approvalId, CancellationToken cancellationToken);

    ValueTask<bool> TrySetStateAsync(string approvalId, ApprovalState state, CancellationToken cancellationToken);

    ValueTask CancelForInstanceAsync(string instanceId, CancellationToken cancellationToken);
}

/// <summary>
/// Runtime-editable gate policy (PRD FR-9.2), keyed by tenant/workflow/version/executor.
/// </summary>
/// <remarks>
/// A null <c>tenantId</c> addresses the host-wide policy, which applies to every tenant that has no
/// policy of its own. Resolution order in <see cref="FindAsync"/> is: per-instance override, the
/// tenant's own policy, the host-wide policy, then nothing — leaving the definition's gate to stand.
/// </remarks>
public interface IGatePolicyStore
{
    ValueTask<ApprovalGate?> FindAsync(
        string? tenantId, string workflowName, string workflowVersion, string executorId, string? instanceId,
        CancellationToken cancellationToken);

    /// <summary>Policies written at exactly this scope, by executor id. Never merged with another scope.</summary>
    ValueTask<IReadOnlyDictionary<string, ApprovalGate>> ListAsync(
        string? tenantId, string workflowName, string workflowVersion, CancellationToken cancellationToken);

    ValueTask SetAsync(
        string? tenantId, string workflowName, string workflowVersion, string executorId, ApprovalGate gate,
        CancellationToken cancellationToken);

    /// <summary>Drops the policy at this scope. Returns false when there was nothing to drop.</summary>
    ValueTask<bool> RemoveAsync(
        string? tenantId, string workflowName, string workflowVersion, string executorId,
        CancellationToken cancellationToken);

    ValueTask SetInstanceOverrideAsync(string instanceId, string executorId, ApprovalGate gate, CancellationToken cancellationToken);
}

/// <summary>Overflow storage for large checkpoint payloads.</summary>
public interface IBlobStore
{
    ValueTask<string> UploadAsync(string key, byte[] content, CancellationToken cancellationToken);

    ValueTask<byte[]> DownloadAsync(string uri, CancellationToken cancellationToken);

    ValueTask DeleteAsync(string uri, CancellationToken cancellationToken);
}

public interface IAuditStore
{
    ValueTask WriteAsync(AuditEntry entry, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<AuditEntry>> QueryAsync(string? instanceId, int limit, CancellationToken cancellationToken);
}

public sealed record AuditEntry
{
    public required string Action { get; init; }
    public string? InstanceId { get; init; }
    public string? ApprovalId { get; init; }
    public required string ActorId { get; init; }
    public string? Reason { get; init; }
    public string? Detail { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
}

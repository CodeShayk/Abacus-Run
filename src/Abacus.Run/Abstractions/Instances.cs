namespace Abacus.Run.Abstractions;

/// <summary>
/// Control-plane state of an instance. A superset of the engine's <c>RunStatus</c>: it adds the
/// states the engine does not model (retry scheduling, dead-stop, approval parking).
/// </summary>
public enum InstanceStatus
{
    Pending,
    Running,
    AwaitingInput,
    AwaitingApproval,
    Suspended,
    RetryScheduled,
    Dispatchable,
    Completed,
    Failed,
    DeadStopped,
    Cancelled
}

public static class InstanceStatusExtensions
{
    public static bool IsTerminal(this InstanceStatus status) => status
        is InstanceStatus.Completed
        or InstanceStatus.Failed
        or InstanceStatus.DeadStopped
        or InstanceStatus.Cancelled;

    /// <summary>True when the dispatcher may lease the instance.</summary>
    public static bool IsClaimable(this InstanceStatus status) => status
        is InstanceStatus.Pending
        or InstanceStatus.RetryScheduled
        or InstanceStatus.Dispatchable;
}

public sealed record WorkflowInstance
{
    public required string InstanceId { get; init; }
    public required string TenantId { get; init; }
    public required string WorkflowName { get; init; }
    public required string WorkflowVersion { get; init; }
    public required InstanceStatus Status { get; init; }
    public string? ContextJson { get; init; }
    public string? ResultJson { get; init; }
    public string? TerminalReason { get; init; }
    public int AttemptCount { get; init; }
    public int MaxAttempts { get; init; } = 5;
    public DateTimeOffset? NextRetryAt { get; init; }
    public string? LatestCheckpointId { get; init; }
    public string? LeaseOwner { get; init; }
    public DateTimeOffset? LeaseExpiresAt { get; init; }
    public string? CorrelationId { get; init; }
    public string? IdempotencyKey { get; init; }
    public string? RerunOfInstanceId { get; init; }
    public bool CancellationRequested { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public long Version { get; init; }
}

public sealed record CreateInstanceRequest
{
    public required string InstanceId { get; init; }
    public required string TenantId { get; init; }
    public required string WorkflowName { get; init; }
    public required string WorkflowVersion { get; init; }
    public string? ContextJson { get; init; }
    public string? CorrelationId { get; init; }
    public string? IdempotencyKey { get; init; }
    public string? RerunOfInstanceId { get; init; }
    public int MaxAttempts { get; init; } = 5;
}

public sealed record InstanceQuery
{
    public string? TenantId { get; init; }
    public IReadOnlyList<InstanceStatus>? Statuses { get; init; }
    public string? WorkflowName { get; init; }
    public string? WorkflowVersion { get; init; }
    public string? CorrelationId { get; init; }
    public DateTimeOffset? CreatedAfter { get; init; }
    public DateTimeOffset? CreatedBefore { get; init; }
    public int Limit { get; init; } = 50;
    public int Offset { get; init; }
}

public sealed record Page<T>(IReadOnlyList<T> Items, int Total, string? NextCursor = null);

/// <summary>A durable progress event. The SSE <c>id:</c> and the stored sequence are the same value.</summary>
public sealed record EventEnvelope
{
    public required string InstanceId { get; init; }
    public required long Sequence { get; init; }
    public required string EventType { get; init; }
    public string? TenantId { get; init; }
    public string? ExecutorId { get; init; }
    public int? Superstep { get; init; }
    public required string PayloadJson { get; init; }
    public string? TraceId { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
}

public static class WorkflowEventTypes
{
    public const string WorkflowStarted = "workflow.started";
    public const string SuperstepStarted = "superstep.started";
    public const string SuperstepCompleted = "superstep.completed";
    public const string ExecutorInvoked = "executor.invoked";
    public const string ExecutorCompleted = "executor.completed";
    public const string ExecutorFailed = "executor.failed";
    public const string LlmDelta = "llm.delta";
    public const string RequestPending = "request.pending";
    public const string ApprovalRequested = "approval.requested";
    public const string ApprovalDecided = "approval.decided";
    public const string ApprovalExpired = "approval.expired";
    public const string WorkflowWarning = "workflow.warning";
    public const string WorkflowOutput = "workflow.output";
    public const string WorkflowTerminated = "workflow.terminated";
    public const string InstanceCancelled = "instance.cancelled";
    public const string InstanceRerunRequested = "instance.rerun_requested";
    public const string InstanceSuspended = "instance.suspended";
    public const string InstanceResumed = "instance.resumed";
    public const string InstanceRetryForced = "instance.retry_forced";
    public const string Heartbeat = "heartbeat";
}

public sealed record InstanceLogEntry
{
    public required string InstanceId { get; init; }
    public required long Sequence { get; init; }
    public required string Level { get; init; }
    public string? ExecutorId { get; init; }
    public int? Superstep { get; init; }
    public required string Message { get; init; }
    public string? ExceptionType { get; init; }
    public string? StackTrace { get; init; }
    public string? TraceId { get; init; }
    public required DateTimeOffset LoggedAt { get; init; }
}

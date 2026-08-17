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

    /// <summary>
    /// The workflow this instance is running. Denormalised onto the event so the log can be read and
    /// filtered by workflow without joining back to the instance row.
    /// </summary>
    public string? WorkflowName { get; init; }

    /// <summary>Where this event goes: the durable log, live subscribers, or both.</summary>
    public EventDeliveryMode Delivery { get; init; } = EventDeliveryMode.StreamAndLog;

    /// <summary>True when the event carries no sequence number and leaves no durable record.</summary>
    public bool IsStreamOnly => Delivery == EventDeliveryMode.StreamOnly;
}

/// <summary>
/// Where one event is delivered. One enum rather than a pair of booleans, because "neither" is not a
/// meaningful destination and should not be representable.
/// </summary>
/// <remarks>
/// This is the runtime's own view of a single envelope, not a menu a workflow picks from. A workflow
/// switches the live stream on or off through <c>NotificationPolicy.StreamEvents</c>; the durable
/// log is unconditional for every event that has one, and nothing a workflow can declare turns it
/// off.
/// </remarks>
public enum EventDeliveryMode
{
    /// <summary>Appended to the durable log and fanned out to live subscribers. The default.</summary>
    StreamAndLog,

    /// <summary>
    /// Appended to the durable log, with no live fan-out. What every ordinary event becomes when its
    /// workflow has turned streaming off — the record is unchanged, only its timeliness.
    /// </summary>
    LogOnly,

    /// <summary>
    /// Fanned out to live subscribers only, carrying no sequence number and leaving no record.
    /// </summary>
    /// <remarks>
    /// For data with no replay value — streamed LLM tokens, where the complete text is in the
    /// executor's output anyway. Taking no sequence number keeps the durable sequence gapless so
    /// <c>Last-Event-ID</c> catch-up still works, and the event is written to SSE without an
    /// <c>id:</c> field, which stops a reconnecting client waiting for a chunk that no longer exists.
    /// A token stream is not resumable and the transport should say so.
    /// </remarks>
    StreamOnly
}

/// <summary>
/// Streaming token event, carried on the engine's own event stream so it stays ordered with the
/// executor events around it.
/// </summary>
/// <remarks>
/// Lives here rather than beside <c>LlmExecutor</c> because the runner has to translate it, and
/// <c>Core</c> does not reference <c>Executors</c>. Translated to a transient
/// <see cref="WorkflowEventTypes.LlmDelta"/> — live fan-out, never stored.
/// </remarks>
public sealed class LlmDeltaWorkflowEvent : Microsoft.Agents.AI.Workflows.WorkflowEvent
{
    public LlmDeltaWorkflowEvent(string executorId, string delta) : base(delta)
    {
        ExecutorId = executorId;
        Delta = delta;
    }

    public string ExecutorId { get; }
    public string Delta { get; }
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

    /// <summary>One per LLM invocation: model, tokens, cost, latency, finish reason.</summary>
    public const string LlmCompleted = "llm.completed";
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

    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        WorkflowStarted, SuperstepStarted, SuperstepCompleted,
        ExecutorInvoked, ExecutorCompleted, ExecutorFailed,
        LlmDelta, LlmCompleted, RequestPending,
        ApprovalRequested, ApprovalDecided, ApprovalExpired,
        WorkflowWarning, WorkflowOutput, WorkflowTerminated,
        InstanceCancelled, InstanceRerunRequested, InstanceSuspended,
        InstanceResumed, InstanceRetryForced, Heartbeat
    };

    /// <summary>
    /// Whether the framework owns this event name. Workflow-defined events are always prefixed, so
    /// the two namespaces cannot collide and a consumer can tell them apart without a lookup.
    /// </summary>
    public static bool IsReserved(string eventType) => Reserved.Contains(eventType);
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

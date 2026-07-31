namespace Abacus.Run.Abstractions;

/// <summary>Whether an executor runs on its own or pauses for a human decision (PRD FR-9.1).</summary>
public enum ExecutionMode
{
    Autonomous,
    RequireApproval,
    Conditional
}

/// <summary>What happens when an approval window closes with no decision (PRD FR-9.9).</summary>
public enum ExpiryAction
{
    DeadStop,
    Reject,
    AutoApprove,
    Escalate
}

public enum ApprovalState
{
    Pending,
    Approved,
    Rejected,
    Expired,
    Cancelled
}

public enum ApprovalOutcomeKind
{
    Approve,
    Reject,
    ApproveWithModification
}

/// <summary>
/// Gate configuration for one executor. Immutable; produced by <see cref="ApprovalGateBuilder"/>.
/// </summary>
public sealed record ApprovalGate
{
    public static ApprovalGate Autonomous { get; } = new();

    public ExecutionMode Mode { get; init; } = ExecutionMode.Autonomous;

    /// <summary>Evaluated only when <see cref="Mode"/> is <see cref="ExecutionMode.Conditional"/>.</summary>
    public Func<object, ValueTask<bool>>? Predicate { get; init; }

    public string? Reason { get; init; }
    public IReadOnlyList<string> Assignees { get; init; } = [];
    public int RequiredApprovers { get; init; } = 1;
    public TimeSpan Expiry { get; init; } = TimeSpan.FromHours(24);
    public ExpiryAction OnExpiry { get; init; } = ExpiryAction.DeadStop;
    public IReadOnlyList<string> EscalationAssignees { get; init; } = [];
    public bool AllowModification { get; init; }
    public bool RequireSegregationOfDuties { get; init; }
}

public sealed class ApprovalGateBuilder
{
    private ApprovalGate _gate = new() { Mode = ExecutionMode.RequireApproval };

    public ApprovalGateBuilder Mode(ExecutionMode mode)
    {
        _gate = _gate with { Mode = mode };
        return this;
    }

    /// <summary>Typed predicate. A message of an unexpected type never trips the gate.</summary>
    public ApprovalGateBuilder When<T>(Func<T, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        _gate = _gate with
        {
            Mode = ExecutionMode.Conditional,
            Predicate = input => new ValueTask<bool>(input is T typed && predicate(typed))
        };
        return this;
    }

    public ApprovalGateBuilder WhenAsync(Func<object, ValueTask<bool>> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        _gate = _gate with { Mode = ExecutionMode.Conditional, Predicate = predicate };
        return this;
    }

    public ApprovalGateBuilder Reason(string reason)
    {
        _gate = _gate with { Reason = reason };
        return this;
    }

    public ApprovalGateBuilder AssignTo(params string[] assignees)
    {
        _gate = _gate with { Assignees = assignees };
        return this;
    }

    public ApprovalGateBuilder RequireApprovers(int count)
    {
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count), "At least one approver is required.");
        _gate = _gate with { RequiredApprovers = count };
        return this;
    }

    public ApprovalGateBuilder ExpiresAfter(TimeSpan expiry)
    {
        if (expiry <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(expiry));
        _gate = _gate with { Expiry = expiry };
        return this;
    }

    public ApprovalGateBuilder OnExpiry(ExpiryAction action, params string[] escalationAssignees)
    {
        _gate = _gate with { OnExpiry = action, EscalationAssignees = escalationAssignees };
        return this;
    }

    public ApprovalGateBuilder AllowModification(bool allow = true)
    {
        _gate = _gate with { AllowModification = allow };
        return this;
    }

    public ApprovalGateBuilder RequireSegregationOfDuties(bool require = true)
    {
        _gate = _gate with { RequireSegregationOfDuties = require };
        return this;
    }

    public ApprovalGate Build() => _gate;
}

/// <summary>A durable approval request. Persisted before the instance parks.</summary>
public sealed record ApprovalRequest
{
    public required string ApprovalId { get; init; }
    public required string InstanceId { get; init; }
    public required string TenantId { get; init; }
    public required string ExecutorId { get; init; }
    public int Superstep { get; init; }
    public string? CheckpointId { get; init; }
    public string? Reason { get; init; }
    public string? ProposedInputJson { get; init; }
    public IReadOnlyList<string> Assignees { get; init; } = [];
    public int RequiredApprovers { get; init; } = 1;
    public bool AllowModification { get; init; }
    public bool RequireSegregationOfDuties { get; init; }

    /// <summary>Principal that started the instance. Compared against the decider when SoD is on.</summary>
    public string? InitiatorId { get; init; }

    public ApprovalState State { get; init; } = ApprovalState.Pending;
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public ExpiryAction OnExpiry { get; init; } = ExpiryAction.DeadStop;
    public bool EscalatedOnce { get; init; }
}

public sealed record ApprovalDecision
{
    public required string ApprovalId { get; init; }
    public required string DeciderId { get; init; }
    public required ApprovalOutcomeKind Outcome { get; init; }
    public string? Comment { get; init; }
    public string? ModifiedInputJson { get; init; }
    public required DateTimeOffset DecidedAt { get; init; }
    public bool AutoApproved { get; init; }
}

/// <summary>Result of applying a decision, mapped to HTTP by the API layer.</summary>
public enum DecisionResultKind
{
    Accepted,
    AlreadyDecided,
    NotFound,
    Forbidden,
    InvalidModification,
    QuorumPending
}

public sealed record DecisionResult(DecisionResultKind Kind, ApprovalRequest? Approval = null, string? Detail = null)
{
    public bool IsSuccess => Kind is DecisionResultKind.Accepted or DecisionResultKind.QuorumPending;
}

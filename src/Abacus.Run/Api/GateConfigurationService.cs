using System.Security.Claims;
using System.Text.Json;
using Abacus.Run.Abstractions;
using Abacus.Run.Core;

namespace Abacus.Run.Api;

/// <summary>
/// A tenant-supplied execution policy for one executor. Every field except <c>Mode</c> is optional
/// and falls back to the value the workflow definition declared, so
/// <c>{"mode":"requireApproval"}</c> keeps the author's assignees, quorum and expiry.
/// </summary>
public sealed record ExecutionPolicyDto(
    string Mode,
    string? Reason = null,
    IReadOnlyList<string>? Assignees = null,
    int? RequiredApprovers = null,
    int? ExpirySeconds = null,
    string? OnExpiry = null,
    IReadOnlyList<string>? EscalationAssignees = null,
    bool? AllowModification = null,
    bool? RequireSegregationOfDuties = null);

/// <summary>Bulk configuration body: executor id to policy.</summary>
public sealed record NodeConfigurationRequestDto(IReadOnlyDictionary<string, ExecutionPolicyDto> Nodes);

/// <summary>Where the gate an instance would actually run under came from.</summary>
public static class GateSources
{
    public const string Definition = "definition";
    public const string Host = "host";
    public const string Tenant = "tenant";
}

public sealed record ExecutorNodeDto(
    string ExecutorId,
    string ExecutorType,
    string? InputType,
    string? OutputType,
    bool Configurable,
    bool Locked,
    ExecutionPolicyDto Declared,
    ExecutionPolicyDto? TenantOverride,
    ExecutionPolicyDto Effective,
    string EffectiveSource);

public sealed record WorkflowNodesDto(
    string WorkflowName,
    string WorkflowVersion,
    string TenantId,
    IReadOnlyList<ExecutorNodeDto> Nodes);

public enum GateConfigResultKind
{
    Ok,
    UnknownWorkflow,
    UnknownExecutor,
    NotConfigurable,
    Invalid,
    Rejected
}

public sealed record GateConfigResult(
    GateConfigResultKind Kind,
    WorkflowNodesDto? Nodes = null,
    IReadOnlyDictionary<string, string[]>? Errors = null,
    string? Detail = null);

public interface IGateConfigurationService
{
    Task<GateConfigResult> GetNodesAsync(
        string workflowName, string? version, string tenantId, CancellationToken cancellationToken);

    Task<GateConfigResult> SetNodesAsync(
        string workflowName, string? version, string tenantId,
        IReadOnlyDictionary<string, ExecutionPolicyDto> policies, ClaimsPrincipal? user, CancellationToken cancellationToken);

    Task<GateConfigResult> ResetNodeAsync(
        string workflowName, string? version, string tenantId, string executorId,
        ClaimsPrincipal? user, CancellationToken cancellationToken);
}

/// <summary>
/// Reads a workflow's declared executor nodes and applies a tenant's execution policy over them.
/// </summary>
/// <remarks>
/// Writes are all-or-nothing: every policy in a request is validated against the declaration before
/// any of them is persisted, so a rejected node cannot leave a half-applied configuration behind.
/// </remarks>
public sealed class GateConfigurationService : IGateConfigurationService
{
    private readonly IWorkflowRegistry _registry;
    private readonly IWorkflowInspector _inspector;
    private readonly IGatePolicyStore _policies;
    private readonly IAuditStore? _audit;
    private readonly TimeProvider _clock;

    public GateConfigurationService(
        IWorkflowRegistry registry,
        IWorkflowInspector inspector,
        IGatePolicyStore policies,
        IAuditStore? audit = null,
        TimeProvider? clock = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
        _policies = policies ?? throw new ArgumentNullException(nameof(policies));
        _audit = audit;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<GateConfigResult> GetNodesAsync(
        string workflowName, string? version, string tenantId, CancellationToken cancellationToken)
    {
        WorkflowDescriptor? descriptor = Resolve(workflowName, version);
        return descriptor is null
            ? new GateConfigResult(GateConfigResultKind.UnknownWorkflow)
            : new GateConfigResult(GateConfigResultKind.Ok,
                await ProjectAsync(descriptor, tenantId, cancellationToken).ConfigureAwait(false));
    }

    public async Task<GateConfigResult> SetNodesAsync(
        string workflowName, string? version, string tenantId,
        IReadOnlyDictionary<string, ExecutionPolicyDto> policies, ClaimsPrincipal? user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policies);

        WorkflowDescriptor? descriptor = Resolve(workflowName, version);
        if (descriptor is null)
        {
            return new GateConfigResult(GateConfigResultKind.UnknownWorkflow);
        }

        if (policies.Count == 0)
        {
            return new GateConfigResult(GateConfigResultKind.Invalid, Errors: new Dictionary<string, string[]>
            {
                ["nodes"] = ["At least one executor policy is required."]
            });
        }

        IReadOnlyList<WorkflowNodeDescriptor> nodes =
            await _inspector.InspectAsync(descriptor, cancellationToken).ConfigureAwait(false);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var resolved = new List<(WorkflowNodeDescriptor Node, ApprovalGate Gate)>(policies.Count);

        foreach ((string executorId, ExecutionPolicyDto policy) in policies)
        {
            WorkflowNodeDescriptor? node = nodes
                .FirstOrDefault(n => string.Equals(n.ExecutorId, executorId, StringComparison.Ordinal));

            if (node is null)
            {
                return new GateConfigResult(GateConfigResultKind.UnknownExecutor,
                    Detail: $"Workflow '{descriptor.Name}' version '{descriptor.Version}' has no executor '{executorId}'.");
            }

            if (!node.Configurable)
            {
                return new GateConfigResult(GateConfigResultKind.NotConfigurable,
                    Detail: $"Executor '{executorId}' is bound as a raw node and cannot be approval-gated.");
            }

            if (!TryBuildGate(node.DeclaredGate, policy, out ApprovalGate gate, out string[] policyErrors))
            {
                errors[executorId] = policyErrors;
                continue;
            }

            IReadOnlyList<string> violations = GatePolicyRules.Violations(node.DeclaredGate, gate);
            if (violations.Count > 0)
            {
                return new GateConfigResult(GateConfigResultKind.Rejected,
                    Detail: $"Executor '{executorId}' is locked by the workflow definition. " + string.Join(" ", violations));
            }

            resolved.Add((node, gate));
        }

        if (errors.Count > 0)
        {
            return new GateConfigResult(GateConfigResultKind.Invalid, Errors: errors);
        }

        foreach ((WorkflowNodeDescriptor node, ApprovalGate gate) in resolved)
        {
            await _policies
                .SetAsync(Tenancy.Normalize(tenantId), descriptor.Name, descriptor.Version, node.ExecutorId, gate, cancellationToken)
                .ConfigureAwait(false);

            await AuditAsync("gate.policy.set", descriptor, tenantId, node.ExecutorId, user, gate.Mode.ToString(), cancellationToken)
                .ConfigureAwait(false);
        }

        return new GateConfigResult(GateConfigResultKind.Ok,
            await ProjectAsync(descriptor, tenantId, cancellationToken).ConfigureAwait(false));
    }

    public async Task<GateConfigResult> ResetNodeAsync(
        string workflowName, string? version, string tenantId, string executorId,
        ClaimsPrincipal? user, CancellationToken cancellationToken)
    {
        WorkflowDescriptor? descriptor = Resolve(workflowName, version);
        if (descriptor is null)
        {
            return new GateConfigResult(GateConfigResultKind.UnknownWorkflow);
        }

        IReadOnlyList<WorkflowNodeDescriptor> nodes =
            await _inspector.InspectAsync(descriptor, cancellationToken).ConfigureAwait(false);

        if (!nodes.Any(n => string.Equals(n.ExecutorId, executorId, StringComparison.Ordinal)))
        {
            return new GateConfigResult(GateConfigResultKind.UnknownExecutor,
                Detail: $"Workflow '{descriptor.Name}' version '{descriptor.Version}' has no executor '{executorId}'.");
        }

        await _policies
            .RemoveAsync(Tenancy.Normalize(tenantId), descriptor.Name, descriptor.Version, executorId, cancellationToken)
            .ConfigureAwait(false);

        await AuditAsync("gate.policy.reset", descriptor, tenantId, executorId, user, null, cancellationToken)
            .ConfigureAwait(false);

        return new GateConfigResult(GateConfigResultKind.Ok,
            await ProjectAsync(descriptor, tenantId, cancellationToken).ConfigureAwait(false));
    }

    private WorkflowDescriptor? Resolve(string workflowName, string? version)
        => _registry.Resolve(
            workflowName,
            string.IsNullOrWhiteSpace(version) || string.Equals(version, "latest", StringComparison.OrdinalIgnoreCase)
                ? null
                : version);

    private async Task<WorkflowNodesDto> ProjectAsync(
        WorkflowDescriptor descriptor, string tenantId, CancellationToken cancellationToken)
    {
        IReadOnlyList<WorkflowNodeDescriptor> nodes =
            await _inspector.InspectAsync(descriptor, cancellationToken).ConfigureAwait(false);

        IReadOnlyDictionary<string, ApprovalGate> tenantPolicies = await _policies
            .ListAsync(Tenancy.Normalize(tenantId), descriptor.Name, descriptor.Version, cancellationToken).ConfigureAwait(false);

        IReadOnlyDictionary<string, ApprovalGate> hostPolicies = await _policies
            .ListAsync(null, descriptor.Name, descriptor.Version, cancellationToken).ConfigureAwait(false);

        var projected = new List<ExecutorNodeDto>(nodes.Count);

        foreach (WorkflowNodeDescriptor node in nodes)
        {
            tenantPolicies.TryGetValue(node.ExecutorId, out ApprovalGate? tenantGate);
            hostPolicies.TryGetValue(node.ExecutorId, out ApprovalGate? hostGate);

            ApprovalGate? applied = tenantGate ?? hostGate;
            string source = tenantGate is not null
                ? GateSources.Tenant
                : hostGate is not null ? GateSources.Host : GateSources.Definition;

            ApprovalGate effective = applied is null
                ? node.DeclaredGate
                : GatePolicyRules.Reconcile(node.DeclaredGate, applied);

            projected.Add(new ExecutorNodeDto(
                node.ExecutorId,
                node.ExecutorType,
                node.InputType,
                node.OutputType,
                node.Configurable,
                node.DeclaredGate.Locked,
                ToDto(node.DeclaredGate),
                tenantGate is null ? null : ToDto(tenantGate),
                ToDto(effective),
                source));
        }

        return new WorkflowNodesDto(descriptor.Name, descriptor.Version, tenantId, projected);
    }

    /// <summary>
    /// Folds a tenant policy onto the declaration. Conditional is deliberately not accepted: its
    /// predicate is code, and no JSON body can supply one.
    /// </summary>
    private static bool TryBuildGate(
        ApprovalGate declared, ExecutionPolicyDto? policy, out ApprovalGate gate, out string[] errors)
    {
        gate = declared;

        if (policy is null)
        {
            errors = ["A policy body is required."];
            return false;
        }

        var problems = new List<string>();

        if (!TryParseMode(policy.Mode, out ExecutionMode mode))
        {
            problems.Add("mode must be one of: autonomous, requireApproval.");
        }

        ExpiryAction onExpiry = declared.OnExpiry;
        if (policy.OnExpiry is { Length: > 0 } &&
            !Enum.TryParse(policy.OnExpiry, ignoreCase: true, out onExpiry))
        {
            problems.Add("onExpiry must be one of: deadStop, reject, autoApprove, escalate.");
        }

        if (policy.RequiredApprovers is { } approvers && approvers < 1)
        {
            problems.Add("requiredApprovers must be at least 1.");
        }

        if (policy.ExpirySeconds is { } seconds && seconds <= 0)
        {
            problems.Add("expirySeconds must be greater than zero.");
        }

        if (problems.Count > 0)
        {
            errors = [.. problems];
            return false;
        }

        gate = declared with
        {
            Mode = mode,

            // A tenant switching a Conditional gate to always-on (or off) discards the predicate:
            // keeping it would silently re-gate invocations the tenant asked to let through.
            Predicate = null,
            Reason = policy.Reason ?? declared.Reason,
            Assignees = policy.Assignees ?? declared.Assignees,
            RequiredApprovers = policy.RequiredApprovers ?? declared.RequiredApprovers,
            Expiry = policy.ExpirySeconds is { } expiry ? TimeSpan.FromSeconds(expiry) : declared.Expiry,
            OnExpiry = onExpiry,
            EscalationAssignees = policy.EscalationAssignees ?? declared.EscalationAssignees,
            AllowModification = policy.AllowModification ?? declared.AllowModification,
            RequireSegregationOfDuties = policy.RequireSegregationOfDuties ?? declared.RequireSegregationOfDuties
        };

        errors = [];
        return true;
    }

    internal static bool TryParseMode(string? value, out ExecutionMode mode)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "autonomous":
                mode = ExecutionMode.Autonomous;
                return true;
            case "requireapproval":
            case "require_approval":
                mode = ExecutionMode.RequireApproval;
                return true;
            default:
                mode = default;
                return false;
        }
    }

    private static ExecutionPolicyDto ToDto(ApprovalGate gate) => new(
        Mode: gate.Mode switch
        {
            ExecutionMode.RequireApproval => "requireApproval",
            ExecutionMode.Conditional => "conditional",
            _ => "autonomous"
        },
        Reason: gate.Reason,
        Assignees: gate.Assignees,
        RequiredApprovers: gate.RequiredApprovers,
        ExpirySeconds: (int)gate.Expiry.TotalSeconds,
        OnExpiry: gate.OnExpiry.ToString(),
        EscalationAssignees: gate.EscalationAssignees,
        AllowModification: gate.AllowModification,
        RequireSegregationOfDuties: gate.RequireSegregationOfDuties);

    private ValueTask AuditAsync(
        string action, WorkflowDescriptor descriptor, string tenantId, string executorId,
        ClaimsPrincipal? user, string? mode, CancellationToken cancellationToken)
        => _audit is null
            ? ValueTask.CompletedTask
            : _audit.WriteAsync(new AuditEntry
            {
                Action = action,
                ActorId = ActorOf(user),
                Detail = JsonSerializer.Serialize(new
                {
                    tenantId,
                    workflow = descriptor.Name,
                    version = descriptor.Version,
                    executorId,
                    mode
                }),
                OccurredAt = _clock.GetUtcNow()
            }, cancellationToken);

    private static string ActorOf(ClaimsPrincipal? user)
        => user?.FindFirst(ClaimTypes.NameIdentifier)?.Value
           ?? user?.FindFirst("sub")?.Value
           ?? user?.Identity?.Name
           ?? "anonymous";
}

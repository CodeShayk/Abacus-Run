using System.Security.Claims;
using System.Text.Json;
using Abacus.Run.Abstractions;
using Abacus.Run.Core;

namespace Abacus.Run.Api;

public enum ControlResultKind
{
    Accepted,
    NotFound,
    AlreadyTerminal,
    NotResumable,
    InvalidState,
    UnknownWorkflow
}

public sealed record ControlResult(ControlResultKind Kind, WorkflowInstance? Instance = null, string? Detail = null)
{
    public bool IsSuccess => Kind == ControlResultKind.Accepted;

    public static ControlResult Accepted(WorkflowInstance instance) => new(ControlResultKind.Accepted, instance);
}

public enum RerunMode
{
    Restart,
    Resume
}

public interface IInstanceControl
{
    Task<ControlResult> CancelAsync(string instanceId, string? reason, ClaimsPrincipal? user, CancellationToken cancellationToken);

    Task<ControlResult> RestartAsync(string instanceId, JsonElement? context, string? reason, ClaimsPrincipal? user, CancellationToken cancellationToken);

    Task<ControlResult> ResumeAsync(string instanceId, string? fromCheckpointId, string? reason, ClaimsPrincipal? user, CancellationToken cancellationToken);

    Task<ControlResult> RetryNowAsync(string instanceId, string? reason, ClaimsPrincipal? user, CancellationToken cancellationToken);

    Task<ControlResult> SuspendAsync(string instanceId, string? reason, ClaimsPrincipal? user, CancellationToken cancellationToken);

    Task<ControlResult> ResumeSuspendedAsync(string instanceId, string? reason, ClaimsPrincipal? user, CancellationToken cancellationToken);
}

/// <summary>
/// Applies operator control actions. Every action is audited, emits an event on the instance's own
/// stream, and is safe to retry.
/// </summary>
public sealed class InstanceControlService : IInstanceControl
{
    private readonly IInstanceStore _instances;
    private readonly IApprovalStore _approvals;
    private readonly INotificationSink _events;
    private readonly NotificationSequencer _sequencer;
    private readonly IAuditStore _audit;
    private readonly IWorkflowRegistry _registry;
    private readonly ICheckpointDescriber? _checkpoints;
    private readonly TimeProvider _clock;

    public InstanceControlService(
        IInstanceStore instances,
        IApprovalStore approvals,
        INotificationSink events,
        NotificationSequencer sequencer,
        IAuditStore audit,
        IWorkflowRegistry registry,
        ICheckpointDescriber? checkpoints = null,
        TimeProvider? clock = null)
    {
        _instances = instances;
        _approvals = approvals;
        _events = events;
        _sequencer = sequencer;
        _audit = audit;
        _registry = registry;
        _checkpoints = checkpoints;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<ControlResult> CancelAsync(
        string instanceId, string? reason, ClaimsPrincipal? user, CancellationToken cancellationToken)
    {
        WorkflowInstance? instance = await _instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return new ControlResult(ControlResultKind.NotFound);
        }

        if (instance.Status.IsTerminal())
        {
            return new ControlResult(ControlResultKind.AlreadyTerminal, instance,
                $"Instance is already {instance.Status}.");
        }

        DateTimeOffset now = _clock.GetUtcNow();

        // Cooperative: the owning replica observes CancellationRequested and unwinds. When no replica
        // owns it, this transition alone is sufficient.
        await _instances.UpdateAsync(instanceId, m =>
        {
            m.Status = InstanceStatus.Cancelled;
            m.CancellationRequested = true;
            m.CompletedAt = now;
            m.TerminalReason = reason ?? "Cancelled by operator.";
            m.ClearLease = true;
        }, cancellationToken).ConfigureAwait(false);

        // Orphaned approvals would otherwise sit in queues forever.
        await _approvals.CancelForInstanceAsync(instanceId, cancellationToken).ConfigureAwait(false);

        await EmitAsync(instance, WorkflowEventTypes.InstanceCancelled, new
        {
            reason,
            actor = ActorOf(user),
            note = "Side effects already committed by executors are not rolled back."
        }, cancellationToken).ConfigureAwait(false);

        await AuditAsync("instance.cancel", instanceId, user, reason, cancellationToken).ConfigureAwait(false);

        WorkflowInstance? updated = await _instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        return ControlResult.Accepted(updated!);
    }

    public async Task<ControlResult> RestartAsync(
        string instanceId, JsonElement? context, string? reason, ClaimsPrincipal? user, CancellationToken cancellationToken)
    {
        WorkflowInstance? source = await _instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (source is null)
        {
            return new ControlResult(ControlResultKind.NotFound);
        }

        WorkflowDescriptor? descriptor = _registry.Resolve(source.WorkflowName);
        if (descriptor is null)
        {
            return new ControlResult(ControlResultKind.UnknownWorkflow, source,
                $"Workflow '{source.WorkflowName}' is no longer registered.");
        }

        // A new instance at the current version; the source stays terminal and untouched.
        WorkflowInstance created = await _instances.CreateAsync(new CreateInstanceRequest
        {
            InstanceId = IdGenerator.NewId(),
            TenantId = source.TenantId,
            WorkflowName = source.WorkflowName,
            WorkflowVersion = descriptor.Version,
            ContextJson = context?.GetRawText() ?? source.ContextJson,
            CorrelationId = source.CorrelationId,
            RerunOfInstanceId = source.InstanceId,
            MaxAttempts = source.MaxAttempts
        }, cancellationToken).ConfigureAwait(false);

        await EmitAsync(source, WorkflowEventTypes.InstanceRerunRequested, new
        {
            mode = "restart",
            newInstanceId = created.InstanceId,
            reason,
            actor = ActorOf(user)
        }, cancellationToken).ConfigureAwait(false);

        await AuditAsync("instance.rerun.restart", instanceId, user, reason, cancellationToken).ConfigureAwait(false);
        return ControlResult.Accepted(created);
    }

    public async Task<ControlResult> ResumeAsync(
        string instanceId, string? fromCheckpointId, string? reason, ClaimsPrincipal? user, CancellationToken cancellationToken)
    {
        WorkflowInstance? instance = await _instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return new ControlResult(ControlResultKind.NotFound);
        }

        string? checkpointId = fromCheckpointId ?? instance.LatestCheckpointId;
        if (checkpointId is null)
        {
            return new ControlResult(ControlResultKind.NotResumable, instance,
                "No checkpoint is available for this instance; use mode 'restart' instead.");
        }

        // Retention may have pruned the chain out from under a still-queryable instance.
        if (_checkpoints is not null && !_checkpoints.Exists(instanceId, checkpointId))
        {
            return new ControlResult(ControlResultKind.NotResumable, instance,
                "The checkpoint chain has been pruned by retention; use mode 'restart' instead.");
        }

        if (_registry.Resolve(instance.WorkflowName, instance.WorkflowVersion) is null)
        {
            return new ControlResult(ControlResultKind.UnknownWorkflow, instance,
                $"Version '{instance.WorkflowVersion}' of '{instance.WorkflowName}' is no longer registered.");
        }

        await _instances.UpdateAsync(instanceId, m =>
        {
            m.Status = InstanceStatus.Dispatchable;
            m.LatestCheckpointId = checkpointId;
            m.AttemptCount = instance.AttemptCount + 1;
            m.ClearCompletedAt = true;
            m.ClearLease = true;
            m.ClearNextRetryAt = true;

            // The only path permitted to revive a DeadStopped or Failed instance (FR-10.7).
            m.AllowTerminalTransition = true;
        }, cancellationToken).ConfigureAwait(false);

        await EmitAsync(instance, WorkflowEventTypes.InstanceRerunRequested, new
        {
            mode = "resume",
            fromCheckpointId = checkpointId,
            reason,
            actor = ActorOf(user)
        }, cancellationToken).ConfigureAwait(false);

        await AuditAsync("instance.rerun.resume", instanceId, user, reason, cancellationToken).ConfigureAwait(false);

        WorkflowInstance? updated = await _instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        return ControlResult.Accepted(updated!);
    }

    public async Task<ControlResult> RetryNowAsync(
        string instanceId, string? reason, ClaimsPrincipal? user, CancellationToken cancellationToken)
    {
        WorkflowInstance? instance = await _instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return new ControlResult(ControlResultKind.NotFound);
        }

        if (instance.Status != InstanceStatus.RetryScheduled)
        {
            return new ControlResult(ControlResultKind.InvalidState, instance,
                $"Only RetryScheduled instances can be forced; this one is {instance.Status}.");
        }

        await _instances.UpdateAsync(instanceId, m =>
        {
            m.Status = InstanceStatus.Dispatchable;
            m.ClearNextRetryAt = true;
            m.ClearLease = true;
        }, cancellationToken).ConfigureAwait(false);

        await EmitAsync(instance, WorkflowEventTypes.InstanceRetryForced,
            new { reason, actor = ActorOf(user) }, cancellationToken).ConfigureAwait(false);

        await AuditAsync("instance.retry", instanceId, user, reason, cancellationToken).ConfigureAwait(false);

        WorkflowInstance? updated = await _instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        return ControlResult.Accepted(updated!);
    }

    public async Task<ControlResult> SuspendAsync(
        string instanceId, string? reason, ClaimsPrincipal? user, CancellationToken cancellationToken)
    {
        WorkflowInstance? instance = await _instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return new ControlResult(ControlResultKind.NotFound);
        }

        if (instance.Status.IsTerminal())
        {
            return new ControlResult(ControlResultKind.AlreadyTerminal, instance, $"Instance is {instance.Status}.");
        }

        await _instances.UpdateAsync(instanceId, m =>
        {
            m.Status = InstanceStatus.Suspended;
            m.ClearLease = true;
        }, cancellationToken).ConfigureAwait(false);

        await EmitAsync(instance, WorkflowEventTypes.InstanceSuspended,
            new { reason, actor = ActorOf(user) }, cancellationToken).ConfigureAwait(false);

        await AuditAsync("instance.suspend", instanceId, user, reason, cancellationToken).ConfigureAwait(false);

        WorkflowInstance? updated = await _instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        return ControlResult.Accepted(updated!);
    }

    public async Task<ControlResult> ResumeSuspendedAsync(
        string instanceId, string? reason, ClaimsPrincipal? user, CancellationToken cancellationToken)
    {
        WorkflowInstance? instance = await _instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return new ControlResult(ControlResultKind.NotFound);
        }

        if (instance.Status != InstanceStatus.Suspended)
        {
            return new ControlResult(ControlResultKind.InvalidState, instance,
                $"Only Suspended instances can be resumed; this one is {instance.Status}.");
        }

        await _instances.UpdateAsync(instanceId, m =>
        {
            m.Status = InstanceStatus.Dispatchable;
            m.ClearLease = true;
        }, cancellationToken).ConfigureAwait(false);

        await EmitAsync(instance, WorkflowEventTypes.InstanceResumed,
            new { reason, actor = ActorOf(user) }, cancellationToken).ConfigureAwait(false);

        await AuditAsync("instance.resume", instanceId, user, reason, cancellationToken).ConfigureAwait(false);

        WorkflowInstance? updated = await _instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        return ControlResult.Accepted(updated!);
    }

    private ValueTask EmitAsync(WorkflowInstance instance, string eventType, object payload, CancellationToken cancellationToken)
        => _events.PublishAsync(
            NotificationFactory.Create(
                instance.InstanceId, _sequencer.Next(instance.InstanceId), eventType, payload,
                tenantId: instance.TenantId, at: _clock.GetUtcNow()),
            cancellationToken);

    private ValueTask AuditAsync(string action, string instanceId, ClaimsPrincipal? user, string? reason, CancellationToken cancellationToken)
        => _audit.WriteAsync(new AuditEntry
        {
            Action = action,
            InstanceId = instanceId,
            ActorId = ActorOf(user),
            Reason = reason,
            OccurredAt = _clock.GetUtcNow()
        }, cancellationToken);

    private static string ActorOf(ClaimsPrincipal? user)
        => user?.FindFirst(ClaimTypes.NameIdentifier)?.Value
           ?? user?.FindFirst("sub")?.Value
           ?? user?.Identity?.Name
           ?? "anonymous";
}

/// <summary>Lets the control service check checkpoint existence without depending on the store type.</summary>
public interface ICheckpointDescriber
{
    bool Exists(string sessionId, string checkpointId);

    IReadOnlyList<(string CheckpointId, int SizeBytes, DateTimeOffset CommittedAt)> Describe(string sessionId);
}

using System.Collections.Concurrent;
using Abacus.Run.Abstractions;
using Abacus.Run.Core;

namespace Abacus.Run.Persistence;

/// <summary>
/// Instance store backed by memory. Models the concurrency semantics the SQL store must provide:
/// version-checked transitions, terminal-state protection, and disjoint atomic lease claims.
/// </summary>
public sealed class InMemoryInstanceStore : IInstanceStore
{
    private readonly ConcurrentDictionary<string, WorkflowInstance> _instances = new(StringComparer.Ordinal);
    private readonly object _claimLock = new();
    private readonly TimeProvider _clock;

    public InMemoryInstanceStore(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    public int Count => _instances.Count;

    public ValueTask<WorkflowInstance> CreateAsync(CreateInstanceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        DateTimeOffset now = _clock.GetUtcNow();

        var instance = new WorkflowInstance
        {
            InstanceId = request.InstanceId,
            TenantId = request.TenantId,
            WorkflowName = request.WorkflowName,
            WorkflowVersion = request.WorkflowVersion,
            Status = InstanceStatus.Pending,
            ContextJson = request.ContextJson,
            CorrelationId = request.CorrelationId,
            IdempotencyKey = request.IdempotencyKey,
            RerunOfInstanceId = request.RerunOfInstanceId,
            MaxAttempts = request.MaxAttempts,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1
        };

        if (!_instances.TryAdd(instance.InstanceId, instance))
        {
            throw new InvalidOperationException($"Instance '{instance.InstanceId}' already exists.");
        }

        return ValueTask.FromResult(instance);
    }

    public ValueTask<WorkflowInstance?> GetAsync(string instanceId, CancellationToken cancellationToken)
        => ValueTask.FromResult(_instances.TryGetValue(instanceId, out WorkflowInstance? instance) ? instance : null);

    public ValueTask<WorkflowInstance?> FindByIdempotencyKeyAsync(
        string tenantId, string idempotencyKey, CancellationToken cancellationToken)
        => ValueTask.FromResult(_instances.Values.FirstOrDefault(i =>
            i.TenantId == tenantId &&
            i.IdempotencyKey is not null &&
            string.Equals(i.IdempotencyKey, idempotencyKey, StringComparison.Ordinal)));

    public ValueTask<Page<WorkflowInstance>> QueryAsync(InstanceQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        IEnumerable<WorkflowInstance> results = _instances.Values;

        if (query.TenantId is { Length: > 0 } tenant)
        {
            results = results.Where(i => i.TenantId == tenant);
        }
        if (query.Statuses is { Count: > 0 } statuses)
        {
            results = results.Where(i => statuses.Contains(i.Status));
        }
        if (query.WorkflowName is { Length: > 0 } name)
        {
            results = results.Where(i => string.Equals(i.WorkflowName, name, StringComparison.OrdinalIgnoreCase));
        }
        if (query.WorkflowVersion is { Length: > 0 } version)
        {
            results = results.Where(i => string.Equals(i.WorkflowVersion, version, StringComparison.OrdinalIgnoreCase));
        }
        if (query.CorrelationId is { Length: > 0 } correlation)
        {
            results = results.Where(i => i.CorrelationId == correlation);
        }
        if (query.CreatedAfter is { } after)
        {
            results = results.Where(i => i.CreatedAt >= after);
        }
        if (query.CreatedBefore is { } before)
        {
            results = results.Where(i => i.CreatedAt <= before);
        }

        WorkflowInstance[] ordered = results.OrderByDescending(i => i.CreatedAt).ToArray();
        WorkflowInstance[] page = ordered.Skip(query.Offset).Take(query.Limit).ToArray();

        return ValueTask.FromResult(new Page<WorkflowInstance>(page, ordered.Length));
    }

    public ValueTask<bool> TryTransitionAsync(
        string instanceId, InstanceStatus from, InstanceStatus to, Action<InstanceMutation>? mutate, CancellationToken cancellationToken)
    {
        if (!_instances.TryGetValue(instanceId, out WorkflowInstance? current))
        {
            return ValueTask.FromResult(false);
        }

        if (current.Status != from || current.Status.IsTerminal())
        {
            return ValueTask.FromResult(false);
        }

        var mutation = new InstanceMutation { Status = to };
        mutate?.Invoke(mutation);

        WorkflowInstance updated = Apply(current, mutation);

        // Version check stands in for the SQL rowversion: a concurrent writer invalidates this write.
        return ValueTask.FromResult(_instances.TryUpdate(instanceId, updated, current));
    }

    public ValueTask<bool> UpdateAsync(string instanceId, Action<InstanceMutation> mutate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        for (int attempt = 0; attempt < 8; attempt++)
        {
            if (!_instances.TryGetValue(instanceId, out WorkflowInstance? current))
            {
                return ValueTask.FromResult(false);
            }

            var mutation = new InstanceMutation();
            mutate(mutation);

            // Terminal states are final unless a caller explicitly opts in (operator revive).
            if (!mutation.AllowTerminalTransition &&
                current.Status.IsTerminal() && mutation.Status is { } target && target != current.Status)
            {
                return ValueTask.FromResult(false);
            }

            WorkflowInstance updated = Apply(current, mutation);
            if (_instances.TryUpdate(instanceId, updated, current))
            {
                return ValueTask.FromResult(true);
            }
        }

        return ValueTask.FromResult(false);
    }

    public ValueTask<IReadOnlyList<WorkflowInstance>> ClaimAsync(
        string replicaId, int max, TimeSpan leaseDuration, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (max <= 0)
        {
            return ValueTask.FromResult<IReadOnlyList<WorkflowInstance>>([]);
        }

        var claimed = new List<WorkflowInstance>(max);

        // The lock is the in-memory analogue of the single atomic UPDATE...OUTPUT claim: concurrent
        // claimers must receive disjoint sets, never the same instance twice.
        lock (_claimLock)
        {
            foreach (WorkflowInstance instance in _instances.Values.OrderBy(i => i.CreatedAt))
            {
                if (claimed.Count >= max)
                {
                    break;
                }

                if (!IsClaimable(instance, now))
                {
                    continue;
                }

                var mutation = new InstanceMutation
                {
                    LeaseOwner = replicaId,
                    LeaseExpiresAt = now + leaseDuration,
                    Status = instance.Status == InstanceStatus.Pending ? InstanceStatus.Running : instance.Status,
                    StartedAt = instance.StartedAt ?? now
                };

                if (instance.Status is InstanceStatus.RetryScheduled or InstanceStatus.Dispatchable)
                {
                    mutation.Status = InstanceStatus.Running;
                    mutation.ClearNextRetryAt = true;
                }

                WorkflowInstance updated = Apply(instance, mutation);
                if (_instances.TryUpdate(instance.InstanceId, updated, instance))
                {
                    claimed.Add(updated);
                }
            }
        }

        return ValueTask.FromResult<IReadOnlyList<WorkflowInstance>>(claimed);
    }

    private static bool IsClaimable(WorkflowInstance instance, DateTimeOffset now)
    {
        if (instance.Status.IsTerminal())
        {
            return false;
        }

        bool leaseHeld = instance.LeaseExpiresAt is { } expiry && expiry > now;
        if (leaseHeld)
        {
            return false;
        }

        return instance.Status switch
        {
            InstanceStatus.Pending => true,
            InstanceStatus.Dispatchable => true,
            InstanceStatus.RetryScheduled => instance.NextRetryAt is null || instance.NextRetryAt <= now,

            // An active-status instance whose lease has expired (crash) or was explicitly released
            // (graceful drain) is unowned, and must be recoverable in both cases. Requiring an
            // expired lease here would strand every instance handed back during a rolling restart.
            InstanceStatus.Running => true,

            // Parked states are only reclaimed via orphan recovery: they are woken by an approval
            // decision or a timer, which moves them to Dispatchable first.
            InstanceStatus.AwaitingInput or InstanceStatus.AwaitingApproval or InstanceStatus.Suspended
                => instance.LeaseExpiresAt is not null,
            _ => false
        };
    }

    public ValueTask<bool> RenewLeaseAsync(
        string instanceId, string replicaId, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        if (!_instances.TryGetValue(instanceId, out WorkflowInstance? current) || current.LeaseOwner != replicaId)
        {
            return ValueTask.FromResult(false);
        }

        WorkflowInstance updated = Apply(current, new InstanceMutation
        {
            LeaseOwner = replicaId,
            LeaseExpiresAt = _clock.GetUtcNow() + leaseDuration
        });

        return ValueTask.FromResult(_instances.TryUpdate(instanceId, updated, current));
    }

    public ValueTask ReleaseLeaseAsync(string instanceId, string replicaId, CancellationToken cancellationToken)
    {
        if (_instances.TryGetValue(instanceId, out WorkflowInstance? current) && current.LeaseOwner == replicaId)
        {
            WorkflowInstance updated = Apply(current, new InstanceMutation { ClearLease = true });
            _instances.TryUpdate(instanceId, updated, current);
        }

        return ValueTask.CompletedTask;
    }

    private WorkflowInstance Apply(WorkflowInstance current, InstanceMutation m) => current with
    {
        Status = m.Status ?? current.Status,
        ResultJson = m.ResultJson ?? current.ResultJson,
        TerminalReason = m.TerminalReason ?? current.TerminalReason,
        AttemptCount = m.AttemptCount ?? current.AttemptCount,
        NextRetryAt = m.ClearNextRetryAt ? null : m.NextRetryAt ?? current.NextRetryAt,
        LatestCheckpointId = m.LatestCheckpointId ?? current.LatestCheckpointId,
        LeaseOwner = m.ClearLease ? null : m.LeaseOwner ?? current.LeaseOwner,
        LeaseExpiresAt = m.ClearLease ? null : m.LeaseExpiresAt ?? current.LeaseExpiresAt,
        StartedAt = m.StartedAt ?? current.StartedAt,
        CompletedAt = m.ClearCompletedAt ? null : m.CompletedAt ?? current.CompletedAt,
        CancellationRequested = m.CancellationRequested ?? current.CancellationRequested,
        UpdatedAt = _clock.GetUtcNow(),
        Version = current.Version + 1
    };
}

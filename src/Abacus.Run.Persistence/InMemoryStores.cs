using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Abacus.Run.Abstractions;
using Abacus.Run.Core;

namespace Abacus.Run.Persistence;

public sealed class InMemoryEventStore : IEventStore
{
    private readonly ConcurrentDictionary<string, List<EventEnvelope>> _events = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public ValueTask AppendBatchAsync(IReadOnlyList<EventEnvelope> events, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(events);

        lock (_sync)
        {
            foreach (EventEnvelope envelope in events)
            {
                List<EventEnvelope> list = _events.GetOrAdd(envelope.InstanceId, _ => []);
                list.Add(envelope);
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<Page<EventEnvelope>> QueryAsync(EventQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        List<EventEnvelope> snapshot = Snapshot(query.InstanceId);

        IEnumerable<EventEnvelope> filtered = snapshot.Where(e => e.Sequence > query.FromExclusive);
        if (query.ToInclusive is { } to)
        {
            filtered = filtered.Where(e => e.Sequence <= to);
        }
        if (query.Types is { Count: > 0 } types)
        {
            filtered = filtered.Where(e => types.Contains(e.EventType, StringComparer.Ordinal));
        }

        EventEnvelope[] ordered = filtered.OrderBy(e => e.Sequence).ToArray();
        EventEnvelope[] page = ordered.Take(Math.Clamp(query.Limit, 1, 1000)).ToArray();
        string? nextCursor = page.Length < ordered.Length ? page[^1].Sequence.ToString() : null;

        return ValueTask.FromResult(new Page<EventEnvelope>(page, ordered.Length, nextCursor));
    }

    public async IAsyncEnumerable<EventEnvelope> ReadAsync(
        string instanceId, long fromExclusive, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (EventEnvelope envelope in Snapshot(instanceId).Where(e => e.Sequence > fromExclusive).OrderBy(e => e.Sequence))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return envelope;
        }

        await ValueTask.CompletedTask;
    }

    public ValueTask<long> MaxSequenceAsync(string instanceId, CancellationToken cancellationToken)
    {
        List<EventEnvelope> snapshot = Snapshot(instanceId);
        return ValueTask.FromResult(snapshot.Count == 0 ? 0 : snapshot.Max(e => e.Sequence));
    }

    private List<EventEnvelope> Snapshot(string instanceId)
    {
        lock (_sync)
        {
            return _events.TryGetValue(instanceId, out List<EventEnvelope>? list) ? [.. list] : [];
        }
    }
}

public sealed class InMemoryLogStore : ILogStore
{
    private readonly ConcurrentDictionary<string, List<InstanceLogEntry>> _logs = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public ValueTask AppendAsync(InstanceLogEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_sync)
        {
            _logs.GetOrAdd(entry.InstanceId, _ => []).Add(entry);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<InstanceLogEntry>> QueryAsync(
        string instanceId, string? level, string? executorId, int limit, CancellationToken cancellationToken)
    {
        List<InstanceLogEntry> snapshot;
        lock (_sync)
        {
            snapshot = _logs.TryGetValue(instanceId, out List<InstanceLogEntry>? list) ? [.. list] : [];
        }

        IEnumerable<InstanceLogEntry> filtered = snapshot;
        if (level is { Length: > 0 })
        {
            filtered = filtered.Where(l => string.Equals(l.Level, level, StringComparison.OrdinalIgnoreCase));
        }
        if (executorId is { Length: > 0 })
        {
            filtered = filtered.Where(l => l.ExecutorId == executorId);
        }

        return ValueTask.FromResult<IReadOnlyList<InstanceLogEntry>>(
            filtered.OrderBy(l => l.LoggedAt).Take(Math.Clamp(limit, 1, 1000)).ToArray());
    }
}

public sealed class InMemoryApprovalStore : IApprovalStore
{
    private readonly ConcurrentDictionary<string, ApprovalRequest> _approvals = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, List<ApprovalDecision>> _decisions = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public ValueTask<ApprovalRequest> CreateAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_approvals.TryAdd(request.ApprovalId, request))
        {
            throw new InvalidOperationException($"Approval '{request.ApprovalId}' already exists.");
        }
        return ValueTask.FromResult(request);
    }

    public ValueTask<ApprovalRequest?> GetAsync(string approvalId, CancellationToken cancellationToken)
        => ValueTask.FromResult(_approvals.TryGetValue(approvalId, out ApprovalRequest? a) ? a : null);

    public ValueTask<IReadOnlyList<ApprovalRequest>> ListForInstanceAsync(string instanceId, CancellationToken cancellationToken)
        => ValueTask.FromResult<IReadOnlyList<ApprovalRequest>>(
            _approvals.Values.Where(a => a.InstanceId == instanceId).OrderBy(a => a.CreatedAt).ToArray());

    public ValueTask<IReadOnlyList<ApprovalRequest>> QueryPendingAsync(
        string? tenantId, IReadOnlyList<string>? assignees, int limit, CancellationToken cancellationToken)
    {
        IEnumerable<ApprovalRequest> results = _approvals.Values.Where(a => a.State == ApprovalState.Pending);

        if (tenantId is { Length: > 0 })
        {
            results = results.Where(a => a.TenantId == tenantId);
        }
        if (assignees is { Count: > 0 })
        {
            results = results.Where(a => a.Assignees.Count == 0 || a.Assignees.Any(x => assignees.Contains(x)));
        }

        // Expiring soonest first: the queue should surface what is about to time out.
        return ValueTask.FromResult<IReadOnlyList<ApprovalRequest>>(
            results.OrderBy(a => a.ExpiresAt).Take(Math.Clamp(limit, 1, 500)).ToArray());
    }

    public ValueTask<IReadOnlyList<ApprovalRequest>> ClaimExpiredAsync(
        DateTimeOffset now, int max, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            ApprovalRequest[] due = _approvals.Values
                .Where(a => a.State == ApprovalState.Pending && a.ExpiresAt <= now)
                .OrderBy(a => a.ExpiresAt)
                .Take(max)
                .ToArray();

            return ValueTask.FromResult<IReadOnlyList<ApprovalRequest>>(due);
        }
    }

    public ValueTask<bool> TryRecordDecisionAsync(
        string approvalId, ApprovalDecision decision, ApprovalState? newState, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decision);

        lock (_sync)
        {
            if (!_approvals.TryGetValue(approvalId, out ApprovalRequest? current) || current.State != ApprovalState.Pending)
            {
                return ValueTask.FromResult(false);
            }

            List<ApprovalDecision> votes = _decisions.GetOrAdd(approvalId, _ => []);
            if (votes.Any(v => string.Equals(v.DeciderId, decision.DeciderId, StringComparison.OrdinalIgnoreCase)))
            {
                return ValueTask.FromResult(false);   // one vote per decider
            }

            votes.Add(decision);

            if (newState is { } state)
            {
                _approvals[approvalId] = current with { State = state };
            }

            return ValueTask.FromResult(true);
        }
    }

    public ValueTask<IReadOnlyList<ApprovalDecision>> GetDecisionsAsync(string approvalId, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            return ValueTask.FromResult<IReadOnlyList<ApprovalDecision>>(
                _decisions.TryGetValue(approvalId, out List<ApprovalDecision>? votes) ? [.. votes] : []);
        }
    }

    public ValueTask<bool> TrySetStateAsync(string approvalId, ApprovalState state, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (!_approvals.TryGetValue(approvalId, out ApprovalRequest? current))
            {
                return ValueTask.FromResult(false);
            }

            _approvals[approvalId] = current with
            {
                State = state,
                EscalatedOnce = current.EscalatedOnce || state == ApprovalState.Pending
            };
            return ValueTask.FromResult(true);
        }
    }

    public ValueTask CancelForInstanceAsync(string instanceId, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            foreach (ApprovalRequest approval in _approvals.Values.Where(a =>
                         a.InstanceId == instanceId && a.State == ApprovalState.Pending))
            {
                _approvals[approval.ApprovalId] = approval with { State = ApprovalState.Cancelled };
            }
        }
        return ValueTask.CompletedTask;
    }
}

public sealed class InMemoryGatePolicyStore : IGatePolicyStore
{
    private readonly ConcurrentDictionary<string, ApprovalGate> _policies = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ApprovalGate> _overrides = new(StringComparer.OrdinalIgnoreCase);

    public ValueTask<ApprovalGate?> FindAsync(
        string workflowName, string workflowVersion, string executorId, string? instanceId, CancellationToken cancellationToken)
    {
        // Per-instance override outranks the workflow-level policy.
        if (instanceId is { Length: > 0 } && _overrides.TryGetValue($"{instanceId}|{executorId}", out ApprovalGate? instanceGate))
        {
            return ValueTask.FromResult<ApprovalGate?>(instanceGate);
        }

        return ValueTask.FromResult(
            _policies.TryGetValue($"{workflowName}|{workflowVersion}|{executorId}", out ApprovalGate? gate) ? gate : null);
    }

    public ValueTask SetAsync(
        string workflowName, string workflowVersion, string executorId, ApprovalGate gate, CancellationToken cancellationToken)
    {
        _policies[$"{workflowName}|{workflowVersion}|{executorId}"] = gate;
        return ValueTask.CompletedTask;
    }

    public ValueTask SetInstanceOverrideAsync(string instanceId, string executorId, ApprovalGate gate, CancellationToken cancellationToken)
    {
        _overrides[$"{instanceId}|{executorId}"] = gate;
        return ValueTask.CompletedTask;
    }
}

public sealed class InMemoryBlobStore : IBlobStore
{
    private readonly ConcurrentDictionary<string, byte[]> _blobs = new(StringComparer.Ordinal);

    public int Count => _blobs.Count;

    public ValueTask<string> UploadAsync(string key, byte[] content, CancellationToken cancellationToken)
    {
        string uri = $"mem://{key}";
        _blobs[uri] = content;
        return ValueTask.FromResult(uri);
    }

    public ValueTask<byte[]> DownloadAsync(string uri, CancellationToken cancellationToken)
        => _blobs.TryGetValue(uri, out byte[]? content)
            ? ValueTask.FromResult(content)
            : throw new KeyNotFoundException($"Blob '{uri}' not found.");

    public ValueTask DeleteAsync(string uri, CancellationToken cancellationToken)
    {
        _blobs.TryRemove(uri, out _);
        return ValueTask.CompletedTask;
    }
}

public sealed class InMemoryAuditStore : IAuditStore
{
    private readonly List<AuditEntry> _entries = [];
    private readonly object _sync = new();

    public ValueTask WriteAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_sync)
        {
            _entries.Add(entry);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<AuditEntry>> QueryAsync(string? instanceId, int limit, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            IEnumerable<AuditEntry> results = _entries;
            if (instanceId is { Length: > 0 })
            {
                results = results.Where(e => e.InstanceId == instanceId);
            }
            return ValueTask.FromResult<IReadOnlyList<AuditEntry>>(
                results.OrderByDescending(e => e.OccurredAt).Take(Math.Clamp(limit, 1, 1000)).ToArray());
        }
    }
}

using System.Collections.Concurrent;
using Abacus.Run.Abstractions;

namespace Abacus.Run.Persistence;

/// <summary>
/// Default <see cref="IAuditRecordStore"/> so a host runs with no storage configured. Records live
/// for the life of the process — deployments that need the audit trail to survive a restart replace
/// this with a durable store.
/// </summary>
public sealed class InMemoryAuditRecordStore : IAuditRecordStore
{
    private readonly ConcurrentDictionary<string, AuditRecordRoot> _roots = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, AuditRecordEntry>> _entries =
        new(StringComparer.Ordinal);

    public ValueTask UpsertRootAsync(AuditRecordRoot root, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(root);
        _roots[root.InstanceId] = root;
        return ValueTask.CompletedTask;
    }

    public ValueTask AppendAsync(AuditRecordEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        ConcurrentDictionary<string, AuditRecordEntry> forInstance =
            _entries.GetOrAdd(entry.InstanceId, _ => new ConcurrentDictionary<string, AuditRecordEntry>(StringComparer.Ordinal));

        forInstance[EntryKey(entry.SectionKind, entry.Key)] = entry;
        return ValueTask.CompletedTask;
    }

    public ValueTask<AuditRecordDocument?> GetAsync(string instanceId, CancellationToken cancellationToken)
    {
        if (!_roots.TryGetValue(instanceId, out AuditRecordRoot? root))
        {
            return ValueTask.FromResult<AuditRecordDocument?>(null);
        }

        IReadOnlyList<AuditRecordEntry> entries = _entries.TryGetValue(instanceId, out var forInstance)
            ? [.. forInstance.Values.OrderBy(e => e.Sequence)]
            : [];

        return ValueTask.FromResult<AuditRecordDocument?>(new AuditRecordDocument(root, entries));
    }

    public ValueTask<IReadOnlyList<AuditRecordRoot>> ListAsync(
        string workflowName, string? rootKey, string? status, int limit, CancellationToken cancellationToken)
    {
        IReadOnlyList<AuditRecordRoot> results =
        [
            .. _roots.Values
                .Where(r => string.Equals(r.WorkflowName, workflowName, StringComparison.Ordinal))
                .Where(r => rootKey is null || string.Equals(r.RootKey, rootKey, StringComparison.Ordinal))
                .Where(r => status is null || string.Equals(r.Status, status, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(r => r.OpenedUtc)
                .Take(Math.Max(1, limit))
        ];

        return ValueTask.FromResult(results);
    }

    private static string EntryKey(string sectionKind, string? key) => $"{sectionKind}{key ?? string.Empty}";
}

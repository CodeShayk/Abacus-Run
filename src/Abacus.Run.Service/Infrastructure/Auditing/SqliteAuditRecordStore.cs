using Abacus.Run.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Abacus.Run.Service.Infrastructure.Auditing;

/// <summary>
/// SQLite-backed <see cref="IAuditRecordStore"/>. Storage is generic by design — the framework hands
/// it a root and a stream of JSON-payload entries, and the workflow that declared them is the only
/// thing that knows what they mean.
/// </summary>
public sealed class SqliteAuditRecordStore(IDbContextFactory<AuditRecordDbContext> dbFactory) : IAuditRecordStore
{
    public async ValueTask UpsertRootAsync(AuditRecordRoot root, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(root);

        await using AuditRecordDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        AuditRecordRootRow? existing = await db.AuditRecords
            .FirstOrDefaultAsync(r => r.InstanceId == root.InstanceId, cancellationToken).ConfigureAwait(false);

        AuditRecordRootRow row = existing ?? new AuditRecordRootRow { InstanceId = root.InstanceId };

        row.WorkflowName = root.WorkflowName;
        row.WorkflowVersion = root.WorkflowVersion;
        row.RootKind = root.RootKind;
        row.RootKey = root.RootKey;
        row.Status = root.Status;
        row.AttributesJson = root.AttributesJson;
        row.OpenedUtc = root.OpenedUtc;
        row.ClosedUtc = root.ClosedUtc;

        if (existing is null) db.AuditRecords.Add(row);

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask AppendAsync(AuditRecordEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await using AuditRecordDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        AuditRecordEntryRow? existing = await db.AuditRecordEntries
            .FirstOrDefaultAsync(
                e => e.InstanceId == entry.InstanceId && e.SectionKind == entry.SectionKind && e.Key == entry.Key,
                cancellationToken).ConfigureAwait(false);

        AuditRecordEntryRow row = existing ?? new AuditRecordEntryRow
        {
            Id = entry.Id,
            InstanceId = entry.InstanceId,
            SectionKind = entry.SectionKind,
            Key = entry.Key
        };

        row.PayloadJson = entry.PayloadJson;
        row.Sequence = entry.Sequence;
        row.RecordedUtc = entry.RecordedUtc;

        if (existing is null) db.AuditRecordEntries.Add(row);

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<AuditRecordDocument?> GetAsync(string instanceId, CancellationToken cancellationToken)
    {
        await using AuditRecordDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        AuditRecordRootRow? root = await db.AuditRecords
            .AsNoTracking()
            .Include(r => r.Entries)
            .FirstOrDefaultAsync(r => r.InstanceId == instanceId, cancellationToken).ConfigureAwait(false);

        if (root is null) return null;

        return new AuditRecordDocument(
            Map(root),
            [.. root.Entries.OrderBy(e => e.Sequence).Select(Map)]);
    }

    public async ValueTask<IReadOnlyList<AuditRecordRoot>> ListAsync(
        string workflowName, string? rootKey, string? status, int limit, CancellationToken cancellationToken)
    {
        await using AuditRecordDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        IQueryable<AuditRecordRootRow> query = db.AuditRecords
            .AsNoTracking()
            .Where(r => r.WorkflowName == workflowName);

        if (rootKey is not null) query = query.Where(r => r.RootKey == rootKey);
        if (status is not null) query = query.Where(r => r.Status == status);

        List<AuditRecordRootRow> rows = await query
            .OrderByDescending(r => r.OpenedUtc)
            .Take(Math.Max(1, limit))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return [.. rows.Select(Map)];
    }

    private static AuditRecordRoot Map(AuditRecordRootRow row) => new()
    {
        InstanceId = row.InstanceId,
        WorkflowName = row.WorkflowName,
        WorkflowVersion = row.WorkflowVersion,
        RootKind = row.RootKind,
        RootKey = row.RootKey,
        Status = row.Status,
        AttributesJson = row.AttributesJson,
        OpenedUtc = row.OpenedUtc,
        ClosedUtc = row.ClosedUtc
    };

    private static AuditRecordEntry Map(AuditRecordEntryRow row) => new()
    {
        Id = row.Id,
        InstanceId = row.InstanceId,
        SectionKind = row.SectionKind,
        Key = row.Key,
        PayloadJson = row.PayloadJson,
        Sequence = row.Sequence,
        RecordedUtc = row.RecordedUtc
    };
}

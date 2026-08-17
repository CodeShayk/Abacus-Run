using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Abacus.Run.Abstractions;
using Abacus.Run.Core;
using Microsoft.EntityFrameworkCore;

namespace Abacus.Run.Service;

public sealed class JsonRow
{
    public required string Kind { get; set; }
    public required string Key { get; set; }
    public required string Payload { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public byte[]? RowVersion { get; set; }
}

public sealed class AbacusDbContext(DbContextOptions<AbacusDbContext> options) : DbContext(options)
{
    public DbSet<InstanceRow> Instances => Set<InstanceRow>();
    public DbSet<EventRow> Events => Set<EventRow>();
    public DbSet<JsonRow> JsonRows => Set<JsonRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<InstanceRow>(entity =>
        {
            entity.ToTable("WorkflowInstances");
            entity.HasKey(row => row.InstanceId);
            entity.Property(row => row.Payload).IsRequired();
            entity.Property(row => row.RowVersion).IsRowVersion();
            entity.HasIndex(row => new { row.TenantId, row.IdempotencyKey }).IsUnique();
            entity.HasIndex(row => new { row.Status, row.NextRetryAt });
        });

        modelBuilder.Entity<EventRow>(entity =>
        {
            entity.ToTable("WorkflowEvents");
            entity.HasKey(row => new { row.InstanceId, row.Sequence });
            entity.Property(row => row.Payload).IsRequired();
            entity.HasIndex(row => row.EventType);

            // Supports reading a workflow's log across instances, which is the query the
            // denormalised column exists for.
            entity.HasIndex(row => new { row.WorkflowName, row.OccurredAt });
        });

        modelBuilder.Entity<JsonRow>(entity =>
        {
            entity.ToTable("AbacusJsonRows");
            entity.HasKey(row => new { row.Kind, row.Key });
            entity.Property(row => row.Payload).IsRequired();
            entity.Property(row => row.RowVersion).IsRowVersion();
        });
    }
}

public sealed class InstanceRow
{
    public required string InstanceId { get; set; }
    public required string TenantId { get; set; }
    public required string WorkflowName { get; set; }
    public required string WorkflowVersion { get; set; }
    public required int Status { get; set; }
    public DateTimeOffset? NextRetryAt { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public string? IdempotencyKey { get; set; }
    public required string Payload { get; set; }
    public byte[]? RowVersion { get; set; }
}

public sealed class EventRow
{
    public required string InstanceId { get; set; }
    public required long Sequence { get; set; }
    public required string EventType { get; set; }

    /// <summary>
    /// Denormalised from the instance so the log can be filtered by workflow without a join. Nullable
    /// because rows written before this column existed do not have it.
    /// </summary>
    public string? WorkflowName { get; set; }
    public string? TenantId { get; set; }
    public string? ExecutorId { get; set; }
    public int? Superstep { get; set; }
    public required string Payload { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

public sealed class SqlServerInstanceStore(IDbContextFactory<AbacusDbContext> factory, TimeProvider clock) : IInstanceStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async ValueTask<WorkflowInstance> CreateAsync(CreateInstanceRequest request, CancellationToken cancellationToken)
    {
        await using AbacusDbContext db = await factory.CreateDbContextAsync(cancellationToken);
        DateTimeOffset now = clock.GetUtcNow();
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

        db.Instances.Add(ToRow(instance));
        await db.SaveChangesAsync(cancellationToken);
        return instance;
    }

    public async ValueTask<WorkflowInstance?> GetAsync(string instanceId, CancellationToken cancellationToken)
    {
        await using AbacusDbContext db = await factory.CreateDbContextAsync(cancellationToken);
        InstanceRow? row = await db.Instances.AsNoTracking().SingleOrDefaultAsync(x => x.InstanceId == instanceId, cancellationToken);
        return row is null ? null : Deserialize(row.Payload);
    }

    public async ValueTask<WorkflowInstance?> FindByIdempotencyKeyAsync(string tenantId, string idempotencyKey, CancellationToken cancellationToken)
    {
        await using AbacusDbContext db = await factory.CreateDbContextAsync(cancellationToken);
        InstanceRow? row = await db.Instances.AsNoTracking().SingleOrDefaultAsync(
            x => x.TenantId == tenantId && x.IdempotencyKey == idempotencyKey, cancellationToken);
        return row is null ? null : Deserialize(row.Payload);
    }

    public async ValueTask<Page<WorkflowInstance>> QueryAsync(InstanceQuery query, CancellationToken cancellationToken)
    {
        await using AbacusDbContext db = await factory.CreateDbContextAsync(cancellationToken);
        IQueryable<InstanceRow> rows = db.Instances.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.TenantId)) rows = rows.Where(x => x.TenantId == query.TenantId);
        if (query.Statuses is { Count: > 0 })
        {
            int[] statuses = query.Statuses.Select(status => (int)status).ToArray();
            rows = rows.Where(x => statuses.Contains(x.Status));
        }
        if (!string.IsNullOrWhiteSpace(query.WorkflowName)) rows = rows.Where(x => x.WorkflowName == query.WorkflowName);
        if (!string.IsNullOrWhiteSpace(query.WorkflowVersion)) rows = rows.Where(x => x.WorkflowVersion == query.WorkflowVersion);

        List<WorkflowInstance> all = (await rows.ToListAsync(cancellationToken)).Select(row => Deserialize(row.Payload))
            .OrderByDescending(instance => instance.CreatedAt).ToList();
        return new Page<WorkflowInstance>(all.Skip(query.Offset).Take(Math.Clamp(query.Limit, 1, 1000)).ToArray(), all.Count);
    }

    public async ValueTask<bool> TryTransitionAsync(string instanceId, InstanceStatus from, InstanceStatus to, Action<InstanceMutation>? mutate, CancellationToken cancellationToken)
    {
        await using AbacusDbContext db = await factory.CreateDbContextAsync(cancellationToken);
        InstanceRow? row = await db.Instances.SingleOrDefaultAsync(x => x.InstanceId == instanceId, cancellationToken);
        if (row is null) return false;
        WorkflowInstance current = Deserialize(row.Payload);
        if (current.Status != from || current.Status.IsTerminal()) return false;
        var mutation = new InstanceMutation { Status = to };
        mutate?.Invoke(mutation);
        Apply(row, current, mutation);
        return await SaveAsync(db, cancellationToken);
    }

    public async ValueTask<bool> UpdateAsync(string instanceId, Action<InstanceMutation> mutate, CancellationToken cancellationToken)
    {
        await using AbacusDbContext db = await factory.CreateDbContextAsync(cancellationToken);
        InstanceRow? row = await db.Instances.SingleOrDefaultAsync(x => x.InstanceId == instanceId, cancellationToken);
        if (row is null) return false;
        WorkflowInstance current = Deserialize(row.Payload);
        var mutation = new InstanceMutation();
        mutate(mutation);
        if (!mutation.AllowTerminalTransition && current.Status.IsTerminal() && mutation.Status is { } target && target != current.Status)
        {
            return false;
        }
        Apply(row, current, mutation);
        return await SaveAsync(db, cancellationToken);
    }

    public async ValueTask<IReadOnlyList<WorkflowInstance>> ClaimAsync(string replicaId, int max, TimeSpan leaseDuration, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using AbacusDbContext db = await factory.CreateDbContextAsync(cancellationToken);
        List<InstanceRow> rows = await db.Instances.Where(row =>
            row.Status == (int)InstanceStatus.Pending || row.Status == (int)InstanceStatus.Dispatchable ||
            row.Status == (int)InstanceStatus.RetryScheduled || row.Status == (int)InstanceStatus.Running)
            .Where(row => row.LeaseExpiresAt == null || row.LeaseExpiresAt <= now)
            .OrderBy(row => row.Payload).Take(max).ToListAsync(cancellationToken);
        var claimed = new List<WorkflowInstance>();
        foreach (InstanceRow row in rows)
        {
            WorkflowInstance current = Deserialize(row.Payload);
            if (current.Status == InstanceStatus.RetryScheduled && current.NextRetryAt > now) continue;
            Apply(row, current, new InstanceMutation
            {
                Status = InstanceStatus.Running,
                LeaseOwner = replicaId,
                LeaseExpiresAt = now + leaseDuration,
                StartedAt = current.StartedAt ?? now,
                ClearNextRetryAt = true
            });
            claimed.Add(Deserialize(row.Payload));
        }
        await db.SaveChangesAsync(cancellationToken);
        return claimed;
    }

    public async ValueTask<bool> RenewLeaseAsync(string instanceId, string replicaId, TimeSpan leaseDuration, CancellationToken cancellationToken)
        => await UpdateAsync(instanceId, mutation =>
        {
            mutation.LeaseOwner = replicaId;
            mutation.LeaseExpiresAt = clock.GetUtcNow() + leaseDuration;
        }, cancellationToken);

    public ValueTask ReleaseLeaseAsync(string instanceId, string replicaId, CancellationToken cancellationToken)
        => new(UpdateAsync(instanceId, mutation => mutation.ClearLease = true, cancellationToken).AsTask());

    private async ValueTask<bool> SaveAsync(AbacusDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }

    private void Apply(InstanceRow row, WorkflowInstance current, InstanceMutation mutation)
    {
        WorkflowInstance updated = current with
        {
            Status = mutation.Status ?? current.Status,
            ResultJson = mutation.ResultJson ?? current.ResultJson,
            TerminalReason = mutation.TerminalReason ?? current.TerminalReason,
            AttemptCount = mutation.AttemptCount ?? current.AttemptCount,
            NextRetryAt = mutation.ClearNextRetryAt ? null : mutation.NextRetryAt ?? current.NextRetryAt,
            LatestCheckpointId = mutation.LatestCheckpointId ?? current.LatestCheckpointId,
            LeaseOwner = mutation.ClearLease ? null : mutation.LeaseOwner ?? current.LeaseOwner,
            LeaseExpiresAt = mutation.ClearLease ? null : mutation.LeaseExpiresAt ?? current.LeaseExpiresAt,
            StartedAt = mutation.StartedAt ?? current.StartedAt,
            CompletedAt = mutation.ClearCompletedAt ? null : mutation.CompletedAt ?? current.CompletedAt,
            CancellationRequested = mutation.CancellationRequested ?? current.CancellationRequested,
            UpdatedAt = clock.GetUtcNow(),
            Version = current.Version + 1
        };
        row.Status = (int)updated.Status;
        row.NextRetryAt = updated.NextRetryAt;
        row.LeaseOwner = updated.LeaseOwner;
        row.LeaseExpiresAt = updated.LeaseExpiresAt;
        row.Payload = JsonSerializer.Serialize(updated, Json);
    }

    private static InstanceRow ToRow(WorkflowInstance instance) => new()
    {
        InstanceId = instance.InstanceId,
        TenantId = instance.TenantId,
        WorkflowName = instance.WorkflowName,
        WorkflowVersion = instance.WorkflowVersion,
        Status = (int)instance.Status,
        NextRetryAt = instance.NextRetryAt,
        LeaseOwner = instance.LeaseOwner,
        LeaseExpiresAt = instance.LeaseExpiresAt,
        IdempotencyKey = instance.IdempotencyKey,
        Payload = JsonSerializer.Serialize(instance, Json)
    };

    private static WorkflowInstance Deserialize(string payload)
        => JsonSerializer.Deserialize<WorkflowInstance>(payload, Json)
           ?? throw new InvalidOperationException("Stored workflow instance payload was empty.");
}

public sealed class SqlServerEventStore(IDbContextFactory<AbacusDbContext> factory) : IEventStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async ValueTask AppendBatchAsync(IReadOnlyList<EventEnvelope> events, CancellationToken cancellationToken)
    {
        await using AbacusDbContext db = await factory.CreateDbContextAsync(cancellationToken);
        foreach (EventEnvelope envelope in events)
        {
            db.Events.Add(new EventRow
            {
                InstanceId = envelope.InstanceId,
                Sequence = envelope.Sequence,
                EventType = envelope.EventType,
                WorkflowName = envelope.WorkflowName,
                TenantId = envelope.TenantId,
                ExecutorId = envelope.ExecutorId,
                Superstep = envelope.Superstep,
                Payload = JsonSerializer.Serialize(envelope, Json),
                OccurredAt = envelope.OccurredAt
            });
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    public async ValueTask<Page<EventEnvelope>> QueryAsync(EventQuery query, CancellationToken cancellationToken)
    {
        await using AbacusDbContext db = await factory.CreateDbContextAsync(cancellationToken);
        IQueryable<EventRow> rows = db.Events.AsNoTracking().Where(row => row.InstanceId == query.InstanceId && row.Sequence > query.FromExclusive);
        if (query.ToInclusive is { } to) rows = rows.Where(row => row.Sequence <= to);
        if (query.Types is { Count: > 0 }) rows = rows.Where(row => query.Types.Contains(row.EventType));
        List<EventEnvelope> all = (await rows.OrderBy(row => row.Sequence).Take(Math.Clamp(query.Limit, 1, 1000)).ToListAsync(cancellationToken))
            .Select(row => JsonSerializer.Deserialize<EventEnvelope>(row.Payload, Json)!).ToList();
        return new Page<EventEnvelope>(all, all.Count);
    }

    public async IAsyncEnumerable<EventEnvelope> ReadAsync(string instanceId, long fromExclusive, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using AbacusDbContext db = await factory.CreateDbContextAsync(cancellationToken);
        List<string> payloads = await db.Events.AsNoTracking().Where(row => row.InstanceId == instanceId && row.Sequence > fromExclusive)
            .OrderBy(row => row.Sequence).Select(row => row.Payload).ToListAsync(cancellationToken);
        foreach (string payload in payloads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return JsonSerializer.Deserialize<EventEnvelope>(payload, Json)!;
        }
    }

    public async ValueTask<long> MaxSequenceAsync(string instanceId, CancellationToken cancellationToken)
    {
        await using AbacusDbContext db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.Events.Where(row => row.InstanceId == instanceId).Select(row => (long?)row.Sequence).MaxAsync(cancellationToken) ?? 0;
    }
}

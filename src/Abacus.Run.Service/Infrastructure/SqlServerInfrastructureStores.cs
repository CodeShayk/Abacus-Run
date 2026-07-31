using System.Text.Json;
using Abacus.Run.Api;
using Abacus.Run.Abstractions;
using Abacus.Run.Core;
using Abacus.Run.Persistence;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.EntityFrameworkCore;

namespace Abacus.Run.Service;

// Serialization envelope for the approval row; an implementation detail of the SQL store.
internal sealed record ApprovalDocument(ApprovalRequest Request, IReadOnlyList<ApprovalDecision> Decisions);

public sealed class SqlServerLogStore(IDbContextFactory<AbacusDbContext> factory) : ILogStore
{
    public async ValueTask AppendAsync(InstanceLogEntry entry, CancellationToken cancellationToken)
    {
        await using AbacusDbContext db = await factory.CreateDbContextAsync(cancellationToken);
        db.JsonRows.Add(Row("log", $"{entry.InstanceId}:{entry.Sequence}", entry));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async ValueTask<IReadOnlyList<InstanceLogEntry>> QueryAsync(string instanceId, string? level, string? executorId, int limit, CancellationToken cancellationToken)
    {
        await using AbacusDbContext db = await factory.CreateDbContextAsync(cancellationToken);
        List<string> payloads = await db.JsonRows.AsNoTracking().Where(row => row.Kind == "log" && row.Key.StartsWith(instanceId + ":"))
            .Select(row => row.Payload).ToListAsync(cancellationToken);
        IEnumerable<InstanceLogEntry> query = payloads
            .Select(payload => JsonSerializer.Deserialize<InstanceLogEntry>(payload))
            .Where(entry => entry is not null)!;
        if (!string.IsNullOrWhiteSpace(level)) query = query.Where(entry => entry.Level.Equals(level, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(executorId)) query = query.Where(entry => entry.ExecutorId == executorId);
        return query.OrderByDescending(entry => entry.LoggedAt).Take(Math.Clamp(limit, 1, 1000)).ToArray();
    }

    private static JsonRow Row(string kind, string key, object value) => new()
    {
        Kind = kind, Key = key, Payload = JsonSerializer.Serialize(value), UpdatedAt = DateTimeOffset.UtcNow
    };
}

public sealed class SqlServerAuditStore(IDbContextFactory<AbacusDbContext> factory) : IAuditStore
{
    public async ValueTask WriteAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        await using AbacusDbContext db = await factory.CreateDbContextAsync(cancellationToken);
        db.JsonRows.Add(new JsonRow
        {
            Kind = "audit", Key = Guid.NewGuid().ToString("N"), Payload = JsonSerializer.Serialize(entry), UpdatedAt = entry.OccurredAt
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async ValueTask<IReadOnlyList<AuditEntry>> QueryAsync(string? instanceId, int limit, CancellationToken cancellationToken)
    {
        await using AbacusDbContext db = await factory.CreateDbContextAsync(cancellationToken);
        List<AuditEntry> entries = await db.JsonRows.AsNoTracking().Where(row => row.Kind == "audit")
            .OrderByDescending(row => row.UpdatedAt).Take(Math.Clamp(limit, 1, 1000)).Select(row => row.Payload)
            .ToListAsync(cancellationToken).ContinueWith(task => task.Result.Select(payload => JsonSerializer.Deserialize<AuditEntry>(payload)!).ToList(), cancellationToken);
        return instanceId is null ? entries : entries.Where(entry => entry.InstanceId == instanceId).ToArray();
    }
}

public sealed class SqlServerBlobStore(IDbContextFactory<AbacusDbContext> factory) : IBlobStore
{
    public async ValueTask<string> UploadAsync(string key, byte[] content, CancellationToken cancellationToken)
    {
        string uri = $"sqlblob://{key}";
        await using AbacusDbContext db = await factory.CreateDbContextAsync(cancellationToken);
        JsonRow? current = await db.JsonRows.SingleOrDefaultAsync(row => row.Kind == "blob" && row.Key == uri, cancellationToken);
        if (current is null)
        {
            db.JsonRows.Add(new JsonRow { Kind = "blob", Key = uri, Payload = Convert.ToBase64String(content), UpdatedAt = DateTimeOffset.UtcNow });
        }
        else
        {
            current.Payload = Convert.ToBase64String(content);
            current.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(cancellationToken);
        return uri;
    }

    public async ValueTask<byte[]> DownloadAsync(string uri, CancellationToken cancellationToken)
    {
        await using AbacusDbContext db = await factory.CreateDbContextAsync(cancellationToken);
        JsonRow row = await db.JsonRows.AsNoTracking().SingleAsync(item => item.Kind == "blob" && item.Key == uri, cancellationToken);
        return Convert.FromBase64String(row.Payload);
    }

    public async ValueTask DeleteAsync(string uri, CancellationToken cancellationToken)
    {
        await using AbacusDbContext db = await factory.CreateDbContextAsync(cancellationToken);
        JsonRow? row = await db.JsonRows.SingleOrDefaultAsync(item => item.Kind == "blob" && item.Key == uri, cancellationToken);
        if (row is not null) { db.JsonRows.Remove(row); await db.SaveChangesAsync(cancellationToken); }
    }
}

public sealed class SqlServerGatePolicyStore(IDbContextFactory<AbacusDbContext> factory) : IGatePolicyStore
{
    public async ValueTask<ApprovalGate?> FindAsync(string workflowName, string workflowVersion, string executorId, string? instanceId, CancellationToken cancellationToken)
    {
        await using AbacusDbContext db = await factory.CreateDbContextAsync(cancellationToken);
        string key = instanceId is null ? $"gate:{workflowName}:{workflowVersion}:{executorId}" : $"gate-instance:{instanceId}:{executorId}";
        JsonRow? row = await db.JsonRows.AsNoTracking().SingleOrDefaultAsync(item => item.Kind == "gate" && item.Key == key, cancellationToken);
        return row is null ? null : JsonSerializer.Deserialize<ApprovalGate>(row.Payload);
    }

    public ValueTask SetAsync(string workflowName, string workflowVersion, string executorId, ApprovalGate gate, CancellationToken cancellationToken)
        => SetCoreAsync($"gate:{workflowName}:{workflowVersion}:{executorId}", gate, cancellationToken);

    public ValueTask SetInstanceOverrideAsync(string instanceId, string executorId, ApprovalGate gate, CancellationToken cancellationToken)
        => SetCoreAsync($"gate-instance:{instanceId}:{executorId}", gate, cancellationToken);

    private async ValueTask SetCoreAsync(string key, ApprovalGate gate, CancellationToken cancellationToken)
    {
        await using AbacusDbContext db = await factory.CreateDbContextAsync(cancellationToken);
        JsonRow? row = await db.JsonRows.SingleOrDefaultAsync(item => item.Kind == "gate" && item.Key == key, cancellationToken);
        if (row is null) db.JsonRows.Add(new JsonRow { Kind = "gate", Key = key, Payload = JsonSerializer.Serialize(gate), UpdatedAt = DateTimeOffset.UtcNow });
        else { row.Payload = JsonSerializer.Serialize(gate); row.UpdatedAt = DateTimeOffset.UtcNow; }
        await db.SaveChangesAsync(cancellationToken);
    }
}

public sealed class SqlServerApprovalStore(IDbContextFactory<AbacusDbContext> factory) : IApprovalStore
{
    public async ValueTask<ApprovalRequest> CreateAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        await using AbacusDbContext db = await factory.CreateDbContextAsync(cancellationToken);
        db.JsonRows.Add(Document(request, []));
        await db.SaveChangesAsync(cancellationToken);
        return request;
    }

    public async ValueTask<ApprovalRequest?> GetAsync(string approvalId, CancellationToken cancellationToken)
        => (await ReadAsync(approvalId, cancellationToken))?.Request;

    public async ValueTask<IReadOnlyList<ApprovalRequest>> ListForInstanceAsync(string instanceId, CancellationToken cancellationToken)
    {
        List<ApprovalDocument> docs = await Documents(cancellationToken);
        return docs.Where(doc => doc.Request.InstanceId == instanceId).Select(doc => doc.Request).OrderBy(doc => doc.CreatedAt).ToArray();
    }

    public async ValueTask<IReadOnlyList<ApprovalRequest>> QueryPendingAsync(string? tenantId, IReadOnlyList<string>? assignees, int limit, CancellationToken cancellationToken)
    {
        IEnumerable<ApprovalRequest> requests = (await Documents(cancellationToken)).Select(doc => doc.Request).Where(request => request.State == ApprovalState.Pending);
        if (!string.IsNullOrWhiteSpace(tenantId)) requests = requests.Where(request => request.TenantId == tenantId);
        if (assignees is { Count: > 0 }) requests = requests.Where(request => request.Assignees.Count == 0 || request.Assignees.Any(assignees.Contains));
        return requests.OrderBy(request => request.ExpiresAt).Take(Math.Clamp(limit, 1, 500)).ToArray();
    }

    public async ValueTask<IReadOnlyList<ApprovalRequest>> ClaimExpiredAsync(DateTimeOffset now, int max, CancellationToken cancellationToken)
        => (await Documents(cancellationToken)).Select(doc => doc.Request).Where(request => request.State == ApprovalState.Pending && request.ExpiresAt <= now)
            .OrderBy(request => request.ExpiresAt).Take(max).ToArray();

    public async ValueTask<bool> TryRecordDecisionAsync(string approvalId, ApprovalDecision decision, ApprovalState? newState, CancellationToken cancellationToken)
    {
        await using AbacusDbContext db = await factory.CreateDbContextAsync(cancellationToken);
        JsonRow? row = await db.JsonRows.SingleOrDefaultAsync(item => item.Kind == "approval" && item.Key == approvalId, cancellationToken);
        if (row is null) return false;
        ApprovalDocument document = JsonSerializer.Deserialize<ApprovalDocument>(row.Payload)!;
        if (document.Request.State != ApprovalState.Pending || document.Decisions.Any(item => item.DeciderId == decision.DeciderId)) return false;
        row.Payload = JsonSerializer.Serialize(document with { Request = newState is null ? document.Request : document.Request with { State = newState.Value }, Decisions = [.. document.Decisions, decision] });
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async ValueTask<IReadOnlyList<ApprovalDecision>> GetDecisionsAsync(string approvalId, CancellationToken cancellationToken)
        => (await ReadAsync(approvalId, cancellationToken))?.Decisions ?? [];

    public async ValueTask<bool> TrySetStateAsync(string approvalId, ApprovalState state, CancellationToken cancellationToken)
    {
        await using AbacusDbContext db = await factory.CreateDbContextAsync(cancellationToken);
        JsonRow? row = await db.JsonRows.SingleOrDefaultAsync(item => item.Kind == "approval" && item.Key == approvalId, cancellationToken);
        if (row is null) return false;
        ApprovalDocument document = JsonSerializer.Deserialize<ApprovalDocument>(row.Payload)!;
        row.Payload = JsonSerializer.Serialize(document with { Request = document.Request with { State = state, EscalatedOnce = document.Request.EscalatedOnce || state == ApprovalState.Pending } });
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async ValueTask CancelForInstanceAsync(string instanceId, CancellationToken cancellationToken)
    {
        foreach (ApprovalRequest request in await ListForInstanceAsync(instanceId, cancellationToken)) await TrySetStateAsync(request.ApprovalId, ApprovalState.Cancelled, cancellationToken);
    }

    private async Task<ApprovalDocument?> ReadAsync(string id, CancellationToken cancellationToken)
    {
        await using AbacusDbContext db = await factory.CreateDbContextAsync(cancellationToken);
        JsonRow? row = await db.JsonRows.AsNoTracking().SingleOrDefaultAsync(item => item.Kind == "approval" && item.Key == id, cancellationToken);
        return row is null ? null : JsonSerializer.Deserialize<ApprovalDocument>(row.Payload);
    }

    private async Task<List<ApprovalDocument>> Documents(CancellationToken cancellationToken)
    {
        await using AbacusDbContext db = await factory.CreateDbContextAsync(cancellationToken);
        return (await db.JsonRows.AsNoTracking().Where(item => item.Kind == "approval").Select(item => item.Payload).ToListAsync(cancellationToken))
            .Select(payload => JsonSerializer.Deserialize<ApprovalDocument>(payload)!).ToList();
    }

    private static JsonRow Document(ApprovalRequest request, IReadOnlyList<ApprovalDecision> decisions) => new()
    {
        Kind = "approval", Key = request.ApprovalId, Payload = JsonSerializer.Serialize(new ApprovalDocument(request, decisions)), UpdatedAt = request.CreatedAt
    };
}

public sealed class SqlServerCheckpointStore(IDbContextFactory<AbacusDbContext> factory) : ICheckpointStore<JsonElement>
{
    public async ValueTask<CheckpointInfo> CreateCheckpointAsync(string sessionId, JsonElement value, CheckpointInfo? parent = null)
    {
        string id = IdGenerator.NewId();
        await using AbacusDbContext db = await factory.CreateDbContextAsync();
        db.JsonRows.Add(new JsonRow { Kind = "checkpoint", Key = $"{sessionId}:{id}", Payload = value.GetRawText(), UpdatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        return new CheckpointInfo(sessionId, id);
    }

    public async ValueTask<IEnumerable<CheckpointInfo>> RetrieveIndexAsync(string sessionId, CheckpointInfo? withParent = null)
    {
        await using AbacusDbContext db = await factory.CreateDbContextAsync();
        List<string> keys = await db.JsonRows.AsNoTracking().Where(row => row.Kind == "checkpoint" && row.Key.StartsWith(sessionId + ":"))
            .OrderBy(row => row.UpdatedAt).Select(row => row.Key).ToListAsync();
        return keys.Select(key => new CheckpointInfo(sessionId, key[(sessionId.Length + 1)..])).ToArray();
    }

    public async ValueTask<JsonElement> RetrieveCheckpointAsync(string sessionId, CheckpointInfo key)
    {
        await using AbacusDbContext db = await factory.CreateDbContextAsync();
        JsonRow row = await db.JsonRows.AsNoTracking().SingleAsync(item => item.Kind == "checkpoint" && item.Key == $"{sessionId}:{key.CheckpointId}");
        return JsonSerializer.Deserialize<JsonElement>(row.Payload);
    }
}

public sealed class SqlServerCheckpointDescriber(IDbContextFactory<AbacusDbContext> factory) : ICheckpointDescriber
{
    public bool Exists(string sessionId, string checkpointId)
        => factory.CreateDbContext().JsonRows.Any(row => row.Kind == "checkpoint" && row.Key == $"{sessionId}:{checkpointId}");

    public IReadOnlyList<(string CheckpointId, int SizeBytes, DateTimeOffset CommittedAt)> Describe(string sessionId)
        => factory.CreateDbContext().JsonRows.AsNoTracking()
            .Where(row => row.Kind == "checkpoint" && row.Key.StartsWith(sessionId + ":"))
            .OrderBy(row => row.UpdatedAt)
            .AsEnumerable()
            .Select(row => (row.Key[(sessionId.Length + 1)..], row.Payload.Length, row.UpdatedAt))
            .ToArray();
}

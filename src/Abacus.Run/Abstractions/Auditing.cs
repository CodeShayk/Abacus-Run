namespace Abacus.Run.Abstractions;

/// <summary>
/// One kind of child construct a workflow's audit record may contain — a retrieval plan, an
/// execution input, an output, whatever that workflow considers audit-significant.
/// </summary>
/// <param name="Kind">Stable identifier written into storage. Treat as part of the workflow's contract.</param>
/// <param name="Description">What an investigator will find here.</param>
/// <param name="Multiple">False when at most one entry of this kind belongs to a record.</param>
public sealed record AuditSectionDefinition(string Kind, string Description, bool Multiple = true);

/// <summary>
/// The shape of a workflow's audit record: a root aggregate plus the child constructs that may hang
/// off it. Declared by the workflow definition, because only the workflow knows what is worth
/// auditing about its own run — the framework supplies the hook and the storage, not the schema.
/// </summary>
public sealed class AuditRecordDefinition
{
    private readonly Dictionary<string, AuditSectionDefinition> _sections;

    public AuditRecordDefinition(
        string rootKind,
        string description,
        IReadOnlyList<AuditSectionDefinition> sections)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootKind);
        ArgumentNullException.ThrowIfNull(sections);

        RootKind = rootKind;
        Description = description ?? string.Empty;
        Sections = sections;

        _sections = sections.ToDictionary(s => s.Kind, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Kind of the root aggregate — the thing one run of the workflow is about.</summary>
    public string RootKind { get; }

    public string Description { get; }

    public IReadOnlyList<AuditSectionDefinition> Sections { get; }

    public bool Allows(string sectionKind) =>
        !string.IsNullOrWhiteSpace(sectionKind) && _sections.ContainsKey(sectionKind);

    public AuditSectionDefinition? Section(string sectionKind) =>
        _sections.GetValueOrDefault(sectionKind);
}

/// <summary>
/// Implemented by a workflow definition that keeps an audit record. The runtime reads this at build
/// time and hands the definition's executors a recorder bound to it.
/// </summary>
public interface IAuditedWorkflowDefinition
{
    AuditRecordDefinition AuditRecord { get; }
}

/// <summary>
/// The hook a workflow's executors use to build up the audit record as the run progresses. Obtained
/// from <see cref="HostExecutorRuntime.Audit"/> inside an executor, or from
/// <see cref="WorkflowBuildContext.Audit"/> when the definition wires its own nodes.
/// </summary>
/// <remarks>
/// Recording is best-effort by contract: an audit write must never fail the work it describes.
/// Implementations swallow and log storage failures rather than propagating them.
/// </remarks>
public interface IWorkflowAuditRecorder
{
    /// <summary>The shape this recorder accepts, as declared by the workflow definition.</summary>
    AuditRecordDefinition Definition { get; }

    /// <summary>
    /// Opens (or re-opens, on a resumed instance) the root aggregate. <paramref name="rootKey"/> is
    /// the workflow's own identifier for the thing being audited — a case reference, an order id.
    /// </summary>
    ValueTask OpenAsync(
        string rootKey,
        IReadOnlyDictionary<string, object?>? attributes,
        CancellationToken cancellationToken);

    /// <summary>
    /// Appends one child construct. <paramref name="sectionKind"/> must be declared by the
    /// definition; <paramref name="key"/> groups entries within a kind (a check id, a node id).
    /// </summary>
    ValueTask RecordAsync(
        string sectionKind,
        string? key,
        object? payload,
        CancellationToken cancellationToken);

    /// <summary>Settles the record. Called on the terminal path of the workflow.</summary>
    ValueTask CloseAsync(string status, CancellationToken cancellationToken);
}

/// <summary>Root aggregate row as the generic store holds it.</summary>
public sealed record AuditRecordRoot
{
    public required string InstanceId { get; init; }
    public required string WorkflowName { get; init; }
    public required string WorkflowVersion { get; init; }
    public required string RootKind { get; init; }
    public required string RootKey { get; init; }
    public required string Status { get; init; }
    public string AttributesJson { get; init; } = "{}";
    public DateTimeOffset OpenedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ClosedUtc { get; init; }
}

/// <summary>One child construct hanging off a root. Payload is opaque JSON to the framework.</summary>
public sealed record AuditRecordEntry
{
    public required Guid Id { get; init; }
    public required string InstanceId { get; init; }
    public required string SectionKind { get; init; }
    public string? Key { get; init; }
    public required string PayloadJson { get; init; }
    public required int Sequence { get; init; }
    public DateTimeOffset RecordedUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>A complete audit record: the root and everything recorded against it.</summary>
public sealed record AuditRecordDocument(AuditRecordRoot Root, IReadOnlyList<AuditRecordEntry> Entries);

/// <summary>
/// Generic backing storage for audit records. Deliberately workflow-agnostic — it stores a root, a
/// stream of typed-by-string entries, and JSON payloads, so a new workflow needs no schema change.
/// </summary>
public interface IAuditRecordStore
{
    /// <summary>Creates or updates the root. Keyed by instance id, so a resumed run reuses its record.</summary>
    ValueTask UpsertRootAsync(AuditRecordRoot root, CancellationToken cancellationToken);

    /// <summary>
    /// Appends an entry. Replaces any existing entry with the same (instance, kind, key) so a retried
    /// executor corrects its record rather than appending a second, contradictory one.
    /// </summary>
    ValueTask AppendAsync(AuditRecordEntry entry, CancellationToken cancellationToken);

    ValueTask<AuditRecordDocument?> GetAsync(string instanceId, CancellationToken cancellationToken);

    /// <summary>Roots for one workflow, newest first, optionally filtered by root key or status.</summary>
    ValueTask<IReadOnlyList<AuditRecordRoot>> ListAsync(
        string workflowName,
        string? rootKey,
        string? status,
        int limit,
        CancellationToken cancellationToken);
}

/// <summary>Statuses the framework writes; a workflow may use its own beyond these.</summary>
public static class AuditRecordStatus
{
    public const string Open = "Open";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
}

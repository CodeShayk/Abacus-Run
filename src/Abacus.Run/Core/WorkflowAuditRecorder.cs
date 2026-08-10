using System.Text.Json;
using Abacus.Run.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Abacus.Run.Core;

/// <summary>
/// Default <see cref="IWorkflowAuditRecorder"/>: validates entries against the workflow's declared
/// record shape, serializes payloads, and writes them to the generic store. One instance per running
/// workflow instance, so the entry sequence is a simple interlocked counter.
/// </summary>
/// <remarks>
/// Storage failures are logged and swallowed. The audit record explains work that has already
/// happened; failing a check because its explanation could not be filed would trade a correct result
/// for a missing one. An undeclared section kind is a programming error in the workflow definition
/// and is logged as a warning rather than throwing, for the same reason.
/// </remarks>
public sealed class WorkflowAuditRecorder : IWorkflowAuditRecorder
{
    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly IAuditRecordStore _store;
    private readonly string _instanceId;
    private readonly string _workflowName;
    private readonly string _workflowVersion;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;

    private int _sequence;
    private string _rootKey = string.Empty;
    private string _attributesJson = "{}";
    private DateTimeOffset _openedUtc;

    public WorkflowAuditRecorder(
        AuditRecordDefinition definition,
        IAuditRecordStore store,
        string instanceId,
        string workflowName,
        string workflowVersion,
        TimeProvider? clock = null,
        ILogger? logger = null)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _instanceId = instanceId;
        _workflowName = workflowName;
        _workflowVersion = workflowVersion;
        _clock = clock ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
        _openedUtc = _clock.GetUtcNow();
    }

    public AuditRecordDefinition Definition { get; }

    public async ValueTask OpenAsync(
        string rootKey,
        IReadOnlyDictionary<string, object?>? attributes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootKey);

        _rootKey = rootKey;
        _openedUtc = _clock.GetUtcNow();
        _attributesJson = Serialize(attributes ?? new Dictionary<string, object?>());

        await SafeAsync(
            () => _store.UpsertRootAsync(BuildRoot(AuditRecordStatus.Open, closedUtc: null), cancellationToken),
            "open the audit record").ConfigureAwait(false);
    }

    public async ValueTask RecordAsync(
        string sectionKind,
        string? key,
        object? payload,
        CancellationToken cancellationToken)
    {
        if (!Definition.Allows(sectionKind))
        {
            _logger.LogWarning(
                "Audit section '{SectionKind}' is not declared by workflow '{Workflow}'; entry dropped.",
                sectionKind, _workflowName);
            return;
        }

        var entry = new AuditRecordEntry
        {
            Id = Guid.NewGuid(),
            InstanceId = _instanceId,
            SectionKind = sectionKind,
            Key = key,
            PayloadJson = Serialize(payload),
            Sequence = Interlocked.Increment(ref _sequence),
            RecordedUtc = _clock.GetUtcNow()
        };

        await SafeAsync(
            () => _store.AppendAsync(entry, cancellationToken),
            $"record audit section '{sectionKind}'").ConfigureAwait(false);
    }

    public async ValueTask CloseAsync(string status, CancellationToken cancellationToken)
    {
        if (_rootKey.Length == 0)
        {
            // Nothing was opened — closing would write a root with no identity.
            _logger.LogDebug("Audit record for instance {InstanceId} was never opened; nothing to close.", _instanceId);
            return;
        }

        await SafeAsync(
            () => _store.UpsertRootAsync(
                BuildRoot(status, _clock.GetUtcNow()), cancellationToken),
            "close the audit record").ConfigureAwait(false);
    }

    private AuditRecordRoot BuildRoot(string status, DateTimeOffset? closedUtc) => new()
    {
        InstanceId = _instanceId,
        WorkflowName = _workflowName,
        WorkflowVersion = _workflowVersion,
        RootKind = Definition.RootKind,
        RootKey = _rootKey,
        Status = status,
        AttributesJson = _attributesJson,
        OpenedUtc = _openedUtc,
        ClosedUtc = closedUtc
    };

    private string Serialize(object? payload)
    {
        if (payload is null) return "null";
        if (payload is string s) return JsonSerializer.Serialize(s, PayloadJson);

        try
        {
            return JsonSerializer.Serialize(payload, payload.GetType(), PayloadJson);
        }
        catch (NotSupportedException ex)
        {
            _logger.LogWarning(ex, "Audit payload of type {Type} could not be serialized.", payload.GetType().Name);
            return JsonSerializer.Serialize(new { error = "payload-not-serializable", type = payload.GetType().Name }, PayloadJson);
        }
    }

    private async ValueTask SafeAsync(Func<ValueTask> action, string operation)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to {Operation} for instance {InstanceId}.", operation, _instanceId);
        }
    }
}

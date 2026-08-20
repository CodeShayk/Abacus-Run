using System.Text.Json;
using System.Text.Json.Nodes;
using Abacus.Run.Abstractions;
using Abacus.Run.Core;
using Abacus.Run.Dsl.Expressions;
using Abacus.Run.Dsl.Model;
using Abacus.Run.Dsl.Validation;
using Json.Schema;
using Microsoft.Agents.AI.Workflows;

namespace Abacus.Run.Dsl.Interpretation;

/// <summary>
/// A DSL document, registered and executed as an ordinary workflow definition.
/// </summary>
/// <remarks>
/// <para>
/// The document is parsed and validated once, at registration. <see cref="BuildAsync"/> runs per
/// attempt and only walks the model, constructing executors and edges — it never re-parses, never
/// re-validates, and never reads the file again.
/// </para>
/// <para>
/// Notifications and triggers are implemented unconditionally because their "nothing declared"
/// answers — the default policy and an empty trigger list — are exactly what a definition that did
/// not implement them would produce. An audit record is not like that: implementing
/// <c>IAuditedWorkflowDefinition</c> gives every run a record, so that one is a separate type.
/// </para>
/// </remarks>
public class DslWorkflowDefinition
    : IWorkflowDefinition<JsonElement, JsonElement>,
      IContextValidatingWorkflow,
      IDocumentAuthoredWorkflow,
      INotifyingWorkflow,
      IDomainEventTriggeredWorkflow
{
    private readonly DslNodeCatalog _catalog;
    private readonly TimeProvider _clock;
    private readonly JsonSchema? _contextSchema;
    private readonly NotificationPolicy _notifications;
    private readonly IReadOnlyList<DomainEventTrigger> _triggers;

    internal DslWorkflowDefinition(DslDocument document, DslNodeCatalog catalog, TimeProvider? clock = null)
    {
        Document = document ?? throw new ArgumentNullException(nameof(document));
        _catalog = catalog ?? new DslNodeCatalog();
        _clock = clock ?? TimeProvider.System;

        _contextSchema = document.ContextSchema is null
            ? null
            : JsonSchema.FromText(document.ContextSchema.ToJsonString());

        _notifications = BuildNotificationPolicy(document);
        _triggers = BuildTriggers(document);
    }

    /// <summary>Builds the definition, choosing the audited variant when the document declares one.</summary>
    public static DslWorkflowDefinition Create(
        DslDocument document, DslNodeCatalog? catalog = null, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        return document.Audit is null
            ? new DslWorkflowDefinition(document, catalog ?? new DslNodeCatalog(), clock)
            : new DslAuditedWorkflowDefinition(document, catalog ?? new DslNodeCatalog(), clock);
    }

    public DslDocument Document { get; }

    public string Name => Document.Name;

    public string Version => Document.Version;

    /// <summary>The canonical hash of the source document. Identity, and drift detection.</summary>
    public string DocumentHash => Document.Hash;

    /// <summary>Names this front end to the catalog, which cannot name it itself.</summary>
    public string Source => "dsl";

    public NotificationPolicy Notifications => _notifications;

    public IReadOnlyList<DomainEventTrigger> Triggers => _triggers;

    // ---- context validation ------------------------------------------------------------------

    /// <summary>
    /// Validates the start payload against the document's declared schema. The registry's own check
    /// binds to <see cref="JsonElement"/>, which never fails and therefore never says anything.
    /// </summary>
    public ContextValidationResult ValidateContext(JsonElement context)
    {
        if (_contextSchema is null)
        {
            return ContextValidationResult.Valid;
        }

        JsonNode? node = JsonSerializer.Deserialize<JsonNode>(context);

        EvaluationResults results = _contextSchema.Evaluate(node, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List
        });

        if (results.IsValid)
        {
            return ContextValidationResult.Valid;
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        foreach (EvaluationResults detail in Flatten(results).Where(r => r.Errors is { Count: > 0 }))
        {
            // Keyed by instance location so a caller can put each message beside the field it is
            // about, which is what a form needs and a flat list is not.
            string field = detail.InstanceLocation.ToString() is { Length: > 0 } location
                ? location.TrimStart('/').Replace('/', '.')
                : "context";

            string[] messages = [.. detail.Errors!.Values];
            errors[field] = errors.TryGetValue(field, out string[]? existing)
                ? [.. existing, .. messages]
                : messages;
        }

        if (errors.Count == 0)
        {
            errors["context"] = ["The context does not match the workflow's declared schema."];
        }

        return new ContextValidationResult(false, errors);
    }

    private static IEnumerable<EvaluationResults> Flatten(EvaluationResults results)
    {
        yield return results;

        foreach (EvaluationResults detail in results.Details)
        {
            foreach (EvaluationResults nested in Flatten(detail))
            {
                yield return nested;
            }
        }
    }

    // ---- graph -------------------------------------------------------------------------------

    public ValueTask<Workflow> BuildAsync(WorkflowBuildContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var bindings = new Dictionary<string, ExecutorBinding>(StringComparer.Ordinal);
        HashSet<string> outputs = OutputNodeIds();

        foreach (DslNode node in Document.Nodes)
        {
            IHostExecutor executor = DslBuiltInNodes.Create(
                new DslNodeContext(node, Document, context, outputs.Contains(node.Id)),
                _catalog, _clock);

            bindings[node.Id] = context.Node(executor, GateFor(node));
        }

        if (!bindings.TryGetValue(Document.Start, out ExecutorBinding? start))
        {
            throw new InvalidOperationException(
                $"Workflow '{Name}' starts at '{Document.Start}', which is not one of its nodes. " +
                "The document should not have registered.");
        }

        // The graph begins at the entry node, not at the document's start node: the first message
        // the engine sends is the deserialized context, and only the entry node is typed to receive
        // it. Its single edge hands the opened envelope to the declared start.
        ExecutorBinding entry = context.Node(new DslEntryExecutor());

        var builder = new WorkflowBuilder(entry);
        builder.AddEdge(entry, start);

        foreach (DslEdge edge in Document.Edges)
        {
            AddEdge(builder, edge, bindings);
        }

        // Every output node feeds the exit node, and the exit node is what the result is bound to.
        // The declared output nodes produce envelopes; the caller gets the payload.
        ExecutorBinding exit = context.Node(new DslExitExecutor());

        foreach (string id in outputs.Where(bindings.ContainsKey))
        {
            builder.AddEdge(bindings[id], exit);
        }

        builder.WithOutputFrom(exit);

        return new ValueTask<Workflow>(builder
            .WithName(Name)
            .WithDescription(Document.Description ?? string.Empty)
            .Build());
    }

    /// <summary>
    /// The nodes whose payload is the workflow's result: those the document declared, or every
    /// terminal node when it declared none.
    /// </summary>
    private HashSet<string> OutputNodeIds()
    {
        if (Document.Output.Count > 0)
        {
            return [.. Document.Output];
        }

        var hasOutgoing = new HashSet<string>(
            Document.Edges.SelectMany(e => e.From), StringComparer.Ordinal);

        return [.. Document.Nodes.Select(n => n.Id).Where(id => !hasOutgoing.Contains(id))];
    }

    private void AddEdge(
        WorkflowBuilder builder, DslEdge edge, IReadOnlyDictionary<string, ExecutorBinding> bindings)
    {
        if (edge.IsBarrier)
        {
            builder.AddFanInBarrierEdge(
                [.. edge.From.Select(id => bindings[id])], bindings[edge.To[0]], edge.Label);
            return;
        }

        ExecutorBinding source = bindings[edge.From[0]];

        if (edge.IsFanOut)
        {
            ExecutorBinding[] targets = [.. edge.To.Select(id => bindings[id])];

            if (edge.Select is { Length: > 0 } select)
            {
                builder.AddFanOutEdge<DslMessage>(source, targets,
                    targetSelector: (message, count) => SelectTargets(select, message, count),
                    label: edge.Label);
                return;
            }

            if (edge.Label is { Length: > 0 } fanOutLabel)
            {
                builder.AddFanOutEdge(source, targets, fanOutLabel);
                return;
            }

            builder.AddFanOutEdge(source, targets);
            return;
        }

        ExecutorBinding target = bindings[edge.To[0]];

        if (edge.When is { Length: > 0 } when)
        {
            builder.AddEdge<DslMessage>(source, target,
                condition: message => message is not null &&
                                      DslExpressions.Condition(when, message.ToExpressionContext(message.Run)),
                label: edge.Label,
                idempotent: edge.Idempotent);
            return;
        }

        builder.AddEdge(source, target, edge.Label, edge.Idempotent);
    }

    /// <summary>
    /// Resolves a fan-out selector to target indices. A number picks one target, an array picks
    /// several; anything else picks none, which stops the branch rather than failing the run.
    /// </summary>
    private static IEnumerable<int> SelectTargets(string select, DslMessage? message, int count)
    {
        if (message is null)
        {
            return [];
        }

        AbExValue value = DslExpressions.Evaluate(select, message.ToExpressionContext(message.Run));

        IEnumerable<int> indices = value.Kind switch
        {
            AbExValueKind.Number => [(int)value.AsNumber],
            AbExValueKind.Array => ((JsonArray)value.AsNode!)
                .Select(n => n is JsonValue v && v.TryGetValue(out int i) ? i : -1),
            _ => []
        };

        return indices.Where(i => i >= 0 && i < count).Distinct();
    }

    // ---- gates -------------------------------------------------------------------------------

    private static Action<ApprovalGateBuilder>? GateFor(DslNode node)
    {
        // An approval node is the gate: it exists to be the place a human decides, so it carries one
        // whether or not the document spelled the block out.
        DslGate? gate = node.Gate ?? (node is DslApprovalNode
            ? new DslGate { Mode = "requireApproval" }
            : null);

        if (gate is null || string.Equals(gate.Mode, "autonomous", StringComparison.Ordinal))
        {
            return null;
        }

        return builder =>
        {
            if (string.Equals(gate.Mode, "conditional", StringComparison.Ordinal) &&
                gate.When is { Length: > 0 } when)
            {
                builder.WhenAsync(input => new ValueTask<bool>(
                    input is DslMessage message &&
                    DslExpressions.Condition(when, message.ToExpressionContext(message.Run))));
            }
            else
            {
                builder.Mode(ExecutionMode.RequireApproval);
            }

            if (gate.Reason is { Length: > 0 } reason)
            {
                builder.Reason(reason);
            }

            if (gate.AssignTo.Count > 0)
            {
                builder.AssignTo([.. gate.AssignTo]);
            }

            builder.RequireApprovers(gate.RequireApprovers);
            builder.ExpiresAfter(gate.ExpiresAfter);
            builder.OnExpiry(ExpiryActionOf(gate.OnExpiryAction), [.. gate.EscalateTo]);
            builder.AllowModification(gate.AllowModification);
            builder.RequireSegregationOfDuties(gate.RequireSegregationOfDuties);
            builder.Locked(gate.Locked);
        };
    }

    private static ExpiryAction ExpiryActionOf(string action) => action switch
    {
        "reject" => ExpiryAction.Reject,
        "autoApprove" => ExpiryAction.AutoApprove,
        "escalate" => ExpiryAction.Escalate,
        _ => ExpiryAction.DeadStop
    };

    // ---- failure classification -----------------------------------------------------------------

    /// <summary>
    /// Applies the document's rules in order, then defers. Deferring rather than defaulting to retry
    /// matters: the framework's classifier already knows that a rate limit is worth retrying and a
    /// validation error is not, and a document should only have to state where it disagrees.
    /// </summary>
    public FailureDisposition Classify(WorkflowFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        // Ahead of the document's own rules, and deliberately not overridable by them: a document
        // that cannot be interpreted will not interpret on the next attempt either, so retrying
        // spends the attempt budget to arrive at the same message. The run should stop and say so.
        if (Contains<DslInterpretationException>(failure.Exception))
        {
            return FailureDisposition.DeadStop;
        }

        foreach (DslFailureRule rule in Document.OnFailure.Where(r => Matches(r, failure)))
        {
            return rule.Disposition switch
            {
                "deadStop" => FailureDisposition.DeadStop,
                "escalate" => FailureDisposition.Escalate,
                _ => FailureDisposition.Retry
            };
        }

        return DefaultFailureClassifier.Instance.Classify(failure);
    }

    /// <summary>
    /// Walks the chain, because a build failure reaches the classifier wrapped — the engine surfaces
    /// it through whatever the graph construction threw it into.
    /// </summary>
    private static bool Contains<T>(Exception? exception) where T : Exception
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is T)
            {
                return true;
            }

            if (current is AggregateException aggregate &&
                aggregate.InnerExceptions.Any(Contains<T>))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Matches(DslFailureRule rule, WorkflowFailure failure)
    {
        if (rule.Node is { Length: > 0 } node &&
            !string.Equals(node, failure.ExecutorId, StringComparison.Ordinal))
        {
            return false;
        }

        if (rule.Exception is { Length: > 0 } exception &&
            !string.Equals(failure.Exception?.GetType().Name, exception, StringComparison.Ordinal))
        {
            return false;
        }

        if (rule.Status is { Length: > 0 } status)
        {
            if (failure.Exception is not ApiCallFailureException api || !StatusMatches(status, api.StatusCode))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Matches an exact code, or a class such as <c>5xx</c>.</summary>
    internal static bool StatusMatches(string pattern, int status)
    {
        if (int.TryParse(pattern, out int exact))
        {
            return exact == status;
        }

        return pattern.Length == 3 &&
               char.IsDigit(pattern[0]) &&
               status / 100 == pattern[0] - '0';
    }

    // ---- notifications and triggers ---------------------------------------------------------------

    private static NotificationPolicy BuildNotificationPolicy(DslDocument document)
    {
        if (document.Notifications is not { } declared)
        {
            return NotificationPolicy.Default;
        }

        return new NotificationPolicy
        {
            Level = LevelOf(declared.Level),
            StreamEvents = declared.Stream,
            ByNode = declared.ByNode.ToDictionary(p => p.Key, p => LevelOf(p.Value), StringComparer.Ordinal),
            // Names declared up front plus every per-node 'notify', so the catalog advertises
            // everything this workflow can emit rather than only what was listed twice.
            Emits = declared.Emits
                .Concat(document.Nodes.Where(n => n.Notify is not null).Select(n => n.Notify!.Name))
                .Distinct(StringComparer.Ordinal)
                .ToArray()
        };
    }

    private static NotificationLevel LevelOf(string level) => level switch
    {
        "minimal" => NotificationLevel.Minimal,
        "lifecycle" => NotificationLevel.Lifecycle,
        _ => NotificationLevel.Standard
    };

    private static IReadOnlyList<DomainEventTrigger> BuildTriggers(DslDocument document)
        => [.. document.Triggers.Select(t => new DomainEventTrigger
        {
            TopicFilter = t.Topic,

            // A literal filter value, not a projection: the subscription is registered before any
            // message exists. The validator warns about one written to look like an expression.
            CorrelationKey = t.CorrelationKey is { Length: > 0 } key ? key : null,

            ContextSelector = t.ContextFrom is { Length: > 0 } projection
                ? message => ProjectContext(projection, message)
                : null
        })];

    /// <summary>
    /// Maps a triggering message onto the workflow's start context. The payload is bound to
    /// <c>$</c>; there is no <c>$ctx</c> yet, because this is what produces it.
    /// </summary>
    private static string ProjectContext(string expression, DomainEventMessage message)
    {
        JsonNode? payload = null;
        try
        {
            payload = JsonNode.Parse(message.PayloadJson);
        }
        catch (JsonException)
        {
            // A payload that is not JSON cannot be projected. Falling through to an empty context
            // starts the instance and lets its own context schema report the real problem.
        }

        AbExValue value = DslExpressions.Evaluate(expression, new AbExContext(payload, payload, []));

        return value.IsAbsent
            ? message.PayloadJson
            : value.ToNode()?.ToJsonString() ?? message.PayloadJson;
    }
}

/// <summary>A DSL workflow that keeps an audit record, because its document declared one.</summary>
public sealed class DslAuditedWorkflowDefinition : DslWorkflowDefinition, IAuditedWorkflowDefinition
{
    internal DslAuditedWorkflowDefinition(
        DslDocument document, DslNodeCatalog catalog, TimeProvider? clock = null)
        : base(document, catalog, clock)
    {
        DslAudit audit = document.Audit
            ?? throw new InvalidOperationException(
                $"'{document.Name}' was built as audited but declares no audit block.");

        AuditRecord = new AuditRecordDefinition(
            document.Name,
            document.Description ?? $"Audit record for '{document.Name}'.",
            [.. audit.Sections.Select(s => new AuditSectionDefinition(s, $"'{s}' entries."))]);
    }

    public AuditRecordDefinition AuditRecord { get; }
}

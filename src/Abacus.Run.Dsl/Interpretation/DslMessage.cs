using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Abacus.Run.Dsl.Expressions;
using Abacus.Run.Executors;

namespace Abacus.Run.Dsl.Interpretation;

/// <summary>Provenance the interpreter maintains. Read-only to expressions.</summary>
public sealed record DslMeta(string? Node = null, int Superstep = 0, int Attempt = 1);

/// <summary>
/// The single message type every DSL node sends and receives.
/// </summary>
/// <remarks>
/// <para>
/// One envelope type is what lets a JSON document describe a graph whose C# API is generic: every
/// node is <c>HostExecutor&lt;DslMessage, DslMessage&gt;</c>, so every edge type-checks by
/// construction and there is no type-flow analysis to write.
/// </para>
/// <para>
/// <see cref="Ctx"/> is the reason an expression eleven nodes deep can still read the start context.
/// A compiled node closes over whatever C# scope it likes; a document has no scope, so the envelope
/// carries one. It is a reference copy — cloned once at start and never again — so a large context
/// is not duplicated per node.
/// </para>
/// <para>
/// Immutable, so a message a checkpoint captured cannot be mutated by a node that runs later.
/// </para>
/// </remarks>
public sealed class DslMessage : ITemplateBindingSource
{
    private static readonly ConcurrentDictionary<string, AbExResult> TemplateCache =
        new(StringComparer.Ordinal);

    public DslMessage(JsonNode? ctx, JsonNode? data, DslMeta? meta = null, JsonObject? run = null)
    {
        Ctx = ctx;
        Data = data;
        Meta = meta ?? new DslMeta();
        Run = run ?? [];
    }

    /// <summary>The start context, frozen. Copied through every node unchanged.</summary>
    public JsonNode? Ctx { get; }

    /// <summary>The current value. This is what a node reads and what it replaces.</summary>
    public JsonNode? Data { get; }

    public DslMeta Meta { get; }

    /// <summary>
    /// Backs the <c>$run</c> expression root. Ambient runtime identity rather than payload, so it is
    /// not part of the envelope a node reads or writes.
    /// </summary>
    public JsonObject Run { get; }

    /// <summary>Replaces <see cref="Data"/>, carrying <see cref="Ctx"/> through untouched.</summary>
    public DslMessage WithData(JsonNode? data) => new(Ctx, data, Meta, Run);

    public DslMessage WithMeta(DslMeta meta) => new(Ctx, Data, meta, Run);

    public DslMessage WithRun(JsonObject run) => new(Ctx, Data, Meta, run);

    /// <summary>
    /// Resolves a <c>{{ ... }}</c> placeholder as a full AbEx expression, so a template reaches the
    /// same three roots a condition does. Parses are cached: the same handful of templates are
    /// rendered once per invocation for the life of the host.
    /// </summary>
    string? ITemplateBindingSource.Resolve(string expression)
    {
        AbExResult parsed = TemplateCache.GetOrAdd(expression, AbExParser.Parse);

        // An unparseable placeholder renders as empty rather than throwing. The semantic validator
        // has already rejected the document if it got the chance; at run time a broken template must
        // not take down a run that is otherwise fine.
        if (!parsed.IsSuccess)
        {
            return null;
        }

        AbExValue value = AbExEvaluator.Evaluate(parsed.Node!, ToExpressionContext(Run));
        return value.IsAbsent ? null : value.ToText();
    }

    /// <summary>
    /// Opens an envelope from a start context. <c>data</c> begins as a copy of the context, so the
    /// first node reads the payload through <c>$</c> as well as <c>$ctx</c> — a workflow whose first
    /// node needs nothing else should not have to say <c>$ctx</c> to reach it.
    /// </summary>
    public static DslMessage Start(JsonElement context)
    {
        JsonNode? ctx = JsonSerializer.Deserialize<JsonNode>(context);
        return new DslMessage(ctx, ctx?.DeepClone());
    }

    public static DslMessage Start(JsonNode? context)
        => new(context, context?.DeepClone());

    /// <summary>Binds this envelope to the three expression roots.</summary>
    public AbExContext ToExpressionContext(JsonObject run) => new(Data, Ctx, run);

    /// <summary>Builds the <c>$run</c> object from the values the runtime knows.</summary>
    public static JsonObject RunMetadata(
        string instanceId, string? tenantId, string workflow, string version,
        int attempt, int superstep, DateTimeOffset now) =>
        new()
        {
            ["instanceId"] = instanceId,
            ["tenantId"] = tenantId,
            ["workflow"] = workflow,
            ["version"] = version,
            ["attempt"] = attempt,
            ["superstep"] = superstep,
            ["now"] = now.ToString("O")
        };

    public override string ToString()
        => $"DslMessage(node={Meta.Node ?? "-"}, data={Data?.ToJsonString() ?? "null"})";
}

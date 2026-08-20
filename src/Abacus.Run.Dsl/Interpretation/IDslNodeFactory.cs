using System.Text.Json.Nodes;
using Abacus.Run.Abstractions;
using Abacus.Run.Dsl.Model;

namespace Abacus.Run.Dsl.Interpretation;

/// <summary>
/// A document that validated but cannot be turned into a graph.
/// </summary>
/// <remarks>
/// Distinct from <c>DslValidationException</c> because it is thrown during a build rather than at
/// registration, and distinct from an ordinary node failure because it is deterministic: the
/// interpretation that failed on this attempt will fail identically on the next one. Classified as a
/// dead stop for exactly that reason — retrying it only burns attempts before reporting the same
/// message.
/// </remarks>
public sealed class DslInterpretationException : Exception
{
    public DslInterpretationException(string message) : base(message)
    {
    }

    public DslInterpretationException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>What a factory is given when a document asks for one of its nodes.</summary>
public sealed record DslNodeContext(
    DslNode Node,
    DslDocument Document,
    WorkflowBuildContext Build,
    bool IsOutput = false)
{
    /// <summary>The host's services. Same provider a compiled definition resolves from.</summary>
    public IServiceProvider? Services => Build.Services;

    /// <summary>The <c>with</c> block, for a custom node. Empty rather than null so callers need no guard.</summary>
    public JsonObject Parameters => Node is DslCustomNode { With: JsonObject with } ? with : [];

    public T Require<T>() where T : notnull
        => Services is null
            ? throw new InvalidOperationException(
                $"Node '{Node.Id}' needs {typeof(T).Name}, but the build has no service provider.")
            : (T)(Services.GetService(typeof(T))
                ?? throw new InvalidOperationException(
                    $"Node '{Node.Id}' needs {typeof(T).Name}, which is not registered. " +
                    $"A '{Node.Kind}' node cannot run without it."));

    public T? Optional<T>() where T : class => Services?.GetService(typeof(T)) as T;
}

/// <summary>
/// Turns one declared node into an executor.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole extensibility story. The DSL composes registered behaviour and never carries
/// behaviour of its own, so the answer to "the DSL cannot express this" is always <em>ship a node
/// and name it</em> — never <em>embed a script</em>. Engineers extend the vocabulary; authors
/// compose it.
/// </para>
/// <para>
/// The executor returned must be a <see cref="DslExecutor"/>, or at least a
/// <c>HostExecutor&lt;DslMessage, DslMessage&gt;</c>: every edge in a DSL graph carries the envelope,
/// and a node that emitted anything else would break the next edge rather than its own.
/// </para>
/// </remarks>
public interface IDslNodeFactory
{
    /// <summary>The name a document uses in <c>"node"</c>. Lower-kebab, like a node id.</summary>
    string Name { get; }

    /// <summary>
    /// JSON Schema for this node's <c>with</c> block, validated at registration. Null accepts
    /// anything — reasonable for a node with no parameters, and a missed opportunity otherwise,
    /// since a schema here turns a run-time surprise into a startup failure.
    /// </summary>
    JsonNode? ParameterSchema => null;

    IHostExecutor Create(DslNodeContext context);
}

/// <summary>Adapts a delegate into a factory, for a node whose construction is a one-liner.</summary>
public sealed class DelegateDslNodeFactory : IDslNodeFactory
{
    private readonly Func<DslNodeContext, IHostExecutor> _create;

    public DelegateDslNodeFactory(
        string name, Func<DslNodeContext, IHostExecutor> create, JsonNode? parameterSchema = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        _create = create ?? throw new ArgumentNullException(nameof(create));
        ParameterSchema = parameterSchema;
    }

    public string Name { get; }

    public JsonNode? ParameterSchema { get; }

    public IHostExecutor Create(DslNodeContext context) => _create(context);
}

/// <summary>The registered custom node catalog, resolved at registration rather than at run time.</summary>
public sealed class DslNodeCatalog
{
    private readonly Dictionary<string, IDslNodeFactory> _factories = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> Names => _factories.Keys;

    public DslNodeCatalog Add(IDslNodeFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        if (!_factories.TryAdd(factory.Name, factory))
        {
            // Two factories under one name is a composition mistake, and the one that would win is
            // whichever registration ran first — not something to discover from behaviour.
            throw new InvalidOperationException(
                $"A DSL node named '{factory.Name}' is already registered.");
        }

        return this;
    }

    public bool TryGet(string name, out IDslNodeFactory factory) => _factories.TryGetValue(name, out factory!);

    /// <summary>The shape the semantic validator checks <c>with</c> blocks against.</summary>
    public IReadOnlyDictionary<string, JsonNode?> Describe()
        => _factories.ToDictionary(p => p.Key, p => p.Value.ParameterSchema, StringComparer.Ordinal);
}

using Abacus.Run.Abstractions;
using Abacus.Run.Dsl.Interpretation;
using Abacus.Run.Dsl.Model;
using Abacus.Run.Dsl.Validation;

namespace Abacus.Run.Dsl.Hosting;

/// <summary>Where a document came from, for naming it in a diagnostic.</summary>
public sealed record DslSource(string Description, Func<string> Read);

/// <summary>
/// Collects DSL documents and custom node registrations during composition, then resolves them once
/// the container is built.
/// </summary>
/// <remarks>
/// <para>
/// Resolution is deferred for one reason: <c>AddDslWorkflow</c> and <c>AddDslNode</c> can be called
/// in either order, and a document must be validated against the <em>complete</em> node catalog.
/// Validating a document the moment it is added would make correctness depend on the order the
/// composition happened to be written in.
/// </para>
/// <para>
/// A document that fails validation throws here, which surfaces as a startup failure — the same
/// place a bad compiled workflow fails, and for the same reason.
/// </para>
/// </remarks>
public sealed class DslRegistry
{
    private readonly List<DslSource> _sources = [];
    private readonly DslNodeCatalog _catalog = new();
    private readonly Lock _gate = new();

    private IReadOnlyList<DslWorkflowDefinition>? _resolved;

    public DslPolicy Policy { get; set; } = DslPolicy.Default;

    /// <summary>Whether <c>http</c> nodes must declare an allow-list. Mirrors the host's egress setting.</summary>
    public bool EnforceEgress { get; set; } = true;

    public TimeProvider Clock { get; set; } = TimeProvider.System;

    public DslNodeCatalog Catalog => _catalog;

    /// <summary>
    /// Adds a document and returns its position, which is also its position in the resolved list —
    /// validation either succeeds for every source or throws, so the two stay one to one.
    /// </summary>
    public int AddSource(DslSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        lock (_gate)
        {
            if (_resolved is not null)
            {
                throw new InvalidOperationException(
                    "DSL documents cannot be added after the registry has been resolved.");
            }

            _sources.Add(source);
            return _sources.Count - 1;
        }
    }

    public DslRegistry AddNode(IDslNodeFactory factory)
    {
        lock (_gate)
        {
            if (_resolved is not null)
            {
                throw new InvalidOperationException(
                    "DSL nodes cannot be registered after the registry has been resolved.");
            }

            _catalog.Add(factory);
        }

        return this;
    }

    /// <summary>
    /// Parses and validates every document against the complete catalog, once. Later calls return the
    /// same definitions — the registry is read at startup and never again.
    /// </summary>
    public IReadOnlyList<DslWorkflowDefinition> Resolve()
    {
        lock (_gate)
        {
            if (_resolved is not null)
            {
                return _resolved;
            }

            var definitions = new List<DslWorkflowDefinition>(_sources.Count);
            var published = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var failures = new List<string>();

            foreach (DslSource source in _sources)
            {
                var environment = new DslEnvironment
                {
                    CustomNodes = _catalog.Describe(),
                    EnforceEgress = EnforceEgress,

                    // Accumulated as documents resolve, so a second document claiming a published
                    // (name, version) with different content is caught here rather than by the
                    // registry's duplicate-version check, which cannot say why they differ.
                    PublishedHashes = published,
                    Policy = Policy
                };

                DslParseResult result;
                try
                {
                    result = DslParser.Parse(source.Read(), environment);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failures.Add($"{source.Description}: {ex.Message}");
                    continue;
                }

                if (!result.IsValid)
                {
                    failures.Add($"{source.Description}:{Environment.NewLine}{result.Validation.Describe()}");
                    continue;
                }

                DslDocument document = result.Document!;
                published[$"{document.Name}@{document.Version}"] = document.Hash;
                definitions.Add(DslWorkflowDefinition.Create(document, _catalog, Clock));
            }

            if (failures.Count > 0)
            {
                // Every failure, not the first. A composition with three broken documents should take
                // one startup to fix, not three.
                throw new DslValidationException(
                    $"{failures.Count} DSL document(s) failed validation:{Environment.NewLine}{Environment.NewLine}" +
                    string.Join(Environment.NewLine + Environment.NewLine, failures),
                    DslValidationResult.Empty);
            }

            _resolved = definitions;
            return _resolved;
        }
    }

    /// <summary>The registered documents, for the catalog endpoint. Resolves if it has not already.</summary>
    public IReadOnlyList<DslWorkflowDefinition> Definitions => Resolve();

    /// <summary>Looks up a definition by name and version, for reporting a document's hash.</summary>
    public DslWorkflowDefinition? Find(string name, string? version = null)
        => Resolve().FirstOrDefault(d =>
            string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase) &&
            (version is null || string.Equals(d.Version, version, StringComparison.OrdinalIgnoreCase)));
}

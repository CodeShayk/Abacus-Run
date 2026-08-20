using System.Text.Json.Nodes;

namespace Abacus.Run.Dsl.Validation;

/// <summary>Bounds a document. All configurable down, none up.</summary>
public sealed record DslPolicy
{
    public static DslPolicy Default { get; } = new();

    public int MaxDocumentBytes { get; init; } = 1024 * 1024;
    public int MaxNodes { get; init; } = 500;
    public int MaxEdges { get; init; } = 2000;
    public int MaxExpressionDepth { get; init; } = 32;

    /// <summary>Supported major versions of the <c>dsl</c> media identifier.</summary>
    public IReadOnlyList<int> SupportedMajorVersions { get; init; } = [1];
}

/// <summary>
/// What the host knows that a document alone cannot be checked against: which custom nodes are
/// registered, whether egress is enforced, and which <c>(name, version)</c> pairs are already
/// published.
/// </summary>
/// <remarks>
/// Optional by design. Offline linting has no host, and the checks that need one are reported as
/// <em>skipped</em> rather than passed — a check that silently did not run is worse than one that
/// openly did not.
/// </remarks>
public sealed record DslEnvironment
{
    /// <summary>Registered custom node names, mapped to the parameter schema each one publishes.</summary>
    public IReadOnlyDictionary<string, JsonNode?> CustomNodes { get; init; } =
        new Dictionary<string, JsonNode?>(StringComparer.Ordinal);

    /// <summary>Whether <c>http</c> nodes must declare an allow-list.</summary>
    public bool EnforceEgress { get; init; } = true;

    /// <summary>Already-registered documents, keyed <c>name@version</c>, mapped to their hash.</summary>
    public IReadOnlyDictionary<string, string> PublishedHashes { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public DslPolicy Policy { get; init; } = DslPolicy.Default;
}

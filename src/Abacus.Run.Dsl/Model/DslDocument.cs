using System.Text.Json.Nodes;

namespace Abacus.Run.Dsl.Model;

/// <summary>A parsed, structurally valid DSL document.</summary>
/// <remarks>
/// Every element carries the JSON Pointer it was read from. Positions are built in rather than
/// retrofitted, because a validator that cannot say <em>where</em> is a validator people stop using.
/// </remarks>
public sealed record DslDocument
{
    /// <summary>Media identifier, e.g. <c>abacus.workflow/1.0</c>.</summary>
    public required string Dsl { get; init; }

    public required string Name { get; init; }
    public required string Version { get; init; }
    public string? Description { get; init; }

    /// <summary>JSON Schema the start payload must satisfy.</summary>
    public JsonNode? ContextSchema { get; init; }

    public JsonNode? ResultSchema { get; init; }

    /// <summary>Enforce declared node input/output schemas at run time.</summary>
    public bool Strict { get; init; }

    public required string Start { get; init; }
    public IReadOnlyList<string> Output { get; init; } = [];
    public IReadOnlyList<DslNode> Nodes { get; init; } = [];
    public IReadOnlyList<DslEdge> Edges { get; init; } = [];
    public IReadOnlyList<DslTrigger> Triggers { get; init; } = [];
    public DslNotifications? Notifications { get; init; }
    public IReadOnlyList<DslFailureRule> OnFailure { get; init; } = [];
    public DslAudit? Audit { get; init; }
    public DslLimits Limits { get; init; } = new();

    /// <summary>Canonical SHA-256 of the source document. Identity for the immutability rule.</summary>
    public string Hash { get; init; } = string.Empty;

    /// <summary>Major version of <see cref="Dsl"/>, used to decide interpreter compatibility.</summary>
    public int MajorVersion
    {
        get
        {
            int slash = Dsl.LastIndexOf('/');
            if (slash < 0 || slash + 1 >= Dsl.Length)
            {
                return -1;
            }

            string version = Dsl[(slash + 1)..];
            int dot = version.IndexOf('.');
            return int.TryParse(dot < 0 ? version : version[..dot], out int major) ? major : -1;
        }
    }

    public DslNode? FindNode(string id)
        => Nodes.FirstOrDefault(n => string.Equals(n.Id, id, StringComparison.Ordinal));
}

/// <summary>Approval gate configuration on one node.</summary>
public sealed record DslGate
{
    public required string Mode { get; init; }
    public string? When { get; init; }
    public string? Reason { get; init; }
    public IReadOnlyList<string> AssignTo { get; init; } = [];
    public int RequireApprovers { get; init; } = 1;
    public TimeSpan ExpiresAfter { get; init; } = TimeSpan.FromHours(24);
    public string OnExpiryAction { get; init; } = "deadStop";
    public IReadOnlyList<string> EscalateTo { get; init; } = [];
    public bool AllowModification { get; init; }
    public bool RequireSegregationOfDuties { get; init; }
    public bool Locked { get; init; }
    public string Pointer { get; init; } = string.Empty;
}

/// <summary>A workflow-defined notification a node emits after it succeeds.</summary>
public sealed record DslNotify
{
    public required string Name { get; init; }
    public IReadOnlyDictionary<string, string> Payload { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
    public string Pointer { get; init; } = string.Empty;
}

/// <summary>
/// One edge. <c>From</c> with several entries is a fan-in barrier; <c>To</c> with several is a
/// fan-out. Both are modelled as lists so the graph builder reads one shape.
/// </summary>
public sealed record DslEdge
{
    public IReadOnlyList<string> From { get; init; } = [];
    public IReadOnlyList<string> To { get; init; } = [];
    public string? When { get; init; }
    public string? Select { get; init; }
    public string? Label { get; init; }
    public bool Idempotent { get; init; }
    public string Pointer { get; init; } = string.Empty;

    public bool IsBarrier => From.Count > 1;
    public bool IsFanOut => To.Count > 1;
}

public sealed record DslTrigger
{
    public required string Topic { get; init; }
    public string? CorrelationKey { get; init; }
    public string? ContextFrom { get; init; }
    public string Pointer { get; init; } = string.Empty;
}

public sealed record DslNotifications
{
    public string Level { get; init; } = "standard";
    public bool Stream { get; init; } = true;
    public IReadOnlyDictionary<string, string> ByNode { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
    public IReadOnlyList<string> Emits { get; init; } = [];
    public string Pointer { get; init; } = string.Empty;
}

public sealed record DslFailureRule
{
    public string? Exception { get; init; }
    public string? Status { get; init; }
    public string? Node { get; init; }
    public required string Disposition { get; init; }
    public string Pointer { get; init; } = string.Empty;
}

public sealed record DslAudit
{
    public string? Key { get; init; }
    public IReadOnlyList<string> Sections { get; init; } = [];
    public string Pointer { get; init; } = string.Empty;
}

public sealed record DslLimits
{
    public int MaxAttempts { get; init; } = 5;
    public int? MaxLifetimeHours { get; init; }
}

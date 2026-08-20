namespace Abacus.Run.Dsl.Validation;

public enum DslSeverity
{
    Error,
    Warning
}

/// <summary>
/// One finding about a document, located precisely enough for an editor to underline it.
/// </summary>
/// <remarks>
/// The pointer is not a nicety. A DSL without precise error locations is a DSL people abandon after
/// the third unhelpful failure, so every check is required to produce one.
/// </remarks>
public sealed record DslDiagnostic(
    string Code,
    DslSeverity Severity,
    string Pointer,
    string Message,
    string? Suggestion = null)
{
    public static DslDiagnostic Error(string code, string pointer, string message, string? suggestion = null)
        => new(code, DslSeverity.Error, pointer, message, suggestion);

    public static DslDiagnostic Warning(string code, string pointer, string message, string? suggestion = null)
        => new(code, DslSeverity.Warning, pointer, message, suggestion);

    public override string ToString()
    {
        string severity = Severity == DslSeverity.Error ? "error" : "warn ";
        string location = string.IsNullOrEmpty(Pointer) ? "/" : Pointer;
        string suffix = Suggestion is null ? string.Empty : $" {Suggestion}";
        return $"{Code}  {severity}  {location}  {Message}{suffix}";
    }
}

/// <summary>The outcome of validating one document.</summary>
public sealed record DslValidationResult(
    IReadOnlyList<DslDiagnostic> Diagnostics,
    IReadOnlyList<string> SkippedChecks)
{
    public static DslValidationResult Empty { get; } = new([], []);

    public bool IsValid => !Diagnostics.Any(d => d.Severity == DslSeverity.Error);

    public IEnumerable<DslDiagnostic> Errors => Diagnostics.Where(d => d.Severity == DslSeverity.Error);

    public IEnumerable<DslDiagnostic> Warnings => Diagnostics.Where(d => d.Severity == DslSeverity.Warning);

    public bool Has(string code) => Diagnostics.Any(d => string.Equals(d.Code, code, StringComparison.Ordinal));

    public string Describe() => Diagnostics.Count == 0
        ? "No diagnostics."
        : string.Join(Environment.NewLine, Diagnostics.Select(d => d.ToString()));
}

/// <summary>
/// The stable diagnostic codes. Stable because they end up in logs, editor configuration and support
/// conversations, so renaming one is a breaking change to something other than code.
/// </summary>
public static class DslCodes
{
    // 01xx — document identity
    public const string UnsupportedDslVersion = "DSL0101";
    public const string HashConflict = "DSL0102";
    public const string MalformedJson = "DSL0103";
    public const string SchemaViolation = "DSL0104";

    // 02xx — references
    public const string DuplicateNodeId = "DSL0201";
    public const string StartNotFound = "DSL0202";
    public const string OutputNotFound = "DSL0203";
    public const string EdgeEndpointNotFound = "DSL0207";
    public const string DuplicateEdge = "DSL0208";

    // 03xx — graph shape
    public const string UnreachableNode = "DSL0301";
    public const string DeadEndNode = "DSL0302";
    public const string TightCycle = "DSL0303";
    public const string BarrierSourceUnreachable = "DSL0304";

    // 04xx — expressions
    public const string ExpressionParseError = "DSL0401";
    public const string UnknownFunction = "DSL0412";
    public const string NonDeterministicCondition = "DSL0413";
    public const string ExpressionTooDeep = "DSL0414";

    // 05xx — gates
    public const string GateOnNonGateableKind = "DSL0501";
    public const string ConditionalGateWithoutPredicate = "DSL0502";
    public const string EscalationWithoutAssignees = "DSL0503";

    // 06xx — environment
    public const string UnknownCustomNode = "DSL0601";
    public const string CustomNodeParameters = "DSL0602";
    public const string EgressHostsRequired = "DSL0603";

    // 07xx — policy
    public const string LimitExceeded = "DSL0701";
}

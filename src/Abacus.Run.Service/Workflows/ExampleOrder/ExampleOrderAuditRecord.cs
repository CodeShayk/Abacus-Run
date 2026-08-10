using Abacus.Run.Abstractions;

namespace Abacus.Run.Service.Workflows.ExampleOrder;

/// <summary>
/// The shape of this workflow's audit record, declared in one place.
/// </summary>
/// <remarks>
/// Section kinds are written into storage and read back by callers of the state route, so they are
/// part of the workflow's contract rather than incidental strings. Naming them as constants is what
/// keeps a typo at a call site from silently producing an undeclared section the recorder drops.
/// </remarks>
public static class ExampleOrderAuditRecord
{
    public const string RootKind = "order";

    /// <summary>What the caller asked for. One per run.</summary>
    public const string Submission = "submission";

    /// <summary>The plan formed before any line was priced. One per run.</summary>
    public const string Plan = "plan";

    /// <summary>One priced line, keyed by SKU. Many per run.</summary>
    public const string Line = "line";

    /// <summary>How the run settled — including a failure, when it failed. One per run.</summary>
    public const string Outcome = "outcome";

    public static readonly AuditRecordDefinition Definition = new(
        RootKind,
        "One order, as this workflow processed it: what was asked for, how it was planned, what each "
        + "line cost, and how the run settled.",
        [
            new AuditSectionDefinition(Submission, "The order as submitted.", Multiple: false),
            new AuditSectionDefinition(Plan, "The pricing plan formed before acting.", Multiple: false),
            new AuditSectionDefinition(Line, "One priced order line, keyed by SKU."),
            new AuditSectionDefinition(Outcome, "The settled total, or the failure that stopped the run.", Multiple: false)
        ]);
}

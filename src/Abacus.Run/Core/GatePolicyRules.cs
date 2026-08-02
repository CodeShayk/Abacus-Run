using Abacus.Run.Abstractions;

namespace Abacus.Run.Core;

/// <summary>
/// Decides what a tenant-supplied gate may do to the gate the workflow author declared in code.
/// </summary>
/// <remarks>
/// An unlocked declaration is advisory: tenant configuration replaces it outright, in either
/// direction. A declaration marked <see cref="ApprovalGate.Locked"/> is the author's floor —
/// configuration may still tighten it, never loosen it. <see cref="Violations"/> is what the API
/// reports on a rejected write; <see cref="Reconcile"/> is the runtime's belt-and-braces equivalent,
/// so a policy written before a gate was locked (or through some other store client) still cannot
/// weaken it at execution time.
/// </remarks>
public static class GatePolicyRules
{
    /// <summary>Higher means more human oversight.</summary>
    private static int Enforcement(ExecutionMode mode) => mode switch
    {
        ExecutionMode.RequireApproval => 2,
        ExecutionMode.Conditional => 1,
        _ => 0
    };

    /// <summary>
    /// The ways <paramref name="candidate"/> weakens <paramref name="declared"/>, empty when it does
    /// not. Always empty for an unlocked declaration.
    /// </summary>
    public static IReadOnlyList<string> Violations(ApprovalGate declared, ApprovalGate candidate)
    {
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(candidate);

        if (!declared.Locked)
        {
            return [];
        }

        var violations = new List<string>();

        if (Enforcement(candidate.Mode) < Enforcement(declared.Mode))
        {
            violations.Add($"Mode cannot be weakened from '{declared.Mode}' to '{candidate.Mode}'.");
        }

        if (candidate.RequiredApprovers < declared.RequiredApprovers)
        {
            violations.Add(
                $"RequiredApprovers cannot be lowered below {declared.RequiredApprovers}.");
        }

        if (candidate.AllowModification && !declared.AllowModification)
        {
            violations.Add("AllowModification cannot be enabled.");
        }

        if (declared.RequireSegregationOfDuties && !candidate.RequireSegregationOfDuties)
        {
            violations.Add("RequireSegregationOfDuties cannot be disabled.");
        }

        if (candidate.OnExpiry == ExpiryAction.AutoApprove && declared.OnExpiry != ExpiryAction.AutoApprove)
        {
            violations.Add("OnExpiry cannot be set to AutoApprove.");
        }

        return violations;
    }

    /// <summary>
    /// The gate that actually runs: <paramref name="candidate"/> with every field that weakens a
    /// locked <paramref name="declared"/> pulled back to the declaration.
    /// </summary>
    public static ApprovalGate Reconcile(ApprovalGate declared, ApprovalGate candidate)
    {
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(candidate);

        if (!declared.Locked)
        {
            return candidate;
        }

        bool keepDeclaredMode = Enforcement(candidate.Mode) < Enforcement(declared.Mode);

        return candidate with
        {
            Mode = keepDeclaredMode ? declared.Mode : candidate.Mode,

            // The predicate only lives on the declaration, so a Conditional floor keeps its own.
            Predicate = keepDeclaredMode || candidate.Mode == ExecutionMode.Conditional
                ? declared.Predicate
                : candidate.Predicate,

            RequiredApprovers = Math.Max(candidate.RequiredApprovers, declared.RequiredApprovers),
            AllowModification = candidate.AllowModification && declared.AllowModification,
            RequireSegregationOfDuties = candidate.RequireSegregationOfDuties || declared.RequireSegregationOfDuties,
            OnExpiry = candidate.OnExpiry == ExpiryAction.AutoApprove && declared.OnExpiry != ExpiryAction.AutoApprove
                ? declared.OnExpiry
                : candidate.OnExpiry,
            Locked = true
        };
    }
}

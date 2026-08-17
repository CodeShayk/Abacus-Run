namespace Abacus.Run.Abstractions;

/// <summary>
/// What is scoped to a tenant and what is not.
/// </summary>
/// <remarks>
/// <para>
/// <b>Workflow definitions are global.</b> A definition is code: one registry, one set of versions,
/// the same for everyone. Nothing about a definition is tenant-scoped, so nothing derived purely
/// from one — the catalog, the node list, the graph shape — needs a tenant to answer.
/// </para>
/// <para>
/// <b>Instances are tenant-scoped.</b> An instance is somebody's work, and every query path carries
/// the tenant filter. A cross-tenant read here is a security defect, not a bug (FR-4.7).
/// </para>
/// <para>
/// <b>Configuration sits between the two.</b> A gate policy may be written for one tenant, or for
/// all of them. <see cref="AllTenants"/> is how the second is said out loud.
/// </para>
/// </remarks>
public static class Tenancy
{
    /// <summary>
    /// The tenant that means "every tenant". Policy written at this scope applies wherever no more
    /// specific policy exists.
    /// </summary>
    /// <remarks>
    /// A sentinel rather than null, because null arrives by accident — an unset header, a missing
    /// claim, a forgotten parameter — and "I did not say" must not silently become "I meant
    /// everyone". <c>0</c> has to be typed.
    /// </remarks>
    public const string AllTenants = "0";

    /// <summary>The tenant assumed when a request names none.</summary>
    public const string Default = "default";

    /// <summary>
    /// Whether this identifies the all-tenants scope. Null and blank count, because the policy store
    /// has always used null for it and existing rows must keep resolving.
    /// </summary>
    public static bool IsAllTenants(string? tenantId)
        => string.IsNullOrWhiteSpace(tenantId) || string.Equals(tenantId, AllTenants, StringComparison.Ordinal);

    /// <summary>
    /// Collapses every spelling of the all-tenants scope to null, which is what the stores key on.
    /// </summary>
    /// <remarks>
    /// Normalising at the edge is what stops <c>0</c> becoming a tenant literally named "0" holding
    /// a policy nobody can find — the failure this constant exists to prevent.
    /// </remarks>
    public static string? Normalize(string? tenantId) => IsAllTenants(tenantId) ? null : tenantId;

    /// <summary>
    /// Whether this may own an instance. The all-tenants scope may not: an instance is somebody's
    /// work, and "everyone's" is not an owner.
    /// </summary>
    public static bool CanOwnInstance(string? tenantId) => !IsAllTenants(tenantId);
}

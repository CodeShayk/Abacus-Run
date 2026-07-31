using Abacus.Run.Abstractions;
using Abacus.Run.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Abacus.Run.Dispatch;

/// <summary>
/// Encapsulates lease acquisition, renewal, and release. Lease comparisons use the store's clock
/// (SQL <c>SYSUTCDATETIME()</c> in production) — never replica-local time — so clock skew across
/// replicas cannot produce two owners (§17 risk mitigation).
/// </summary>
public sealed class LeaseManager
{
    private readonly IInstanceStore _instances;
    private readonly WorkflowHostOptions _options;
    private readonly ILogger<LeaseManager> _logger;
    private readonly TimeProvider _clock;
    private readonly string _replicaId;

    public LeaseManager(
        IInstanceStore instances,
        IOptions<WorkflowHostOptions> options,
        ILogger<LeaseManager> logger,
        TimeProvider? clock = null)
    {
        _instances = instances;
        _options = options.Value;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _replicaId = _options.ReplicaId
            ?? Environment.GetEnvironmentVariable("POD_NAME")
            ?? Environment.MachineName;
    }

    public string ReplicaId => _replicaId;

    /// <summary>
    /// Atomically claims up to <paramref name="max"/> instances for this replica. Uses
    /// <c>READPAST</c> + <c>UPDLOCK</c> semantics so N replicas polling concurrently claim disjoint
    /// sets without blocking each other (§8.1).
    /// </summary>
    public ValueTask<IReadOnlyList<WorkflowInstance>> TryAcquireAsync(
        int max, CancellationToken cancellationToken)
        => _instances.ClaimAsync(
            _replicaId,
            Math.Min(max, _options.ClaimBatchSize),
            _options.Lease.Duration,
            _clock.GetUtcNow(),
            cancellationToken);

    /// <summary>
    /// Extends the lease for a running instance. Returns <c>false</c> when ownership was lost
    /// (another replica claimed it or an admin released it), which signals the run to cancel.
    /// </summary>
    public ValueTask<bool> RenewAsync(string instanceId, CancellationToken cancellationToken)
        => _instances.RenewLeaseAsync(instanceId, _replicaId, _options.Lease.Duration, cancellationToken);

    /// <summary>
    /// Explicitly releases a lease so another replica can pick it up immediately rather than
    /// waiting for TTL expiry. Called on normal run completion and during graceful drain.
    /// </summary>
    public ValueTask ReleaseAsync(string instanceId, CancellationToken cancellationToken)
        => _instances.ReleaseLeaseAsync(instanceId, _replicaId, cancellationToken);

    /// <summary>
    /// Releases all leases held by this replica. Used during graceful drain (§8.4) to turn a
    /// 90-second orphan recovery into a sub-2-second handoff on planned restarts.
    /// </summary>
    public async Task ReleaseAllAsync(IEnumerable<string> instanceIds, CancellationToken cancellationToken)
    {
        foreach (string instanceId in instanceIds)
        {
            try
            {
                await _instances.ReleaseLeaseAsync(instanceId, _replicaId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to release lease for {InstanceId} during drain.", instanceId);
            }
        }
    }

    /// <summary>
    /// Renews the lease if it is due (time since last renewal ≥ renewal interval). This is called
    /// from the run loop on superstep boundaries as an additional renewal mechanism beyond the
    /// independent timer loop.
    /// </summary>
    public async Task<bool> RenewIfDueAsync(
        string instanceId, DateTimeOffset lastRenewed, CancellationToken cancellationToken)
    {
        if (_clock.GetUtcNow() - lastRenewed < _options.Lease.Renewal)
        {
            return true;
        }

        bool renewed = await RenewAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (!renewed)
        {
            _logger.LogWarning("Lease renewal failed for {InstanceId} — ownership lost.", instanceId);
        }
        return renewed;
    }
}

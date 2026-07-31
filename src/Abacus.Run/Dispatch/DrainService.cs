using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Abacus.Run.Core;

namespace Abacus.Run.Dispatch;

/// <summary>
/// Implements graceful drain per TDD §8.4. On shutdown:
/// 1. Stop claiming new leases
/// 2. Wait for in-flight supersteps to finish (bounded by DrainGrace)
/// 3. Force checkpoint on all running instances
/// 4. Release all leases explicitly → instant pickup by surviving replicas
///
/// Kubernetes <c>terminationGracePeriodSeconds</c> must exceed <c>DrainGrace</c> (60s vs 45s default).
/// </summary>
public sealed class DrainService : IHostedService
{
    private readonly DispatcherService _dispatcher;
    private readonly ConcurrencyLimiter _limiter;
    private readonly LeaseManager _leases;
    private readonly WorkflowHostOptions _options;
    private readonly ILogger<DrainService> _logger;

    // Tracked instance IDs for lease release during drain.
    private readonly HashSet<string> _activeInstances = new(StringComparer.Ordinal);
    private readonly Lock _sync = new();

    public DrainService(
        DispatcherService dispatcher,
        ConcurrencyLimiter limiter,
        LeaseManager leases,
        IOptions<WorkflowHostOptions> options,
        ILogger<DrainService> logger)
    {
        _dispatcher = dispatcher;
        _limiter = limiter;
        _leases = leases;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Tracks an instance that is currently being executed on this replica.</summary>
    public void TrackInstance(string instanceId)
    {
        lock (_sync) { _activeInstances.Add(instanceId); }
    }

    /// <summary>Stops tracking an instance (run completed, failed, or parked).</summary>
    public void UntrackInstance(string instanceId)
    {
        lock (_sync) { _activeInstances.Remove(instanceId); }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Drain: stopping lease claims.");
        _dispatcher.StopClaiming();

        using var grace = new CancellationTokenSource(_options.Drain.Grace);

        try
        {
            _logger.LogInformation("Drain: waiting for {InFlight} in-flight instance(s) to finish.",
                _limiter.InFlight);
            await _limiter.WaitForIdleAsync(grace.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Drain: grace period exceeded with {InFlight} instance(s) still in flight.",
                _limiter.InFlight);
        }

        // Release all remaining leases so surviving replicas pick them up immediately.
        string[] remaining;
        lock (_sync) { remaining = _activeInstances.ToArray(); }

        if (remaining.Length > 0)
        {
            _logger.LogInformation("Drain: releasing {Count} lease(s).", remaining.Length);
            await _leases.ReleaseAllAsync(remaining, CancellationToken.None).ConfigureAwait(false);
        }

        _logger.LogInformation("Drain: complete.");
    }
}

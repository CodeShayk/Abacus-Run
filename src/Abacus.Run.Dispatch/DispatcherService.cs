using Abacus.Run.Abstractions;
using Abacus.Run.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Abacus.Run.Dispatch;

/// <summary>Bounds in-flight work globally and per workflow.</summary>
public sealed class ConcurrencyLimiter
{
    private readonly SemaphoreSlim _global;
    private readonly Dictionary<string, SemaphoreSlim> _perWorkflow;
    private int _inFlight;

    public ConcurrencyLimiter(int maxConcurrent, IReadOnlyDictionary<string, int>? perWorkflow = null)
    {
        _global = new SemaphoreSlim(maxConcurrent, maxConcurrent);
        Max = maxConcurrent;
        _perWorkflow = (perWorkflow ?? new Dictionary<string, int>())
            .ToDictionary(kv => kv.Key, kv => new SemaphoreSlim(kv.Value, kv.Value), StringComparer.OrdinalIgnoreCase);
    }

    public int Max { get; }
    public int InFlight => Volatile.Read(ref _inFlight);
    public int AvailableCapacity => Math.Max(0, Max - InFlight);

    public bool TryTake(string workflowName, out IDisposable? slot)
    {
        slot = null;
        if (!_global.Wait(0))
        {
            return false;
        }

        SemaphoreSlim? scoped = null;
        if (_perWorkflow.TryGetValue(workflowName, out SemaphoreSlim? perWorkflow))
        {
            if (!perWorkflow.Wait(0))
            {
                _global.Release();
                return false;
            }
            scoped = perWorkflow;
        }

        Interlocked.Increment(ref _inFlight);
        slot = new Slot(this, scoped);
        return true;
    }

    public async Task WaitForIdleAsync(CancellationToken cancellationToken)
    {
        while (InFlight > 0 && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class Slot : IDisposable
    {
        private readonly ConcurrencyLimiter _owner;
        private readonly SemaphoreSlim? _scoped;
        private int _disposed;

        public Slot(ConcurrencyLimiter owner, SemaphoreSlim? scoped)
        {
            _owner = owner;
            _scoped = scoped;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _scoped?.Release();
            _owner._global.Release();
            Interlocked.Decrement(ref _owner._inFlight);
        }
    }
}

public interface IWorkflowRunnerFactory
{
    WorkflowRunner Create(WorkflowInstance instance);
}

/// <summary>
/// Claims leases and drives runs. Claiming is deliberately poll-based with jitter so N replicas do
/// not converge on the same instant.
/// </summary>
public sealed class DispatcherService : BackgroundService
{
    private readonly IInstanceStore _instances;
    private readonly IWorkflowRunnerFactory _runners;
    private readonly ConcurrencyLimiter _limiter;
    private readonly WorkflowHostOptions _options;
    private readonly ILogger<DispatcherService> _logger;
    private readonly TimeProvider _clock;
    private readonly string _replicaId;

    private volatile bool _claiming = true;

    public DispatcherService(
        IInstanceStore instances,
        IWorkflowRunnerFactory runners,
        ConcurrencyLimiter limiter,
        IOptions<WorkflowHostOptions> options,
        ILogger<DispatcherService> logger,
        TimeProvider? clock = null)
    {
        _instances = instances;
        _runners = runners;
        _limiter = limiter;
        _options = options.Value;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _replicaId = _options.ReplicaId
            ?? Environment.GetEnvironmentVariable("POD_NAME")
            ?? Environment.MachineName;
    }

    public string ReplicaId => _replicaId;

    public void StopClaiming() => _claiming = false;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                int claimed = await PollOnceAsync(stoppingToken).ConfigureAwait(false);
                if (claimed == 0)
                {
                    await DelayWithJitterAsync(stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Dispatcher poll failed; backing off.");
                await DelayWithJitterAsync(stoppingToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>One claim-and-dispatch cycle. Exposed so tests can drive the loop deterministically.</summary>
    public async Task<int> PollOnceAsync(CancellationToken cancellationToken)
    {
        if (!_claiming)
        {
            return 0;
        }

        int capacity = _limiter.AvailableCapacity;
        if (capacity <= 0)
        {
            return 0;
        }

        IReadOnlyList<WorkflowInstance> leases = await _instances.ClaimAsync(
            _replicaId,
            Math.Min(capacity, _options.ClaimBatchSize),
            _options.Lease.Duration,
            _clock.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);

        foreach (WorkflowInstance instance in leases)
        {
            if (!_limiter.TryTake(instance.WorkflowName, out IDisposable? slot))
            {
                // Capacity vanished between the claim and the take; hand it straight back.
                await _instances.ReleaseLeaseAsync(instance.InstanceId, _replicaId, cancellationToken).ConfigureAwait(false);
                continue;
            }

            _ = RunLeasedAsync(instance, slot!, cancellationToken);
        }

        return leases.Count;
    }

    /// <summary>Runs one leased instance to a stopping point. Public for deterministic testing.</summary>
    public async Task RunLeasedAsync(WorkflowInstance instance, IDisposable slot, CancellationToken cancellationToken)
    {
        using (slot)
        {
            using var renewal = new CancellationTokenSource();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, renewal.Token);

            Task renewer = RenewLeaseLoopAsync(instance.InstanceId, renewal, linked.Token);

            try
            {
                WorkflowRunner runner = _runners.Create(instance);
                await runner.RunAsync(instance, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (renewal.IsCancellationRequested)
            {
                _logger.LogWarning("Lease lost for instance {InstanceId}; abandoning run on this replica.",
                    instance.InstanceId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled failure running instance {InstanceId}.", instance.InstanceId);
            }
            finally
            {
                await renewal.CancelAsync().ConfigureAwait(false);
                try { await renewer.ConfigureAwait(false); } catch (OperationCanceledException) { }
                await _instances.ReleaseLeaseAsync(instance.InstanceId, _replicaId, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task RenewLeaseLoopAsync(string instanceId, CancellationTokenSource lost, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(_options.Lease.Renewal, _clock, cancellationToken).ConfigureAwait(false);

                bool renewed = await _instances
                    .RenewLeaseAsync(instanceId, _replicaId, _options.Lease.Duration, cancellationToken)
                    .ConfigureAwait(false);

                if (!renewed)
                {
                    // Ownership is gone. Cancel the run before another replica duplicates the work.
                    await lost.CancelAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    private async Task DelayWithJitterAsync(CancellationToken cancellationToken)
    {
        int delay = Random.Shared.Next(500, 1500);
        await Task.Delay(TimeSpan.FromMilliseconds(delay), _clock, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Applies approval expiry policy on a fixed cadence.</summary>
public sealed class ExpirySweeperService : BackgroundService
{
    private readonly IApprovalService _approvals;
    private readonly WorkflowHostOptions _options;
    private readonly ILogger<ExpirySweeperService> _logger;
    private readonly TimeProvider _clock;

    public ExpirySweeperService(
        IApprovalService approvals,
        IOptions<WorkflowHostOptions> options,
        ILogger<ExpirySweeperService> logger,
        TimeProvider? clock = null)
    {
        _approvals = approvals;
        _options = options.Value;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(_options.Approvals.SweepIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, _clock, stoppingToken).ConfigureAwait(false);
                int swept = await _approvals.SweepExpiredAsync(stoppingToken).ConfigureAwait(false);
                if (swept > 0)
                {
                    _logger.LogInformation("Applied expiry policy to {Count} approval(s).", swept);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Approval expiry sweep failed.");
            }
        }
    }
}

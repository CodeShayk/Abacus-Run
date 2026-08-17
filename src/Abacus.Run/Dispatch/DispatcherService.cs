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
    private readonly IControlChannel? _control;
    private readonly string _replicaId;

    private volatile bool _claiming = true;

    public DispatcherService(
        IInstanceStore instances,
        IWorkflowRunnerFactory runners,
        ConcurrencyLimiter limiter,
        IOptions<WorkflowHostOptions> options,
        ILogger<DispatcherService> logger,
        TimeProvider? clock = null,
        IControlChannel? control = null)
    {
        _instances = instances;
        _runners = runners;
        _limiter = limiter;
        _options = options.Value;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _control = control;
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

    /// <summary>
    /// Listens for control signals aimed at this instance. Null when no channel is registered, or
    /// when subscribing fails — the run proceeds either way, because the instance row still carries
    /// the instruction and the next claim will see it.
    /// </summary>
    private async Task<IAsyncDisposable?> SubscribeToControlAsync(
        string instanceId, CancellationTokenSource signalled)
    {
        if (_control is null)
        {
            return null;
        }

        try
        {
            return await _control.SubscribeAsync(instanceId, signal =>
            {
                _logger.LogInformation(
                    "Control signal {Action} received for instance {InstanceId}.", signal.Action, instanceId);

                // Cancel and suspend both mean "stop taking steps". What the instance becomes was
                // already decided by the row the control service wrote.
                signalled.Cancel();
                return Task.CompletedTask;
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not subscribe to control signals for {InstanceId}; falling back to the instance row.",
                instanceId);
            return null;
        }
    }

    /// <summary>
    /// Whether the instance was cancelled between being claimed and being subscribed to.
    /// </summary>
    private async Task<bool> WasCancelledBeforeStartAsync(string instanceId, CancellationToken cancellationToken)
    {
        WorkflowInstance? current = await _instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);

        return current is null || current.CancellationRequested || current.Status.IsTerminal();
    }

    /// <summary>Runs one leased instance to a stopping point. Public for deterministic testing.</summary>
    public async Task RunLeasedAsync(WorkflowInstance instance, IDisposable slot, CancellationToken cancellationToken)
    {
        using (slot)
        {
            using var renewal = new CancellationTokenSource();
            using var signalled = new CancellationTokenSource();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, renewal.Token, signalled.Token);

            // Subscribe before running, so a signal sent while the graph is building is not missed.
            IAsyncDisposable? control = await SubscribeToControlAsync(instance.InstanceId, signalled)
                .ConfigureAwait(false);

            Task renewer = RenewLeaseLoopAsync(instance.InstanceId, renewal, linked.Token);

            try
            {
                // Closes the race between ClaimAsync and the subscription above: a cancel that
                // landed in that window left the flag on the row and nothing on the wire.
                if (await WasCancelledBeforeStartAsync(instance.InstanceId, cancellationToken).ConfigureAwait(false))
                {
                    _logger.LogInformation(
                        "Instance {InstanceId} was cancelled before this replica started it.", instance.InstanceId);
                    return;
                }

                WorkflowRunner runner = _runners.Create(instance);
                await runner.RunAsync(instance, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (signalled.IsCancellationRequested)
            {
                // Checked before the lease-loss case: both surface as a cancelled token, and
                // reporting an operator's cancel as a lost lease would send someone hunting a
                // clustering problem that does not exist. The control service already wrote the
                // terminal row, so nothing is transitioned here.
                _logger.LogInformation(
                    "Instance {InstanceId} stopped on an operator control signal.", instance.InstanceId);
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
                if (control is not null)
                {
                    await control.DisposeAsync().ConfigureAwait(false);
                }

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

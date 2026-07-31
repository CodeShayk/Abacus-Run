using Abacus.Run.Abstractions;
using Microsoft.Agents.AI.Workflows;

namespace Abacus.Run.Executors;

/// <summary>Pure mapping node.</summary>
public sealed class TransformExecutor<TIn, TOut> : HostExecutor<TIn, TOut>
    where TOut : class
{
    private readonly Func<TIn, TOut> _transform;

    public TransformExecutor(string id, Func<TIn, TOut> transform) : base(id)
        => _transform = transform ?? throw new ArgumentNullException(nameof(transform));

    public override IReadOnlyDictionary<string, object?> Metadata =>
        new Dictionary<string, object?> { ["node.kind"] = "transform" };

    protected override ValueTask<TOut> ExecuteCoreAsync(TIn input, IWorkflowContext context, CancellationToken cancellationToken)
        => ValueTask.FromResult(_transform(input));
}

/// <summary>Async delegate node — the general-purpose custom executor for simple work.</summary>
public sealed class DelegateExecutor<TIn, TOut> : HostExecutor<TIn, TOut>
    where TOut : class
{
    private readonly Func<TIn, IWorkflowContext, CancellationToken, ValueTask<TOut>> _handler;

    public DelegateExecutor(string id, Func<TIn, IWorkflowContext, CancellationToken, ValueTask<TOut>> handler)
        : base(id) => _handler = handler ?? throw new ArgumentNullException(nameof(handler));

    public DelegateExecutor(string id, Func<TIn, TOut> handler)
        : this(id, (input, _, _) => ValueTask.FromResult(handler(input))) { }

    public override IReadOnlyDictionary<string, object?> Metadata =>
        new Dictionary<string, object?> { ["node.kind"] = "delegate" };

    protected override ValueTask<TOut> ExecuteCoreAsync(TIn input, IWorkflowContext context, CancellationToken cancellationToken)
        => _handler(input, context, cancellationToken);
}

public sealed record TimerElapsed(string ExecutorId, DateTimeOffset WakeAt);

public interface ITimerService
{
    ValueTask ScheduleAsync(string instanceId, string executorId, DateTimeOffset wakeAt, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<(string InstanceId, string ExecutorId)>> ClaimDueAsync(DateTimeOffset now, int max, CancellationToken cancellationToken);
}

/// <summary>
/// Durable delay. Writes a timer row and halts rather than blocking a thread or holding a lease, so a
/// long delay costs no execution capacity.
/// </summary>
public sealed class DelayExecutor : HostExecutor<object, TimerElapsed>
{
    private readonly TimeSpan _delay;
    private readonly ITimerService _timers;
    private readonly TimeProvider _clock;

    public DelayExecutor(string id, TimeSpan delay, ITimerService timers, TimeProvider? clock = null) : base(id)
    {
        if (delay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(delay));
        _delay = delay;
        _timers = timers ?? throw new ArgumentNullException(nameof(timers));
        _clock = clock ?? TimeProvider.System;
    }

    public override IReadOnlyDictionary<string, object?> Metadata =>
        new Dictionary<string, object?> { ["node.kind"] = "delay", ["delay.seconds"] = _delay.TotalSeconds };

    protected override async ValueTask<TimerElapsed> ExecuteCoreAsync(
        object input, IWorkflowContext context, CancellationToken cancellationToken)
    {
        DateTimeOffset wakeAt = _clock.GetUtcNow() + _delay;

        await context.QueueStateUpdateAsync("delay.wakeAt", wakeAt, cancellationToken).ConfigureAwait(false);
        await _timers.ScheduleAsync(Runtime.InstanceId, Id, wakeAt, cancellationToken).ConfigureAwait(false);

        return new TimerElapsed(Id, wakeAt);
    }
}

/// <summary>
/// Explicit human-in-the-loop node for workflows that want approval as part of their own logic rather
/// than as host configuration. Equivalent to a permanently-<see cref="ExecutionMode.RequireApproval"/>
/// gate, but visible in the graph.
/// </summary>
public sealed class HumanApprovalExecutor<T> : HostExecutor<T, T>
    where T : class
{
    public HumanApprovalExecutor(string id) : base(id) { }

    public override IReadOnlyDictionary<string, object?> Metadata =>
        new Dictionary<string, object?> { ["node.kind"] = "human-approval" };

    protected override ValueTask<T> ExecuteCoreAsync(T input, IWorkflowContext context, CancellationToken cancellationToken)
        => ValueTask.FromResult(input);
}

/// <summary>Aggregates a fan-in barrier's inputs into one message.</summary>
public sealed class FanInExecutor<TItem, TOut> : HostExecutor<List<TItem>, TOut>
    where TOut : class
{
    private readonly Func<IReadOnlyList<TItem>, TOut> _aggregate;

    public FanInExecutor(string id, Func<IReadOnlyList<TItem>, TOut> aggregate) : base(id)
        => _aggregate = aggregate ?? throw new ArgumentNullException(nameof(aggregate));

    public override IReadOnlyDictionary<string, object?> Metadata =>
        new Dictionary<string, object?> { ["node.kind"] = "fan-in" };

    protected override ValueTask<TOut> ExecuteCoreAsync(
        List<TItem> input, IWorkflowContext context, CancellationToken cancellationToken)
        => ValueTask.FromResult(_aggregate(input ?? []));
}

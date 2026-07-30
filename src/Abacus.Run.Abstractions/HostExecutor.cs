using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Abacus.Run.Abstractions.Middleware;
using Microsoft.Agents.AI.Workflows;

namespace Abacus.Run.Abstractions;

/// <summary>Non-generic view of a host executor, used by the graph builder and the runtime.</summary>
public interface IHostExecutor
{
    string Id { get; }
    HostExecutorRuntime Runtime { get; set; }
    Type InputType { get; }
    Type OutputType { get; }
    IReadOnlyDictionary<string, object?> Metadata { get; }
}

/// <summary>What a gate evaluation concluded for one invocation.</summary>
public enum GateOutcomeKind
{
    /// <summary>No gate, or the predicate did not trip. Run normally.</summary>
    Proceed,

    /// <summary>A decision already exists and approved this invocation. Run, possibly with modified input.</summary>
    ProceedApproved,

    /// <summary>Gate tripped and no decision exists. Raise an approval and park.</summary>
    Pause,

    /// <summary>A decision exists and rejected this invocation.</summary>
    Rejected
}

public sealed record GateOutcome(
    GateOutcomeKind Kind,
    ApprovalGate? Gate = null,
    object? ModifiedInput = null,
    string? ApprovalId = null,
    string? Comment = null)
{
    public static GateOutcome Proceed { get; } = new(GateOutcomeKind.Proceed);
}

public interface IGateEvaluator
{
    /// <summary>Decides whether this invocation runs, pauses, or fails. Never mutates workflow state.</summary>
    ValueTask<GateOutcome> EvaluateAsync(string instanceId, string executorId, object? input, CancellationToken cancellationToken);
}

public interface IApprovalCoordinator
{
    /// <summary>Persists an approval request for a tripped gate. Called before the instance parks.</summary>
    ValueTask<ApprovalRequest> RaiseAsync(
        string instanceId, string executorId, ApprovalGate gate, object? input, int superstep, CancellationToken cancellationToken);
}

/// <summary>
/// Per-instance context injected into an executor by the graph builder. Executors are constructed by
/// the workflow definition before the instance id is known, so this cannot be constructor-injected.
/// </summary>
public sealed class HostExecutorRuntime
{
    /// <summary>
    /// Used when an executor is exercised outside a host (unit tests, local composition): no gate,
    /// no middleware, direct invocation.
    /// </summary>
    public static HostExecutorRuntime Unattached { get; } = new()
    {
        InstanceId = "unattached",
        Descriptor = new ExecutorDescriptor("unattached", typeof(object), "unattached", "0.0.0", ExecutionMode.Autonomous)
    };

    public required string InstanceId { get; init; }
    public required ExecutorDescriptor Descriptor { get; init; }
    public int Attempt { get; init; } = 1;
    public ExecutorDelegate? Pipeline { get; init; }
    public IGateEvaluator? Gates { get; init; }
    public IApprovalCoordinator? Approvals { get; init; }
    public IServiceProvider? Services { get; init; }

    /// <summary>Called when a host executor begins handling a message, before gate evaluation.</summary>
    public Func<string, int, ValueTask>? ExecutorInvoked { get; init; }

    /// <summary>Ambient superstep, updated by the run loop as the engine advances.</summary>
    public Func<int>? SuperstepAccessor { get; init; }

    public int CurrentSuperstep => SuperstepAccessor?.Invoke() ?? 0;
}

/// <summary>
/// Base for every host executor. <see cref="HandleAsync"/> is sealed: gate evaluation and the
/// executor middleware pipeline live there and cannot be overridden away. Authors implement
/// <see cref="ExecuteCoreAsync"/>.
/// </summary>
/// <remarks>
/// <typeparamref name="TOut"/> is constrained to a reference type because the pause path returns
/// <c>null</c>, and the engine only auto-sends non-null handler results — that is what lets a gated
/// executor park without emitting a bogus message.
/// </remarks>
public abstract class HostExecutor<TIn, TOut> : Executor<TIn, TOut>, IHostExecutor
    where TOut : class
{
    protected HostExecutor(string id, ExecutorOptions? options = null) : base(id, options) { }

    public HostExecutorRuntime Runtime { get; set; } = HostExecutorRuntime.Unattached;

    public Type InputType => typeof(TIn);
    public Type OutputType => typeof(TOut);

    public virtual IReadOnlyDictionary<string, object?> Metadata => EmptyMetadata;

    private static readonly IReadOnlyDictionary<string, object?> EmptyMetadata =
        new Dictionary<string, object?>();

    public sealed override async ValueTask<TOut> HandleAsync(
        TIn message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        object? input = message;

        if (Runtime.ExecutorInvoked is { } executorInvoked)
        {
            await executorInvoked(Id, Runtime.CurrentSuperstep).ConfigureAwait(false);
        }

        if (Runtime.Gates is { } gates)
        {
            GateOutcome outcome = await gates
                .EvaluateAsync(Runtime.InstanceId, Id, message, cancellationToken)
                .ConfigureAwait(false);

            switch (outcome.Kind)
            {
                case GateOutcomeKind.Rejected:
                    throw new ApprovalRejectedException(Id, outcome.ApprovalId ?? "unknown", outcome.Comment);

                case GateOutcomeKind.Pause:
                    // Nothing downstream of this point has run, so no side effect has occurred.
                    if (Runtime.Approvals is { } approvals)
                    {
                        await approvals.RaiseAsync(
                            Runtime.InstanceId, Id, outcome.Gate ?? ApprovalGate.Autonomous,
                            message, Runtime.CurrentSuperstep, cancellationToken).ConfigureAwait(false);
                    }

                    await context.RequestHaltAsync().ConfigureAwait(false);
                    return null!;   // never auto-sent: the engine skips null handler results

                case GateOutcomeKind.ProceedApproved:
                    input = outcome.ModifiedInput ?? message;
                    break;

                case GateOutcomeKind.Proceed:
                default:
                    break;
            }
        }

        return await InvokeThroughPipelineAsync(input, context, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<TOut> InvokeThroughPipelineAsync(
        object? input, IWorkflowContext context, CancellationToken cancellationToken)
    {
        var invocation = new ExecutorInvocationContext
        {
            InstanceId = Runtime.InstanceId,
            Descriptor = Runtime.Descriptor,
            Superstep = Runtime.CurrentSuperstep,
            Attempt = Runtime.Attempt,
            Input = input,
            WorkflowContext = context,
            Services = Runtime.Services
        };

        long start = Stopwatch.GetTimestamp();
        try
        {
            ExecutorDelegate pipeline = Runtime.Pipeline ?? ExecuteTerminalAsync;
            await pipeline(invocation, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            invocation.Elapsed = Stopwatch.GetElapsedTime(start);
        }

        if (invocation.Exception is { } exception)
        {
            // Preserves the original stack for the workflow's classifier.
            ExceptionDispatchInfo.Capture(exception).Throw();
        }

        return (TOut)invocation.Output!;
    }

    /// <summary>
    /// The innermost delegate of the executor pipeline. Exceptions are captured rather than thrown so
    /// middleware can observe, replace, or swallow them as it unwinds.
    /// </summary>
    public async ValueTask ExecuteTerminalAsync(ExecutorInvocationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            context.Output = await ExecuteCoreAsync(
                (TIn)context.Input!, context.WorkflowContext!, cancellationToken).ConfigureAwait(false);
            context.Exception = null;
        }
        catch (Exception ex)
        {
            context.Exception = ex;
            context.Output = null;
        }
    }

    /// <summary>Author-implemented work.</summary>
    protected abstract ValueTask<TOut> ExecuteCoreAsync(TIn input, IWorkflowContext context, CancellationToken cancellationToken);
}

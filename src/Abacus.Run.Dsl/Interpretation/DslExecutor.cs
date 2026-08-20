using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;
using Abacus.Run.Abstractions;
using Abacus.Run.Abstractions.Middleware;
using Abacus.Run.Dsl.Expressions;
using Abacus.Run.Dsl.Model;
using Microsoft.Agents.AI.Workflows;

namespace Abacus.Run.Dsl.Interpretation;

/// <summary>
/// Base for every DSL node. Binds the expression roots, applies the declared notification, and keeps
/// the park path intact.
/// </summary>
/// <remarks>
/// <see cref="ExecuteCoreAsync"/> is sealed so no node can skip the envelope handling. A subclass
/// implements <see cref="RunAsync"/> and returns null to park, exactly as the framework's own
/// approval and event-wait executors do.
/// </remarks>
public abstract class DslExecutor : HostExecutor<DslMessage, DslMessage>
{
    private readonly TimeProvider _clock;

    protected DslExecutor(DslNode node, TimeProvider? clock = null) : base(node.Id)
    {
        Node = node;
        _clock = clock ?? TimeProvider.System;
    }

    protected DslNode Node { get; }

    public override IReadOnlyDictionary<string, object?> Metadata => new Dictionary<string, object?>
    {
        ["node.kind"] = Node.Kind,
        ["dsl.node"] = Node.Id
    };

    protected sealed override async ValueTask<DslMessage> ExecuteCoreAsync(
        DslMessage input, IWorkflowContext context, CancellationToken cancellationToken)
    {
        DslMessage bound = Bind(input);

        DslMessage? result = await RunAsync(bound, context, cancellationToken).ConfigureAwait(false);

        // Null is the park signal — the engine declines to send a null handler result, which is what
        // lets a gated or waiting node halt without emitting a bogus message downstream.
        if (result is null)
        {
            return null!;
        }

        await NotifyAsync(result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>The node's own work. Return null to park the instance.</summary>
    protected abstract ValueTask<DslMessage?> RunAsync(
        DslMessage input, IWorkflowContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Refreshes <c>$run</c> and <c>meta</c> for this hop. Rebuilt per node rather than carried,
    /// because superstep and attempt are the two things that change as a run proceeds.
    /// </summary>
    protected DslMessage Bind(DslMessage input)
    {
        JsonObject run = DslMessage.RunMetadata(
            Runtime.InstanceId,
            Runtime.TenantId,
            Runtime.Descriptor.WorkflowName,
            Runtime.Descriptor.WorkflowVersion,
            Runtime.Attempt,
            Runtime.CurrentSuperstep,
            _clock.GetUtcNow());

        return input
            .WithRun(run)
            .WithMeta(new DslMeta(Id, Runtime.CurrentSuperstep, Runtime.Attempt));
    }

    protected AbExContext Context(DslMessage message) => message.ToExpressionContext(message.Run);

    private async ValueTask NotifyAsync(DslMessage result, CancellationToken cancellationToken)
    {
        if (Node.Notify is not { } notify || Runtime.Notify is not { } notifier)
        {
            return;
        }

        var payload = new JsonObject();
        AbExContext context = Context(result);

        foreach ((string key, string expression) in notify.Payload)
        {
            AbExValue value = DslExpressions.Evaluate(expression, context);
            if (!value.IsAbsent)
            {
                payload[key] = value.ToNode();
            }
        }

        await notifier.NotifyAsync(notify.Name, payload, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Hosts one of the framework's own executors inside a DSL node.
/// </summary>
/// <remarks>
/// <para>
/// The built-in executors are typed to their own inputs and outputs — <c>ApiCallResult</c>,
/// <c>LlmResult</c>, <c>TimerElapsed</c> — which is exactly what the uniform envelope cannot carry.
/// Rather than reimplement any of them, this calls
/// <see cref="HostExecutor{TIn,TOut}.ExecuteTerminalAsync"/> on the inner executor and projects the
/// result back into the envelope. No HTTP, egress, idempotency, prompt or cost logic is duplicated.
/// </para>
/// <para>
/// The inner executor's own gate and middleware pipeline are deliberately bypassed: this node has
/// already run both, and running them twice would double every middleware and evaluate the gate
/// against an input that has already passed it.
/// </para>
/// </remarks>
internal sealed class DslHostedExecutor : DslExecutor
{
    private readonly IHostExecutor _inner;
    private readonly Func<ExecutorInvocationContext, CancellationToken, ValueTask> _invoke;
    private readonly Func<object, DslMessage, DslMessage> _project;

    internal DslHostedExecutor(
        DslNode node,
        IHostExecutor inner,
        Func<ExecutorInvocationContext, CancellationToken, ValueTask> invoke,
        Func<object, DslMessage, DslMessage> project,
        TimeProvider? clock = null) : base(node, clock)
    {
        _inner = inner;
        _invoke = invoke;
        _project = project;
    }

    protected override async ValueTask<DslMessage?> RunAsync(
        DslMessage input, IWorkflowContext context, CancellationToken cancellationToken)
    {
        // The inner executor reads the instance, tenant, attempt and notifier off its runtime; giving
        // it this node's means an idempotency key or an llm.completed event is attributed here.
        _inner.Runtime = Runtime;

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

        await _invoke(invocation, cancellationToken).ConfigureAwait(false);

        if (invocation.Exception is { } exception)
        {
            // Preserves the original stack so the document's failure rules classify the real fault.
            ExceptionDispatchInfo.Capture(exception).Throw();
        }

        // A null output from the inner executor means it parked — an event wait registering its
        // subscription, for instance. Propagated rather than wrapped, or the park would be undone by
        // an envelope the engine would happily send onward.
        return invocation.Output is null ? null : _project(invocation.Output, input);
    }
}

using Microsoft.Agents.AI.Workflows;

namespace Abacus.Run.Abstractions;

/// <summary>Non-generic definition surface used by the registry and runner.</summary>
public interface IWorkflowDefinition
{
    string Name { get; }
    string Version { get; }
    Type ContextType { get; }
    Type ResultType { get; }

    ValueTask<Workflow> BuildAsync(WorkflowBuildContext context, CancellationToken cancellationToken);

    FailureDisposition Classify(WorkflowFailure failure);
}

/// <summary>Typed workflow definition. Authors implement this and nothing else.</summary>
public interface IWorkflowDefinition<TContext, TResult> : IWorkflowDefinition
    where TContext : notnull
{
    Type IWorkflowDefinition.ContextType => typeof(TContext);

    Type IWorkflowDefinition.ResultType => typeof(TResult);

    FailureDisposition IWorkflowDefinition.Classify(WorkflowFailure failure)
        => DefaultFailureClassifier.Instance.Classify(failure);
}

/// <summary>
/// Supplied to <see cref="IWorkflowDefinition.BuildAsync"/>. The single place approval gates are
/// declared and the only supported way to attach an executor to the host runtime.
/// </summary>
public sealed class WorkflowBuildContext
{
    private readonly Func<IHostExecutor, ApprovalGate, ExecutorBinding> _attach;

    public WorkflowBuildContext(
        string instanceId,
        string tenantId,
        string workflowName,
        string workflowVersion,
        int attempt,
        IServiceProvider? services,
        Func<IHostExecutor, ApprovalGate, ExecutorBinding> attach)
    {
        InstanceId = instanceId;
        TenantId = tenantId;
        WorkflowName = workflowName;
        WorkflowVersion = workflowVersion;
        Attempt = attempt;
        Services = services;
        _attach = attach ?? throw new ArgumentNullException(nameof(attach));
    }

    public string InstanceId { get; }
    public string TenantId { get; }
    public string WorkflowName { get; }
    public string WorkflowVersion { get; }
    public int Attempt { get; }
    public IServiceProvider? Services { get; }

    /// <summary>Gates declared during this build, by executor id. Read by the runtime.</summary>
    public IReadOnlyDictionary<string, ApprovalGate> Gates => _gates;

    private readonly Dictionary<string, ApprovalGate> _gates = [];

    /// <summary>
    /// Attaches a host executor: wires the middleware pipeline, the gate, and the instance runtime,
    /// and returns the binding to use in the graph.
    /// </summary>
    public ExecutorBinding Node(IHostExecutor executor, Action<ApprovalGateBuilder>? gate = null)
    {
        ArgumentNullException.ThrowIfNull(executor);

        ApprovalGate configured = ApprovalGate.Autonomous;
        if (gate is not null)
        {
            var builder = new ApprovalGateBuilder();
            gate(builder);
            configured = builder.Build();
        }

        _gates[executor.Id] = configured;
        return _attach(executor, configured);
    }

    /// <summary>
    /// Escape hatch for raw framework executors and <c>AIAgent</c> bindings. These participate in the
    /// graph but not in executor middleware, and cannot be approval-gated.
    /// </summary>
    public ExecutorBinding RawNode(ExecutorBinding binding, Action<ApprovalGateBuilder>? gate = null)
    {
        ArgumentNullException.ThrowIfNull(binding);

        if (gate is not null)
        {
            throw new InvalidOperationException(
                $"Executor '{binding.Id}' is bound via RawNode and cannot be approval-gated. " +
                "Approval gates require a HostExecutor<TIn, TOut>; convert the executor or remove the gate.");
        }

        return binding;
    }

    /// <summary>Build context for read-only inspection (graph rendering), with no runtime attachment.</summary>
    public static WorkflowBuildContext ForInspection(string workflowName, string workflowVersion)
        => new("inspection", "inspection", workflowName, workflowVersion, 0, null,
            (executor, _) =>
            {
                executor.Runtime = HostExecutorRuntime.Unattached;
                return (Executor)executor;
            });
}

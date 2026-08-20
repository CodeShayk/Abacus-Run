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
/// Implemented by a definition that was authored somewhere other than C#, so the catalog can say
/// where a workflow came from and which revision of that source is running.
/// </summary>
/// <remarks>
/// <para>
/// The framework cannot name the DSL — <c>Abacus.Run</c> does not reference it, and must not. So the
/// front end answers for itself, and the catalog reports <c>compiled</c> for any definition that does
/// not implement this. That keeps provenance discoverable without the framework knowing what front
/// ends exist.
/// </para>
/// <para>
/// <see cref="DocumentHash"/> is what makes two hosts comparable: same name, same version and same
/// hash is the same workflow, and a differing hash for a published version is a deployment fault
/// rather than a curiosity.
/// </para>
/// </remarks>
public interface IDocumentAuthoredWorkflow
{
    /// <summary>The front end that produced this definition, lowercase — for example <c>dsl</c>.</summary>
    string Source { get; }

    /// <summary>The canonical hash of the source document.</summary>
    string DocumentHash { get; }
}

/// <summary>
/// One executor node of a workflow graph as the definition declared it. Produced during a build and
/// surfaced by the catalog API so a tenant can see what there is to configure.
/// </summary>
/// <remarks>
/// <c>Configurable</c> is false for <see cref="WorkflowBuildContext.RawNode"/> bindings: they run
/// outside the host executor pipeline and therefore cannot carry an approval gate at all.
/// </remarks>
public sealed record WorkflowNodeDescriptor(
    string ExecutorId,
    string ExecutorType,
    string? InputType,
    string? OutputType,
    ApprovalGate DeclaredGate,
    bool Configurable);

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
        Func<IHostExecutor, ApprovalGate, ExecutorBinding> attach,
        IWorkflowAuditRecorder? audit = null)
    {
        InstanceId = instanceId;
        TenantId = tenantId;
        WorkflowName = workflowName;
        WorkflowVersion = workflowVersion;
        Attempt = attempt;
        Services = services;
        Audit = audit;
        _attach = attach ?? throw new ArgumentNullException(nameof(attach));
    }

    public string InstanceId { get; }
    public string TenantId { get; }
    public string WorkflowName { get; }
    public string WorkflowVersion { get; }
    public int Attempt { get; }
    public IServiceProvider? Services { get; }

    /// <summary>
    /// The audit hook for this instance, present when the definition implements
    /// <see cref="IAuditedWorkflowDefinition"/>. Available here so a definition can hand it to the
    /// executors it constructs; executors attached with <see cref="Node"/> also reach it through
    /// <see cref="HostExecutorRuntime.Audit"/>.
    /// </summary>
    public IWorkflowAuditRecorder? Audit { get; }

    /// <summary>Gates declared during this build, by executor id. Read by the runtime.</summary>
    public IReadOnlyDictionary<string, ApprovalGate> Gates => _gates;

    /// <summary>
    /// Every node attached during this build, in declaration order. Read by the catalog API to
    /// describe what a tenant may configure.
    /// </summary>
    public IReadOnlyList<WorkflowNodeDescriptor> Nodes => _nodes;

    private readonly Dictionary<string, ApprovalGate> _gates = [];
    private readonly List<WorkflowNodeDescriptor> _nodes = [];

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
        Record(new WorkflowNodeDescriptor(
            executor.Id, executor.GetType().Name, executor.InputType.Name, executor.OutputType.Name,
            configured, Configurable: true));

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

        Record(new WorkflowNodeDescriptor(
            binding.Id, binding.GetType().Name, null, null, ApprovalGate.Autonomous, Configurable: false));

        return binding;
    }

    /// <summary>Last declaration of an id wins, matching how <see cref="Gates"/> is built.</summary>
    private void Record(WorkflowNodeDescriptor node)
    {
        int existing = _nodes.FindIndex(n => string.Equals(n.ExecutorId, node.ExecutorId, StringComparison.Ordinal));
        if (existing >= 0)
        {
            _nodes[existing] = node;
        }
        else
        {
            _nodes.Add(node);
        }
    }

    /// <summary>Build context for read-only inspection (graph rendering), with no runtime attachment.</summary>
    public static WorkflowBuildContext ForInspection(
        string workflowName, string workflowVersion, IServiceProvider? services = null)
        => new("inspection", "inspection", workflowName, workflowVersion, 0, services,
            (executor, _) =>
            {
                executor.Runtime = HostExecutorRuntime.Unattached;
                return (Executor)executor;
            });
}

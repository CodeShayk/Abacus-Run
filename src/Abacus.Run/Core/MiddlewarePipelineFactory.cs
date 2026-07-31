using System.Collections.Concurrent;
using Abacus.Run.Abstractions.Middleware;

namespace Abacus.Run.Core;

/// <summary>
/// Compiles middleware chains once per (workflow, version, executor) and caches them. Rebuilding per
/// invocation is what would blow the per-invocation overhead budget.
/// </summary>
public sealed class MiddlewarePipelineFactory
{
    private readonly IReadOnlyList<IExecutorMiddleware> _executorMiddleware;
    private readonly IReadOnlyList<IWorkflowMiddleware> _workflowMiddleware;
    private readonly ConcurrentDictionary<PipelineKey, IReadOnlyList<IExecutorMiddleware>> _applicableCache = new();

    public MiddlewarePipelineFactory(
        IEnumerable<IExecutorMiddleware>? executorMiddleware = null,
        IEnumerable<IWorkflowMiddleware>? workflowMiddleware = null)
    {
        _executorMiddleware = (executorMiddleware ?? []).OrderBy(m => m.Order).ToArray();
        _workflowMiddleware = (workflowMiddleware ?? []).OrderBy(m => m.Order).ToArray();
    }

    public int ExecutorMiddlewareCount => _executorMiddleware.Count;
    public int WorkflowMiddlewareCount => _workflowMiddleware.Count;

    /// <summary>Number of distinct pipelines currently cached. Exposed for tests and diagnostics.</summary>
    public int CachedPipelineCount => _applicableCache.Count;

    public ExecutorDelegate BuildExecutorPipeline(ExecutorDescriptor descriptor, ExecutorDelegate terminal)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(terminal);

        // Applicability depends only on the descriptor, so it is cached; the terminal varies per
        // executor instance, so the chain itself is composed per call over the cached selection.
        IReadOnlyList<IExecutorMiddleware> applicable = _applicableCache.GetOrAdd(
            new PipelineKey(descriptor.WorkflowName, descriptor.WorkflowVersion, descriptor.ExecutorId),
            _ => _executorMiddleware.Where(m => m.AppliesTo(descriptor)).ToArray());

        ExecutorDelegate next = terminal;
        for (int i = applicable.Count - 1; i >= 0; i--)
        {
            IExecutorMiddleware middleware = applicable[i];
            ExecutorDelegate inner = next;
            next = (context, cancellationToken) => middleware.InvokeAsync(context, inner, cancellationToken);
        }

        return next;
    }

    public WorkflowDelegate BuildWorkflowPipeline(WorkflowDelegate terminal)
    {
        ArgumentNullException.ThrowIfNull(terminal);

        WorkflowDelegate next = terminal;
        for (int i = _workflowMiddleware.Count - 1; i >= 0; i--)
        {
            IWorkflowMiddleware middleware = _workflowMiddleware[i];
            WorkflowDelegate inner = next;
            next = (context, cancellationToken) => middleware.InvokeAsync(context, inner, cancellationToken);
        }

        return next;
    }

    private readonly record struct PipelineKey(string WorkflowName, string WorkflowVersion, string ExecutorId);
}

using Microsoft.Agents.AI.Workflows;

namespace Abacus.Run.Abstractions.Middleware;

public delegate ValueTask WorkflowDelegate(WorkflowInvocationContext context, CancellationToken cancellationToken);

public delegate ValueTask ExecutorDelegate(ExecutorInvocationContext context, CancellationToken cancellationToken);

/// <summary>Wraps a whole workflow run.</summary>
public interface IWorkflowMiddleware
{
    int Order => 0;

    ValueTask InvokeAsync(WorkflowInvocationContext context, WorkflowDelegate next, CancellationToken cancellationToken);
}

/// <summary>Wraps each executor invocation. The seam the framework itself does not provide.</summary>
public interface IExecutorMiddleware
{
    int Order => 0;

    bool AppliesTo(ExecutorDescriptor descriptor) => true;

    ValueTask InvokeAsync(ExecutorInvocationContext context, ExecutorDelegate next, CancellationToken cancellationToken);
}

public sealed record ExecutorDescriptor(
    string ExecutorId,
    Type ExecutorType,
    string WorkflowName,
    string WorkflowVersion,
    ExecutionMode Mode)
{
    public IDictionary<string, object?> Metadata { get; init; } = new Dictionary<string, object?>();
}

public sealed class WorkflowInvocationContext
{
    public required string InstanceId { get; init; }
    public required string TenantId { get; init; }
    public required string WorkflowName { get; init; }
    public required string WorkflowVersion { get; init; }
    public int Attempt { get; init; }
    public object? Context { get; init; }
    public object? Result { get; set; }
    public Exception? Exception { get; set; }
    public InboundRequestHandle? Request { get; init; }
    public IServiceProvider? Services { get; init; }
    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>();
}

public sealed class ExecutorInvocationContext
{
    public required string InstanceId { get; init; }
    public required ExecutorDescriptor Descriptor { get; init; }
    public int Superstep { get; init; }
    public int Attempt { get; init; }
    public object? Input { get; set; }
    public object? Output { get; set; }
    public Exception? Exception { get; set; }
    public TimeSpan Elapsed { get; set; }
    public IWorkflowContext? WorkflowContext { get; init; }
    public IServiceProvider? Services { get; init; }
    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>();

    /// <summary>True when the pipeline produced an output and no exception survived.</summary>
    public bool Succeeded => Exception is null;
}

/// <summary>Read-only view of the host API request that started the instance (PRD FR-6.4).</summary>
public sealed record InboundRequestHandle
{
    public required string Method { get; init; }
    public required string Path { get; init; }
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
    public string? CorrelationId { get; init; }
    public string? UserId { get; init; }
}

/// <summary>
/// Observe or replace an outbound HTTP call made by a built-in executor. Middleware may
/// short-circuit the real call by setting a synthetic response.
/// </summary>
public interface IOutboundCallHandle
{
    HttpRequestMessage Request { get; }
    HttpResponseMessage? Response { get; }
    bool IsShortCircuited { get; }
    void SetSyntheticResponse(HttpResponseMessage response);
}

public static class MiddlewareContextKeys
{
    public const string OutboundCall = "outbound.call";
    public const string LlmUsage = "llm.usage";
    public const string PromptVersion = "llm.prompt_version";
}

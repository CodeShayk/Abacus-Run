using System.Collections.Concurrent;
using Abacus.Run.Abstractions;

namespace Abacus.Run.Core;

/// <summary>Describes the executor nodes a registered workflow version declares.</summary>
public interface IWorkflowInspector
{
    ValueTask<IReadOnlyList<WorkflowNodeDescriptor>> InspectAsync(
        WorkflowDescriptor descriptor, CancellationToken cancellationToken);
}

/// <summary>
/// Builds a definition once against an inspection context to read its nodes back.
/// </summary>
/// <remarks>
/// The graph of a registered version is fixed — the registry rejects two definitions for the same
/// version — so the result is cached per name/version. Building attaches nothing to a runtime and
/// runs no executor: <see cref="WorkflowBuildContext.ForInspection"/> hands every node
/// <see cref="HostExecutorRuntime.Unattached"/>.
/// </remarks>
public sealed class WorkflowInspector : IWorkflowInspector
{
    private readonly IServiceProvider? _services;
    private readonly ConcurrentDictionary<string, IReadOnlyList<WorkflowNodeDescriptor>> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public WorkflowInspector(IServiceProvider? services = null) => _services = services;

    public async ValueTask<IReadOnlyList<WorkflowNodeDescriptor>> InspectAsync(
        WorkflowDescriptor descriptor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        string key = $"{descriptor.Name}|{descriptor.Version}";
        if (_cache.TryGetValue(key, out IReadOnlyList<WorkflowNodeDescriptor>? cached))
        {
            return cached;
        }

        var context = WorkflowBuildContext.ForInspection(descriptor.Name, descriptor.Version, _services);
        await descriptor.Definition.BuildAsync(context, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<WorkflowNodeDescriptor> nodes = [.. context.Nodes];
        _cache[key] = nodes;
        return nodes;
    }
}

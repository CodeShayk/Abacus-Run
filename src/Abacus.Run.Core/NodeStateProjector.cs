using System.Text.Json;
using Abacus.Run.Abstractions;

namespace Abacus.Run.Core;

public enum NodeState
{
    Pending,
    Executing,
    Completed,
    Failed,
    AwaitingApproval,
    Skipped
}

public sealed record NodeStates(
    IReadOnlyDictionary<string, NodeState> States,
    IReadOnlySet<(string From, string To)> TraversedEdges)
{
    public IReadOnlyList<string> Executing => States
        .Where(kv => kv.Value == NodeState.Executing)
        .Select(kv => kv.Key)
        .OrderBy(id => id, StringComparer.Ordinal)
        .ToArray();

    public IReadOnlyList<string> Failed => States
        .Where(kv => kv.Value == NodeState.Failed)
        .Select(kv => kv.Key)
        .OrderBy(id => id, StringComparer.Ordinal)
        .ToArray();
}

/// <summary>
/// Derives per-node execution state by folding the event stream. Deriving rather than storing means
/// the graph can never disagree with the timeline, and replayed instances re-derive for free.
/// </summary>
public static class NodeStateProjector
{
    public static NodeStates Project(IEnumerable<EventEnvelope> events, IEnumerable<string>? allNodeIds = null)
    {
        ArgumentNullException.ThrowIfNull(events);

        var states = new Dictionary<string, NodeState>(StringComparer.Ordinal);
        var traversed = new HashSet<(string From, string To)>();
        string? lastCompleted = null;

        foreach (EventEnvelope e in events.OrderBy(e => e.Sequence))
        {
            string? executorId = e.ExecutorId ?? TryReadExecutorId(e.PayloadJson);
            if (executorId is null)
            {
                continue;
            }

            switch (e.EventType)
            {
                case WorkflowEventTypes.ExecutorInvoked:
                    // The engine's invoked event can be sequenced *after* an approval raised inside
                    // the handler, so it must not overwrite a parked or finished node. A node waiting
                    // on a human is not executing, whenever the invoked event happens to land.
                    if (!states.TryGetValue(executorId, out NodeState existing) ||
                        existing is NodeState.Pending or NodeState.Skipped)
                    {
                        states[executorId] = NodeState.Executing;
                    }

                    if (lastCompleted is not null && lastCompleted != executorId)
                    {
                        traversed.Add((lastCompleted, executorId));
                    }
                    break;

                case WorkflowEventTypes.ExecutorCompleted:
                    states[executorId] = NodeState.Completed;
                    lastCompleted = executorId;
                    break;

                case WorkflowEventTypes.ExecutorFailed:
                    states[executorId] = NodeState.Failed;
                    break;

                case WorkflowEventTypes.ApprovalRequested:
                    states[executorId] = NodeState.AwaitingApproval;
                    break;

                case WorkflowEventTypes.ApprovalDecided:
                    // Only a node still parked returns to Executing; a completed node stays completed.
                    if (states.TryGetValue(executorId, out NodeState current) && current == NodeState.AwaitingApproval)
                    {
                        states[executorId] = NodeState.Executing;
                    }
                    break;

                case WorkflowEventTypes.ApprovalExpired:
                    if (states.TryGetValue(executorId, out NodeState expiring) && expiring == NodeState.AwaitingApproval)
                    {
                        states[executorId] = NodeState.Failed;
                    }
                    break;
            }
        }

        if (allNodeIds is not null)
        {
            bool anyTerminalReached = states.Values.Any(s => s is NodeState.Completed or NodeState.Failed);
            foreach (string id in allNodeIds)
            {
                if (states.ContainsKey(id))
                {
                    continue;
                }

                // A node never entered once the run has progressed is a branch not taken, not merely
                // "not yet reached" — but only once something has actually completed.
                states[id] = anyTerminalReached ? NodeState.Skipped : NodeState.Pending;
            }
        }

        return new NodeStates(states, traversed);
    }

    private static string? TryReadExecutorId(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(payloadJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty("executorId", out JsonElement value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

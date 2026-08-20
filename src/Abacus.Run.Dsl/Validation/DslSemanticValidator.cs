using System.Text.Json.Nodes;
using Abacus.Run.Dsl.Expressions;
using Abacus.Run.Dsl.Model;
using Json.Schema;

namespace Abacus.Run.Dsl.Validation;

/// <summary>
/// Phase 2 of validation: everything JSON Schema cannot express.
/// </summary>
/// <remarks>
/// Each item here is a real way to write a structurally valid document that is nonsense — a duplicate
/// id, an edge to a node that does not exist, a cycle with nothing durable on it, a condition whose
/// value changes between the run and its resume. A schema compares a value to a rule; none of these
/// are about one value.
/// </remarks>
public static class DslSemanticValidator
{
    public static DslValidationResult Validate(DslDocument document, DslEnvironment? environment = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        var diagnostics = new List<DslDiagnostic>();
        var skipped = new List<string>();
        DslPolicy policy = environment?.Policy ?? DslPolicy.Default;

        CheckVersion(document, policy, diagnostics);
        CheckLimits(document, policy, diagnostics);

        var ids = CheckNodeIds(document, diagnostics);

        CheckReferences(document, ids, diagnostics);
        CheckGraph(document, ids, diagnostics);
        CheckExpressions(document, policy, diagnostics);
        CheckGates(document, diagnostics);
        CheckNotifications(document, ids, diagnostics);
        CheckEnvironment(document, environment, diagnostics, skipped);

        return new DslValidationResult(diagnostics, skipped);
    }

    // ---- identity and limits ---------------------------------------------------------------

    private static void CheckVersion(DslDocument document, DslPolicy policy, List<DslDiagnostic> diagnostics)
    {
        if (!policy.SupportedMajorVersions.Contains(document.MajorVersion))
        {
            diagnostics.Add(DslDiagnostic.Error(
                DslCodes.UnsupportedDslVersion, "/dsl",
                $"'{document.Dsl}' is not a supported DSL version.",
                $"This interpreter reads major version(s) {string.Join(", ", policy.SupportedMajorVersions)}."));
        }
    }

    private static void CheckLimits(DslDocument document, DslPolicy policy, List<DslDiagnostic> diagnostics)
    {
        if (document.Nodes.Count > policy.MaxNodes)
        {
            diagnostics.Add(DslDiagnostic.Error(
                DslCodes.LimitExceeded, "/nodes",
                $"The document declares {document.Nodes.Count} nodes; the limit is {policy.MaxNodes}."));
        }

        if (document.Edges.Count > policy.MaxEdges)
        {
            diagnostics.Add(DslDiagnostic.Error(
                DslCodes.LimitExceeded, "/edges",
                $"The document declares {document.Edges.Count} edges; the limit is {policy.MaxEdges}."));
        }
    }

    private static HashSet<string> CheckNodeIds(DslDocument document, List<DslDiagnostic> diagnostics)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (DslNode node in document.Nodes)
        {
            if (!ids.Add(node.Id))
            {
                diagnostics.Add(DslDiagnostic.Error(
                    DslCodes.DuplicateNodeId, $"{node.Pointer}/id",
                    $"Node id '{node.Id}' is declared more than once.",
                    "Node ids key gate policy and node state; two nodes cannot share one."));
            }
        }

        return ids;
    }

    // ---- references -------------------------------------------------------------------------

    private static void CheckReferences(
        DslDocument document, HashSet<string> ids, List<DslDiagnostic> diagnostics)
    {
        if (!ids.Contains(document.Start))
        {
            diagnostics.Add(DslDiagnostic.Error(
                DslCodes.StartNotFound, "/start",
                $"'{document.Start}' is not a node.", Suggest(document.Start, ids)));
        }

        for (int i = 0; i < document.Output.Count; i++)
        {
            if (!ids.Contains(document.Output[i]))
            {
                diagnostics.Add(DslDiagnostic.Error(
                    DslCodes.OutputNotFound, $"/output/{i}",
                    $"'{document.Output[i]}' is not a node.", Suggest(document.Output[i], ids)));
            }
        }

        var unconditional = new HashSet<string>(StringComparer.Ordinal);

        foreach (DslEdge edge in document.Edges)
        {
            for (int i = 0; i < edge.From.Count; i++)
            {
                if (!ids.Contains(edge.From[i]))
                {
                    diagnostics.Add(DslDiagnostic.Error(
                        DslCodes.EdgeEndpointNotFound,
                        edge.From.Count == 1 ? $"{edge.Pointer}/from" : $"{edge.Pointer}/from/{i}",
                        $"Edge starts at '{edge.From[i]}', which is not a node.",
                        Suggest(edge.From[i], ids)));
                }
            }

            for (int i = 0; i < edge.To.Count; i++)
            {
                if (!ids.Contains(edge.To[i]))
                {
                    diagnostics.Add(DslDiagnostic.Error(
                        DslCodes.EdgeEndpointNotFound,
                        edge.To.Count == 1 ? $"{edge.Pointer}/to" : $"{edge.Pointer}/to/{i}",
                        $"Edge targets '{edge.To[i]}', which is not a node.",
                        Suggest(edge.To[i], ids)));
                }
            }

            // Only unconditional duplicates are a problem: two conditional edges between the same
            // pair is exactly how a branch with a fallback is written.
            if (edge.When is null && edge.Select is null && !edge.Idempotent && !edge.IsBarrier)
            {
                foreach (string target in edge.To)
                {
                    string key = $"{string.Join(",", edge.From)}->{target}";
                    if (!unconditional.Add(key))
                    {
                        diagnostics.Add(DslDiagnostic.Error(
                            DslCodes.DuplicateEdge, edge.Pointer,
                            $"A second unconditional edge already connects '{edge.From[0]}' to '{target}'.",
                            "Give one a 'when', or set 'idempotent': true if the repeat is intended."));
                    }
                }
            }
        }
    }

    private static string? Suggest(string value, HashSet<string> candidates)
    {
        string? best = null;
        int bestDistance = int.MaxValue;

        foreach (string candidate in candidates)
        {
            int distance = AbExFunctions.EditDistance(value, candidate);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        return best is not null && bestDistance <= Math.Max(2, value.Length / 3)
            ? $"Did you mean '{best}'?"
            : null;
    }

    // ---- graph shape --------------------------------------------------------------------------

    private static void CheckGraph(
        DslDocument document, HashSet<string> ids, List<DslDiagnostic> diagnostics)
    {
        Dictionary<string, List<string>> adjacency = BuildAdjacency(document, ids);

        // Reachability
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        if (ids.Contains(document.Start))
        {
            var queue = new Queue<string>();
            queue.Enqueue(document.Start);
            reachable.Add(document.Start);

            while (queue.Count > 0)
            {
                string current = queue.Dequeue();
                foreach (string next in adjacency.GetValueOrDefault(current, []))
                {
                    if (reachable.Add(next))
                    {
                        queue.Enqueue(next);
                    }
                }
            }

            foreach (DslNode node in document.Nodes.Where(n => !reachable.Contains(n.Id)))
            {
                diagnostics.Add(DslDiagnostic.Warning(
                    DslCodes.UnreachableNode, node.Pointer,
                    $"Node '{node.Id}' is unreachable from '{document.Start}'.",
                    "It will never run. Add an edge to it, or remove it."));
            }
        }

        // Dead ends: a node nothing leaves that is not declared as an output
        var declaredOutputs = new HashSet<string>(document.Output, StringComparer.Ordinal);

        foreach (DslNode node in document.Nodes)
        {
            bool hasOutgoing = adjacency.GetValueOrDefault(node.Id, []).Count > 0;

            if (!hasOutgoing && declaredOutputs.Count > 0 && !declaredOutputs.Contains(node.Id) &&
                reachable.Contains(node.Id))
            {
                diagnostics.Add(DslDiagnostic.Warning(
                    DslCodes.DeadEndNode, node.Pointer,
                    $"Node '{node.Id}' has no outgoing edge and is not declared as an output.",
                    "A run reaching it stops there and produces no result."));
            }
        }

        // Barrier sources must themselves be reachable, or the barrier never releases
        foreach (DslEdge edge in document.Edges.Where(e => e.IsBarrier))
        {
            foreach (string source in edge.From.Where(s => ids.Contains(s) && !reachable.Contains(s)))
            {
                diagnostics.Add(DslDiagnostic.Error(
                    DslCodes.BarrierSourceUnreachable, edge.Pointer,
                    $"Barrier source '{source}' is unreachable, so the barrier can never release.",
                    $"'{string.Join("', '", edge.To)}' would wait forever."));
            }
        }

        CheckCycles(document, adjacency, diagnostics);
    }

    private static Dictionary<string, List<string>> BuildAdjacency(DslDocument document, HashSet<string> ids)
    {
        var adjacency = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (DslEdge edge in document.Edges)
        {
            foreach (string from in edge.From.Where(ids.Contains))
            {
                foreach (string to in edge.To.Where(ids.Contains))
                {
                    if (!adjacency.TryGetValue(from, out List<string>? targets))
                    {
                        adjacency[from] = targets = [];
                    }

                    if (!targets.Contains(to, StringComparer.Ordinal))
                    {
                        targets.Add(to);
                    }
                }
            }
        }

        return adjacency;
    }

    /// <summary>
    /// A cycle is legitimate — polling, retry-until, wait-and-recheck — but only if something on it
    /// yields. A cycle of pure compute nodes is a hot spin that will occupy a dispatcher until the
    /// instance hits its lifetime cap, so it is refused rather than warned about.
    /// </summary>
    private static void CheckCycles(
        DslDocument document, Dictionary<string, List<string>> adjacency, List<DslDiagnostic> diagnostics)
    {
        // Built tolerantly rather than with ToDictionary: duplicate ids are one of the things this
        // validator exists to report, so it has to survive a document that has them.
        var kinds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DslNode node in document.Nodes)
        {
            kinds.TryAdd(node.Id, node.Kind);
        }

        var state = new Dictionary<string, int>(StringComparer.Ordinal);   // 0 unseen, 1 open, 2 closed
        var stack = new List<string>();
        var reported = new HashSet<string>(StringComparer.Ordinal);

        foreach (DslNode node in document.Nodes)
        {
            if (state.GetValueOrDefault(node.Id) == 0)
            {
                Visit(node.Id);
            }
        }

        void Visit(string current)
        {
            state[current] = 1;
            stack.Add(current);

            foreach (string next in adjacency.GetValueOrDefault(current, []))
            {
                int colour = state.GetValueOrDefault(next);

                if (colour == 1)
                {
                    int start = stack.IndexOf(next);
                    List<string> cycle = stack[start..];

                    bool yields = cycle.Any(id =>
                        kinds.GetValueOrDefault(id) is DslNodeKinds.Delay or DslNodeKinds.WaitEvent
                                                    or DslNodeKinds.Approval);

                    string key = string.Join("->", cycle.Order(StringComparer.Ordinal));

                    if (!yields && reported.Add(key))
                    {
                        DslNode? owner = document.FindNode(cycle[0]);
                        diagnostics.Add(DslDiagnostic.Error(
                            DslCodes.TightCycle, owner?.Pointer ?? "/edges",
                            $"The cycle {string.Join(" -> ", cycle)} -> {cycle[0]} has nothing that yields.",
                            "Put a 'delay', 'wait-event' or 'approval' node on it, or break the cycle."));
                    }
                }
                else if (colour == 0)
                {
                    Visit(next);
                }
            }

            stack.RemoveAt(stack.Count - 1);
            state[current] = 2;
        }
    }

    // ---- expressions --------------------------------------------------------------------------

    private static void CheckExpressions(
        DslDocument document, DslPolicy policy, List<DslDiagnostic> diagnostics)
    {
        foreach (DslNode node in document.Nodes)
        {
            foreach ((string pointer, string expression) in node.Expressions())
            {
                // A gate predicate decides whether a node pauses. Resolved on the resume path too,
                // so it carries the same determinism requirement as an edge condition.
                bool requiresDeterminism = node.Gate is not null && pointer.EndsWith("/when", StringComparison.Ordinal);
                Check(pointer, expression, requiresDeterminism, "gate predicate");
            }

            foreach ((string pointer, string template) in node.Templates())
            {
                CheckTemplate(pointer, template);
            }
        }

        foreach (DslEdge edge in document.Edges)
        {
            if (edge.When is { Length: > 0 })
            {
                Check($"{edge.Pointer}/when", edge.When, requiresDeterminism: true, "edge condition");
            }

            if (edge.Select is { Length: > 0 })
            {
                Check($"{edge.Pointer}/select", edge.Select, requiresDeterminism: true, "edge selector");
            }
        }

        foreach (DslTrigger trigger in document.Triggers)
        {
            // A trigger's correlation key is a literal filter value, not a projection: the
            // subscription is registered before any message exists, so there is nothing for a path
            // to read. One that looks like an expression is almost certainly a misunderstanding.
            if (trigger.CorrelationKey is { Length: > 0 } key && key.StartsWith('$'))
            {
                diagnostics.Add(DslDiagnostic.Warning(
                    DslCodes.ExpressionParseError, $"{trigger.Pointer}/correlationKey",
                    $"'{key}' is used as a literal correlation key, not evaluated.",
                    "A trigger subscription is registered before any message arrives, so there is " +
                    "nothing for an expression to read. Use the literal key you expect to match."));
            }

            // contextFrom does have a message in scope — the one that fired the trigger — so it is
            // a real expression, rooted at the payload.
            if (trigger.ContextFrom is { Length: > 0 } from)
            {
                Check($"{trigger.Pointer}/contextFrom", from, false, "context projection");
            }
        }

        if (document.Audit?.Key is { Length: > 0 } auditKey)
        {
            Check($"{document.Audit.Pointer}/key", auditKey, false, "audit key");
        }

        void Check(string pointer, string expression, bool requiresDeterminism, string role)
        {
            ExpressionFacts facts = AbExValidator.Check(expression, policy.MaxExpressionDepth);

            foreach (AbExIssue issue in facts.Issues)
            {
                string code = issue.Message.StartsWith("Unknown function", StringComparison.Ordinal)
                    ? DslCodes.UnknownFunction
                    : issue.Message.Contains("nests", StringComparison.Ordinal)
                        ? DslCodes.ExpressionTooDeep
                        : DslCodes.ExpressionParseError;

                diagnostics.Add(DslDiagnostic.Error(
                    code, pointer, $"{issue.Message} (at offset {issue.Offset})", issue.Suggestion));
            }

            // Routing must be reproducible. BuildAsync runs once per attempt, and a resumed instance
            // has to retrace the branch its checkpoint recorded; a condition reading the clock could
            // take a different one, which is silent, intermittent and close to undebuggable.
            if (requiresDeterminism && facts.IsValid && !facts.IsDeterministic)
            {
                diagnostics.Add(DslDiagnostic.Error(
                    DslCodes.NonDeterministicCondition, pointer,
                    $"A {role} must be deterministic, but this one reads '$run.now'.",
                    "A resumed run must retrace the routing its checkpoint recorded. " +
                    "Compute the value in a 'transform' node and compare against that instead."));
            }
        }

        void CheckTemplate(string pointer, string template)
        {
            foreach ((string expression, int _) in TemplatePlaceholders(template))
            {
                ExpressionFacts facts = AbExValidator.Check(expression, policy.MaxExpressionDepth);

                foreach (AbExIssue issue in facts.Issues)
                {
                    diagnostics.Add(DslDiagnostic.Error(
                        issue.Message.StartsWith("Unknown function", StringComparison.Ordinal)
                            ? DslCodes.UnknownFunction
                            : DslCodes.ExpressionParseError,
                        pointer,
                        $"In placeholder '{{{{ {expression} }}}}': {issue.Message}", issue.Suggestion));
                }
            }
        }
    }

    /// <summary>Extracts <c>{{ ... }}</c> placeholders, matching how the template engine scans.</summary>
    internal static IEnumerable<(string Expression, int Offset)> TemplatePlaceholders(string template)
    {
        int index = 0;

        while (index < template.Length)
        {
            int open = template.IndexOf("{{", index, StringComparison.Ordinal);
            if (open < 0)
            {
                yield break;
            }

            int close = template.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0)
            {
                yield break;
            }

            yield return (template[(open + 2)..close].Trim(), open);
            index = close + 2;
        }
    }

    // ---- gates and notifications ----------------------------------------------------------------

    private static void CheckGates(DslDocument document, List<DslDiagnostic> diagnostics)
    {
        foreach (DslNode node in document.Nodes)
        {
            if (node.Gate is not { } gate)
            {
                continue;
            }

            if (!DslNodeKinds.IsGateable(node.Kind))
            {
                diagnostics.Add(DslDiagnostic.Error(
                    DslCodes.GateOnNonGateableKind, gate.Pointer,
                    $"A '{node.Kind}' node cannot carry an approval gate.",
                    "Gate the node that produces the value instead."));
            }

            if (string.Equals(gate.Mode, "conditional", StringComparison.Ordinal) &&
                string.IsNullOrWhiteSpace(gate.When))
            {
                diagnostics.Add(DslDiagnostic.Error(
                    DslCodes.ConditionalGateWithoutPredicate, gate.Pointer,
                    "A conditional gate needs a 'when' predicate.",
                    "Without one it would never trip, which is the same as having no gate."));
            }

            if (string.Equals(gate.OnExpiryAction, "escalate", StringComparison.Ordinal) &&
                gate.EscalateTo.Count == 0)
            {
                diagnostics.Add(DslDiagnostic.Error(
                    DslCodes.EscalationWithoutAssignees, $"{gate.Pointer}/onExpiry",
                    "Escalation on expiry needs someone to escalate to."));
            }
        }
    }

    private static void CheckNotifications(
        DslDocument document, HashSet<string> ids, List<DslDiagnostic> diagnostics)
    {
        if (document.Notifications is not { } notifications)
        {
            return;
        }

        foreach (string node in notifications.ByNode.Keys.Where(k => !ids.Contains(k)))
        {
            diagnostics.Add(DslDiagnostic.Warning(
                DslCodes.EdgeEndpointNotFound,
                $"{notifications.Pointer}/byNode/{JsonPointer.Escape(node)}",
                $"'{node}' is not a node, so this override does nothing.", Suggest(node, ids)));
        }
    }

    // ---- environment ---------------------------------------------------------------------------

    private static void CheckEnvironment(
        DslDocument document, DslEnvironment? environment,
        List<DslDiagnostic> diagnostics, List<string> skipped)
    {
        if (environment is null)
        {
            // Reported rather than passed. A check that silently did not run is worse than one that
            // openly did not, because only the second can be acted on.
            skipped.Add(DslCodes.UnknownCustomNode);
            skipped.Add(DslCodes.CustomNodeParameters);
            skipped.Add(DslCodes.EgressHostsRequired);
            skipped.Add(DslCodes.HashConflict);
            return;
        }

        foreach (DslCustomNode node in document.Nodes.OfType<DslCustomNode>())
        {
            if (!environment.CustomNodes.TryGetValue(node.NodeName, out JsonNode? parameterSchema))
            {
                string? suggestion = Suggest(
                    node.NodeName, [.. environment.CustomNodes.Keys]);

                diagnostics.Add(DslDiagnostic.Error(
                    DslCodes.UnknownCustomNode, $"{node.Pointer}/node",
                    $"No custom node named '{node.NodeName}' is registered.",
                    suggestion ?? "Register it with AddDslNode(name, factory) before the document is loaded."));
                continue;
            }

            if (parameterSchema is not null)
            {
                EvaluationResults results = JsonSchema.FromText(parameterSchema.ToJsonString())
                    .Evaluate(node.With ?? new JsonObject(), new EvaluationOptions
                    {
                        OutputFormat = OutputFormat.List
                    });

                if (!results.IsValid)
                {
                    diagnostics.Add(DslDiagnostic.Error(
                        DslCodes.CustomNodeParameters, $"{node.Pointer}/with",
                        $"The parameters do not match the schema published by '{node.NodeName}'.",
                        DescribeFirst(results)));
                }
            }
        }

        if (environment.EnforceEgress)
        {
            foreach (DslHttpNode node in document.Nodes.OfType<DslHttpNode>()
                         .Where(n => n.AllowedHosts.Count == 0))
            {
                diagnostics.Add(DslDiagnostic.Error(
                    DslCodes.EgressHostsRequired, $"{node.Pointer}/allowedHosts",
                    $"Node '{node.Id}' calls out but declares no allowed hosts.",
                    "The host enforces an egress allow-list; a document cannot widen it, only name what it needs."));
            }
        }

        string key = $"{document.Name}@{document.Version}";
        if (environment.PublishedHashes.TryGetValue(key, out string? published) &&
            !string.Equals(published, document.Hash, StringComparison.Ordinal))
        {
            diagnostics.Add(DslDiagnostic.Error(
                DslCodes.HashConflict, "/version",
                $"'{key}' is already published with a different document.",
                $"Published {published[..12]}…, this one is {document.Hash[..12]}…. " +
                "A published version is immutable — bump the version instead."));
        }
    }

    private static string? DescribeFirst(EvaluationResults results)
    {
        foreach (EvaluationResults detail in Flatten(results))
        {
            if (detail.Errors is { Count: > 0 })
            {
                return $"{detail.InstanceLocation}: {detail.Errors.First().Value}";
            }
        }

        return null;
    }

    private static IEnumerable<EvaluationResults> Flatten(EvaluationResults results)
    {
        yield return results;

        foreach (EvaluationResults detail in results.Details)
        {
            foreach (EvaluationResults nested in Flatten(detail))
            {
                yield return nested;
            }
        }
    }
}

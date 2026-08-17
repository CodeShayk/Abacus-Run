using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Abacus.Run.Dsl.Expressions;

namespace Abacus.Run.Dsl.Interpretation;

/// <summary>
/// Parses expressions once and evaluates them many times.
/// </summary>
/// <remarks>
/// <c>BuildAsync</c> runs once per attempt and has to stay cheap, so nothing here re-parses. The
/// cache is process-wide and keyed by expression text: two documents using <c>$.total &gt; 0</c>
/// share one tree, and a tree is immutable so sharing is safe.
/// </remarks>
public static class DslExpressions
{
    private static readonly ConcurrentDictionary<string, AbExNode?> Cache = new(StringComparer.Ordinal);

    /// <summary>
    /// Returns the parsed tree, or null when the text does not parse. Null rather than throwing
    /// because validation has already had its chance to report the problem properly; a parse failure
    /// here means something bypassed it, and a run should degrade rather than crash.
    /// </summary>
    public static AbExNode? Tree(string expression)
        => Cache.GetOrAdd(expression, static text => AbExParser.Parse(text).Node);

    public static AbExValue Evaluate(string expression, AbExContext context)
    {
        AbExNode? tree = Tree(expression);
        return tree is null ? AbExValue.Absent : AbExEvaluator.Evaluate(tree, context);
    }

    /// <summary>Strict boolean evaluation: anything that is not <c>true</c> is false.</summary>
    public static bool Condition(string expression, AbExContext context)
        => Evaluate(expression, context).IsTruthy;

    public static JsonNode? Node(string expression, AbExContext context)
        => Evaluate(expression, context).ToNode();

    public static string? Text(string expression, AbExContext context)
    {
        AbExValue value = Evaluate(expression, context);
        return value.IsAbsent ? null : value.ToText();
    }

    /// <summary>
    /// Applies a <c>set</c> map to a value. Every expression reads the value as it was <em>before</em>
    /// the transform, so the order the properties happen to be written in cannot change the result.
    /// </summary>
    public static JsonNode? ApplySet(
        IReadOnlyDictionary<string, string> set, AbExContext context, JsonNode? current, bool replace)
    {
        JsonObject target = replace || current is not JsonObject existing
            ? []
            : (JsonObject)existing.DeepClone();

        foreach ((string path, string expression) in set)
        {
            AbExValue value = Evaluate(expression, context);
            Assign(target, path, value.IsAbsent ? null : value.ToNode());
        }

        return target;
    }

    /// <summary>
    /// Writes to a dotted path, creating intermediate objects. A segment that exists but is not an
    /// object is replaced rather than merged into: the document asked for a property there.
    /// </summary>
    internal static void Assign(JsonObject root, string path, JsonNode? value)
    {
        string[] segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return;
        }

        JsonObject current = root;

        for (int i = 0; i < segments.Length - 1; i++)
        {
            if (current[segments[i]] is JsonObject child)
            {
                current = child;
                continue;
            }

            var created = new JsonObject();
            current[segments[i]] = created;
            current = created;
        }

        current[segments[^1]] = value;
    }
}

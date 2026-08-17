namespace Abacus.Run.Dsl.Expressions;

/// <summary>Arity and evaluation rules for one built-in function.</summary>
public sealed record AbExFunction(string Name, int MinArguments, int MaxArguments, string Summary)
{
    /// <summary>Unbounded arity, used by <c>coalesce</c>.</summary>
    public const int Variadic = int.MaxValue;

    public bool AcceptsArity(int count) => count >= MinArguments && count <= MaxArguments;

    public string DescribeArity() => MaxArguments switch
    {
        Variadic => $"at least {MinArguments}",
        _ when MinArguments == MaxArguments => MinArguments.ToString(),
        _ => $"{MinArguments} to {MaxArguments}"
    };
}

/// <summary>
/// The closed function set. Closed is the point: an unknown name is a validation error the author
/// sees while editing, and growing this list is a deliberate, reviewable change rather than
/// something a document can do to itself.
/// </summary>
public static class AbExFunctions
{
    private static readonly Dictionary<string, AbExFunction> Registry =
        new(StringComparer.Ordinal)
        {
            ["len"] = new("len", 1, 1, "Length of a string, array or object; 0 for anything else."),
            ["has"] = new("has", 1, 1, "Whether the argument resolved to anything at all."),
            ["lower"] = new("lower", 1, 1, "Lowercases a string, invariant culture."),
            ["upper"] = new("upper", 1, 1, "Uppercases a string, invariant culture."),
            ["contains"] = new("contains", 2, 2, "Ordinal substring test."),
            ["startsWith"] = new("startsWith", 2, 2, "Ordinal prefix test."),
            ["endsWith"] = new("endsWith", 2, 2, "Ordinal suffix test."),
            ["matches"] = new("matches", 2, 2, "Regex test. The pattern must be a string literal."),
            ["coalesce"] = new("coalesce", 1, AbExFunction.Variadic, "First argument that is neither absent nor null."),
            ["number"] = new("number", 1, 1, "Coerces to a number, or absent if it cannot."),
            ["string"] = new("string", 1, 1, "Coerces to a string."),
            ["bool"] = new("bool", 1, 1, "Coerces to a boolean, or absent if it cannot.")
        };

    public static IReadOnlyCollection<string> Names => Registry.Keys;

    public static bool TryGet(string name, out AbExFunction function) => Registry.TryGetValue(name, out function!);

    public static bool Exists(string name) => Registry.ContainsKey(name);

    /// <summary>
    /// Nearest known name by edit distance, for "did you mean". Only offered when the candidate is
    /// close enough that the suggestion is likely right rather than merely the least-wrong entry.
    /// </summary>
    public static string? Suggest(string name)
    {
        string? best = null;
        int bestDistance = int.MaxValue;

        foreach (string candidate in Registry.Keys)
        {
            int distance = EditDistance(name, candidate);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        int threshold = Math.Max(2, name.Length / 3);
        return bestDistance <= threshold ? best : null;
    }

    /// <summary>
    /// Case-insensitive Levenshtein distance. Public because the semantic validator suggests
    /// nearest node ids the same way this suggests nearest function names.
    /// </summary>
    public static int EditDistance(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        int[] previous = new int[b.Length + 1];
        int[] current = new int[b.Length + 1];

        for (int j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}

using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Abacus.Run.Dsl.Expressions;

/// <summary>The three roots an expression may read. Nothing else is in scope.</summary>
public sealed record AbExContext(JsonNode? Data, JsonNode? Context, JsonObject Run)
{
    public static AbExContext Empty { get; } = new(null, null, []);
}

/// <summary>
/// Evaluates an AbEx tree.
/// </summary>
/// <remarks>
/// <para>
/// Total by construction: every operation over every value produces a value, and
/// <see cref="AbExValue.Absent"/> is what stands in for "no answer". Nothing here throws, because a
/// workflow must not fail on an expression over a document shape the author did not anticipate — it
/// must take the other branch.
/// </para>
/// <para>
/// Pure by construction: there is no I/O, no state, and no way to reach either from the grammar.
/// </para>
/// </remarks>
public static class AbExEvaluator
{
    /// <summary>Bounds a pathological pattern. A timed-out match is a non-match, never a fault.</summary>
    public static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);

    private static readonly ConcurrentDictionary<string, Regex?> RegexCache = new(StringComparer.Ordinal);

    public static AbExValue Evaluate(AbExNode node, AbExContext context)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(context);

        return node switch
        {
            AbExLiteral literal => literal.Value,
            AbExPath path => ResolvePath(path, context),
            AbExUnary unary => EvaluateUnary(unary, context),
            AbExBinary binary => EvaluateBinary(binary, context),
            AbExCall call => EvaluateCall(call, context),
            _ => AbExValue.Absent
        };
    }

    /// <summary>
    /// Strict boolean coercion: only <c>true</c> is true. Absent, null, <c>0</c> and <c>""</c> are
    /// all false, and there is no truthiness ladder to remember.
    /// </summary>
    public static bool EvaluateCondition(AbExNode node, AbExContext context)
        => Evaluate(node, context).IsTruthy;

    private static AbExValue ResolvePath(AbExPath path, AbExContext context)
    {
        JsonNode? current = path.Root switch
        {
            AbExRoot.Data => context.Data,
            AbExRoot.Context => context.Context,
            _ => context.Run
        };

        // A root that is itself missing is absent, not null: '$ .x' over a null data payload has no
        // answer, and reporting null would let '== null' succeed against a value that is not there.
        if (current is null && path.Segments.Count > 0)
        {
            return AbExValue.Absent;
        }

        foreach (AbExSegment segment in path.Segments)
        {
            if (segment.IsIndex)
            {
                if (current is not JsonArray array || segment.Index < 0 || segment.Index >= array.Count)
                {
                    return AbExValue.Absent;
                }

                current = array[segment.Index];
                continue;
            }

            if (current is not JsonObject obj || !obj.TryGetPropertyValue(segment.Name!, out JsonNode? child))
            {
                return AbExValue.Absent;
            }

            current = child;
        }

        return path.Segments.Count == 0 && current is null
            ? AbExValue.Absent
            : AbExValue.FromNode(current);
    }

    private static AbExValue EvaluateUnary(AbExUnary unary, AbExContext context)
    {
        AbExValue operand = Evaluate(unary.Operand, context);

        return unary.Operator switch
        {
            "!" => operand.Kind == AbExValueKind.Boolean
                ? AbExValue.Bool(!operand.AsBoolean)
                : AbExValue.Absent,

            "-" => operand.Kind == AbExValueKind.Number
                ? AbExValue.Number(-operand.AsNumber)
                : AbExValue.Absent,

            _ => AbExValue.Absent
        };
    }

    private static AbExValue EvaluateBinary(AbExBinary binary, AbExContext context)
    {
        // Short-circuit before evaluating the right operand, so a guard like
        // 'has($.order) && $.order.total > 0' costs nothing when the guard fails.
        if (binary.Operator is "&&" or "||")
        {
            AbExValue left = Evaluate(binary.Left, context);
            if (left.Kind != AbExValueKind.Boolean)
            {
                return AbExValue.Absent;
            }

            if (binary.Operator == "&&" && !left.AsBoolean) return AbExValue.False;
            if (binary.Operator == "||" && left.AsBoolean) return AbExValue.True;

            AbExValue right = Evaluate(binary.Right, context);
            return right.Kind == AbExValueKind.Boolean ? right : AbExValue.Absent;
        }

        AbExValue a = Evaluate(binary.Left, context);
        AbExValue b = Evaluate(binary.Right, context);

        return binary.Operator switch
        {
            "==" or "!=" or "<" or "<=" or ">" or ">=" => Compare(binary.Operator, a, b),
            _ => Arithmetic(binary.Operator, a, b)
        };
    }

    /// <summary>
    /// Absence makes every comparison false, including <c>!=</c>. That is deliberate: a document
    /// asking whether a field it never set differs from a value should not be told "yes". Use
    /// <c>has()</c> to ask about presence.
    /// </summary>
    private static AbExValue Compare(string op, AbExValue a, AbExValue b)
    {
        if (a.IsAbsent || b.IsAbsent)
        {
            return AbExValue.False;
        }

        if (op is "==" or "!=")
        {
            bool equal = a.Equals(b);
            return AbExValue.Bool(op == "==" ? equal : !equal);
        }

        int comparison;
        if (a.Kind == AbExValueKind.Number && b.Kind == AbExValueKind.Number)
        {
            comparison = decimal.Compare(a.AsNumber, b.AsNumber);
        }
        else if (a.Kind == AbExValueKind.String && b.Kind == AbExValueKind.String)
        {
            comparison = string.CompareOrdinal(a.AsString, b.AsString);
        }
        else
        {
            // No coercion ladder: ordering two different JSON types has no defensible answer.
            return AbExValue.False;
        }

        return AbExValue.Bool(op switch
        {
            "<" => comparison < 0,
            "<=" => comparison <= 0,
            ">" => comparison > 0,
            _ => comparison >= 0
        });
    }

    /// <summary>
    /// Decimal, and numbers only. These documents price orders, so binary floating point is the
    /// wrong default. <c>+</c> does not concatenate strings — that is what templates are for, and a
    /// <c>+</c> that sometimes adds and sometimes joins is the single most reliable source of bugs
    /// in languages that allow it.
    /// </summary>
    private static AbExValue Arithmetic(string op, AbExValue a, AbExValue b)
    {
        if (a.Kind != AbExValueKind.Number || b.Kind != AbExValueKind.Number)
        {
            return AbExValue.Absent;
        }

        decimal x = a.AsNumber;
        decimal y = b.AsNumber;

        switch (op)
        {
            case "+": return AbExValue.Number(x + y);
            case "-": return AbExValue.Number(x - y);
            case "*": return AbExValue.Number(x * y);

            case "/":
            case "%":
                if (y == 0m)
                {
                    return AbExValue.Absent;
                }

                return AbExValue.Number(op == "/" ? x / y : x % y);

            default:
                return AbExValue.Absent;
        }
    }

    private static AbExValue EvaluateCall(AbExCall call, AbExContext context)
    {
        // An unknown or mis-arity call cannot reach here through a validated document; if it does,
        // absent keeps the evaluator total rather than surfacing a validator bug as a run failure.
        if (!AbExFunctions.TryGet(call.Name, out AbExFunction function) ||
            !function.AcceptsArity(call.Arguments.Count))
        {
            return AbExValue.Absent;
        }

        switch (call.Name)
        {
            case "has":
                return AbExValue.Bool(!Evaluate(call.Arguments[0], context).IsAbsent);

            case "coalesce":
                foreach (AbExNode argument in call.Arguments)
                {
                    AbExValue candidate = Evaluate(argument, context);
                    if (candidate.Kind is not (AbExValueKind.Absent or AbExValueKind.Null))
                    {
                        return candidate;
                    }
                }

                return AbExValue.Absent;
        }

        AbExValue first = Evaluate(call.Arguments[0], context);

        switch (call.Name)
        {
            case "len":
                return AbExValue.Number(first.Length);

            case "lower":
                return first.Kind == AbExValueKind.String
                    ? AbExValue.String(first.AsString.ToLowerInvariant())
                    : AbExValue.Absent;

            case "upper":
                return first.Kind == AbExValueKind.String
                    ? AbExValue.String(first.AsString.ToUpperInvariant())
                    : AbExValue.Absent;

            case "number":
                return first.Kind switch
                {
                    AbExValueKind.Number => first,
                    AbExValueKind.String when decimal.TryParse(
                        first.AsString, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal parsed)
                        => AbExValue.Number(parsed),
                    _ => AbExValue.Absent
                };

            case "string":
                return first.IsAbsent ? AbExValue.Absent : AbExValue.String(first.ToText());

            case "bool":
                return first.Kind switch
                {
                    AbExValueKind.Boolean => first,
                    AbExValueKind.String when bool.TryParse(first.AsString, out bool parsed)
                        => AbExValue.Bool(parsed),
                    _ => AbExValue.Absent
                };
        }

        AbExValue second = Evaluate(call.Arguments[1], context);

        if (first.Kind != AbExValueKind.String || second.Kind != AbExValueKind.String)
        {
            return AbExValue.Absent;
        }

        string subject = first.AsString;
        string operand = second.AsString;

        return call.Name switch
        {
            "contains" => AbExValue.Bool(subject.Contains(operand, StringComparison.Ordinal)),
            "startsWith" => AbExValue.Bool(subject.StartsWith(operand, StringComparison.Ordinal)),
            "endsWith" => AbExValue.Bool(subject.EndsWith(operand, StringComparison.Ordinal)),
            "matches" => Matches(subject, operand),
            _ => AbExValue.Absent
        };
    }

    private static AbExValue Matches(string subject, string pattern)
    {
        Regex? regex = RegexCache.GetOrAdd(pattern, static p =>
        {
            try
            {
                return new Regex(p, RegexOptions.CultureInvariant, RegexTimeout);
            }
            catch (ArgumentException)
            {
                // Cached as null so a bad pattern is compiled once, not once per evaluation.
                return null;
            }
        });

        if (regex is null)
        {
            return AbExValue.Absent;
        }

        try
        {
            return AbExValue.Bool(regex.IsMatch(subject));
        }
        catch (RegexMatchTimeoutException)
        {
            // A pattern that cannot decide in 200 ms has not matched. Failing the run instead would
            // hand any author a way to stall a dispatcher.
            return AbExValue.False;
        }
    }
}

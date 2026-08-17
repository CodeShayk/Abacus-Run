namespace Abacus.Run.Dsl.Expressions;

/// <summary>One problem found by static analysis of a parsed expression.</summary>
public sealed record AbExIssue(string Message, int Offset, string? Suggestion = null);

/// <summary>What static analysis concluded about an expression.</summary>
public sealed record ExpressionFacts(
    int Depth,
    bool IsDeterministic,
    IReadOnlyList<AbExIssue> Issues)
{
    public bool IsValid => Issues.Count == 0;
}

/// <summary>
/// Static analysis over a parsed tree: everything decidable without a document to evaluate against.
/// </summary>
/// <remarks>
/// Separate from the parser because these are different failures with different audiences. A parse
/// error means the text is not an expression; an issue here means it is a well-formed expression
/// that will never do what it says.
/// </remarks>
public static class AbExValidator
{
    public static ExpressionFacts Analyse(AbExNode node, int maxDepth = AbExParser.MaxDepth)
    {
        ArgumentNullException.ThrowIfNull(node);

        var issues = new List<AbExIssue>();
        bool deterministic = true;

        Walk(node, issues, ref deterministic);

        if (node.Depth > maxDepth)
        {
            issues.Add(new AbExIssue(
                $"Expression nests {node.Depth} deep; the limit is {maxDepth}.", node.Offset));
        }

        return new ExpressionFacts(node.Depth, deterministic, issues);
    }

    /// <summary>Parses and analyses in one step, folding a parse error into the issue list.</summary>
    public static ExpressionFacts Check(string expression, int maxDepth = AbExParser.MaxDepth)
    {
        AbExResult parsed = AbExParser.Parse(expression);

        return parsed.IsSuccess
            ? Analyse(parsed.Node!, maxDepth)
            : new ExpressionFacts(0, true, [new AbExIssue(parsed.Error!.Message, parsed.Error.Offset)]);
    }

    private static void Walk(AbExNode node, List<AbExIssue> issues, ref bool deterministic)
    {
        switch (node)
        {
            case AbExPath path:
                if (path.IsNonDeterministic)
                {
                    deterministic = false;
                }

                break;

            case AbExUnary unary:
                Walk(unary.Operand, issues, ref deterministic);
                break;

            case AbExBinary binary:
                Walk(binary.Left, issues, ref deterministic);
                Walk(binary.Right, issues, ref deterministic);
                break;

            case AbExCall call:
                CheckCall(call, issues);
                foreach (AbExNode argument in call.Arguments)
                {
                    Walk(argument, issues, ref deterministic);
                }

                break;
        }
    }

    private static void CheckCall(AbExCall call, List<AbExIssue> issues)
    {
        if (!AbExFunctions.TryGet(call.Name, out AbExFunction function))
        {
            string? suggestion = AbExFunctions.Suggest(call.Name);
            issues.Add(new AbExIssue(
                $"Unknown function '{call.Name}'.", call.Offset,
                suggestion is null ? null : $"Did you mean '{suggestion}'?"));
            return;
        }

        if (!function.AcceptsArity(call.Arguments.Count))
        {
            issues.Add(new AbExIssue(
                $"'{call.Name}' takes {function.DescribeArity()} argument(s) but was given {call.Arguments.Count}.",
                call.Offset));
        }

        // A pattern assembled at run time cannot be checked at authoring time, and an unbounded
        // pattern is the one genuinely dangerous thing in the grammar. Requiring a literal keeps
        // every regex in a document reviewable by reading the document.
        if (call.Name == "matches" && call.Arguments.Count == 2)
        {
            if (call.Arguments[1] is not AbExLiteral { Value.Kind: AbExValueKind.String } literal)
            {
                issues.Add(new AbExIssue(
                    "The pattern argument to 'matches' must be a string literal.", call.Arguments[1].Offset));
                return;
            }

            try
            {
                _ = new System.Text.RegularExpressions.Regex(literal.Value.AsString);
            }
            catch (ArgumentException ex)
            {
                issues.Add(new AbExIssue(
                    $"Invalid regular expression: {ex.Message}", call.Arguments[1].Offset));
            }
        }
    }
}

using System.Globalization;

namespace Abacus.Run.Dsl.Expressions;

/// <summary>Where a parse failed, and why.</summary>
public sealed record AbExError(string Message, int Offset)
{
    public override string ToString() => $"{Message} (at offset {Offset})";
}

/// <summary>An AST or an error. Never both, and never an exception.</summary>
public sealed record AbExResult(AbExNode? Node, AbExError? Error)
{
    public bool IsSuccess => Node is not null;

    public static AbExResult Ok(AbExNode node) => new(node, null);
    public static AbExResult Fail(string message, int offset) => new(null, new AbExError(message, offset));
}

/// <summary>
/// Recursive-descent parser for AbEx.
/// </summary>
/// <remarks>
/// <para>
/// Hand-written rather than generated: the grammar is a page long, and a hand-written parser is what
/// gives every construct the exact source offset the diagnostics depend on.
/// </para>
/// <para>
/// Parsing never throws. A malformed expression is data — it arrives in a document from outside the
/// build, and the only useful response is a diagnostic pointing at it.
/// </para>
/// </remarks>
public static class AbExParser
{
    /// <summary>Guards against a pathological document; the semantic validator enforces its own limit too.</summary>
    public const int MaxDepth = 32;

    public static AbExResult Parse(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return AbExResult.Fail("An expression is required.", 0);
        }

        var parser = new Parser(expression);
        return parser.ParseAll();
    }

    /// <summary>Parses or throws. For framework-internal call sites that have already validated.</summary>
    public static AbExNode ParseOrThrow(string expression)
    {
        AbExResult result = Parse(expression);
        return result.Node ?? throw new FormatException(
            $"Invalid AbEx expression '{expression}'. {result.Error!.Message}");
    }

    private sealed class Parser
    {
        private readonly string _source;
        private readonly AbExLexer _lexer;
        private AbExToken _current;
        private AbExError? _error;

        internal Parser(string source)
        {
            _source = source;
            _lexer = new AbExLexer(source);
            _current = _lexer.Next();
            CaptureLexError();
        }

        internal AbExResult ParseAll()
        {
            if (_error is not null)
            {
                return new AbExResult(null, _error);
            }

            AbExNode? node = ParseOr();

            if (_error is not null)
            {
                return new AbExResult(null, _error);
            }

            if (_current.Kind != AbExTokenKind.End)
            {
                return AbExResult.Fail(
                    $"Unexpected {_current} after a complete expression.", _current.Offset);
            }

            if (node!.Depth > MaxDepth)
            {
                return AbExResult.Fail(
                    $"Expression nests {node.Depth} deep; the limit is {MaxDepth}.", 0);
            }

            return AbExResult.Ok(node);
        }

        private void Advance()
        {
            _current = _lexer.Next();
            CaptureLexError();
        }

        private void CaptureLexError()
        {
            if (_current.Kind == AbExTokenKind.Invalid && _error is null)
            {
                _error = new AbExError(_lexer.Error ?? "Invalid token.", _current.Offset);
            }
        }

        private void Fail(string message, int offset) => _error ??= new AbExError(message, offset);

        private bool IsOperator(params string[] operators) =>
            _current.Kind == AbExTokenKind.Operator && Array.IndexOf(operators, _current.Text) >= 0;

        private AbExNode? ParseOr()
        {
            AbExNode? left = ParseAnd();
            while (_error is null && IsOperator("||"))
            {
                int offset = _current.Offset;
                Advance();
                AbExNode? right = ParseAnd();
                if (_error is not null) return null;
                left = new AbExBinary("||", left!, right!) { Offset = offset };
            }

            return left;
        }

        private AbExNode? ParseAnd()
        {
            AbExNode? left = ParseComparison();
            while (_error is null && IsOperator("&&"))
            {
                int offset = _current.Offset;
                Advance();
                AbExNode? right = ParseComparison();
                if (_error is not null) return null;
                left = new AbExBinary("&&", left!, right!) { Offset = offset };
            }

            return left;
        }

        /// <summary>
        /// Non-associative: <c>a &lt; b &lt; c</c> is a mistake in every language that quietly allows
        /// it, so it is refused here rather than silently comparing a boolean to a number.
        /// </summary>
        private AbExNode? ParseComparison()
        {
            AbExNode? left = ParseAdditive();
            if (_error is not null || !IsOperator("==", "!=", "<", "<=", ">", ">="))
            {
                return left;
            }

            string op = _current.Text;
            int offset = _current.Offset;
            Advance();

            AbExNode? right = ParseAdditive();
            if (_error is not null) return null;

            if (IsOperator("==", "!=", "<", "<=", ">", ">="))
            {
                Fail($"Chained comparison '{_current.Text}'. Combine two comparisons with '&&' instead.",
                    _current.Offset);
                return null;
            }

            return new AbExBinary(op, left!, right!) { Offset = offset };
        }

        private AbExNode? ParseAdditive()
        {
            AbExNode? left = ParseMultiplicative();
            while (_error is null && IsOperator("+", "-"))
            {
                string op = _current.Text;
                int offset = _current.Offset;
                Advance();
                AbExNode? right = ParseMultiplicative();
                if (_error is not null) return null;
                left = new AbExBinary(op, left!, right!) { Offset = offset };
            }

            return left;
        }

        private AbExNode? ParseMultiplicative()
        {
            AbExNode? left = ParseUnary();
            while (_error is null && IsOperator("*", "/", "%"))
            {
                string op = _current.Text;
                int offset = _current.Offset;
                Advance();
                AbExNode? right = ParseUnary();
                if (_error is not null) return null;
                left = new AbExBinary(op, left!, right!) { Offset = offset };
            }

            return left;
        }

        private AbExNode? ParseUnary()
        {
            if (IsOperator("!", "-"))
            {
                string op = _current.Text;
                int offset = _current.Offset;
                Advance();
                AbExNode? operand = ParseUnary();
                if (_error is not null) return null;
                return new AbExUnary(op, operand!) { Offset = offset };
            }

            return ParsePrimary();
        }

        private AbExNode? ParsePrimary()
        {
            int offset = _current.Offset;

            switch (_current.Kind)
            {
                case AbExTokenKind.Number:
                {
                    decimal value = decimal.Parse(_current.Text, NumberStyles.Number, CultureInfo.InvariantCulture);
                    Advance();
                    return new AbExLiteral(AbExValue.Number(value)) { Offset = offset };
                }

                case AbExTokenKind.String:
                {
                    string text = _current.Text;
                    Advance();
                    return new AbExLiteral(AbExValue.String(text)) { Offset = offset };
                }

                case AbExTokenKind.True:
                    Advance();
                    return new AbExLiteral(AbExValue.True) { Offset = offset };

                case AbExTokenKind.False:
                    Advance();
                    return new AbExLiteral(AbExValue.False) { Offset = offset };

                case AbExTokenKind.NullLiteral:
                    Advance();
                    return new AbExLiteral(AbExValue.Null) { Offset = offset };

                case AbExTokenKind.LParen:
                {
                    Advance();
                    AbExNode? inner = ParseOr();
                    if (_error is not null) return null;

                    if (_current.Kind != AbExTokenKind.RParen)
                    {
                        Fail($"Expected ')' but found {_current}.", _current.Offset);
                        return null;
                    }

                    Advance();
                    return inner;
                }

                case AbExTokenKind.Identifier:
                    return ParseCall();

                case AbExTokenKind.Root:
                    return ParsePath();

                case AbExTokenKind.End:
                    Fail("Expression ends where a value was expected.", offset);
                    return null;

                default:
                    Fail($"Expected a value but found {_current}.", offset);
                    return null;
            }
        }

        private AbExNode? ParseCall()
        {
            string name = _current.Text;
            int offset = _current.Offset;
            Advance();

            if (_current.Kind != AbExTokenKind.LParen)
            {
                // The only bare identifiers AbEx has are function names. A path must start with a
                // root, and saying so is far more useful than "unexpected token".
                Fail($"'{name}' is not a value. Paths start with '$', '$ctx' or '$run'; " +
                     $"functions are called as {name}(...).", offset);
                return null;
            }

            Advance();
            var arguments = new List<AbExNode>();

            if (_current.Kind != AbExTokenKind.RParen)
            {
                while (true)
                {
                    AbExNode? argument = ParseOr();
                    if (_error is not null) return null;
                    arguments.Add(argument!);

                    if (_current.Kind == AbExTokenKind.Comma)
                    {
                        Advance();
                        continue;
                    }

                    break;
                }
            }

            if (_current.Kind != AbExTokenKind.RParen)
            {
                Fail($"Expected ')' to close {name}(...) but found {_current}.", _current.Offset);
                return null;
            }

            Advance();
            return new AbExCall(name, arguments) { Offset = offset };
        }

        private AbExNode? ParsePath()
        {
            int offset = _current.Offset;
            string rootText = _current.Text;

            AbExRoot root;
            switch (rootText)
            {
                case "$": root = AbExRoot.Data; break;
                case "$ctx": root = AbExRoot.Context; break;
                case "$run": root = AbExRoot.Run; break;
                default:
                    Fail($"Unknown root '{rootText}'. Use '$', '$ctx' or '$run'.", offset);
                    return null;
            }

            Advance();
            var segments = new List<AbExSegment>();

            while (true)
            {
                if (_current.Kind == AbExTokenKind.Dot)
                {
                    Advance();
                    if (_current.Kind is not (AbExTokenKind.Identifier or AbExTokenKind.True
                        or AbExTokenKind.False or AbExTokenKind.NullLiteral))
                    {
                        Fail($"Expected a property name after '.' but found {_current}.", _current.Offset);
                        return null;
                    }

                    segments.Add(AbExSegment.Property(_current.Text));
                    Advance();
                    continue;
                }

                if (_current.Kind == AbExTokenKind.LBracket)
                {
                    Advance();
                    if (_current.Kind != AbExTokenKind.Number)
                    {
                        Fail($"Array index must be a literal integer but found {_current}.", _current.Offset);
                        return null;
                    }

                    if (!int.TryParse(_current.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int index))
                    {
                        Fail($"'{_current.Text}' is not a valid array index.", _current.Offset);
                        return null;
                    }

                    segments.Add(AbExSegment.At(index));
                    Advance();

                    if (_current.Kind != AbExTokenKind.RBracket)
                    {
                        Fail($"Expected ']' but found {_current}.", _current.Offset);
                        return null;
                    }

                    Advance();
                    continue;
                }

                break;
            }

            return new AbExPath(root, segments) { Offset = offset };
        }
    }
}

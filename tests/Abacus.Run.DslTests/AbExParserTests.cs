using Abacus.Run.Dsl.Expressions;
using FluentAssertions;
using Xunit;

namespace Abacus.Run.DslTests;

public class AbExParserTests
{
    private static AbExNode Parse(string expression)
    {
        AbExResult result = AbExParser.Parse(expression);
        result.IsSuccess.Should().BeTrue(
            $"'{expression}' should parse but failed: {result.Error?.Message}");
        return result.Node!;
    }

    private static AbExError ParseError(string expression)
    {
        AbExResult result = AbExParser.Parse(expression);
        result.IsSuccess.Should().BeFalse($"'{expression}' should not parse");
        return result.Error!;
    }

    [Theory]
    [InlineData("$")]
    [InlineData("$.total")]
    [InlineData("$ctx.orderId")]
    [InlineData("$run.attempt")]
    [InlineData("$.lines[0].sku")]
    [InlineData("$.a.b.c[3][4].d")]
    public void Paths_parse(string expression) => Parse(expression).Should().BeOfType<AbExPath>();

    [Fact]
    public void Path_records_root_and_segments()
    {
        var path = (AbExPath)Parse("$ctx.lines[2].sku");

        path.Root.Should().Be(AbExRoot.Context);
        path.Segments.Should().HaveCount(3);
        path.Segments[0].Name.Should().Be("lines");
        path.Segments[1].IsIndex.Should().BeTrue();
        path.Segments[1].Index.Should().Be(2);
        path.Segments[2].Name.Should().Be("sku");
    }

    [Fact]
    public void Bare_root_has_no_segments()
    {
        var path = (AbExPath)Parse("$");
        path.Root.Should().Be(AbExRoot.Data);
        path.Segments.Should().BeEmpty();
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("42", 42)]
    [InlineData("3.5", 3.5)]
    [InlineData("0.1", 0.1)]
    public void Number_literals_parse(string expression, decimal expected)
        => ((AbExLiteral)Parse(expression)).Value.AsNumber.Should().Be(expected);

    [Theory]
    [InlineData("'settled'", "settled")]
    [InlineData("\"settled\"", "settled")]
    [InlineData("'it\\'s'", "it's")]
    [InlineData("'a\\nb'", "a\nb")]
    [InlineData("''", "")]
    public void String_literals_parse(string expression, string expected)
        => ((AbExLiteral)Parse(expression)).Value.AsString.Should().Be(expected);

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("null")]
    public void Keyword_literals_parse(string expression)
        => Parse(expression).Should().BeOfType<AbExLiteral>();

    [Fact]
    public void Or_binds_looser_than_and()
    {
        var root = (AbExBinary)Parse("$.a || $.b && $.c");

        root.Operator.Should().Be("||");
        ((AbExBinary)root.Right).Operator.Should().Be("&&");
    }

    [Fact]
    public void And_binds_looser_than_comparison()
    {
        var root = (AbExBinary)Parse("$.a > 1 && $.b < 2");

        root.Operator.Should().Be("&&");
        ((AbExBinary)root.Left).Operator.Should().Be(">");
        ((AbExBinary)root.Right).Operator.Should().Be("<");
    }

    [Fact]
    public void Comparison_binds_looser_than_arithmetic()
    {
        var root = (AbExBinary)Parse("$.a + 1 > $.b * 2");

        root.Operator.Should().Be(">");
        ((AbExBinary)root.Left).Operator.Should().Be("+");
        ((AbExBinary)root.Right).Operator.Should().Be("*");
    }

    [Fact]
    public void Multiplication_binds_tighter_than_addition()
    {
        var root = (AbExBinary)Parse("1 + 2 * 3");

        root.Operator.Should().Be("+");
        ((AbExBinary)root.Right).Operator.Should().Be("*");
    }

    [Fact]
    public void Addition_is_left_associative()
    {
        var root = (AbExBinary)Parse("1 - 2 - 3");

        root.Operator.Should().Be("-");
        ((AbExBinary)root.Left).Operator.Should().Be("-");
        ((AbExLiteral)root.Right).Value.AsNumber.Should().Be(3);
    }

    [Fact]
    public void Parentheses_override_precedence()
    {
        var root = (AbExBinary)Parse("(1 + 2) * 3");
        root.Operator.Should().Be("*");
    }

    /// <summary>
    /// A deviation from the grammar as first written, where '!' sat between '&amp;&amp;' and
    /// comparison. Standard precedence is what an author expects, and '!has($.x) &amp;&amp; ...' is
    /// the common shape.
    /// </summary>
    [Fact]
    public void Not_binds_tighter_than_comparison()
    {
        var root = (AbExBinary)Parse("!$.a == $.b");

        root.Operator.Should().Be("==");
        root.Left.Should().BeOfType<AbExUnary>();
    }

    [Fact]
    public void Unary_minus_parses()
    {
        var root = (AbExUnary)Parse("-$.total");
        root.Operator.Should().Be("-");
    }

    [Fact]
    public void Calls_parse_with_arguments()
    {
        var call = (AbExCall)Parse("contains($.sku, 'ABC')");

        call.Name.Should().Be("contains");
        call.Arguments.Should().HaveCount(2);
    }

    [Fact]
    public void Calls_parse_with_no_arguments()
        => ((AbExCall)Parse("len()")).Arguments.Should().BeEmpty();

    [Fact]
    public void Calls_nest()
    {
        var call = (AbExCall)Parse("lower(coalesce($.a, $.b, 'x'))");

        call.Name.Should().Be("lower");
        ((AbExCall)call.Arguments[0]).Arguments.Should().HaveCount(3);
    }

    [Fact]
    public void Depth_reflects_nesting()
    {
        Parse("1").Depth.Should().Be(1);
        Parse("1 + 2").Depth.Should().Be(2);
        Parse("1 + 2 + 3").Depth.Should().Be(3);
    }

    [Fact]
    public void Non_deterministic_path_is_flagged()
    {
        ((AbExPath)Parse("$run.now")).IsNonDeterministic.Should().BeTrue();
        ((AbExPath)Parse("$run.attempt")).IsNonDeterministic.Should().BeFalse();
        ((AbExPath)Parse("$.now")).IsNonDeterministic.Should().BeFalse();
    }

    // ---- failures -------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Empty_expression_fails(string? expression)
        => AbExParser.Parse(expression).IsSuccess.Should().BeFalse();

    [Fact]
    public void Single_equals_names_the_mistake()
        => ParseError("$.a = 1").Message.Should().Contain("'=='");

    [Fact]
    public void Unterminated_string_fails()
        => ParseError("'abc").Message.Should().Contain("Unterminated");

    [Fact]
    public void Unclosed_paren_fails()
        => ParseError("(1 + 2").Message.Should().Contain("')'");

    [Fact]
    public void Unclosed_call_fails()
        => ParseError("len($.a").Message.Should().Contain("')'");

    [Fact]
    public void Unclosed_bracket_fails()
        => ParseError("$.a[0").Message.Should().Contain("']'");

    [Fact]
    public void Trailing_tokens_fail()
        => ParseError("1 2").Message.Should().Contain("after a complete expression");

    [Fact]
    public void Chained_comparison_is_refused()
        => ParseError("1 < 2 < 3").Message.Should().Contain("Chained comparison");

    [Fact]
    public void Bare_identifier_explains_roots()
        => ParseError("total").Message.Should().Contain("$ctx");

    [Fact]
    public void Unknown_root_is_named()
        => ParseError("$nope.a").Message.Should().Contain("Unknown root");

    [Fact]
    public void Non_literal_array_index_fails()
        => ParseError("$.a[$.i]").Message.Should().Contain("literal integer");

    [Fact]
    public void Property_after_dot_is_required()
        => ParseError("$.a.").Message.Should().Contain("property name");

    [Fact]
    public void Unexpected_character_is_reported()
        => ParseError("$.a @ 1").Message.Should().Contain("Unexpected character");

    [Fact]
    public void Expression_deeper_than_the_limit_fails()
    {
        string deep = string.Join(" + ", Enumerable.Range(0, AbExParser.MaxDepth + 5).Select(i => i.ToString()));
        ParseError(deep).Message.Should().Contain("the limit is");
    }

    [Theory]
    [InlineData("$.a = 1", 4)]
    [InlineData("1 2", 2)]
    [InlineData("(1 + 2", 6)]
    public void Errors_carry_the_offset(string expression, int expectedOffset)
        => ParseError(expression).Offset.Should().Be(expectedOffset);

    [Fact]
    public void ParseOrThrow_throws_on_bad_input()
    {
        Action act = () => AbExParser.ParseOrThrow("$.a = 1");
        act.Should().Throw<FormatException>().WithMessage("*=='*");
    }

    [Fact]
    public void ParseOrThrow_returns_the_tree_on_good_input()
        => AbExParser.ParseOrThrow("$.a").Should().BeOfType<AbExPath>();
}

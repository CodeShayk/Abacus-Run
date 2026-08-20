using System.Text.Json.Nodes;
using Abacus.Run.Dsl.Expressions;
using FluentAssertions;
using Xunit;

namespace Abacus.Run.DslTests;

public class AbExEvaluatorTests
{
    private static readonly AbExContext Sample = new(
        Data: JsonNode.Parse("""
        {
          "total": 429.5,
          "count": 3,
          "status": "settled",
          "flag": true,
          "nothing": null,
          "lines": [ { "sku": "A-1", "qty": 2 }, { "sku": "B-2", "qty": 5 } ],
          "nested": { "deep": { "value": 7 } }
        }
        """),
        Context: JsonNode.Parse("""{ "orderId": "ORD-1", "tier": "gold" }"""),
        Run: new JsonObject
        {
            ["instanceId"] = "inst-1",
            ["attempt"] = 2,
            ["now"] = "2026-08-17T00:00:00.0000000+00:00"
        });

    private static AbExValue Eval(string expression, AbExContext? context = null)
        => AbExEvaluator.Evaluate(AbExParser.ParseOrThrow(expression), context ?? Sample);

    private static bool Cond(string expression, AbExContext? context = null)
        => AbExEvaluator.EvaluateCondition(AbExParser.ParseOrThrow(expression), context ?? Sample);

    // ---- path resolution ------------------------------------------------------------------

    [Fact]
    public void Resolves_data_root() => Eval("$.total").AsNumber.Should().Be(429.5m);

    [Fact]
    public void Resolves_context_root() => Eval("$ctx.orderId").AsString.Should().Be("ORD-1");

    [Fact]
    public void Resolves_run_root() => Eval("$run.instanceId").AsString.Should().Be("inst-1");

    [Fact]
    public void Resolves_array_index() => Eval("$.lines[1].sku").AsString.Should().Be("B-2");

    [Fact]
    public void Resolves_nested_object() => Eval("$.nested.deep.value").AsNumber.Should().Be(7);

    [Fact]
    public void Bare_root_returns_the_whole_object()
        => Eval("$").Kind.Should().Be(AbExValueKind.Object);

    [Theory]
    [InlineData("$.missing")]
    [InlineData("$.nested.missing")]
    [InlineData("$.nested.deep.missing")]
    [InlineData("$.lines[9]")]
    [InlineData("$.total.deeper")]
    [InlineData("$ctx.missing")]
    public void Missing_paths_are_absent(string expression)
        => Eval(expression).IsAbsent.Should().BeTrue();

    [Fact]
    public void Json_null_is_null_not_absent()
    {
        Eval("$.nothing").Kind.Should().Be(AbExValueKind.Null);
        Eval("$.nothing").IsAbsent.Should().BeFalse();
    }

    [Fact]
    public void Path_into_a_null_root_is_absent()
        => Eval("$.a.b", new AbExContext(null, null, [])).IsAbsent.Should().BeTrue();

    // ---- strict boolean conditions --------------------------------------------------------

    [Fact]
    public void Only_true_is_true() => Cond("$.flag").Should().BeTrue();

    [Theory]
    [InlineData("$.total")]     // a number
    [InlineData("$.status")]    // a non-empty string
    [InlineData("$.count")]
    [InlineData("$.nothing")]   // json null
    [InlineData("$.missing")]   // absent
    [InlineData("$")]           // an object
    [InlineData("$.lines")]     // an array
    public void Non_booleans_are_never_true(string expression)
        => Cond(expression).Should().BeFalse();

    [Fact]
    public void Zero_and_empty_string_are_false()
    {
        Cond("0").Should().BeFalse();
        Cond("''").Should().BeFalse();
    }

    // ---- comparison -----------------------------------------------------------------------

    [Theory]
    [InlineData("$.total > 400", true)]
    [InlineData("$.total > 500", false)]
    [InlineData("$.total >= 429.5", true)]
    [InlineData("$.total < 429.5", false)]
    [InlineData("$.total <= 429.5", true)]
    [InlineData("$.count == 3", true)]
    [InlineData("$.count != 3", false)]
    [InlineData("$.count != 4", true)]
    public void Numeric_comparison(string expression, bool expected)
        => Cond(expression).Should().Be(expected);

    [Theory]
    [InlineData("$.status == 'settled'", true)]
    [InlineData("$.status == 'SETTLED'", false)]
    [InlineData("'a' < 'b'", true)]
    [InlineData("'b' < 'a'", false)]
    public void Ordinal_string_comparison(string expression, bool expected)
        => Cond(expression).Should().Be(expected);

    [Fact]
    public void Null_compares_equal_to_null() => Cond("$.nothing == null").Should().BeTrue();

    [Theory]
    [InlineData("$.status == 3")]
    [InlineData("$.count == 'three'")]
    [InlineData("$.count > 'a'")]
    [InlineData("$.flag == 1")]
    public void Cross_type_comparison_is_false(string expression)
        => Cond(expression).Should().BeFalse();

    /// <summary>
    /// Absence makes both '==' and '!=' false. A document asking whether a field it never set
    /// differs from a value must not be told "yes"; has() is how presence is asked about.
    /// </summary>
    [Theory]
    [InlineData("$.missing == 1")]
    [InlineData("$.missing != 1")]
    [InlineData("$.missing == null")]
    [InlineData("$.missing > 0")]
    [InlineData("$.missing < 0")]
    [InlineData("$.missing == $.alsoMissing")]
    public void Absence_makes_every_comparison_false(string expression)
        => Cond(expression).Should().BeFalse();

    // ---- boolean operators ----------------------------------------------------------------

    [Theory]
    [InlineData("true && true", true)]
    [InlineData("true && false", false)]
    [InlineData("false && true", false)]
    [InlineData("true || false", true)]
    [InlineData("false || false", false)]
    [InlineData("!true", false)]
    [InlineData("!false", true)]
    public void Boolean_algebra(string expression, bool expected)
        => Cond(expression).Should().Be(expected);

    [Fact]
    public void And_short_circuits_past_an_absent_right_operand()
        => Cond("false && $.missing.deep").Should().BeFalse();

    [Fact]
    public void Or_short_circuits_past_an_absent_right_operand()
        => Cond("true || $.missing.deep").Should().BeTrue();

    [Fact]
    public void Non_boolean_operand_makes_the_result_absent()
    {
        Eval("$.total && true").IsAbsent.Should().BeTrue();
        Eval("true && $.total").IsAbsent.Should().BeTrue();
        Eval("!$.total").IsAbsent.Should().BeTrue();
    }

    [Fact]
    public void Guard_pattern_works()
    {
        Cond("has($.nested) && $.nested.deep.value > 5").Should().BeTrue();
        Cond("has($.absent) && $.absent.deep.value > 5").Should().BeFalse();
    }

    // ---- arithmetic -----------------------------------------------------------------------

    [Theory]
    [InlineData("1 + 2", 3)]
    [InlineData("5 - 3", 2)]
    [InlineData("4 * 3", 12)]
    [InlineData("10 / 4", 2.5)]
    [InlineData("10 % 3", 1)]
    [InlineData("-5", -5)]
    [InlineData("$.lines[0].qty * 10", 20)]
    public void Arithmetic(string expression, decimal expected)
        => Eval(expression).AsNumber.Should().Be(expected);

    /// <summary>These documents price orders; binary floating point is the wrong default.</summary>
    [Fact]
    public void Arithmetic_is_decimal_not_binary_float()
        => Eval("0.1 + 0.2").AsNumber.Should().Be(0.3m);

    [Theory]
    [InlineData("1 / 0")]
    [InlineData("1 % 0")]
    public void Division_by_zero_is_absent(string expression)
        => Eval(expression).IsAbsent.Should().BeTrue();

    [Theory]
    [InlineData("$.status + 1")]
    [InlineData("'a' + 'b'")]        // no string concatenation: that is what templates are for
    [InlineData("$.missing + 1")]
    [InlineData("$.flag * 2")]
    public void Non_numeric_arithmetic_is_absent(string expression)
        => Eval(expression).IsAbsent.Should().BeTrue();

    // ---- functions ------------------------------------------------------------------------

    [Theory]
    [InlineData("len($.status)", 7)]
    [InlineData("len($.lines)", 2)]
    [InlineData("len($)", 7)]
    [InlineData("len($.missing)", 0)]
    [InlineData("len($.total)", 0)]
    public void Len(string expression, int expected)
        => Eval(expression).AsNumber.Should().Be(expected);

    [Theory]
    [InlineData("has($.total)", true)]
    [InlineData("has($.missing)", false)]
    [InlineData("has($.nothing)", true)]     // a json null is present
    [InlineData("has($.lines[1])", true)]
    [InlineData("has($.lines[9])", false)]
    public void Has(string expression, bool expected)
        => Cond(expression).Should().Be(expected);

    [Fact]
    public void Case_folding()
    {
        Eval("lower($.status)").AsString.Should().Be("settled");
        Eval("upper($.status)").AsString.Should().Be("SETTLED");
        Eval("lower($.total)").IsAbsent.Should().BeTrue();
    }

    [Theory]
    [InlineData("contains($.status, 'ettl')", true)]
    [InlineData("contains($.status, 'xyz')", false)]
    [InlineData("startsWith($.status, 'set')", true)]
    [InlineData("startsWith($.status, 'Set')", false)]
    [InlineData("endsWith($.status, 'led')", true)]
    public void String_tests(string expression, bool expected)
        => Cond(expression).Should().Be(expected);

    [Theory]
    [InlineData("matches($.lines[0].sku, '^[A-Z]-\\\\d+$')", true)]
    [InlineData("matches($.status, '^\\\\d+$')", false)]
    public void Matches(string expression, bool expected)
        => Cond(expression).Should().Be(expected);

    [Fact]
    public void Matches_with_an_invalid_pattern_is_absent()
        => Eval("matches($.status, '[')").IsAbsent.Should().BeTrue();

    [Fact]
    public void Coalesce_takes_the_first_present_non_null()
    {
        Eval("coalesce($.missing, $.nothing, $.status)").AsString.Should().Be("settled");
        Eval("coalesce($.total, $.status)").AsNumber.Should().Be(429.5m);
        Eval("coalesce($.missing, $.alsoMissing)").IsAbsent.Should().BeTrue();
    }

    [Fact]
    public void Coercion()
    {
        Eval("number('42.5')").AsNumber.Should().Be(42.5m);
        Eval("number($.total)").AsNumber.Should().Be(429.5m);
        Eval("number('abc')").IsAbsent.Should().BeTrue();
        Eval("number($.flag)").IsAbsent.Should().BeTrue();

        Eval("string($.total)").AsString.Should().Be("429.5");
        Eval("string($.count)").AsString.Should().Be("3");
        Eval("string($.flag)").AsString.Should().Be("true");
        Eval("string($.missing)").IsAbsent.Should().BeTrue();

        Eval("bool('true')").AsBoolean.Should().BeTrue();
        Eval("bool($.flag)").AsBoolean.Should().BeTrue();
        Eval("bool('yes')").IsAbsent.Should().BeTrue();
    }

    [Fact]
    public void Unknown_function_evaluates_to_absent()
    {
        // Unreachable through a validated document; absent keeps the evaluator total if it happens.
        var call = new AbExCall("nope", [new AbExLiteral(AbExValue.Number(1))]);
        AbExEvaluator.Evaluate(call, Sample).IsAbsent.Should().BeTrue();
    }

    [Fact]
    public void Wrong_arity_evaluates_to_absent()
    {
        var call = new AbExCall("len", []);
        AbExEvaluator.Evaluate(call, Sample).IsAbsent.Should().BeTrue();
    }

    // ---- totality -------------------------------------------------------------------------

    [Theory]
    [InlineData("$.a.b.c.d.e")]
    [InlineData("$.lines[99].sku.deeper")]
    [InlineData("len($.missing) / 0")]
    [InlineData("upper($.lines) + $.nothing")]
    [InlineData("!$.missing && $.missing > $.missing")]
    public void Evaluation_never_throws(string expression)
    {
        Action act = () => Eval(expression);
        act.Should().NotThrow();
    }

    [Fact]
    public void Evaluation_over_an_empty_context_never_throws()
    {
        foreach (string expression in new[]
                 { "$.a", "$ctx.a", "$run.a", "len($)", "$ + 1", "has($.x)", "$.a == $.b" })
        {
            Action act = () => Eval(expression, AbExContext.Empty);
            act.Should().NotThrow($"'{expression}' must be total");
        }
    }

    // ---- rendering ------------------------------------------------------------------------

    [Theory]
    [InlineData("$.total", "429.5")]
    [InlineData("$.count", "3")]
    [InlineData("$.status", "settled")]
    [InlineData("$.flag", "true")]
    [InlineData("$.nothing", "")]
    [InlineData("$.missing", "")]
    [InlineData("1 + 2", "3")]
    [InlineData("10 / 4", "2.5")]
    public void ToText_renders_for_templates(string expression, string expected)
        => Eval(expression).ToText().Should().Be(expected);

    [Fact]
    public void Trailing_zeros_are_trimmed()
        => AbExValue.Number(1.50m).ToText().Should().Be("1.5");

    // ---- value classification -------------------------------------------------------------

    /// <summary>
    /// A JsonValue holds whatever the writer put in it. Probing CLR types in turn used to fail for
    /// an int-backed value — JsonValue.Create(200) will not hand back a decimal — and fall through
    /// to the string branch, so an HTTP status of 200 compared as "200" and never equalled 200.
    /// </summary>
    [Fact]
    public void Numbers_are_read_whatever_clr_type_backs_them()
    {
        var data = new JsonObject
        {
            ["fromInt"] = JsonValue.Create(200),
            ["fromLong"] = JsonValue.Create(11L),
            ["fromDouble"] = JsonValue.Create(1.5d),
            ["fromDecimal"] = JsonValue.Create(429.5m),
            ["fromFloat"] = JsonValue.Create(2.5f)
        };

        var context = new AbExContext(data, null, []);

        foreach (string field in new[] { "fromInt", "fromLong", "fromDouble", "fromDecimal", "fromFloat" })
        {
            Eval($"$.{field}", context).Kind.Should().Be(AbExValueKind.Number, $"{field} is a number");
        }

        Eval("$.fromInt", context).AsNumber.Should().Be(200m);
        Eval("$.fromLong", context).AsNumber.Should().Be(11m);
        Eval("$.fromDecimal", context).AsNumber.Should().Be(429.5m);
        Cond("$.fromInt == 200", context).Should().BeTrue();
        Cond("$.fromInt > 199", context).Should().BeTrue();
    }

    [Fact]
    public void Booleans_and_strings_are_read_whatever_backs_them()
    {
        var data = new JsonObject
        {
            ["flag"] = JsonValue.Create(true),
            ["name"] = JsonValue.Create("abc")
        };

        var context = new AbExContext(data, null, []);

        Eval("$.flag", context).Kind.Should().Be(AbExValueKind.Boolean);
        Cond("$.flag", context).Should().BeTrue();
        Eval("$.name", context).Kind.Should().Be(AbExValueKind.String);
        Eval("$.name", context).AsString.Should().Be("abc");
    }

    /// <summary>Values written by a node must read back the same way after a JSON round trip.</summary>
    [Fact]
    public void Classification_survives_a_serialization_round_trip()
    {
        var original = new JsonObject { ["status"] = JsonValue.Create(200) };
        JsonNode? reparsed = JsonNode.Parse(original.ToJsonString());

        Eval("$.status", new AbExContext(original, null, [])).AsNumber.Should().Be(200m);
        Eval("$.status", new AbExContext(reparsed, null, [])).AsNumber.Should().Be(200m);
    }
}

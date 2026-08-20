using System.Text.Json;
using System.Text.Json.Nodes;
using Abacus.Run.Dsl.Expressions;
using Abacus.Run.Dsl.Interpretation;
using Abacus.Run.Executors;
using FluentAssertions;
using Xunit;

namespace Abacus.Run.DslTests;

public class DslMessageTests
{
    private static DslMessage Sample()
    {
        JsonNode ctx = JsonNode.Parse("""{ "orderId": "ORD-1", "tier": "gold" }""")!;
        return new DslMessage(
            ctx,
            JsonNode.Parse("""{ "total": 429.5, "status": "settled" }"""),
            new DslMeta("price", 2, 1),
            DslMessage.RunMetadata("inst-1", "t-1", "order", "1.0.0", 1, 2,
                DateTimeOffset.Parse("2026-08-17T00:00:00Z")));
    }

    [Fact]
    public void Start_seeds_data_from_the_context()
    {
        using JsonDocument document = JsonDocument.Parse("""{ "orderId": "ORD-9" }""");
        DslMessage message = DslMessage.Start(document.RootElement);

        message.Ctx!.ToJsonString().Should().Contain("ORD-9");
        message.Data!.ToJsonString().Should().Contain("ORD-9");
    }

    /// <summary>
    /// A node reading '$' on the first step must not see the context mutate under it when a later
    /// node replaces data.
    /// </summary>
    [Fact]
    public void Start_clones_data_so_it_is_not_the_context_instance()
    {
        JsonNode ctx = JsonNode.Parse("""{ "a": 1 }""")!;
        DslMessage message = DslMessage.Start(ctx);

        message.Data.Should().NotBeSameAs(message.Ctx);
    }

    [Fact]
    public void WithData_carries_ctx_meta_and_run_through()
    {
        DslMessage original = Sample();
        DslMessage next = original.WithData(JsonNode.Parse("""{ "total": 1 }"""));

        next.Ctx.Should().BeSameAs(original.Ctx);
        next.Meta.Should().BeSameAs(original.Meta);
        next.Run.Should().BeSameAs(original.Run);
        next.Data!.ToJsonString().Should().Contain("\"total\":1");
    }

    [Fact]
    public void Ctx_survives_an_arbitrary_number_of_hops()
    {
        DslMessage message = Sample();
        for (int i = 0; i < 25; i++)
        {
            message = message.WithData(JsonNode.Parse($$"""{ "step": {{i}} }"""));
        }

        AbExValue value = AbExEvaluator.Evaluate(
            AbExParser.ParseOrThrow("$ctx.orderId"), message.ToExpressionContext(message.Run));

        value.AsString.Should().Be("ORD-1");
    }

    [Fact]
    public void Run_metadata_exposes_the_documented_fields()
    {
        JsonObject run = DslMessage.RunMetadata(
            "inst", "tenant", "wf", "2.0.0", 3, 4, DateTimeOffset.Parse("2026-08-17T10:11:12Z"));

        run["instanceId"]!.GetValue<string>().Should().Be("inst");
        run["tenantId"]!.GetValue<string>().Should().Be("tenant");
        run["workflow"]!.GetValue<string>().Should().Be("wf");
        run["version"]!.GetValue<string>().Should().Be("2.0.0");
        run["attempt"]!.GetValue<int>().Should().Be(3);
        run["superstep"]!.GetValue<int>().Should().Be(4);
        run["now"]!.GetValue<string>().Should().StartWith("2026-08-17");
    }

    // ---- templates ------------------------------------------------------------------------

    private static string Render(string template, DslMessage message)
        => TemplateEngine.Render(template, TemplateBindings.From(message));

    [Theory]
    [InlineData("{{ $ctx.orderId }}", "ORD-1")]
    [InlineData("{{ $.total }}", "429.5")]
    [InlineData("{{ $.status }}", "settled")]
    [InlineData("{{ $run.instanceId }}", "inst-1")]
    [InlineData("{{ $run.attempt }}", "1")]
    public void Templates_resolve_every_root(string template, string expected)
        => Render(template, Sample()).Should().Be(expected);

    [Fact]
    public void Templates_evaluate_full_expressions()
        => Render("{{ $.total * 2 }}", Sample()).Should().Be("859");

    [Fact]
    public void Templates_interpolate_into_surrounding_text()
        => Render("order {{ $ctx.orderId }} totals {{ $.total }} USD", Sample())
            .Should().Be("order ORD-1 totals 429.5 USD");

    [Fact]
    public void Templates_build_json_bodies()
        => Render("""{"order":"{{ $ctx.orderId }}","amount":{{ $.total }}}""", Sample())
            .Should().Be("""{"order":"ORD-1","amount":429.5}""");

    [Theory]
    [InlineData("{{ $.missing }}")]
    [InlineData("{{ $ctx.missing }}")]
    [InlineData("{{ $.a.b.c }}")]
    public void Absent_placeholders_render_empty(string template)
        => Render(template, Sample()).Should().BeEmpty();

    [Fact]
    public void A_malformed_placeholder_renders_empty_rather_than_throwing()
    {
        Action act = () => Render("{{ $.a = 1 }}", Sample());
        act.Should().NotThrow();
        Render("x{{ $.a = 1 }}y", Sample()).Should().Be("xy");
    }

    [Fact]
    public void A_template_with_no_placeholders_is_returned_unchanged()
        => Render("https://ledger.internal/v1/settlements", Sample())
            .Should().Be("https://ledger.internal/v1/settlements");

    [Fact]
    public void Unterminated_placeholders_are_emitted_verbatim()
        => Render("a {{ $.total", Sample()).Should().Be("a {{ $.total");

    /// <summary>
    /// The hook is opt-in. A plain POCO context must still resolve by dotted path exactly as it did
    /// before ITemplateBindingSource existed.
    /// </summary>
    [Fact]
    public void Non_dsl_messages_still_resolve_by_dotted_path()
    {
        var poco = new { Invoice = new { Id = "INV-7" } };

        TemplateEngine.Render("{{ context.Invoice.Id }}", TemplateBindings.From(poco))
            .Should().Be("INV-7");
        TemplateEngine.Render("{{ Invoice.Id }}", TemplateBindings.From(poco))
            .Should().Be("INV-7");
    }
}

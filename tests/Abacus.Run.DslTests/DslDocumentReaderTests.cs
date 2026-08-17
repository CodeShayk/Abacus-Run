using System.Text.Json.Nodes;
using Abacus.Run.Dsl.Model;
using Abacus.Run.Dsl.Validation;
using FluentAssertions;
using Xunit;

namespace Abacus.Run.DslTests;

public class DslDocumentReaderTests
{
    private static DslDocument Read(string text) => DslParser.ParseOrThrow(text);

    [Fact]
    public void Reads_document_level_fields()
    {
        DslDocument document = Read(DslFixtures.FullText);

        document.Dsl.Should().Be("abacus.workflow/1.0");
        document.Name.Should().Be("order-settlement");
        document.Version.Should().Be("1.2.0");
        document.Description.Should().Be("Prices an order, escalates large ones, settles.");
        document.Start.Should().Be("validate");
        document.Output.Should().Equal("complete");
        document.ContextSchema.Should().NotBeNull();
        document.Hash.Should().MatchRegex("^[0-9a-f]{64}$");
        document.MajorVersion.Should().Be(1);
    }

    [Theory]
    [InlineData("abacus.workflow/1.0", 1)]
    [InlineData("abacus.workflow/2.3", 2)]
    [InlineData("abacus.workflow/10.0", 10)]
    [InlineData("nonsense", -1)]
    [InlineData("abacus.workflow/", -1)]
    public void Major_version_is_extracted(string dsl, int expected)
        => (Read(DslFixtures.MinimalText) with { Dsl = dsl }).MajorVersion.Should().Be(expected);

    [Fact]
    public void Reads_node_pointers_in_declaration_order()
    {
        DslDocument document = Read(DslFixtures.FullText);

        document.Nodes.Select(n => n.Pointer).Should().Equal("/nodes/0", "/nodes/1", "/nodes/2");
        document.Nodes.Select(n => n.Id).Should().Equal("validate", "settle", "complete");
    }

    [Fact]
    public void Reads_a_transform_node()
    {
        var node = (DslTransformNode)Read(DslFixtures.FullText).FindNode("validate")!;

        node.Set.Should().ContainKey("total").WhoseValue.Should().Be("$ctx.amount");
        node.Replace.Should().BeFalse();
    }

    [Fact]
    public void Reads_an_http_node_with_its_gate()
    {
        var node = (DslHttpNode)Read(DslFixtures.FullText).FindNode("settle")!;

        node.Method.Should().Be("POST");
        node.Url.Should().Be("https://ledger.internal/v1/settlements");
        node.AllowedHosts.Should().Equal("ledger.internal");
        node.Body.Should().Contain("{{ $ctx.orderId }}");
        node.TimeoutSeconds.Should().Be(30);
        node.SendIdempotencyKey.Should().BeTrue();

        DslGate gate = node.Gate!;
        gate.Mode.Should().Be("conditional");
        gate.When.Should().Be("$.total > 25000");
        gate.Reason.Should().Be("RegulatedSettlement");
        gate.AssignTo.Should().Equal("group:finance", "user:cfo");
        gate.RequireApprovers.Should().Be(2);
        gate.ExpiresAfter.Should().Be(TimeSpan.FromHours(8));
        gate.OnExpiryAction.Should().Be("escalate");
        gate.EscalateTo.Should().Equal("group:exec");
        gate.AllowModification.Should().BeTrue();
        gate.RequireSegregationOfDuties.Should().BeTrue();
        gate.Locked.Should().BeTrue();
        gate.Pointer.Should().Be("/nodes/1/gate");
    }

    [Fact]
    public void Gate_defaults_apply_when_unstated()
    {
        string text = DslFixtures.Broken(d =>
            DslFixtures.Node(d, 0)["gate"] = new JsonObject { ["mode"] = "requireApproval" });

        DslGate gate = Read(text).Nodes[0].Gate!;

        gate.RequireApprovers.Should().Be(1);
        gate.ExpiresAfter.Should().Be(TimeSpan.FromHours(24));
        gate.OnExpiryAction.Should().Be("deadStop");
        gate.Locked.Should().BeFalse();
    }

    [Theory]
    [InlineData("PT30S", 30)]
    [InlineData("PT5M", 300)]
    [InlineData("PT8H", 28800)]
    [InlineData("P3D", 259200)]
    public void Reads_iso8601_durations(string iso, int expectedSeconds)
    {
        string text = DslFixtures.Broken(d =>
        {
            ((JsonArray)d["nodes"]!).Add(new JsonObject
            {
                ["id"] = "wait", ["kind"] = "delay", ["for"] = iso
            });
            ((JsonArray)d["edges"]!).Add(new JsonObject { ["from"] = "b", ["to"] = "wait" });
            d["output"] = new JsonArray("wait");
        });

        var node = (DslDelayNode)Read(text).FindNode("wait")!;
        node.For.Should().Be(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Fact]
    public void Reads_edges_including_barriers_and_fan_out()
    {
        string text = DslFixtures.Broken(d =>
        {
            ((JsonArray)d["nodes"]!).Add(new JsonObject
            {
                ["id"] = "c", ["kind"] = "transform", ["set"] = new JsonObject { ["x"] = "1" }
            });
            ((JsonArray)d["nodes"]!).Add(new JsonObject
            {
                ["id"] = "join", ["kind"] = "fan-in", ["into"] = "results"
            });
            // Replaced rather than appended: the fixture's own a->b plus a fan-out a->[b,c] would be
            // two unconditional edges between the same pair, which DSL0208 rightly refuses.
            d["edges"] = new JsonArray(
                new JsonObject { ["from"] = "a", ["to"] = new JsonArray("b", "c"), ["label"] = "split" },
                new JsonObject { ["from"] = new JsonArray("b", "c"), ["to"] = "join" });
            d["output"] = new JsonArray("join");
        });

        DslDocument document = Read(text);

        DslEdge fanOut = document.Edges[0];
        fanOut.IsFanOut.Should().BeTrue();
        fanOut.IsBarrier.Should().BeFalse();
        fanOut.From.Should().Equal("a");
        fanOut.To.Should().Equal("b", "c");
        fanOut.Label.Should().Be("split");

        DslEdge barrier = document.Edges[1];
        barrier.IsBarrier.Should().BeTrue();
        barrier.IsFanOut.Should().BeFalse();
        barrier.From.Should().Equal("b", "c");
        barrier.To.Should().Equal("join");
        barrier.Pointer.Should().Be("/edges/1");
    }

    [Fact]
    public void Reads_triggers_notifications_failure_rules_and_audit()
    {
        DslDocument document = Read(DslFixtures.FullText);

        document.Triggers.Should().ContainSingle();
        document.Triggers[0].Topic.Should().Be("orders.placed");
        document.Triggers[0].CorrelationKey.Should().Be("$.orderId");

        document.Notifications!.Level.Should().Be("standard");
        document.Notifications.Stream.Should().BeTrue();
        document.Notifications.Emits.Should().Equal("priced");

        document.OnFailure.Should().ContainSingle();
        document.OnFailure[0].Exception.Should().Be("ApiCallFailureException");
        document.OnFailure[0].Status.Should().Be("5xx");
        document.OnFailure[0].Disposition.Should().Be("retry");

        document.Audit!.Key.Should().Be("$ctx.orderId");
        document.Audit.Sections.Should().Equal("submission", "outcome");

        document.Limits.MaxAttempts.Should().Be(5);
    }

    [Fact]
    public void Expressions_are_enumerated_with_pointers()
    {
        DslDocument document = Read(DslFixtures.FullText);
        DslNode settle = document.FindNode("settle")!;

        settle.Expressions().Should().Contain(e => e.Pointer == "/nodes/1/gate/when");

        DslNode validate = document.FindNode("validate")!;
        validate.Expressions().Should().Contain(e =>
            e.Pointer == "/nodes/0/set/total" && e.Expression == "$ctx.amount");
    }

    [Fact]
    public void Templates_are_enumerated_with_pointers()
    {
        DslNode settle = Read(DslFixtures.FullText).FindNode("settle")!;

        settle.Templates().Select(t => t.Pointer).Should().Contain("/nodes/1/url");
        settle.Templates().Select(t => t.Pointer).Should().Contain("/nodes/1/body");
    }

    [Fact]
    public void Pointer_segments_are_escaped()
    {
        JsonPointer.Escape("a/b").Should().Be("a~1b");
        JsonPointer.Escape("a~b").Should().Be("a~0b");
        JsonPointer.Escape("plain").Should().Be("plain");
    }

    [Fact]
    public void FindNode_returns_null_for_an_unknown_id()
        => Read(DslFixtures.MinimalText).FindNode("nope").Should().BeNull();
}

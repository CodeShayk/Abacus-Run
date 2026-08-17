using System.Text.Json.Nodes;
using Abacus.Run.Dsl.Model;
using Abacus.Run.Dsl.Validation;
using FluentAssertions;
using Xunit;

namespace Abacus.Run.DslTests;

public class DslValidationTests
{
    private static DslParseResult Parse(string text, DslEnvironment? environment = null)
        => DslParser.Parse(text, environment);

    /// <summary>An environment with nothing registered, so the environment checks actually run.</summary>
    private static DslEnvironment BareEnvironment(bool enforceEgress = false)
        => new() { EnforceEgress = enforceEgress };

    // ---- valid documents --------------------------------------------------------------------

    [Fact]
    public void Minimal_document_is_valid() => Parse(DslFixtures.MinimalText).ShouldBeClean();

    [Fact]
    public void The_designs_worked_example_is_valid()
        => Parse(DslFixtures.FullText, BareEnvironment()).ShouldBeClean();

    [Theory]
    [InlineData("""{ "id": "n", "kind": "transform", "set": { "x": "1" } }""")]
    [InlineData("""{ "id": "n", "kind": "http", "url": "https://x.internal/a", "allowedHosts": ["x.internal"] }""")]
    [InlineData("""{ "id": "n", "kind": "llm", "model": "gpt", "prompt": "Summarise {{ $.text }}" }""")]
    [InlineData("""{ "id": "n", "kind": "delay", "for": "PT5M" }""")]
    [InlineData("""{ "id": "n", "kind": "approval" }""")]
    [InlineData("""{ "id": "n", "kind": "publish", "topic": "orders.placed", "correlationKey": "$.id" }""")]
    [InlineData("""{ "id": "n", "kind": "wait-event", "topic": "payment.settled", "timeout": "P3D" }""")]
    [InlineData("""{ "id": "n", "kind": "fan-in", "into": "results" }""")]
    public void Every_node_kind_parses(string nodeJson)
    {
        string text = DslFixtures.Broken(document =>
        {
            var node = (JsonObject)JsonNode.Parse(nodeJson)!;
            ((JsonArray)document["nodes"]!).Add(node);
            ((JsonArray)document["edges"]!).Add(new JsonObject { ["from"] = "b", ["to"] = "n" });
            document["output"] = new JsonArray("n");
        });

        Parse(text, BareEnvironment()).ShouldBeClean();
    }

    [Fact]
    public void Custom_node_parses_when_registered()
    {
        string text = DslFixtures.Broken(document =>
        {
            ((JsonArray)document["nodes"]!).Add(new JsonObject
            {
                ["id"] = "score",
                ["kind"] = "custom",
                ["node"] = "score-risk",
                ["with"] = new JsonObject { ["threshold"] = 0.8 }
            });
            ((JsonArray)document["edges"]!).Add(new JsonObject { ["from"] = "b", ["to"] = "score" });
            document["output"] = new JsonArray("score");
        });

        var environment = new DslEnvironment
        {
            EnforceEgress = false,
            CustomNodes = new Dictionary<string, JsonNode?> { ["score-risk"] = null }
        };

        Parse(text, environment).ShouldBeClean();
    }

    // ---- DSL01xx: identity ------------------------------------------------------------------

    [Fact]
    public void Malformed_json_is_reported()
    {
        DslParseResult result = Parse("{ not json");
        result.ShouldReport(DslCodes.MalformedJson).Pointer.Should().BeEmpty();
    }

    [Fact]
    public void A_json_array_is_not_a_document()
        => Parse("[]").ShouldReport(DslCodes.MalformedJson);

    [Fact]
    public void Unsupported_dsl_version_is_reported()
    {
        // Schema-valid shape but a major version this interpreter does not read.
        string text = DslFixtures.Broken(d => d["dsl"] = "abacus.workflow/1.0");
        var document = (JsonObject)JsonNode.Parse(text)!;
        DslDocument model = DslDocumentReader.Read(document) with { Dsl = "abacus.workflow/2.0" };

        DslValidationResult result = DslSemanticValidator.Validate(model);
        result.Has(DslCodes.UnsupportedDslVersion).Should().BeTrue();
    }

    [Fact]
    public void Wrong_dsl_constant_fails_the_schema()
        => Parse(DslFixtures.Broken(d => d["dsl"] = "abacus.workflow/9.9"))
            .ShouldReport(DslCodes.SchemaViolation).Pointer.Should().Be("/dsl");

    [Fact]
    public void Hash_conflict_is_reported()
    {
        DslParseResult first = Parse(DslFixtures.MinimalText, BareEnvironment());
        string differentText = DslFixtures.Broken(d => d["description"] = "changed");

        var environment = new DslEnvironment
        {
            EnforceEgress = false,
            PublishedHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["minimal@1.0.0"] = first.Document!.Hash
            }
        };

        DslDiagnostic diagnostic = Parse(differentText, environment).ShouldReport(DslCodes.HashConflict);
        diagnostic.Pointer.Should().Be("/version");
        diagnostic.Suggestion.Should().Contain("immutable");
    }

    [Fact]
    public void The_same_document_does_not_conflict_with_itself()
    {
        DslParseResult first = Parse(DslFixtures.MinimalText, BareEnvironment());

        var environment = new DslEnvironment
        {
            EnforceEgress = false,
            PublishedHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["minimal@1.0.0"] = first.Document!.Hash
            }
        };

        Parse(DslFixtures.MinimalText, environment).ShouldBeClean();
    }

    // ---- DSL02xx: references ----------------------------------------------------------------

    [Fact]
    public void Duplicate_node_id_is_reported()
    {
        string text = DslFixtures.Broken(d => DslFixtures.Node(d, 1)["id"] = "a");
        DslParseResult result = Parse(text);

        result.ShouldReport(DslCodes.DuplicateNodeId).Pointer.Should().Be("/nodes/1/id");
    }

    [Fact]
    public void Unknown_start_is_reported()
    {
        DslDiagnostic diagnostic = Parse(DslFixtures.Broken(d => d["start"] = "nope"))
            .ShouldReport(DslCodes.StartNotFound);

        diagnostic.Pointer.Should().Be("/start");
    }

    [Fact]
    public void Unknown_output_is_reported()
        => Parse(DslFixtures.Broken(d => d["output"] = new JsonArray("nope")))
            .ShouldReport(DslCodes.OutputNotFound).Pointer.Should().Be("/output/0");

    [Fact]
    public void Unknown_edge_target_is_reported_with_a_suggestion()
    {
        DslDiagnostic diagnostic = Parse(DslFixtures.Broken(d => DslFixtures.Edge(d, 0)["to"] = "bb"))
            .ShouldReport(DslCodes.EdgeEndpointNotFound);

        diagnostic.Pointer.Should().Be("/edges/0/to");
        diagnostic.Suggestion.Should().Contain("'b'");
    }

    [Fact]
    public void Unknown_edge_source_is_reported()
        => Parse(DslFixtures.Broken(d => DslFixtures.Edge(d, 0)["from"] = "zzz"))
            .ShouldReport(DslCodes.EdgeEndpointNotFound).Pointer.Should().Be("/edges/0/from");

    [Fact]
    public void Fan_out_endpoints_report_the_element_pointer()
    {
        string text = DslFixtures.Broken(d =>
            DslFixtures.Edge(d, 0)["to"] = new JsonArray("b", "missing"));

        Parse(text).ShouldReport(DslCodes.EdgeEndpointNotFound).Pointer.Should().Be("/edges/0/to/1");
    }

    [Fact]
    public void Duplicate_unconditional_edge_is_reported()
    {
        string text = DslFixtures.Broken(d =>
            ((JsonArray)d["edges"]!).Add(new JsonObject { ["from"] = "a", ["to"] = "b" }));

        Parse(text).ShouldReport(DslCodes.DuplicateEdge).Pointer.Should().Be("/edges/1");
    }

    [Fact]
    public void Duplicate_edge_is_allowed_when_marked_idempotent()
    {
        string text = DslFixtures.Broken(d =>
            ((JsonArray)d["edges"]!).Add(new JsonObject
            {
                ["from"] = "a", ["to"] = "b", ["idempotent"] = true
            }));

        Parse(text).ShouldBeClean();
    }

    /// <summary>Two conditional edges between the same pair is how a branch with a fallback is written.</summary>
    [Fact]
    public void Two_conditional_edges_between_the_same_pair_are_allowed()
    {
        string text = DslFixtures.Broken(d =>
        {
            DslFixtures.Edge(d, 0)["when"] = "$.x > 1";
            ((JsonArray)d["edges"]!).Add(new JsonObject
            {
                ["from"] = "a", ["to"] = "b", ["when"] = "$.x <= 1"
            });
        });

        Parse(text).ShouldBeClean();
    }

    // ---- DSL03xx: graph shape ---------------------------------------------------------------

    [Fact]
    public void Unreachable_node_is_a_warning()
    {
        string text = DslFixtures.Broken(d =>
            ((JsonArray)d["nodes"]!).Add(new JsonObject
            {
                ["id"] = "orphan",
                ["kind"] = "transform",
                ["set"] = new JsonObject { ["x"] = "1" }
            }));

        DslDiagnostic diagnostic = Parse(text)
            .ShouldReport(DslCodes.UnreachableNode, DslSeverity.Warning);

        diagnostic.Pointer.Should().Be("/nodes/2");
        Parse(text).IsValid.Should().BeTrue("an unreachable node is a warning, not an error");
    }

    [Fact]
    public void Dead_end_node_is_a_warning()
    {
        string text = DslFixtures.Broken(d =>
        {
            ((JsonArray)d["nodes"]!).Add(new JsonObject
            {
                ["id"] = "c", ["kind"] = "transform", ["set"] = new JsonObject { ["x"] = "1" }
            });
            ((JsonArray)d["edges"]!).Add(new JsonObject { ["from"] = "b", ["to"] = "c" });
        });

        Parse(text).ShouldReport(DslCodes.DeadEndNode, DslSeverity.Warning);
    }

    [Fact]
    public void A_cycle_with_nothing_that_yields_is_refused()
    {
        string text = DslFixtures.Broken(d =>
            ((JsonArray)d["edges"]!).Add(new JsonObject { ["from"] = "b", ["to"] = "a" }));

        DslDiagnostic diagnostic = Parse(text).ShouldReport(DslCodes.TightCycle);
        diagnostic.Message.Should().Contain("nothing that yields");
        diagnostic.Suggestion.Should().Contain("delay");
    }

    [Theory]
    [InlineData("delay", """{ "id": "wait", "kind": "delay", "for": "PT1M" }""")]
    [InlineData("wait-event", """{ "id": "wait", "kind": "wait-event", "topic": "x.y" }""")]
    [InlineData("approval", """{ "id": "wait", "kind": "approval" }""")]
    public void A_cycle_is_allowed_when_something_on_it_yields(string _, string nodeJson)
    {
        string text = DslFixtures.Broken(d =>
        {
            ((JsonArray)d["nodes"]!).Add((JsonObject)JsonNode.Parse(nodeJson)!);
            ((JsonArray)d["edges"]!).Add(new JsonObject { ["from"] = "b", ["to"] = "wait" });
            ((JsonArray)d["edges"]!).Add(new JsonObject { ["from"] = "wait", ["to"] = "a" });
        });

        Parse(text).Validation.Has(DslCodes.TightCycle).Should().BeFalse();
    }

    [Fact]
    public void Unreachable_barrier_source_is_refused()
    {
        string text = DslFixtures.Broken(d =>
        {
            ((JsonArray)d["nodes"]!).Add(new JsonObject
            {
                ["id"] = "orphan", ["kind"] = "transform", ["set"] = new JsonObject { ["x"] = "1" }
            });
            ((JsonArray)d["nodes"]!).Add(new JsonObject
            {
                ["id"] = "join", ["kind"] = "fan-in", ["into"] = "results"
            });
            ((JsonArray)d["edges"]!).Add(new JsonObject
            {
                ["from"] = new JsonArray("b", "orphan"), ["to"] = "join"
            });
            d["output"] = new JsonArray("join");
        });

        DslDiagnostic diagnostic = Parse(text).ShouldReport(DslCodes.BarrierSourceUnreachable);
        diagnostic.Message.Should().Contain("never release");
    }

    // ---- DSL04xx: expressions ---------------------------------------------------------------

    [Fact]
    public void Unparseable_expression_is_reported_at_its_pointer()
    {
        DslDiagnostic diagnostic = Parse(DslFixtures.Broken(d => DslFixtures.Edge(d, 0)["when"] = "$.a = 1"))
            .ShouldReport(DslCodes.ExpressionParseError);

        diagnostic.Pointer.Should().Be("/edges/0/when");
        diagnostic.Message.Should().Contain("'=='");
    }

    [Fact]
    public void Unparseable_transform_expression_points_at_the_key()
    {
        string text = DslFixtures.Broken(d =>
            DslFixtures.Node(d, 0)["set"] = new JsonObject { ["total"] = "$.a +" });

        Parse(text).ShouldReport(DslCodes.ExpressionParseError).Pointer.Should().Be("/nodes/0/set/total");
    }

    [Fact]
    public void Unknown_function_is_reported_with_a_suggestion()
    {
        DslDiagnostic diagnostic = Parse(DslFixtures.Broken(d => DslFixtures.Edge(d, 0)["when"] = "lenn($.a) > 0"))
            .ShouldReport(DslCodes.UnknownFunction);

        diagnostic.Pointer.Should().Be("/edges/0/when");
        diagnostic.Suggestion.Should().Contain("len");
    }

    /// <summary>
    /// The rule that keeps a resumed run on the branch its checkpoint recorded.
    /// </summary>
    [Fact]
    public void Non_deterministic_edge_condition_is_refused()
    {
        DslDiagnostic diagnostic = Parse(DslFixtures.Broken(d => DslFixtures.Edge(d, 0)["when"] = "$run.now > '2020'"))
            .ShouldReport(DslCodes.NonDeterministicCondition);

        diagnostic.Pointer.Should().Be("/edges/0/when");
        diagnostic.Suggestion.Should().Contain("checkpoint");
    }

    [Fact]
    public void Non_deterministic_gate_predicate_is_refused()
    {
        string text = DslFixtures.Broken(d => DslFixtures.Node(d, 1)["gate"] = new JsonObject
        {
            ["mode"] = "conditional",
            ["when"] = "$run.now != null"
        });

        Parse(text).ShouldReport(DslCodes.NonDeterministicCondition)
            .Pointer.Should().Be("/nodes/1/gate/when");
    }

    /// <summary>Templates are rendered, not routed on, so the clock is fair game there.</summary>
    [Fact]
    public void Non_deterministic_template_is_allowed()
    {
        string text = DslFixtures.Broken(d =>
        {
            ((JsonArray)d["nodes"]!).Add(new JsonObject
            {
                ["id"] = "call",
                ["kind"] = "http",
                ["url"] = "https://x.internal/at/{{ $run.now }}",
                ["allowedHosts"] = new JsonArray("x.internal")
            });
            ((JsonArray)d["edges"]!).Add(new JsonObject { ["from"] = "b", ["to"] = "call" });
            d["output"] = new JsonArray("call");
        });

        Parse(text, BareEnvironment()).ShouldBeClean();
    }

    [Fact]
    public void Broken_template_placeholder_is_reported()
    {
        string text = DslFixtures.Broken(d =>
        {
            ((JsonArray)d["nodes"]!).Add(new JsonObject
            {
                ["id"] = "call",
                ["kind"] = "http",
                ["url"] = "https://x.internal/{{ nope($.a) }}",
                ["allowedHosts"] = new JsonArray("x.internal")
            });
            ((JsonArray)d["edges"]!).Add(new JsonObject { ["from"] = "b", ["to"] = "call" });
            d["output"] = new JsonArray("call");
        });

        DslDiagnostic diagnostic = Parse(text, BareEnvironment()).ShouldReport(DslCodes.UnknownFunction);
        diagnostic.Pointer.Should().Be("/nodes/2/url");
        diagnostic.Message.Should().Contain("placeholder");
    }

    [Fact]
    public void Expression_deeper_than_the_policy_is_reported()
    {
        string deep = string.Join(" + ", Enumerable.Range(1, 12).Select(i => i.ToString()));
        string text = DslFixtures.Broken(d =>
            DslFixtures.Node(d, 0)["set"] = new JsonObject { ["x"] = deep });

        var environment = new DslEnvironment
        {
            EnforceEgress = false,
            Policy = DslPolicy.Default with { MaxExpressionDepth = 4 }
        };

        Parse(text, environment).ShouldReport(DslCodes.ExpressionTooDeep);
    }

    // ---- DSL05xx: gates ---------------------------------------------------------------------

    [Fact]
    public void Gate_on_a_fan_in_node_is_refused_by_the_schema()
    {
        string text = DslFixtures.Broken(d =>
            ((JsonArray)d["nodes"]!).Add(new JsonObject
            {
                ["id"] = "join",
                ["kind"] = "fan-in",
                ["gate"] = new JsonObject { ["mode"] = "requireApproval" }
            }));

        Parse(text).IsValid.Should().BeFalse();
    }

    /// <summary>
    /// The semantic check exists independently of the schema's, because the schema can only forbid
    /// the kinds it knows about today.
    /// </summary>
    [Fact]
    public void Gate_on_a_non_gateable_kind_is_refused_semantically()
    {
        var model = DslDocumentReader.Read(DslFixtures.Minimal()) with
        {
            Nodes =
            [
                new DslFanInNode
                {
                    Id = "join",
                    Kind = DslNodeKinds.FanIn,
                    Pointer = "/nodes/0",
                    Gate = new DslGate { Mode = "requireApproval", Pointer = "/nodes/0/gate" }
                }
            ],
            Start = "join",
            Output = ["join"],
            Edges = []
        };

        DslValidationResult result = DslSemanticValidator.Validate(model);
        result.Has(DslCodes.GateOnNonGateableKind).Should().BeTrue();
    }

    [Fact]
    public void Conditional_gate_without_a_predicate_is_refused()
    {
        // The schema catches this too; the semantic check is asserted directly so both layers hold.
        var model = DslDocumentReader.Read(DslFixtures.Minimal());
        DslNode gated = model.Nodes[0] with
        {
            Gate = new DslGate { Mode = "conditional", Pointer = "/nodes/0/gate" }
        };

        DslValidationResult result = DslSemanticValidator.Validate(
            model with { Nodes = [gated, model.Nodes[1]] });

        result.Has(DslCodes.ConditionalGateWithoutPredicate).Should().BeTrue();
    }

    [Fact]
    public void Escalation_without_assignees_is_refused()
    {
        var model = DslDocumentReader.Read(DslFixtures.Minimal());
        DslNode gated = model.Nodes[0] with
        {
            Gate = new DslGate
            {
                Mode = "requireApproval",
                OnExpiryAction = "escalate",
                Pointer = "/nodes/0/gate"
            }
        };

        DslValidationResult result = DslSemanticValidator.Validate(
            model with { Nodes = [gated, model.Nodes[1]] });

        result.Has(DslCodes.EscalationWithoutAssignees).Should().BeTrue();
    }

    // ---- DSL06xx: environment ---------------------------------------------------------------

    [Fact]
    public void Unregistered_custom_node_is_refused()
    {
        string text = DslFixtures.Broken(d =>
        {
            ((JsonArray)d["nodes"]!).Add(new JsonObject
            {
                ["id"] = "score", ["kind"] = "custom", ["node"] = "score-risk"
            });
            ((JsonArray)d["edges"]!).Add(new JsonObject { ["from"] = "b", ["to"] = "score" });
            d["output"] = new JsonArray("score");
        });

        DslDiagnostic diagnostic = Parse(text, BareEnvironment()).ShouldReport(DslCodes.UnknownCustomNode);
        diagnostic.Pointer.Should().Be("/nodes/2/node");
        diagnostic.Suggestion.Should().Contain("AddDslNode");
    }

    [Fact]
    public void Custom_node_parameters_are_checked_against_the_factory_schema()
    {
        string text = DslFixtures.Broken(d =>
        {
            ((JsonArray)d["nodes"]!).Add(new JsonObject
            {
                ["id"] = "score",
                ["kind"] = "custom",
                ["node"] = "score-risk",
                ["with"] = new JsonObject { ["threshold"] = "not a number" }
            });
            ((JsonArray)d["edges"]!).Add(new JsonObject { ["from"] = "b", ["to"] = "score" });
            d["output"] = new JsonArray("score");
        });

        var environment = new DslEnvironment
        {
            EnforceEgress = false,
            CustomNodes = new Dictionary<string, JsonNode?>
            {
                ["score-risk"] = JsonNode.Parse(
                    """{ "type": "object", "properties": { "threshold": { "type": "number" } } }""")
            }
        };

        Parse(text, environment).ShouldReport(DslCodes.CustomNodeParameters)
            .Pointer.Should().Be("/nodes/2/with");
    }

    [Fact]
    public void Http_node_without_allowed_hosts_is_refused_when_egress_is_enforced()
    {
        string text = DslFixtures.Broken(d =>
        {
            ((JsonArray)d["nodes"]!).Add(new JsonObject
            {
                ["id"] = "call", ["kind"] = "http", ["url"] = "https://x.internal/a"
            });
            ((JsonArray)d["edges"]!).Add(new JsonObject { ["from"] = "b", ["to"] = "call" });
            d["output"] = new JsonArray("call");
        });

        DslDiagnostic diagnostic = Parse(text, BareEnvironment(enforceEgress: true))
            .ShouldReport(DslCodes.EgressHostsRequired);

        diagnostic.Pointer.Should().Be("/nodes/2/allowedHosts");
    }

    /// <summary>
    /// Offline linting has no host. The checks that need one must report as skipped rather than
    /// pass, because a check that silently did not run is worse than one that openly did not.
    /// </summary>
    [Fact]
    public void Environment_checks_are_skipped_not_passed_when_there_is_no_environment()
    {
        string text = DslFixtures.Broken(d =>
        {
            ((JsonArray)d["nodes"]!).Add(new JsonObject
            {
                ["id"] = "score", ["kind"] = "custom", ["node"] = "never-registered"
            });
            ((JsonArray)d["edges"]!).Add(new JsonObject { ["from"] = "b", ["to"] = "score" });
            d["output"] = new JsonArray("score");
        });

        DslParseResult result = Parse(text);

        result.Validation.Has(DslCodes.UnknownCustomNode).Should().BeFalse();
        result.Validation.SkippedChecks.Should().Contain(DslCodes.UnknownCustomNode);
        result.Validation.SkippedChecks.Should().Contain(DslCodes.EgressHostsRequired);
        result.Validation.SkippedChecks.Should().Contain(DslCodes.HashConflict);
    }

    [Fact]
    public void Nothing_is_skipped_when_an_environment_is_supplied()
        => Parse(DslFixtures.MinimalText, BareEnvironment()).Validation.SkippedChecks.Should().BeEmpty();

    // ---- DSL07xx: limits --------------------------------------------------------------------

    [Fact]
    public void A_document_over_the_byte_limit_is_refused()
    {
        var environment = new DslEnvironment { Policy = DslPolicy.Default with { MaxDocumentBytes = 64 } };
        Parse(DslFixtures.MinimalText, environment).ShouldReport(DslCodes.LimitExceeded);
    }

    [Fact]
    public void Too_many_nodes_is_refused()
    {
        var environment = new DslEnvironment
        {
            EnforceEgress = false,
            Policy = DslPolicy.Default with { MaxNodes = 1 }
        };

        Parse(DslFixtures.MinimalText, environment).ShouldReport(DslCodes.LimitExceeded)
            .Pointer.Should().Be("/nodes");
    }

    [Fact]
    public void Too_many_edges_is_refused()
    {
        var environment = new DslEnvironment
        {
            EnforceEgress = false,
            Policy = DslPolicy.Default with { MaxEdges = 0 }
        };

        Parse(DslFixtures.MinimalText, environment).ShouldReport(DslCodes.LimitExceeded)
            .Pointer.Should().Be("/edges");
    }

    // ---- schema-level structural errors -------------------------------------------------------

    [Theory]
    [InlineData("name")]
    [InlineData("version")]
    [InlineData("start")]
    [InlineData("nodes")]
    public void Missing_required_property_is_a_schema_violation(string property)
        => Parse(DslFixtures.Broken(d => d.Remove(property))).ShouldReport(DslCodes.SchemaViolation);

    [Fact]
    public void Unknown_top_level_property_is_refused()
        => Parse(DslFixtures.Broken(d => d["script"] = "rm -rf /"))
            .ShouldReport(DslCodes.SchemaViolation);

    [Fact]
    public void Uppercase_node_id_is_refused()
        => Parse(DslFixtures.Broken(d => DslFixtures.Node(d, 0)["id"] = "Validate"))
            .ShouldReport(DslCodes.SchemaViolation);

    [Fact]
    public void Non_semver_version_is_refused()
        => Parse(DslFixtures.Broken(d => d["version"] = "1.0")).ShouldReport(DslCodes.SchemaViolation);

    [Fact]
    public void Unknown_kind_is_refused()
        => Parse(DslFixtures.Broken(d => DslFixtures.Node(d, 0)["kind"] = "script"))
            .ShouldReport(DslCodes.SchemaViolation);

    [Fact]
    public void An_unlisted_exception_name_is_refused()
    {
        string text = DslFixtures.Broken(d => d["onFailure"] = new JsonArray(
            new JsonObject
            {
                ["match"] = new JsonObject { ["exception"] = "MyOwnException" },
                ["disposition"] = "retry"
            }));

        Parse(text).ShouldReport(DslCodes.SchemaViolation);
    }

    [Fact]
    public void Structural_errors_stop_before_the_semantic_pass()
    {
        // Removing 'nodes' would make every reference check fire; the caller should see the one real
        // problem instead of a cascade from a half-understood document.
        DslParseResult result = Parse(DslFixtures.Broken(d => d.Remove("nodes")));

        result.Document.Should().BeNull();
        result.Validation.Diagnostics.Should().OnlyContain(d => d.Code == DslCodes.SchemaViolation);
    }
}

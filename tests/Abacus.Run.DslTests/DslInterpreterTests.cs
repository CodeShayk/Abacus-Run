using System.Text.Json;
using System.Text.Json.Nodes;
using Abacus.Run.Abstractions;
using Abacus.Run.Core;
using Abacus.Run.Dsl.Expressions;
using Abacus.Run.Dsl.Interpretation;
using Abacus.Run.Dsl.Model;
using Abacus.Run.Dsl.Validation;
using FluentAssertions;
using Xunit;

namespace Abacus.Run.DslTests;

public class DslExpressionApplyTests
{
    private static AbExContext Context(string data, string? ctx = null)
        => new(JsonNode.Parse(data), ctx is null ? null : JsonNode.Parse(ctx), []);

    [Fact]
    public void Set_merges_into_existing_data()
    {
        JsonNode? result = DslExpressions.ApplySet(
            new Dictionary<string, string> { ["b"] = "2" },
            Context("""{ "a": 1 }"""),
            JsonNode.Parse("""{ "a": 1 }"""),
            replace: false);

        result!.ToJsonString().Should().Contain("\"a\":1").And.Contain("\"b\":2");
    }

    [Fact]
    public void Replace_discards_existing_data()
    {
        JsonNode? result = DslExpressions.ApplySet(
            new Dictionary<string, string> { ["b"] = "2" },
            Context("""{ "a": 1 }"""),
            JsonNode.Parse("""{ "a": 1 }"""),
            replace: true);

        result!.ToJsonString().Should().NotContain("\"a\"").And.Contain("\"b\":2");
    }

    [Fact]
    public void Set_writes_dotted_paths()
    {
        JsonNode? result = DslExpressions.ApplySet(
            new Dictionary<string, string> { ["order.total"] = "10 * 2" },
            Context("{}"), JsonNode.Parse("{}"), replace: false);

        result!["order"]!["total"]!.GetValue<decimal>().Should().Be(20);
    }

    /// <summary>
    /// Every expression reads the value as it was before the transform, so the order the properties
    /// happen to be written in cannot change the result.
    /// </summary>
    [Fact]
    public void Set_expressions_read_the_pre_transform_value()
    {
        JsonNode? result = DslExpressions.ApplySet(
            new Dictionary<string, string> { ["a"] = "$.a + 1", ["b"] = "$.a + 10" },
            Context("""{ "a": 1 }"""),
            JsonNode.Parse("""{ "a": 1 }"""),
            replace: false);

        result!["a"]!.GetValue<decimal>().Should().Be(2);
        result["b"]!.GetValue<decimal>().Should().Be(11, "b reads a as it was, not as a just became");
    }

    [Fact]
    public void Absent_expressions_write_null()
    {
        JsonNode? result = DslExpressions.ApplySet(
            new Dictionary<string, string> { ["x"] = "$.missing" },
            Context("{}"), JsonNode.Parse("{}"), replace: false);

        result!["x"].Should().BeNull();
    }

    [Fact]
    public void Assign_replaces_a_non_object_segment()
    {
        var root = new JsonObject { ["a"] = 1 };
        DslExpressions.Assign(root, "a.b", JsonValue.Create(2));

        root["a"]!["b"]!.GetValue<int>().Should().Be(2);
    }

    [Fact]
    public void Trees_are_cached_by_text()
        => DslExpressions.Tree("$.a + 1").Should().BeSameAs(DslExpressions.Tree("$.a + 1"));

    [Fact]
    public void An_unparseable_expression_yields_a_null_tree_rather_than_throwing()
    {
        Action act = () => DslExpressions.Tree("$.a = 1");
        act.Should().NotThrow();
        DslExpressions.Tree("$.a = 1").Should().BeNull();
        DslExpressions.Evaluate("$.a = 1", AbExContext.Empty).IsAbsent.Should().BeTrue();
    }
}

public class DslWorkflowDefinitionTests
{
    private static DslWorkflowDefinition Build(string text)
        => DslWorkflowDefinition.Create(DslParser.ParseOrThrow(text));

    [Fact]
    public void Exposes_name_version_and_hash()
    {
        DslWorkflowDefinition definition = Build(DslFixtures.FullText);

        definition.Name.Should().Be("order-settlement");
        definition.Version.Should().Be("1.2.0");
        definition.DocumentHash.Should().MatchRegex("^[0-9a-f]{64}$");
        ((IWorkflowDefinition)definition).ContextType.Should().Be<JsonElement>();
    }

    // ---- context validation --------------------------------------------------------------------

    [Fact]
    public void Accepts_a_context_matching_the_declared_schema()
    {
        using JsonDocument document = JsonDocument.Parse("""{ "orderId": "ORD-1", "amount": 10 }""");

        Build(DslFixtures.FullText).ValidateContext(document.RootElement).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Rejects_a_context_missing_a_required_property()
    {
        using JsonDocument document = JsonDocument.Parse("""{ "amount": 10 }""");

        ContextValidationResult result = Build(DslFixtures.FullText).ValidateContext(document.RootElement);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().NotBeEmpty();
    }

    [Fact]
    public void Rejects_a_context_with_a_wrong_property_type()
    {
        using JsonDocument document = JsonDocument.Parse("""{ "orderId": 7 }""");

        ContextValidationResult result = Build(DslFixtures.FullText).ValidateContext(document.RootElement);

        result.IsValid.Should().BeFalse();
        result.Errors.Keys.Should().Contain(k => k.Contains("orderId", StringComparison.Ordinal));
    }

    [Fact]
    public void A_document_with_no_context_schema_accepts_anything()
    {
        using JsonDocument document = JsonDocument.Parse("""{ "whatever": true }""");

        Build(DslFixtures.MinimalText).ValidateContext(document.RootElement).IsValid.Should().BeTrue();
    }

    // ---- notifications --------------------------------------------------------------------------

    [Fact]
    public void A_document_with_no_notifications_block_gets_the_default_policy()
    {
        NotificationPolicy policy = Build(DslFixtures.MinimalText).Notifications;

        policy.Level.Should().Be(NotificationLevel.Standard);
        policy.StreamEvents.Should().BeTrue();
    }

    [Theory]
    [InlineData("minimal", NotificationLevel.Minimal)]
    [InlineData("lifecycle", NotificationLevel.Lifecycle)]
    [InlineData("standard", NotificationLevel.Standard)]
    public void Notification_level_is_mapped(string declared, NotificationLevel expected)
    {
        string text = DslFixtures.Broken(d => d["notifications"] = new JsonObject { ["level"] = declared });
        Build(text).Notifications.Level.Should().Be(expected);
    }

    [Fact]
    public void Stream_false_becomes_log_only()
    {
        string text = DslFixtures.Broken(d => d["notifications"] = new JsonObject { ["stream"] = false });

        NotificationPolicy policy = Build(text).Notifications;
        policy.StreamEvents.Should().BeFalse();
        policy.IsLogOnly.Should().BeTrue();
    }

    [Fact]
    public void Per_node_overrides_are_mapped()
    {
        string text = DslFixtures.Broken(d => d["notifications"] = new JsonObject
        {
            ["level"] = "minimal",
            ["byNode"] = new JsonObject { ["b"] = "standard" }
        });

        Build(text).Notifications.ByNode["b"].Should().Be(NotificationLevel.Standard);
    }

    /// <summary>
    /// The catalog should advertise everything the workflow can emit, not only what an author
    /// remembered to list in two places.
    /// </summary>
    [Fact]
    public void Per_node_notify_names_join_the_declared_emits()
    {
        string text = DslFixtures.Broken(d =>
        {
            d["notifications"] = new JsonObject { ["emits"] = new JsonArray("declared") };
            DslFixtures.Node(d, 0)["notify"] = new JsonObject { ["name"] = "priced" };
        });

        Build(text).Notifications.Emits.Should().BeEquivalentTo("declared", "priced");
    }

    // ---- triggers -------------------------------------------------------------------------------

    [Fact]
    public void A_document_with_no_triggers_declares_none()
        => Build(DslFixtures.MinimalText).Triggers.Should().BeEmpty();

    [Fact]
    public void Trigger_topics_are_mapped()
    {
        IReadOnlyList<DomainEventTrigger> triggers = Build(DslFixtures.FullText).Triggers;

        triggers.Should().ContainSingle();
        triggers[0].TopicFilter.Should().Be("orders.placed");
    }

    [Fact]
    public void A_literal_correlation_key_on_a_trigger_is_kept()
    {
        string text = DslFixtures.Broken(d => d["triggers"] = new JsonArray(
            new JsonObject { ["topic"] = "orders.placed", ["correlationKey"] = "fixed-key" }));

        Build(text).Triggers[0].CorrelationKey.Should().Be("fixed-key");
    }

    /// <summary>
    /// A trigger subscription is registered before any message exists, so a correlation key written
    /// as an expression is a misunderstanding rather than a projection. Warned about, not silently
    /// evaluated or silently dropped.
    /// </summary>
    [Fact]
    public void An_expression_shaped_correlation_key_is_warned_about()
    {
        DslParseResult result = DslParser.Parse(DslFixtures.FullText);

        result.IsValid.Should().BeTrue("a warning must not stop the document registering");
        result.Validation.Warnings.Should().Contain(d => d.Pointer == "/triggers/0/correlationKey");
    }

    [Fact]
    public void ContextFrom_projects_the_triggering_message()
    {
        string text = DslFixtures.Broken(d => d["triggers"] = new JsonArray(
            new JsonObject { ["topic"] = "orders.placed", ["contextFrom"] = "$.order" }));

        DomainEventTrigger trigger = Build(text).Triggers[0];
        trigger.ContextSelector.Should().NotBeNull();

        string projected = trigger.ContextSelector!(new DomainEventMessage
        {
            MessageId = "m-1",
            Topic = "orders.placed",
            PayloadJson = """{ "order": { "id": "ORD-3" }, "noise": 1 }""",
            OccurredAt = DateTimeOffset.UtcNow
        });

        projected.Should().Contain("ORD-3").And.NotContain("noise");
    }

    [Fact]
    public void ContextFrom_falls_back_to_the_whole_payload_when_it_resolves_to_nothing()
    {
        string text = DslFixtures.Broken(d => d["triggers"] = new JsonArray(
            new JsonObject { ["topic"] = "orders.placed", ["contextFrom"] = "$.missing" }));

        string projected = Build(text).Triggers[0].ContextSelector!(new DomainEventMessage
        {
            MessageId = "m-1",
            Topic = "orders.placed",
            PayloadJson = """{ "order": 1 }""",
            OccurredAt = DateTimeOffset.UtcNow
        });

        projected.Should().Contain("order");
    }

    // ---- audit ------------------------------------------------------------------------------------

    [Fact]
    public void A_document_with_no_audit_block_keeps_no_record()
        => Build(DslFixtures.MinimalText).Should().NotBeAssignableTo<IAuditedWorkflowDefinition>();

    [Fact]
    public void A_document_with_an_audit_block_declares_a_record()
    {
        var audited = Build(DslFixtures.FullText) as IAuditedWorkflowDefinition;

        audited.Should().NotBeNull();
        audited!.AuditRecord.RootKind.Should().Be("order-settlement");
        audited.AuditRecord.Allows("submission").Should().BeTrue();
        audited.AuditRecord.Allows("outcome").Should().BeTrue();
        audited.AuditRecord.Allows("never-declared").Should().BeFalse();
    }

    // ---- failure classification ---------------------------------------------------------------------

    private static WorkflowFailure Failure(Exception exception, string executorId = "settle")
        => WorkflowFailure.Create(executorId, exception);

    [Fact]
    public void A_matching_rule_decides()
    {
        DslWorkflowDefinition definition = Build(DslFixtures.FullText);

        definition.Classify(Failure(new ApiCallFailureException(503, "unavailable", null)))
            .Should().Be(FailureDisposition.Retry);
    }

    [Fact]
    public void A_status_class_matches_the_whole_range()
    {
        DslWorkflowDefinition.StatusMatches("5xx", 500).Should().BeTrue();
        DslWorkflowDefinition.StatusMatches("5xx", 599).Should().BeTrue();
        DslWorkflowDefinition.StatusMatches("5xx", 499).Should().BeFalse();
        DslWorkflowDefinition.StatusMatches("404", 404).Should().BeTrue();
        DslWorkflowDefinition.StatusMatches("404", 400).Should().BeFalse();
    }

    [Fact]
    public void A_rule_scoped_to_a_node_does_not_match_another()
    {
        string text = DslFixtures.Broken(d => d["onFailure"] = new JsonArray(
            new JsonObject
            {
                ["match"] = new JsonObject { ["node"] = "a" },
                ["disposition"] = "escalate"
            }));

        DslWorkflowDefinition definition = Build(text);

        definition.Classify(Failure(new InvalidOperationException(), "a"))
            .Should().Be(FailureDisposition.Escalate);
        definition.Classify(Failure(new InvalidOperationException(), "b"))
            .Should().NotBe(FailureDisposition.Escalate);
    }

    /// <summary>
    /// A document that cannot be interpreted will not interpret on the next attempt either. Retrying
    /// it spends the attempt budget to arrive at the same message, so it stops.
    /// </summary>
    [Fact]
    public void An_interpretation_failure_dead_stops()
    {
        DslWorkflowDefinition definition = Build(DslFixtures.MinimalText);

        definition.Classify(Failure(new DslInterpretationException("wrong shape")))
            .Should().Be(FailureDisposition.DeadStop);
    }

    /// <summary>
    /// A build failure arrives wrapped in whatever the graph construction threw it into, so the
    /// classifier walks the chain rather than testing the outermost type.
    /// </summary>
    [Fact]
    public void A_wrapped_interpretation_failure_dead_stops_too()
    {
        DslWorkflowDefinition definition = Build(DslFixtures.MinimalText);

        definition.Classify(Failure(new InvalidOperationException("build failed",
                new DslInterpretationException("wrong shape"))))
            .Should().Be(FailureDisposition.DeadStop);

        definition.Classify(Failure(new AggregateException(
                new DslInterpretationException("wrong shape"))))
            .Should().Be(FailureDisposition.DeadStop);
    }

    /// <summary>
    /// And not overridable by the document, which is the one classification rule an author does not
    /// get a say in. The schema does not even offer <c>DslInterpretationException</c> as a matchable
    /// name, so the way a document could reach it is a rule matched on something else — here the node
    /// the failure came from.
    /// </summary>
    [Fact]
    public void A_documents_retry_rule_cannot_reopen_an_interpretation_failure()
    {
        string text = DslFixtures.Broken(d => d["onFailure"] = new JsonArray(
            new JsonObject
            {
                ["match"] = new JsonObject { ["node"] = "a" },
                ["disposition"] = "retry"
            }));

        DslWorkflowDefinition definition = Build(text);

        definition.Classify(Failure(new DslInterpretationException("wrong shape"), "a"))
            .Should().Be(FailureDisposition.DeadStop);

        // The rule still governs everything else from that node, so the guard is narrow rather than
        // a blanket override of the document.
        definition.Classify(Failure(new InvalidOperationException("something else"), "a"))
            .Should().Be(FailureDisposition.Retry);
    }

    /// <summary>
    /// A document should only have to state where it disagrees; the framework already knows a rate
    /// limit is worth retrying and a validation error is not.
    /// </summary>
    [Fact]
    public void An_unmatched_failure_defers_to_the_framework_classifier()
    {
        DslWorkflowDefinition definition = Build(DslFixtures.MinimalText);
        var failure = Failure(new WorkflowDeadStopException("no"));

        definition.Classify(failure)
            .Should().Be(DefaultFailureClassifier.Instance.Classify(failure));
    }

    [Fact]
    public void Rules_are_applied_in_declaration_order()
    {
        string text = DslFixtures.Broken(d => d["onFailure"] = new JsonArray(
            new JsonObject
            {
                ["match"] = new JsonObject { ["exception"] = "ApiCallFailureException" },
                ["disposition"] = "deadStop"
            },
            new JsonObject
            {
                ["match"] = new JsonObject { ["exception"] = "ApiCallFailureException" },
                ["disposition"] = "retry"
            }));

        Build(text).Classify(Failure(new ApiCallFailureException(500, "x", null)))
            .Should().Be(FailureDisposition.DeadStop);
    }
}

public class DslNodeCatalogTests
{
    private sealed class StubFactory(string name, JsonNode? schema = null) : IDslNodeFactory
    {
        public string Name => name;
        public JsonNode? ParameterSchema => schema;
        public IHostExecutor Create(DslNodeContext context) => throw new NotSupportedException();
    }

    [Fact]
    public void Registers_and_resolves_by_name()
    {
        var catalog = new DslNodeCatalog().Add(new StubFactory("score-risk"));

        catalog.TryGet("score-risk", out IDslNodeFactory factory).Should().BeTrue();
        factory.Name.Should().Be("score-risk");
        catalog.Names.Should().Equal("score-risk");
    }

    [Fact]
    public void An_unknown_name_does_not_resolve()
        => new DslNodeCatalog().TryGet("nope", out _).Should().BeFalse();

    /// <summary>
    /// Which one wins would be whichever registration ran first — not something to discover from
    /// behaviour.
    /// </summary>
    [Fact]
    public void A_duplicate_name_is_refused()
    {
        var catalog = new DslNodeCatalog().Add(new StubFactory("score-risk"));

        Action act = () => catalog.Add(new StubFactory("score-risk"));
        act.Should().Throw<InvalidOperationException>().WithMessage("*already registered*");
    }

    [Fact]
    public void Describe_exposes_parameter_schemas_for_validation()
    {
        JsonNode schema = JsonNode.Parse("""{ "type": "object" }""")!;
        var catalog = new DslNodeCatalog().Add(new StubFactory("with-schema", schema))
                                          .Add(new StubFactory("without-schema"));

        IReadOnlyDictionary<string, JsonNode?> described = catalog.Describe();

        described.Should().HaveCount(2);
        described["with-schema"].Should().NotBeNull();
        described["without-schema"].Should().BeNull();
    }

    [Fact]
    public void A_delegate_factory_carries_its_name_and_schema()
    {
        JsonNode schema = JsonNode.Parse("""{ "type": "object" }""")!;
        var factory = new DelegateDslNodeFactory("quick", _ => throw new NotSupportedException(), schema);

        factory.Name.Should().Be("quick");
        factory.ParameterSchema.Should().BeSameAs(schema);
    }
}

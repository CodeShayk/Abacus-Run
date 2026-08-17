using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Abacus.Run.Abstractions;
using Abacus.Run.Core;
using Abacus.Run.Dsl.Interpretation;
using FluentAssertions;
using Xunit;

namespace Abacus.Run.IntegrationTests;

/// <summary>
/// End-to-end coverage of every DSL conversion: each node kind, each edge shape, gates,
/// notifications, triggers, audit and failure rules, run through the real host.
/// </summary>
public class DslWorkflowTests : IClassFixture<DslHostFixture>
{
    private readonly DslHostFixture _fixture;

    public DslWorkflowTests(DslHostFixture fixture) => _fixture = fixture;

    private static object Context(string orderId = "ORD-1", decimal amount = 100m)
        => new { orderId, amount };

    // ---- linear and the envelope ---------------------------------------------------------------

    [Fact]
    public async Task Linear_document_runs_to_completion()
    {
        using HttpClient client = _fixture.CreateClient();

        WorkflowInstance instance = await _fixture.RunAsync(
            client, "dsl-linear", Context(amount: 50m));

        instance.Status.Should().Be(InstanceStatus.Completed);
    }

    /// <summary>
    /// The whole reason the envelope carries the start context: an expression several nodes deep can
    /// still read it, without every node having to forward it by hand.
    /// </summary>
    [Fact]
    public async Task Context_survives_to_the_last_node()
    {
        using HttpClient client = _fixture.CreateClient();

        string id = await _fixture.StartAsync(client, "dsl-linear", Context("ORD-CARRY", 21m));
        await _fixture.WaitForStatusAsync(id, InstanceStatus.Completed);

        JsonNode? result = await _fixture.ResultOfAsync(id);

        result!["carried"]!.GetValue<string>().Should().Be("ORD-CARRY");
        result["total"]!.GetValue<decimal>().Should().Be(42m, "the first node computed amount * 2");
    }

    [Fact]
    public async Task A_context_failing_the_declared_schema_is_rejected()
    {
        using HttpClient client = _fixture.CreateClient();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/workflows/dsl-linear/instances", new { context = new { amount = 10 } });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("orderId");
    }

    [Fact]
    public async Task A_context_matching_the_declared_schema_is_accepted()
    {
        using HttpClient client = _fixture.CreateClient();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/workflows/dsl-linear/instances", new { context = Context() });

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Accepted, HttpStatusCode.Created, HttpStatusCode.OK);
    }

    // ---- edges ------------------------------------------------------------------------------------

    [Theory]
    [InlineData(5000, "large")]
    [InlineData(10, "small")]
    public async Task Conditional_edges_route_by_predicate(int amount, string expected)
    {
        using HttpClient client = _fixture.CreateClient();

        string id = await _fixture.StartAsync(client, "dsl-branch", Context(amount: amount));
        await _fixture.WaitForStatusAsync(id, InstanceStatus.Completed);

        JsonNode? result = await _fixture.ResultOfAsync(id);
        result!["band"]!.GetValue<string>().Should().Be(expected);
    }

    [Fact]
    public async Task Fan_out_and_barrier_collect_both_branches()
    {
        using HttpClient client = _fixture.CreateClient();

        string id = await _fixture.StartAsync(client, "dsl-fan", Context(amount: 10m));
        await _fixture.WaitForStatusAsync(id, InstanceStatus.Completed);

        JsonElement history = await client.GetFromJsonAsync<JsonElement>($"/instances/{id}/events/history");
        string ran = string.Join(", ", history.GetProperty("items").EnumerateArray()
            .Select(e => $"{(e.TryGetProperty("eventType", out JsonElement t) ? t.GetString() : "?")}" +
                         $"/{(e.TryGetProperty("executorId", out JsonElement x) ? x.GetString() : "-")}"));

        JsonNode? result = await _fixture.ResultOfAsync(id);
        result.Should().NotBeNull($"the run should have produced a result; events were: {ran}");
        result!["branches"].Should().NotBeNull($"the barrier should have aggregated; result was {result.ToJsonString()}");

        var branches = (JsonArray)result["branches"]!;

        branches.Should().HaveCount(2);
        branches.Select(b => b!["side"]!.GetValue<string>())
            .Should().BeEquivalentTo("left", "right");
        branches.Select(b => b!["value"]!.GetValue<decimal>())
            .Should().BeEquivalentTo(new[] { 11m, 12m });
    }

    [Theory]
    [InlineData(0, "first")]
    [InlineData(1, "second")]
    public async Task Fan_out_selector_picks_a_target_by_index(int pick, string expected)
    {
        using HttpClient client = _fixture.CreateClient();

        string id = await _fixture.StartAsync(client, "dsl-select", Context(amount: pick));
        await _fixture.WaitForStatusAsync(id, InstanceStatus.Completed);

        JsonNode? result = await _fixture.ResultOfAsync(id);
        result!["chosen"]!.GetValue<string>().Should().Be(expected);
    }

    // ---- gates -------------------------------------------------------------------------------------

    [Fact]
    public async Task A_gate_below_the_threshold_does_not_trip()
    {
        using HttpClient client = _fixture.CreateClient();

        WorkflowInstance instance = await _fixture.RunAsync(client, "dsl-gated", Context(amount: 100m));
        instance.Status.Should().Be(InstanceStatus.Completed);
    }

    [Fact]
    public async Task A_gate_above_the_threshold_parks_the_instance()
    {
        using HttpClient client = _fixture.CreateClient();

        string id = await _fixture.StartAsync(client, "dsl-gated", Context(amount: 50_000m));
        await _fixture.WaitForStatusAsync(id, InstanceStatus.AwaitingApproval);

        JsonElement approvals = await client.GetFromJsonAsync<JsonElement>($"/instances/{id}/approvals");
        approvals.GetProperty("items").EnumerateArray().Should().ContainSingle();

        JsonElement approval = approvals.GetProperty("items").EnumerateArray().First();
        approval.GetProperty("executorId").GetString().Should().Be("settle");
        approval.GetProperty("reason").GetString().Should().Be("RegulatedSettlement");
    }

    [Fact]
    public async Task An_approved_gate_resumes_the_run()
    {
        using HttpClient client = _fixture.CreateClient();

        string id = await _fixture.StartAsync(client, "dsl-gated-open", Context(amount: 60_000m));
        await _fixture.WaitForStatusAsync(id, InstanceStatus.AwaitingApproval);

        JsonElement approvals = await client.GetFromJsonAsync<JsonElement>($"/instances/{id}/approvals");
        string approvalId = approvals.GetProperty("items").EnumerateArray().First().GetProperty("approvalId").GetString()!;

        HttpResponseMessage decision = await client.PostAsJsonAsync(
            $"/approvals/{approvalId}/decision", new { decision = "approve" });

        decision.EnsureSuccessStatusCode();

        WorkflowInstance instance = await _fixture.WaitForStatusAsync(id, InstanceStatus.Completed);
        instance.Status.Should().Be(InstanceStatus.Completed);
    }

    /// <summary>An approval node carries a gate whether or not the document spelled the block out.</summary>
    [Fact]
    public async Task An_approval_node_always_parks()
    {
        using HttpClient client = _fixture.CreateClient();

        string id = await _fixture.StartAsync(client, "dsl-approval", Context(amount: 1m));
        await _fixture.WaitForStatusAsync(id, InstanceStatus.AwaitingApproval);

        JsonElement approvals = await client.GetFromJsonAsync<JsonElement>($"/instances/{id}/approvals");
        approvals.GetProperty("items").EnumerateArray().First().GetProperty("executorId").GetString().Should().Be("sign-off");
    }

    // ---- http ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Http_node_calls_out_and_projects_the_response()
    {
        _fixture.Http.Respond = _ => (HttpStatusCode.OK, """{ "reference": "LDG-77" }""");

        using HttpClient client = _fixture.CreateClient();

        string id = await _fixture.StartAsync(client, "dsl-http", Context("ORD-HTTP", 33m));
        await _fixture.WaitForStatusAsync(id, InstanceStatus.Completed);

        JsonNode? result = await _fixture.ResultOfAsync(id);
        result!["status"]!.GetValue<int>().Should().Be(200);
        result["reference"]!.GetValue<string>().Should().Be("LDG-77");
    }

    [Fact]
    public async Task Http_node_renders_url_headers_and_body_templates()
    {
        _fixture.Http.Requests.Clear();
        _fixture.Http.Respond = _ => (HttpStatusCode.OK, """{ "reference": "X" }""");

        using HttpClient client = _fixture.CreateClient();

        string id = await _fixture.StartAsync(client, "dsl-http", Context("ORD-TEMPLATE", 77m));
        await _fixture.WaitForStatusAsync(id, InstanceStatus.Completed);

        _fixture.Http.Requests.TryDequeue(out HttpRequestMessage? request).Should().BeTrue();

        request!.RequestUri!.ToString().Should().EndWith("/v1/orders/ORD-TEMPLATE");
        request.Headers.GetValues("X-Order").Should().Equal("ORD-TEMPLATE");
        request.Method.Should().Be(HttpMethod.Post);
    }

    /// <summary>The framework's idempotency key still travels — the DSL did not fork the HTTP path.</summary>
    [Fact]
    public async Task Http_node_still_sends_an_idempotency_key()
    {
        _fixture.Http.Requests.Clear();
        _fixture.Http.Respond = _ => (HttpStatusCode.OK, "{}");

        using HttpClient client = _fixture.CreateClient();

        string id = await _fixture.StartAsync(client, "dsl-http", Context("ORD-IDEM", 5m));
        await _fixture.WaitForStatusAsync(id, InstanceStatus.Completed);

        _fixture.Http.Requests.TryDequeue(out HttpRequestMessage? request).Should().BeTrue();
        request!.Headers.Contains("Idempotency-Key").Should().BeTrue();
    }

    [Fact]
    public async Task A_non_json_response_body_still_lands_on_the_envelope()
    {
        _fixture.Http.Respond = _ => (HttpStatusCode.OK, "plain text");

        using HttpClient client = _fixture.CreateClient();

        WorkflowInstance instance = await _fixture.RunAsync(client, "dsl-http", Context(amount: 1m));
        instance.Status.Should().Be(InstanceStatus.Completed);
    }

    // ---- llm -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Llm_node_calls_the_model_and_projects_text_and_usage()
    {
        _fixture.Chat.Reply = "One order, summarised.";

        using HttpClient client = _fixture.CreateClient();

        string id = await _fixture.StartAsync(client, "dsl-llm", Context("ORD-LLM", 9m));
        await _fixture.WaitForStatusAsync(id, InstanceStatus.Completed);

        JsonNode? result = await _fixture.ResultOfAsync(id);
        result!["summary"]!.GetValue<string>().Should().Be("One order, summarised.");
        result["inputTokens"]!.GetValue<int>().Should().Be(11);
        result["model"]!.GetValue<string>().Should().Be("stub-model");
    }

    [Fact]
    public async Task Llm_node_renders_its_prompt_templates()
    {
        _fixture.Chat.Prompts.Clear();

        using HttpClient client = _fixture.CreateClient();

        string id = await _fixture.StartAsync(client, "dsl-llm", Context("ORD-PROMPT", 12m));
        await _fixture.WaitForStatusAsync(id, InstanceStatus.Completed);

        _fixture.Chat.Prompts.Should().Contain(p => p.Contains("ORD-PROMPT", StringComparison.Ordinal));
        _fixture.Chat.Prompts.Should().Contain(p => p.Contains("You summarise orders", StringComparison.Ordinal));
    }

    // ---- delay ------------------------------------------------------------------------------------------

    /// <summary>
    /// A delay is about when the next node runs, not about what it receives, so the envelope must
    /// pass through rather than being replaced by a timer record.
    /// </summary>
    [Fact]
    public async Task Delay_node_schedules_a_timer_and_passes_the_envelope_through()
    {
        using HttpClient client = _fixture.CreateClient();

        string id = await _fixture.StartAsync(client, "dsl-delay", Context(amount: 1m));
        await _fixture.WaitForStatusAsync(id, InstanceStatus.Completed);

        JsonNode? result = await _fixture.ResultOfAsync(id);
        result!["carried"]!.GetValue<string>().Should().Be("before");

        _fixture.Timers.Scheduled.Should().Contain(t => t.InstanceId == id && t.ExecutorId == "wait");
    }

    // ---- domain events -------------------------------------------------------------------------------------

    [Fact]
    public async Task Publish_node_emits_a_domain_event_and_passes_input_through()
    {
        var broker = _fixture.Resolve<IDomainEventBroker>();
        var received = new List<DomainEventMessage>();

        await using IAsyncDisposable subscription = await broker.SubscribeAsync(
            new DomainEventSubscriptionOptions { TopicFilter = "dsl.orders.priced" },
            (delivery, _) =>
            {
                lock (received) { received.Add(delivery.Message); }
                return ValueTask.FromResult(DeliveryResult.Ack);
            },
            default);

        using HttpClient client = _fixture.CreateClient();

        string id = await _fixture.StartAsync(client, "dsl-publish", Context("ORD-PUB", 3m));
        await _fixture.WaitForStatusAsync(id, InstanceStatus.Completed);

        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            lock (received)
            {
                if (received.Count > 0) break;
            }

            await Task.Delay(25);
        }

        lock (received)
        {
            received.Should().ContainSingle();
            received[0].Topic.Should().Be("dsl.orders.priced");
            received[0].CorrelationKey.Should().Be("ORD-PUB");
            received[0].PayloadJson.Should().Contain("ORD-PUB");
        }

        JsonNode? result = await _fixture.ResultOfAsync(id);
        result!["status"]!.GetValue<string>().Should().Be("published");
    }

    [Fact]
    public async Task Wait_event_node_parks_then_resumes_with_the_payload()
    {
        using HttpClient client = _fixture.CreateClient();

        string id = await _fixture.StartAsync(client, "dsl-wait", Context("ORD-WAIT", 1m));
        await _fixture.WaitForStatusAsync(id, InstanceStatus.AwaitingInput);

        HttpResponseMessage published = await client.PostAsJsonAsync("/events", new
        {
            topic = "dsl.payment.settled",
            payload = new { amount = 250 }
        });

        published.EnsureSuccessStatusCode();

        await _fixture.WaitForStatusAsync(id, InstanceStatus.Completed);

        JsonNode? result = await _fixture.ResultOfAsync(id);
        result!["paidAmount"]!.GetValue<decimal>().Should().Be(250m);
        result["order"]!.GetValue<string>().Should().Be("ORD-WAIT",
            "the envelope's context survives the park and resume");
    }

    // ---- custom nodes -------------------------------------------------------------------------------------

    [Fact]
    public async Task A_registered_custom_node_runs_with_its_parameters()
    {
        using HttpClient client = _fixture.CreateClient();

        string id = await _fixture.StartAsync(client, "dsl-custom", Context(amount: 7m));
        await _fixture.WaitForStatusAsync(id, InstanceStatus.Completed);

        JsonNode? result = await _fixture.ResultOfAsync(id);
        result!["value"]!.GetValue<decimal>().Should().Be(21m, "times was 3");
    }

    // ---- notifications ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_node_notification_reaches_the_event_log()
    {
        using HttpClient client = _fixture.CreateClient();

        string id = await _fixture.StartAsync(client, "dsl-notify", Context("ORD-NOTIFY", 4m));
        await _fixture.WaitForStatusAsync(id, InstanceStatus.Completed);

        JsonElement history = await client.GetFromJsonAsync<JsonElement>($"/instances/{id}/events/history");

        JsonElement[] custom = [.. history.GetProperty("items").EnumerateArray()
            .Where(e => e.TryGetProperty("eventType", out JsonElement t) && t.GetString() == "custom.priced")];

        custom.Should().ContainSingle(
            "the event log held: " + string.Join(", ", history.GetProperty("items").EnumerateArray()
                .Select(e => e.TryGetProperty("eventType", out JsonElement t) ? t.GetString() : "?")));
        // The payload travels as raw JSON, so read it back rather than assuming how the endpoint
        // chose to embed it.
        JsonElement raw = custom[0].GetProperty("payloadJson");
        JsonNode payload = (raw.ValueKind == JsonValueKind.String
            ? JsonNode.Parse(raw.GetString()!)
            : JsonNode.Parse(raw.GetRawText()))!;

        payload["total"]!.GetValue<decimal>().Should().Be(8m);
        payload["order"]!.GetValue<string>().Should().Be("ORD-NOTIFY");
    }

    [Fact]
    public async Task The_catalog_advertises_workflow_defined_notifications()
    {
        using HttpClient client = _fixture.CreateClient();

        JsonElement workflow = await client.GetFromJsonAsync<JsonElement>("/workflows/dsl-notify");
        workflow.GetRawText().Should().Contain("priced");
    }

    // ---- failure classification -------------------------------------------------------------------------------

    [Fact]
    public async Task A_documents_failure_rule_dead_stops_the_run()
    {
        _fixture.Http.Respond = _ => (HttpStatusCode.BadRequest, """{ "error": "nope" }""");

        using HttpClient client = _fixture.CreateClient();

        string id = await _fixture.StartAsync(client, "dsl-failing", Context(amount: 1m));
        WorkflowInstance instance = await _fixture.WaitForStatusAsync(
            id, InstanceStatus.DeadStopped, InstanceStatus.Failed);

        instance.Status.Should().Be(InstanceStatus.DeadStopped);
        instance.AttemptCount.Should().BeLessThanOrEqualTo(1, "a dead stop must not burn attempts discovering it");
    }

    // ---- audit ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_document_declaring_an_audit_block_gets_an_audit_record_shape()
    {
        using HttpClient client = _fixture.CreateClient();

        string id = await _fixture.StartAsync(client, "dsl-audited", Context(amount: 1m));
        await _fixture.WaitForStatusAsync(id, InstanceStatus.Completed);

        JsonElement state = await client.GetFromJsonAsync<JsonElement>(
            $"/workflows/dsl-audited/instances/{id}/state");

        state.TryGetProperty("audit", out JsonElement audit).Should().BeTrue();
        audit.ValueKind.Should().NotBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_document_with_no_audit_block_reports_no_record()
    {
        using HttpClient client = _fixture.CreateClient();

        string id = await _fixture.StartAsync(client, "dsl-linear", Context(amount: 1m));
        await _fixture.WaitForStatusAsync(id, InstanceStatus.Completed);

        JsonElement state = await client.GetFromJsonAsync<JsonElement>(
            $"/workflows/dsl-linear/instances/{id}/state");

        if (state.TryGetProperty("audit", out JsonElement audit))
        {
            audit.ValueKind.Should().Be(JsonValueKind.Null);
        }
    }

    // ---- triggers --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_published_event_starts_a_triggered_document()
    {
        using HttpClient client = _fixture.CreateClient();

        var instances = _fixture.Resolve<IInstanceStore>();
        Page<WorkflowInstance> before = await instances.QueryAsync(
            new InstanceQuery { WorkflowName = "dsl-triggered" }, default);

        HttpResponseMessage published = await client.PostAsJsonAsync("/events", new
        {
            topic = "dsl.orders.placed",
            payload = new { orderId = "ORD-TRIGGER", amount = 5 }
        });

        published.EnsureSuccessStatusCode();

        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        Page<WorkflowInstance> after = before;

        while (DateTime.UtcNow < deadline)
        {
            after = await instances.QueryAsync(
                new InstanceQuery { WorkflowName = "dsl-triggered" }, default);

            if (after.Total > before.Total) break;
            await Task.Delay(50);
        }

        after.Total.Should().BeGreaterThan(before.Total, "the topic should have started an instance");
    }

    // ---- the catalog ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Every_document_appears_in_the_workflow_catalog()
    {
        using HttpClient client = _fixture.CreateClient();

        JsonElement catalog = await client.GetFromJsonAsync<JsonElement>("/workflows");
        string[] names = [.. catalog.EnumerateArray().Select(w => w.GetProperty("name").GetString()!)];

        foreach ((string _, string text) in DslDocuments.All)
        {
            string name = JsonNode.Parse(text)!["name"]!.GetValue<string>();
            names.Should().Contain(name);
        }
    }

    [Fact]
    public async Task A_dsl_workflow_exposes_its_nodes_to_the_catalog()
    {
        using HttpClient client = _fixture.CreateClient();

        JsonElement nodes = await client.GetFromJsonAsync<JsonElement>(
            "/workflows/dsl-gated/versions/1.0.0/nodes");

        string raw = nodes.GetRawText();
        raw.Should().Contain("prepare").And.Contain("settle");
    }
}

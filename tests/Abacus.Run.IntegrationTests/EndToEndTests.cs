using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Abacus.Run.Abstractions;
using Abacus.Run.Core;
using FluentAssertions;
using Xunit;

namespace Abacus.Run.IntegrationTests;

public class EndToEndTests : IClassFixture<HostFixture>
{
    private readonly HostFixture _host;
    private readonly HttpClient _client;

    public EndToEndTests(HostFixture host)
    {
        _host = host;
        _client = host.CreateClient();
    }

    [Fact]
    public async Task Catalog_lists_registered_workflows_with_their_schemas()
    {
        JsonElement body = await _client.GetFromJsonAsync<JsonElement>("/workflows");

        string[] names = body.EnumerateArray().Select(w => w.GetProperty("name").GetString()!).ToArray();

        names.Should().Contain("order").And.Contain("gated-order");

        JsonElement order = body.EnumerateArray().First(w => w.GetProperty("name").GetString() == "order");
        order.GetProperty("contextType").GetString().Should().Be(nameof(OrderContext));
        order.GetProperty("resultType").GetString().Should().Be(nameof(OrderResult));
    }

    [Fact]
    public async Task Workflow_detail_reports_versions()
    {
        JsonElement body = await _client.GetFromJsonAsync<JsonElement>("/workflows/order");

        body.GetProperty("versions").EnumerateArray().Should().ContainSingle();
    }

    [Fact]
    public async Task Unknown_workflow_detail_is_not_found()
        => (await _client.GetAsync("/workflows/nope")).StatusCode.Should().Be(HttpStatusCode.NotFound);

    [Fact]
    public async Task An_autonomous_workflow_runs_to_completion()
    {
        string instanceId = await _host.StartAsync(_client, "order", new OrderContext("ORD-100"));

        WorkflowInstance instance = await _host.WaitForStatusAsync(instanceId, InstanceStatus.Completed);

        instance.Status.Should().Be(InstanceStatus.Completed);
        _host.Ledger.Entries.Should().Contain($"submit:{instanceId}");
    }

    [Fact]
    public async Task Start_returns_202_with_a_location_header()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/workflows/order/instances", new { context = new OrderContext("ORD-202") });

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        response.Headers.Location!.ToString().Should().StartWith("/instances/");
    }

    [Fact]
    public async Task Starting_an_unknown_workflow_is_a_problem_response()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/workflows/missing/instances", new { context = new { } });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Unknown workflow");
    }

    [Fact]
    public async Task An_invalid_context_is_rejected_and_creates_no_instance()
    {
        var store = _host.Resolve<IInstanceStore>();
        Page<WorkflowInstance> before = await store.QueryAsync(new InstanceQuery { Limit = 500 }, default);

        HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/workflows/order/instances", new { context = new { amount = "not-a-number" } });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        Page<WorkflowInstance> after = await store.QueryAsync(new InstanceQuery { Limit = 500 }, default);
        after.Total.Should().Be(before.Total, "an invalid start must not leave a row behind");
    }

    [Fact]
    public async Task An_idempotency_key_returns_the_original_instance()
    {
        string key = $"key-{Guid.NewGuid():N}";

        string first = await _host.StartAsync(_client, "order", new OrderContext("ORD-IDEM"), key);
        string second = await _host.StartAsync(_client, "order", new OrderContext("ORD-IDEM"), key);

        second.Should().Be(first);
    }

    [Fact]
    public async Task A_business_rejection_dead_stops_without_consuming_retries()
    {
        string instanceId = await _host.StartAsync(_client, "order", new OrderContext("ORD-BAD", Fail: true));

        WorkflowInstance instance = await _host.WaitForStatusAsync(instanceId, InstanceStatus.DeadStopped);

        instance.TerminalReason.Should().Contain("rejected");
        instance.AttemptCount.Should().Be(0);
        _host.Ledger.CountFor("submit").Should().Be(
            _host.Ledger.Entries.Count(e => e == $"submit:{instanceId}") == 0
                ? _host.Ledger.CountFor("submit")
                : _host.Ledger.CountFor("submit"));
        _host.Ledger.Entries.Should().NotContain($"submit:{instanceId}",
            "the downstream node must never run after a dead stop upstream");
    }

    [Fact]
    public async Task Instance_detail_and_query_expose_the_run()
    {
        string instanceId = await _host.StartAsync(_client, "order", new OrderContext("ORD-Q"));
        await _host.WaitForStatusAsync(instanceId, InstanceStatus.Completed);

        JsonElement detail = await _client.GetFromJsonAsync<JsonElement>($"/instances/{instanceId}");
        detail.GetProperty("workflowName").GetString().Should().Be("order");
        detail.GetProperty("status").GetString().Should().Be("Completed");

        JsonElement list = await _client.GetFromJsonAsync<JsonElement>("/instances?status=Completed&limit=100");
        list.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("instanceId").GetString())
            .Should().Contain(instanceId);
    }

    [Fact]
    public async Task Unknown_instance_detail_is_not_found()
        => (await _client.GetAsync("/instances/does-not-exist")).StatusCode.Should().Be(HttpStatusCode.NotFound);

    [Fact]
    public async Task Event_history_returns_the_progress_narrative()
    {
        string instanceId = await _host.StartAsync(_client, "order", new OrderContext("ORD-EV"));
        await _host.WaitForStatusAsync(instanceId, InstanceStatus.Completed);

        JsonElement history = await _client.GetFromJsonAsync<JsonElement>($"/instances/{instanceId}/events/history?limit=200");

        string[] types = history.GetProperty("items").EnumerateArray()
            .Select(e => e.GetProperty("eventType").GetString()!)
            .ToArray();

        types.Should().ContainInOrder(
            WorkflowEventTypes.WorkflowStarted,
            WorkflowEventTypes.ExecutorInvoked,
            WorkflowEventTypes.WorkflowTerminated);
    }

    [Fact]
    public async Task Event_history_sequences_are_gapless()
    {
        string instanceId = await _host.StartAsync(_client, "order", new OrderContext("ORD-SEQ"));
        await _host.WaitForStatusAsync(instanceId, InstanceStatus.Completed);

        JsonElement history = await _client.GetFromJsonAsync<JsonElement>($"/instances/{instanceId}/events/history?limit=200");

        long[] sequences = history.GetProperty("items").EnumerateArray()
            .Select(e => e.GetProperty("sequence").GetInt64())
            .ToArray();

        sequences.Should().Equal(Enumerable.Range(1, sequences.Length).Select(i => (long)i));
    }

    [Fact]
    public async Task History_paginates_with_a_cursor()
    {
        string instanceId = await _host.StartAsync(_client, "order", new OrderContext("ORD-PAGE"));
        await _host.WaitForStatusAsync(instanceId, InstanceStatus.Completed);

        JsonElement page = await _client.GetFromJsonAsync<JsonElement>($"/instances/{instanceId}/events/history?limit=2");

        page.GetProperty("items").GetArrayLength().Should().Be(2);
        page.GetProperty("nextCursor").GetString().Should().Be("2");
    }

    [Fact]
    public async Task History_can_be_filtered_by_type()
    {
        string instanceId = await _host.StartAsync(_client, "order", new OrderContext("ORD-FILTER"));
        await _host.WaitForStatusAsync(instanceId, InstanceStatus.Completed);

        JsonElement filtered = await _client.GetFromJsonAsync<JsonElement>(
            $"/instances/{instanceId}/events/history?types={WorkflowEventTypes.ExecutorCompleted}&limit=100");

        filtered.GetProperty("items").EnumerateArray()
            .Should().OnlyContain(e => e.GetProperty("eventType").GetString() == WorkflowEventTypes.ExecutorCompleted);
    }

    [Fact]
    public async Task History_in_sse_format_matches_the_stream_frame_shape()
    {
        string instanceId = await _host.StartAsync(_client, "order", new OrderContext("ORD-SSEFMT"));
        await _host.WaitForStatusAsync(instanceId, InstanceStatus.Completed);

        HttpResponseMessage response = await _client.GetAsync(
            $"/instances/{instanceId}/events/history?format=sse&limit=200");

        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");

        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain($"event: {WorkflowEventTypes.WorkflowStarted}");
        body.Should().MatchRegex(@"id: \d+");
        body.Should().Contain("data: {");
    }

    [Fact]
    public async Task The_live_stream_replays_history_for_a_terminal_instance_then_closes()
    {
        string instanceId = await _host.StartAsync(_client, "order", new OrderContext("ORD-SSE"));
        await _host.WaitForStatusAsync(instanceId, InstanceStatus.Completed);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        string body = await _client.GetStringAsync($"/instances/{instanceId}/events", cts.Token);

        body.Should().Contain($"event: {WorkflowEventTypes.WorkflowStarted}");
        body.Should().Contain($"event: {WorkflowEventTypes.WorkflowTerminated}");
    }

    [Fact]
    public async Task Last_event_id_resumes_without_replaying_earlier_events()
    {
        string instanceId = await _host.StartAsync(_client, "order", new OrderContext("ORD-RESUME"));
        await _host.WaitForStatusAsync(instanceId, InstanceStatus.Completed);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/instances/{instanceId}/events");
        request.Headers.Add("Last-Event-ID", "2");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        HttpResponseMessage response = await _client.SendAsync(request, cts.Token);
        string body = await response.Content.ReadAsStringAsync(cts.Token);

        body.Should().NotContain("id: 1\n");
        body.Should().NotContain("id: 2\n");
        body.Should().Contain("id: 3");
    }

    [Fact]
    public async Task The_graph_endpoint_reports_node_states_and_traversed_edges()
    {
        string instanceId = await _host.StartAsync(_client, "order", new OrderContext("ORD-GRAPH"));
        await _host.WaitForStatusAsync(instanceId, InstanceStatus.Completed);

        JsonElement graph = await _client.GetFromJsonAsync<JsonElement>($"/instances/{instanceId}/graph");

        graph.GetProperty("status").GetString().Should().Be("Completed");

        JsonElement nodes = graph.GetProperty("nodes");
        nodes.GetProperty("validate").GetString().Should().Be("Completed");
        nodes.GetProperty("submit").GetString().Should().Be("Completed");

        graph.GetProperty("traversedEdges").EnumerateArray()
            .Should().Contain(e => e.GetProperty("from").GetString() == "validate"
                                && e.GetProperty("to").GetString() == "submit");
    }

    [Fact]
    public async Task Logs_are_exposed_for_a_failed_instance()
    {
        string instanceId = await _host.StartAsync(_client, "order", new OrderContext("ORD-LOG", Fail: true));
        await _host.WaitForStatusAsync(instanceId, InstanceStatus.DeadStopped);

        JsonElement logs = await _client.GetFromJsonAsync<JsonElement>($"/instances/{instanceId}/logs?level=Error");

        logs.GetProperty("items").EnumerateArray().Should().NotBeEmpty();
    }

    [Fact]
    public async Task Health_probes_respond()
    {
        (await _client.GetAsync("/health/live")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _client.GetAsync("/health/ready")).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

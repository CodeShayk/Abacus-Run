using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Abacus.Run.Abstractions;
using Abacus.Run.Core;
using FluentAssertions;
using Xunit;

namespace Abacus.Run.IntegrationTests;

public class ApprovalFlowTests : IClassFixture<HostFixture>
{
    private readonly HostFixture _host;
    private readonly HttpClient _client;

    public ApprovalFlowTests(HostFixture host)
    {
        _host = host;
        _client = host.CreateClient();
    }

    private async Task<JsonElement> ApprovalForAsync(string instanceId)
    {
        JsonElement body = await _client.GetFromJsonAsync<JsonElement>($"/instances/{instanceId}/approvals");
        return body.GetProperty("items").EnumerateArray().First();
    }

    [Fact]
    public async Task A_below_threshold_order_runs_without_an_approval()
    {
        string instanceId = await _host.StartAsync(_client, "gated-order", new OrderContext("ORD-SMALL", Amount: 100m));

        await _host.WaitForStatusAsync(instanceId, InstanceStatus.Completed);

        JsonElement approvals = await _client.GetFromJsonAsync<JsonElement>($"/instances/{instanceId}/approvals");
        approvals.GetProperty("items").GetArrayLength().Should().Be(0);
        _host.Ledger.Entries.Should().Contain($"post-payment:{instanceId}");
    }

    [Fact]
    public async Task An_above_threshold_order_parks_before_the_side_effect()
    {
        string instanceId = await _host.StartAsync(_client, "gated-order", new OrderContext("ORD-BIG", Amount: 50_000m));

        await _host.WaitForStatusAsync(instanceId, InstanceStatus.AwaitingApproval);

        // The gated node must not have run: no side effect may precede a decision.
        _host.Ledger.Entries.Should().NotContain($"post-payment:{instanceId}");
        _host.Ledger.Entries.Should().Contain($"validate:{instanceId}");

        JsonElement approval = await ApprovalForAsync(instanceId);
        approval.GetProperty("executorId").GetString().Should().Be("post-payment");
        approval.GetProperty("reason").GetString().Should().Be("AmountAboveThreshold");
        approval.GetProperty("state").GetString().Should().Be("Pending");
        approval.GetProperty("allowModification").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task The_approval_request_is_relayed_on_the_same_stream_as_progress()
    {
        string instanceId = await _host.StartAsync(_client, "gated-order", new OrderContext("ORD-RELAY", Amount: 40_000m));
        await _host.WaitForStatusAsync(instanceId, InstanceStatus.AwaitingApproval);

        JsonElement history = await _client.GetFromJsonAsync<JsonElement>(
            $"/instances/{instanceId}/events/history?limit=200");

        JsonElement[] events = history.GetProperty("items").EnumerateArray().ToArray();
        string[] types = events.Select(e => e.GetProperty("eventType").GetString()!).ToArray();

        // Same transport, same sequence space, ordered with the surrounding executor events.
        types.Should().ContainInOrder(
            WorkflowEventTypes.WorkflowStarted,
            WorkflowEventTypes.ExecutorInvoked,
            WorkflowEventTypes.ApprovalRequested);

        events.Select(e => e.GetProperty("sequence").GetInt64()).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task The_approval_event_payload_is_enough_to_render_and_submit_a_decision()
    {
        string instanceId = await _host.StartAsync(_client, "gated-order", new OrderContext("ORD-PAYLOAD", Amount: 60_000m));
        await _host.WaitForStatusAsync(instanceId, InstanceStatus.AwaitingApproval);

        JsonElement history = await _client.GetFromJsonAsync<JsonElement>(
            $"/instances/{instanceId}/events/history?types={WorkflowEventTypes.ApprovalRequested}");

        JsonElement raised = history.GetProperty("items").EnumerateArray().First();
        using JsonDocument payload = JsonDocument.Parse(raised.GetProperty("payloadJson").GetString()!);

        JsonElement root = payload.RootElement;
        root.GetProperty("approvalId").GetString().Should().NotBeNullOrEmpty();
        root.GetProperty("executorId").GetString().Should().Be("post-payment");
        root.GetProperty("decisionUrl").GetString().Should().StartWith("/approvals/");
        root.GetProperty("expiresAt").GetDateTimeOffset().Should().BeAfter(DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task The_graph_shows_the_gated_node_awaiting_approval()
    {
        string instanceId = await _host.StartAsync(_client, "gated-order", new OrderContext("ORD-GGRAPH", Amount: 70_000m));
        await _host.WaitForStatusAsync(instanceId, InstanceStatus.AwaitingApproval);

        JsonElement graph = await _client.GetFromJsonAsync<JsonElement>($"/instances/{instanceId}/graph");

        graph.GetProperty("nodes").GetProperty("post-payment").GetString().Should().Be("AwaitingApproval");
        graph.GetProperty("nodes").GetProperty("validate").GetString().Should().Be("Completed");
    }

    [Fact]
    public async Task Approving_resumes_the_instance_and_runs_the_node_exactly_once()
    {
        string instanceId = await _host.StartAsync(_client, "gated-order", new OrderContext("ORD-APPROVE", Amount: 45_000m));
        await _host.WaitForStatusAsync(instanceId, InstanceStatus.AwaitingApproval);

        JsonElement approval = await ApprovalForAsync(instanceId);
        string approvalId = approval.GetProperty("approvalId").GetString()!;

        HttpResponseMessage decision = await _client.PostAsJsonAsync(
            $"/approvals/{approvalId}/decision", new { decision = "approve", comment = "verified" });

        decision.StatusCode.Should().Be(HttpStatusCode.Accepted);

        await _host.WaitForStatusAsync(instanceId, InstanceStatus.Completed);

        _host.Ledger.Entries.Count(e => e == $"post-payment:{instanceId}")
            .Should().Be(1, "the approved node must run exactly once");
    }

    [Fact]
    public async Task Rejecting_dead_stops_the_instance_and_the_node_never_runs()
    {
        string instanceId = await _host.StartAsync(_client, "gated-order", new OrderContext("ORD-REJECT", Amount: 55_000m));
        await _host.WaitForStatusAsync(instanceId, InstanceStatus.AwaitingApproval);

        JsonElement approval = await ApprovalForAsync(instanceId);
        string approvalId = approval.GetProperty("approvalId").GetString()!;

        await _client.PostAsJsonAsync($"/approvals/{approvalId}/decision",
            new { decision = "reject", comment = "duplicate" });

        await _host.WaitForStatusAsync(instanceId, InstanceStatus.DeadStopped);

        _host.Ledger.Entries.Should().NotContain($"post-payment:{instanceId}");
    }

    [Fact]
    public async Task A_second_decision_conflicts()
    {
        string instanceId = await _host.StartAsync(_client, "gated-order", new OrderContext("ORD-TWICE", Amount: 80_000m));
        await _host.WaitForStatusAsync(instanceId, InstanceStatus.AwaitingApproval);

        string approvalId = (await ApprovalForAsync(instanceId)).GetProperty("approvalId").GetString()!;

        await _client.PostAsJsonAsync($"/approvals/{approvalId}/decision", new { decision = "approve" });

        HttpResponseMessage second = await _client.PostAsJsonAsync(
            $"/approvals/{approvalId}/decision", new { decision = "reject" });

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task An_unknown_approval_is_not_found()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/approvals/apr_missing/decision", new { decision = "approve" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_unrecognised_decision_verb_is_a_validation_problem()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/approvals/apr_any/decision", new { decision = "maybe" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_approval_queue_surfaces_pending_work()
    {
        string instanceId = await _host.StartAsync(_client, "gated-order", new OrderContext("ORD-QUEUE", Amount: 90_000m));
        await _host.WaitForStatusAsync(instanceId, InstanceStatus.AwaitingApproval);

        JsonElement queue = await _client.GetFromJsonAsync<JsonElement>("/approvals?limit=100");

        queue.GetProperty("items").EnumerateArray()
            .Select(a => a.GetProperty("instanceId").GetString())
            .Should().Contain(instanceId);
    }

    [Fact]
    public async Task An_approval_can_be_fetched_by_id()
    {
        string instanceId = await _host.StartAsync(_client, "gated-order", new OrderContext("ORD-BYID", Amount: 35_000m));
        await _host.WaitForStatusAsync(instanceId, InstanceStatus.AwaitingApproval);

        string approvalId = (await ApprovalForAsync(instanceId)).GetProperty("approvalId").GetString()!;

        JsonElement fetched = await _client.GetFromJsonAsync<JsonElement>($"/approvals/{approvalId}");
        fetched.GetProperty("instanceId").GetString().Should().Be(instanceId);
    }

    [Fact]
    public async Task Cancelling_an_instance_cancels_its_pending_approval()
    {
        string instanceId = await _host.StartAsync(_client, "gated-order", new OrderContext("ORD-CANCEL", Amount: 65_000m));
        await _host.WaitForStatusAsync(instanceId, InstanceStatus.AwaitingApproval);

        await _client.PostAsJsonAsync($"/instances/{instanceId}/cancel", new { reason = "no longer needed" });

        await _host.WaitForStatusAsync(instanceId, InstanceStatus.Cancelled);

        JsonElement approval = await ApprovalForAsync(instanceId);
        approval.GetProperty("state").GetString().Should().Be("Cancelled",
            "approval queues must never retain orphaned work items");
    }
}

public class ControlActionTests : IClassFixture<HostFixture>
{
    private readonly HostFixture _host;
    private readonly HttpClient _client;

    public ControlActionTests(HostFixture host)
    {
        _host = host;
        _client = host.CreateClient();
    }

    [Fact]
    public async Task Cancelling_a_terminal_instance_conflicts_and_is_retry_safe()
    {
        string instanceId = await _host.StartAsync(_client, "order", new OrderContext("ORD-CT"));
        await _host.WaitForStatusAsync(instanceId, InstanceStatus.Completed);

        HttpResponseMessage response = await _client.PostAsJsonAsync(
            $"/instances/{instanceId}/cancel", new { reason = "too late" });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        WorkflowInstance instance = await _host.WaitForStatusAsync(instanceId, InstanceStatus.Completed);
        instance.Status.Should().Be(InstanceStatus.Completed, "a losing cancel must not alter the outcome");
    }

    [Fact]
    public async Task Cancelling_an_unknown_instance_is_not_found()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/instances/nope/cancel", new { reason = "x" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Rerun_restart_creates_a_new_instance_linked_to_the_source()
    {
        string original = await _host.StartAsync(_client, "order", new OrderContext("ORD-RR", Fail: true));
        await _host.WaitForStatusAsync(original, InstanceStatus.DeadStopped);

        HttpResponseMessage response = await _client.PostAsJsonAsync(
            $"/instances/{original}/rerun",
            new { mode = "restart", context = new OrderContext("ORD-RR-FIXED"), reason = "data corrected" });

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        JsonElement created = await response.Content.ReadFromJsonAsync<JsonElement>();
        string rerunId = created.GetProperty("instanceId").GetString()!;

        rerunId.Should().NotBe(original);
        created.GetProperty("rerunOfInstanceId").GetString().Should().Be(original);

        await _host.WaitForStatusAsync(rerunId, InstanceStatus.Completed);

        // The source stays terminal and untouched.
        WorkflowInstance source = await _host.WaitForStatusAsync(original, InstanceStatus.DeadStopped);
        source.Status.Should().Be(InstanceStatus.DeadStopped);
    }

    [Fact]
    public async Task Rerun_defaults_to_restart_when_no_mode_is_given()
    {
        string original = await _host.StartAsync(_client, "order", new OrderContext("ORD-DEFAULT", Fail: true));
        await _host.WaitForStatusAsync(original, InstanceStatus.DeadStopped);

        HttpResponseMessage response = await _client.PostAsJsonAsync(
            $"/instances/{original}/rerun", new { reason = "retry" });

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        JsonElement created = await response.Content.ReadFromJsonAsync<JsonElement>();
        created.GetProperty("instanceId").GetString().Should().NotBe(original);
    }

    [Fact]
    public async Task Rerun_resume_without_a_checkpoint_is_refused_with_a_remedy()
    {
        string original = await _host.StartAsync(_client, "order", new OrderContext("ORD-NOCK", Fail: true));
        await _host.WaitForStatusAsync(original, InstanceStatus.DeadStopped);

        HttpResponseMessage response = await _client.PostAsJsonAsync(
            $"/instances/{original}/rerun", new { mode = "resume" });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).Should().Contain("restart");
    }

    [Fact]
    public async Task Retry_now_is_refused_for_an_instance_that_is_not_retry_scheduled()
    {
        string instanceId = await _host.StartAsync(_client, "order", new OrderContext("ORD-RETRY"));
        await _host.WaitForStatusAsync(instanceId, InstanceStatus.Completed);

        HttpResponseMessage response = await _client.PostAsJsonAsync(
            $"/instances/{instanceId}/retry", new { reason = "force" });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Control_actions_are_recorded_on_the_instance_event_stream()
    {
        string instanceId = await _host.StartAsync(_client, "order", new OrderContext("ORD-AUDIT", Fail: true));
        await _host.WaitForStatusAsync(instanceId, InstanceStatus.DeadStopped);

        await _client.PostAsJsonAsync($"/instances/{instanceId}/rerun", new { mode = "restart", reason = "audit me" });

        JsonElement history = await _client.GetFromJsonAsync<JsonElement>(
            $"/instances/{instanceId}/events/history?limit=200");

        history.GetProperty("items").EnumerateArray()
            .Select(e => e.GetProperty("eventType").GetString())
            .Should().Contain(WorkflowEventTypes.InstanceRerunRequested);
    }

    [Fact]
    public async Task Checkpoints_are_queryable_for_an_instance()
    {
        string instanceId = await _host.StartAsync(_client, "order", new OrderContext("ORD-CK"));
        await _host.WaitForStatusAsync(instanceId, InstanceStatus.Completed);

        JsonElement checkpoints = await _client.GetFromJsonAsync<JsonElement>($"/instances/{instanceId}/checkpoints");

        checkpoints.GetProperty("items").EnumerateArray().Should().NotBeEmpty(
            "the engine checkpoints at superstep boundaries by default");
    }

    [Fact]
    public async Task Suspend_and_resume_are_refused_in_the_wrong_state()
    {
        string instanceId = await _host.StartAsync(_client, "order", new OrderContext("ORD-SUSP"));
        await _host.WaitForStatusAsync(instanceId, InstanceStatus.Completed);

        (await _client.PostAsJsonAsync($"/instances/{instanceId}/suspend", new { reason = "x" }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);

        (await _client.PostAsJsonAsync($"/instances/{instanceId}/resume", new { reason = "x" }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
    }
}

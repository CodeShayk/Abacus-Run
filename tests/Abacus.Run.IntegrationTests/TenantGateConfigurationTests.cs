using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Abacus.Run.Abstractions;
using FluentAssertions;
using Xunit;

namespace Abacus.Run.IntegrationTests;

/// <summary>
/// The configuration API itself: what a tenant can see, change, and is refused.
/// </summary>
public class NodeConfigurationApiTests : IClassFixture<HostFixture>
{
    private readonly HostFixture _host;

    public NodeConfigurationApiTests(HostFixture host) => _host = host;

    private HttpClient ClientFor(string tenantId)
    {
        HttpClient client = _host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenantId);
        return client;
    }

    private static JsonElement Node(JsonElement body, string executorId)
        => body.GetProperty("nodes").EnumerateArray()
            .Single(n => n.GetProperty("executorId").GetString() == executorId);

    [Fact]
    public async Task The_catalog_lists_every_executor_node_of_a_version()
    {
        HttpClient client = ClientFor("cat-tenant");

        JsonElement body = await client.GetFromJsonAsync<JsonElement>("/workflows/tenant-order/versions/1.0.0/nodes");

        body.GetProperty("workflowName").GetString().Should().Be("tenant-order");
        body.GetProperty("workflowVersion").GetString().Should().Be("1.0.0");
        body.GetProperty("tenantId").GetString().Should().Be("cat-tenant");

        body.GetProperty("nodes").EnumerateArray()
            .Select(n => n.GetProperty("executorId").GetString())
            .Should().Equal("prepare", "dispatch", "settle");
    }

    [Fact]
    public async Task Nodes_the_author_did_not_gate_are_autonomous_by_default()
    {
        JsonElement body = await ClientFor("default-tenant")
            .GetFromJsonAsync<JsonElement>("/workflows/tenant-order/versions/1.0.0/nodes");

        JsonElement dispatch = Node(body, "dispatch");
        dispatch.GetProperty("effective").GetProperty("mode").GetString().Should().Be("autonomous");
        dispatch.GetProperty("effectiveSource").GetString().Should().Be("definition");
        dispatch.GetProperty("tenantOverride").ValueKind.Should().Be(JsonValueKind.Null);
        dispatch.GetProperty("locked").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task A_locked_node_is_advertised_as_locked_with_its_declared_gate()
    {
        JsonElement body = await ClientFor("locked-tenant")
            .GetFromJsonAsync<JsonElement>("/workflows/tenant-order/versions/1.0.0/nodes");

        JsonElement settle = Node(body, "settle");
        settle.GetProperty("locked").GetBoolean().Should().BeTrue();
        settle.GetProperty("declared").GetProperty("mode").GetString().Should().Be("conditional");
        settle.GetProperty("declared").GetProperty("reason").GetString().Should().Be("RegulatedSettlement");
    }

    [Fact]
    public async Task Configuring_a_node_is_reflected_in_the_catalog_for_that_tenant_only()
    {
        HttpClient owner = ClientFor("api-owner");
        HttpClient bystander = ClientFor("api-bystander");

        HttpResponseMessage put = await owner.PutAsJsonAsync(
            "/workflows/tenant-order/versions/1.0.0/nodes/dispatch",
            new { mode = "requireApproval", reason = "four eyes", assignees = new[] { "group:ops" } });

        put.StatusCode.Should().Be(HttpStatusCode.OK);

        JsonElement dispatch = Node(await put.Content.ReadFromJsonAsync<JsonElement>(), "dispatch");
        dispatch.GetProperty("effective").GetProperty("mode").GetString().Should().Be("requireApproval");
        dispatch.GetProperty("effectiveSource").GetString().Should().Be("tenant");
        dispatch.GetProperty("tenantOverride").GetProperty("reason").GetString().Should().Be("four eyes");

        JsonElement theirs = await bystander.GetFromJsonAsync<JsonElement>("/workflows/tenant-order/versions/1.0.0/nodes");
        Node(theirs, "dispatch").GetProperty("effective").GetProperty("mode").GetString().Should().Be("autonomous");
    }

    [Fact]
    public async Task Several_nodes_can_be_configured_in_one_call()
    {
        HttpClient client = ClientFor("api-bulk");

        HttpResponseMessage put = await client.PutAsJsonAsync("/workflows/tenant-order/versions/1.0.0/nodes", new
        {
            nodes = new Dictionary<string, object>
            {
                ["prepare"] = new { mode = "requireApproval" },
                ["dispatch"] = new { mode = "requireApproval" }
            }
        });

        put.StatusCode.Should().Be(HttpStatusCode.OK);

        JsonElement body = await put.Content.ReadFromJsonAsync<JsonElement>();
        Node(body, "prepare").GetProperty("effectiveSource").GetString().Should().Be("tenant");
        Node(body, "dispatch").GetProperty("effectiveSource").GetString().Should().Be("tenant");
    }

    [Fact]
    public async Task Deleting_a_configuration_restores_the_declared_gate()
    {
        HttpClient client = ClientFor("api-reset");

        await client.PutAsJsonAsync(
            "/workflows/tenant-order/versions/1.0.0/nodes/dispatch", new { mode = "requireApproval" });

        HttpResponseMessage delete = await client.DeleteAsync("/workflows/tenant-order/versions/1.0.0/nodes/dispatch");
        delete.StatusCode.Should().Be(HttpStatusCode.OK);

        JsonElement dispatch = Node(await delete.Content.ReadFromJsonAsync<JsonElement>(), "dispatch");
        dispatch.GetProperty("effective").GetProperty("mode").GetString().Should().Be("autonomous");
        dispatch.GetProperty("tenantOverride").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Un_gating_a_locked_node_is_a_conflict()
    {
        HttpClient client = ClientFor("api-locked");

        HttpResponseMessage put = await client.PutAsJsonAsync(
            "/workflows/tenant-order/versions/1.0.0/nodes/settle", new { mode = "autonomous" });

        put.StatusCode.Should().Be(HttpStatusCode.Conflict);

        JsonElement body = await client.GetFromJsonAsync<JsonElement>("/workflows/tenant-order/versions/1.0.0/nodes");
        Node(body, "settle").GetProperty("tenantOverride").ValueKind
            .Should().Be(JsonValueKind.Null, "a refused write must persist nothing");
    }

    [Fact]
    public async Task Tightening_a_locked_node_is_allowed()
    {
        HttpClient client = ClientFor("api-tighten");

        HttpResponseMessage put = await client.PutAsJsonAsync(
            "/workflows/tenant-order/versions/1.0.0/nodes/settle",
            new { mode = "requireApproval", requiredApprovers = 2 });

        put.StatusCode.Should().Be(HttpStatusCode.OK);

        JsonElement settle = Node(await put.Content.ReadFromJsonAsync<JsonElement>(), "settle");
        settle.GetProperty("effective").GetProperty("mode").GetString().Should().Be("requireApproval");
        settle.GetProperty("effective").GetProperty("requiredApprovers").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task An_unknown_workflow_version_or_executor_is_not_found()
    {
        HttpClient client = ClientFor("api-404");

        (await client.GetAsync("/workflows/nope/versions/1.0.0/nodes"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync("/workflows/tenant-order/versions/9.9.9/nodes"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PutAsJsonAsync("/workflows/tenant-order/versions/1.0.0/nodes/ghost", new { mode = "autonomous" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_unsupported_mode_is_a_validation_problem()
    {
        HttpClient client = ClientFor("api-invalid");

        HttpResponseMessage put = await client.PutAsJsonAsync(
            "/workflows/tenant-order/versions/1.0.0/nodes/dispatch", new { mode = "conditional" });

        put.StatusCode.Should().Be(HttpStatusCode.BadRequest, "a predicate cannot be supplied over HTTP");
    }
}

/// <summary>
/// The point of the whole feature: what the runtime does with a tenant's configuration.
/// </summary>
public class TenantGatedExecutionTests : IClassFixture<HostFixture>
{
    private readonly HostFixture _host;

    public TenantGatedExecutionTests(HostFixture host) => _host = host;

    private HttpClient ClientFor(string tenantId)
    {
        HttpClient client = _host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenantId);
        return client;
    }

    [Fact]
    public async Task With_no_configuration_every_node_runs_autonomously()
    {
        HttpClient client = ClientFor("run-default");

        string instanceId = await _host.StartAsync(
            client, "tenant-order", new OrderContext("ORD-AUTO", Amount: 100m), tenantId: "run-default");

        await _host.WaitForStatusAsync(instanceId, InstanceStatus.Completed);

        _host.Ledger.Entries.Should().Contain($"dispatch:{instanceId}");
        _host.Ledger.Entries.Should().Contain($"settle:{instanceId}");
    }

    [Fact]
    public async Task A_tenant_that_requires_approval_parks_before_the_side_effect()
    {
        HttpClient client = ClientFor("run-gated");

        HttpResponseMessage put = await client.PutAsJsonAsync(
            "/workflows/tenant-order/versions/1.0.0/nodes/dispatch",
            new { mode = "requireApproval", reason = "TenantPolicy", assignees = new[] { "group:ops" } });
        put.EnsureSuccessStatusCode();

        string instanceId = await _host.StartAsync(
            client, "tenant-order", new OrderContext("ORD-GATED", Amount: 100m), tenantId: "run-gated");

        await _host.WaitForStatusAsync(instanceId, InstanceStatus.AwaitingApproval);

        _host.Ledger.Entries.Should().Contain($"prepare:{instanceId}");
        _host.Ledger.Entries.Should().NotContain($"dispatch:{instanceId}",
            "the tenant's gate must stop the node before its side effect");

        JsonElement approvals = await client.GetFromJsonAsync<JsonElement>($"/instances/{instanceId}/approvals");
        JsonElement approval = approvals.GetProperty("items").EnumerateArray().Single();
        approval.GetProperty("executorId").GetString().Should().Be("dispatch");
        approval.GetProperty("reason").GetString().Should().Be("TenantPolicy");
        approval.GetProperty("assignees").EnumerateArray().Select(a => a.GetString()).Should().Contain("group:ops");
    }

    [Fact]
    public async Task Approving_a_tenant_configured_gate_resumes_the_run()
    {
        HttpClient client = ClientFor("run-approve");

        (await client.PutAsJsonAsync(
            "/workflows/tenant-order/versions/1.0.0/nodes/dispatch",
            new { mode = "requireApproval" })).EnsureSuccessStatusCode();

        string instanceId = await _host.StartAsync(
            client, "tenant-order", new OrderContext("ORD-RESUME", Amount: 100m), tenantId: "run-approve");

        await _host.WaitForStatusAsync(instanceId, InstanceStatus.AwaitingApproval);

        JsonElement approvals = await client.GetFromJsonAsync<JsonElement>($"/instances/{instanceId}/approvals");
        string approvalId = approvals.GetProperty("items").EnumerateArray().Single()
            .GetProperty("approvalId").GetString()!;

        (await client.PostAsJsonAsync($"/approvals/{approvalId}/decision", new { decision = "approve" }))
            .StatusCode.Should().Be(HttpStatusCode.Accepted);

        await _host.WaitForStatusAsync(instanceId, InstanceStatus.Completed);

        _host.Ledger.Entries.Count(e => e == $"dispatch:{instanceId}")
            .Should().Be(1, "the approved node runs exactly once");
    }

    [Fact]
    public async Task One_tenants_gate_does_not_stop_another_tenants_run()
    {
        HttpClient cautious = ClientFor("run-cautious");
        (await cautious.PutAsJsonAsync(
            "/workflows/tenant-order/versions/1.0.0/nodes/dispatch",
            new { mode = "requireApproval" })).EnsureSuccessStatusCode();

        string gated = await _host.StartAsync(
            cautious, "tenant-order", new OrderContext("ORD-CAUTIOUS", Amount: 100m), tenantId: "run-cautious");
        string free = await _host.StartAsync(
            ClientFor("run-relaxed"), "tenant-order", new OrderContext("ORD-RELAXED", Amount: 100m),
            tenantId: "run-relaxed");

        await _host.WaitForStatusAsync(gated, InstanceStatus.AwaitingApproval);
        await _host.WaitForStatusAsync(free, InstanceStatus.Completed);

        _host.Ledger.Entries.Should().NotContain($"dispatch:{gated}");
        _host.Ledger.Entries.Should().Contain($"dispatch:{free}");
    }

    [Fact]
    public async Task Removing_the_configuration_lets_the_next_run_go_through()
    {
        HttpClient client = ClientFor("run-reset");

        (await client.PutAsJsonAsync(
            "/workflows/tenant-order/versions/1.0.0/nodes/dispatch",
            new { mode = "requireApproval" })).EnsureSuccessStatusCode();

        string parked = await _host.StartAsync(
            client, "tenant-order", new OrderContext("ORD-BEFORE", Amount: 100m), tenantId: "run-reset");
        await _host.WaitForStatusAsync(parked, InstanceStatus.AwaitingApproval);

        (await client.DeleteAsync("/workflows/tenant-order/versions/1.0.0/nodes/dispatch")).EnsureSuccessStatusCode();

        string after = await _host.StartAsync(
            client, "tenant-order", new OrderContext("ORD-AFTER", Amount: 100m), tenantId: "run-reset");

        await _host.WaitForStatusAsync(after, InstanceStatus.Completed);
        _host.Ledger.Entries.Should().Contain($"dispatch:{after}");
    }

    [Fact]
    public async Task A_locked_gate_still_stops_a_tenant_that_tried_to_remove_it()
    {
        HttpClient client = ClientFor("run-locked");

        (await client.PutAsJsonAsync(
            "/workflows/tenant-order/versions/1.0.0/nodes/settle", new { mode = "autonomous" }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);

        string instanceId = await _host.StartAsync(
            client, "tenant-order", new OrderContext("ORD-LOCKED", Amount: 50_000m), tenantId: "run-locked");

        await _host.WaitForStatusAsync(instanceId, InstanceStatus.AwaitingApproval);

        _host.Ledger.Entries.Should().NotContain($"settle:{instanceId}");

        JsonElement approvals = await client.GetFromJsonAsync<JsonElement>($"/instances/{instanceId}/approvals");
        approvals.GetProperty("items").EnumerateArray().Single()
            .GetProperty("executorId").GetString().Should().Be("settle");
    }

    [Fact]
    public async Task A_locked_conditional_gate_still_lets_a_below_threshold_run_through()
    {
        HttpClient client = ClientFor("run-below");

        string instanceId = await _host.StartAsync(
            client, "tenant-order", new OrderContext("ORD-BELOW", Amount: 10m), tenantId: "run-below");

        await _host.WaitForStatusAsync(instanceId, InstanceStatus.Completed);
        _host.Ledger.Entries.Should().Contain($"settle:{instanceId}");
    }
}

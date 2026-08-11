using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Abacus.Run.Abstractions;
using FluentAssertions;
using Xunit;

namespace Abacus.Run.IntegrationTests;

/// <summary>
/// <c>GET /workflows/{name}/instances/{id}/state</c> — lifecycle status plus whatever audit record
/// the workflow declared. The endpoint is generic: it presents the sections the definition declared
/// and knows nothing about any particular workflow.
/// </summary>
public class InstanceStateTests : IClassFixture<HostFixture>
{
    private readonly HostFixture _fixture;

    public InstanceStateTests(HostFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task State_returns_the_audit_record_the_workflow_declared()
    {
        using HttpClient client = _fixture.CreateClient();

        string instanceId = await _fixture.StartAsync(client, "audited-order", new OrderContext("ORD-STATE-1", 250m));
        await _fixture.WaitForStatusAsync(instanceId, InstanceStatus.Completed);

        JsonElement state = await client.GetFromJsonAsync<JsonElement>(
            $"/workflows/audited-order/instances/{instanceId}/state");

        state.GetProperty("instance").GetProperty("status").GetString().Should().Be("Completed");

        JsonElement audit = state.GetProperty("audit");
        audit.GetProperty("rootKind").GetString().Should().Be("order");
        audit.GetProperty("rootKey").GetString().Should().Be("ORD-STATE-1");
        audit.GetProperty("status").GetString().Should().Be("Completed");
        audit.GetProperty("closedUtc").ValueKind.Should().NotBe(JsonValueKind.Null);
        audit.GetProperty("attributes").GetProperty("amount").GetDecimal().Should().Be(250m);

        JsonElement[] sections = [.. audit.GetProperty("sections").EnumerateArray()];

        sections.Select(s => s.GetProperty("kind").GetString())
            .Should().ContainInOrder(["submission", "step", "outcome"],
                "the declaration order is the presentation order");

        JsonElement steps = sections.Single(s => s.GetProperty("kind").GetString() == "step");
        steps.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("key").GetString())
            .Should().ContainInOrder(["validate", "submit"]);

        JsonElement outcome = sections.Single(s => s.GetProperty("kind").GetString() == "outcome");
        outcome.GetProperty("entries")[0].GetProperty("payload").GetProperty("status").GetString()
            .Should().Be("submitted");
    }

    [Fact]
    public async Task A_workflow_that_declares_no_record_reports_no_audit()
    {
        using HttpClient client = _fixture.CreateClient();

        string instanceId = await _fixture.StartAsync(client, "order", new OrderContext("ORD-STATE-2"));
        await _fixture.WaitForStatusAsync(instanceId, InstanceStatus.Completed);

        JsonElement state = await client.GetFromJsonAsync<JsonElement>(
            $"/workflows/order/instances/{instanceId}/state");

        state.GetProperty("instance").GetProperty("instanceId").GetString().Should().Be(instanceId);
        state.GetProperty("audit").ValueKind.Should().Be(JsonValueKind.Null,
            "auditing is opt-in; a workflow that declares nothing records nothing");
    }

    [Fact]
    public async Task A_section_filter_narrows_the_record_without_changing_its_shape()
    {
        using HttpClient client = _fixture.CreateClient();

        string instanceId = await _fixture.StartAsync(client, "audited-order", new OrderContext("ORD-STATE-3"));
        await _fixture.WaitForStatusAsync(instanceId, InstanceStatus.Completed);

        JsonElement state = await client.GetFromJsonAsync<JsonElement>(
            $"/workflows/audited-order/instances/{instanceId}/state?section=step");

        JsonElement[] sections = [.. state.GetProperty("audit").GetProperty("sections").EnumerateArray()];

        sections.Should().ContainSingle();
        sections[0].GetProperty("kind").GetString().Should().Be("step");
        sections[0].GetProperty("entries").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task Reading_an_instance_through_another_workflows_route_is_not_found()
    {
        using HttpClient client = _fixture.CreateClient();

        string instanceId = await _fixture.StartAsync(client, "audited-order", new OrderContext("ORD-STATE-4"));
        await _fixture.WaitForStatusAsync(instanceId, InstanceStatus.Completed);

        HttpResponseMessage response = await client.GetAsync($"/workflows/order/instances/{instanceId}/state");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "the workflow segment identifies the resource, so a mismatch is a wrong URL");
    }

    [Fact]
    public async Task An_unknown_instance_is_not_found()
    {
        using HttpClient client = _fixture.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/workflows/audited-order/instances/does-not-exist/state");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}

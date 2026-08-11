using System.Net.Http.Json;
using System.Text.Json;
using Abacus.Run.Abstractions;
using Abacus.Run.Service.Infrastructure.Auditing;
using Abacus.Run.Service.Workflows.ExampleOrder;
using FluentAssertions;
using Xunit;

namespace Abacus.Run.IntegrationTests;

/// <summary>
/// The example workflow the host ships, driven end to end through the real API.
/// </summary>
/// <remarks>
/// <see cref="InstanceStateTests"/> covers the state route against a fixture-local workflow, which
/// proves the endpoint is generic. These tests cover the other half: that a workflow registered by
/// the host in the ordinary way — declaring a record, calling the recorder from its executors —
/// produces the record a reader expects, on the success path and the failure path, and that the
/// host's durable store is what backs it.
/// </remarks>
public class ExampleOrderWorkflowTests : IClassFixture<HostFixture>
{
    private const string Workflow = "example-order";

    private readonly HostFixture _fixture;

    public ExampleOrderWorkflowTests(HostFixture fixture) => _fixture = fixture;

    private static ExampleOrderContext Order(string orderId, string? failOnSku = null) => new(
        orderId,
        [
            new ExampleOrderLine("SKU-A", 2, 10.50m),
            new ExampleOrderLine("SKU-B", 1, 99.00m)
        ],
        failOnSku);

    [Fact]
    public async Task The_example_workflow_is_registered_and_discoverable()
    {
        using HttpClient client = _fixture.CreateClient();

        JsonElement catalog = await client.GetFromJsonAsync<JsonElement>("/workflows");

        catalog.EnumerateArray().Select(w => w.GetProperty("name").GetString())
            .Should().Contain(Workflow, "the host registers the example in Program.cs");
    }

    [Fact]
    public async Task A_completed_run_records_every_declared_section()
    {
        using HttpClient client = _fixture.CreateClient();

        string instanceId = await _fixture.StartAsync(client, Workflow, Order("ORD-EX-1"));
        await _fixture.WaitForStatusAsync(instanceId, InstanceStatus.Completed);

        JsonElement audit = await ReadAuditAsync(client, instanceId);

        audit.GetProperty("rootKind").GetString().Should().Be("order");
        audit.GetProperty("rootKey").GetString().Should().Be("ORD-EX-1",
            "the root key is the workflow's own identifier for the thing being audited");
        audit.GetProperty("status").GetString().Should().Be(AuditRecordStatus.Completed);
        audit.GetProperty("closedUtc").ValueKind.Should().NotBe(JsonValueKind.Null);
        audit.GetProperty("attributes").GetProperty("lineCount").GetInt32().Should().Be(2);

        JsonElement[] sections = [.. audit.GetProperty("sections").EnumerateArray()];

        sections.Select(s => s.GetProperty("kind").GetString())
            .Should().ContainInOrder(["submission", "plan", "line", "outcome"],
                "the declaration order is the presentation order");

        Section(sections, ExampleOrderAuditRecord.Submission)
            .GetProperty("entries")[0].GetProperty("payload").GetProperty("orderId").GetString()
            .Should().Be("ORD-EX-1");

        Section(sections, ExampleOrderAuditRecord.Plan)
            .GetProperty("entries")[0].GetProperty("payload").GetProperty("skus")
            .EnumerateArray().Select(s => s.GetString())
            .Should().Equal("SKU-A", "SKU-B");

        JsonElement outcome = Section(sections, ExampleOrderAuditRecord.Outcome);
        outcome.GetProperty("entries")[0].GetProperty("payload").GetProperty("total").GetDecimal()
            .Should().Be(120.00m, "2 x 10.50 plus 1 x 99.00");
    }

    [Fact]
    public async Task Line_entries_are_keyed_by_sku_and_ordered_by_sequence()
    {
        using HttpClient client = _fixture.CreateClient();

        string instanceId = await _fixture.StartAsync(client, Workflow, Order("ORD-EX-2"));
        await _fixture.WaitForStatusAsync(instanceId, InstanceStatus.Completed);

        JsonElement audit = await ReadAuditAsync(client, instanceId);
        JsonElement lines = Section([.. audit.GetProperty("sections").EnumerateArray()], ExampleOrderAuditRecord.Line);

        lines.GetProperty("multiple").GetBoolean().Should().BeTrue();

        JsonElement[] entries = [.. lines.GetProperty("entries").EnumerateArray()];

        entries.Select(e => e.GetProperty("key").GetString())
            .Should().ContainInOrder(["SKU-A", "SKU-B"], "entries come back in recorded order");

        entries.Select(e => e.GetProperty("sequence").GetInt32())
            .Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();

        entries[0].GetProperty("payload").GetProperty("lineTotal").GetDecimal().Should().Be(21.00m);
    }

    [Fact]
    public async Task A_failed_run_records_the_failure_that_stopped_it()
    {
        using HttpClient client = _fixture.CreateClient();

        string instanceId = await _fixture.StartAsync(client, Workflow, Order("ORD-EX-3", failOnSku: "SKU-B"));
        await _fixture.WaitForStatusAsync(instanceId, InstanceStatus.DeadStopped);

        JsonElement audit = await ReadAuditAsync(client, instanceId);

        audit.GetProperty("status").GetString().Should().Be(AuditRecordStatus.Failed);

        JsonElement[] sections = [.. audit.GetProperty("sections").EnumerateArray()];

        JsonElement outcome = Section(sections, ExampleOrderAuditRecord.Outcome).GetProperty("entries")[0]
            .GetProperty("payload");
        outcome.GetProperty("status").GetString().Should().Be("Failed");
        outcome.GetProperty("failedSku").GetString().Should().Be("SKU-B",
            "the record is written before the exception propagates, so it explains the failure it caused");

        Section(sections, ExampleOrderAuditRecord.Line).GetProperty("entries")
            .EnumerateArray().Select(e => e.GetProperty("key").GetString())
            .Should().ContainSingle("the line priced before the failure is still on the record")
            .Which.Should().Be("SKU-A");
    }

    [Fact]
    public async Task Sections_untouched_by_a_run_come_back_empty_rather_than_missing()
    {
        using HttpClient client = _fixture.CreateClient();

        // No lines: the workflow opens the record, plans, and settles without pricing anything.
        string instanceId = await _fixture.StartAsync(
            client, Workflow, new ExampleOrderContext("ORD-EX-4", Lines: []));
        await _fixture.WaitForStatusAsync(instanceId, InstanceStatus.Completed);

        JsonElement audit = await ReadAuditAsync(client, instanceId);
        JsonElement[] sections = [.. audit.GetProperty("sections").EnumerateArray()];

        sections.Should().HaveCount(4, "every declared section is presented, recorded into or not");
        Section(sections, ExampleOrderAuditRecord.Line).GetProperty("entries").GetArrayLength()
            .Should().Be(0, "an outstanding section is visible as empty, not absent");
    }

    [Fact]
    public async Task A_section_filter_narrows_the_example_record()
    {
        using HttpClient client = _fixture.CreateClient();

        string instanceId = await _fixture.StartAsync(client, Workflow, Order("ORD-EX-5"));
        await _fixture.WaitForStatusAsync(instanceId, InstanceStatus.Completed);

        JsonElement state = await client.GetFromJsonAsync<JsonElement>(
            $"/workflows/{Workflow}/instances/{instanceId}/state?section=plan,outcome");

        state.GetProperty("audit").GetProperty("sections").EnumerateArray()
            .Select(s => s.GetProperty("kind").GetString())
            .Should().Equal("plan", "outcome");
    }

    [Fact]
    public async Task The_record_is_held_by_the_hosts_durable_store()
    {
        using HttpClient client = _fixture.CreateClient();

        string instanceId = await _fixture.StartAsync(client, Workflow, Order("ORD-EX-6"));
        await _fixture.WaitForStatusAsync(instanceId, InstanceStatus.Completed);

        var store = _fixture.Resolve<IAuditRecordStore>();
        store.Should().BeOfType<SqliteAuditRecordStore>(
            "the host displaces the framework's in-memory default");

        // Read straight from the store rather than through the API: the record outlives the request
        // that produced it, which is the whole point of substituting a durable store.
        AuditRecordDocument? document = await store.GetAsync(instanceId, default);

        document.Should().NotBeNull();
        document!.Root.RootKey.Should().Be("ORD-EX-6");
        document.Root.WorkflowName.Should().Be(Workflow);
        document.Entries.Should().HaveCount(5, "submission, plan, two lines, outcome");

        IReadOnlyList<AuditRecordRoot> roots = await store.ListAsync(Workflow, "ORD-EX-6", null, 10, default);
        roots.Should().ContainSingle().Which.InstanceId.Should().Be(instanceId);
    }

    private static async Task<JsonElement> ReadAuditAsync(HttpClient client, string instanceId)
    {
        JsonElement state = await client.GetFromJsonAsync<JsonElement>(
            $"/workflows/{Workflow}/instances/{instanceId}/state");

        JsonElement audit = state.GetProperty("audit");
        audit.ValueKind.Should().NotBe(JsonValueKind.Null, "the example workflow declares a record");
        return audit;
    }

    private static JsonElement Section(JsonElement[] sections, string kind) =>
        sections.Single(s => s.GetProperty("kind").GetString() == kind);
}

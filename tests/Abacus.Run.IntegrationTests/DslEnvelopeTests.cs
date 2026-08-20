using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Abacus.Run.Abstractions;
using Abacus.Run.Core;
using Abacus.Run.Persistence;
using FluentAssertions;
using Xunit;

namespace Abacus.Run.IntegrationTests;

/// <summary>
/// The two things the envelope is charged with getting right. It carries the whole start context to
/// every node, which is what makes the DSL usable — and is also two liabilities: more data in flight
/// per message, and a context that could be copied per hop. Both are asserted here against the real
/// host rather than argued about in the design doc.
/// </summary>
public class DslRedactionTests : IClassFixture<DslRedactedHostFixture>
{
    private const string Card = "4111111111111111";

    private readonly DslRedactedHostFixture _fixture;

    public DslRedactionTests(DslRedactedHostFixture fixture) => _fixture = fixture;

    [Fact]
    public void The_restrictive_policy_is_the_one_the_host_is_running()
        => _fixture.Resolve<IRedactionPolicy>().Should().BeSameAs(_fixture.Policy,
            "every assertion in this class is vacuous under the permissive default");

    /// <summary>
    /// The node reads the secret — it has to, that is the point of the envelope — and the event that
    /// records what the node did does not carry it. Redaction is applied at write time, so the
    /// history API cannot leak what the live stream withheld.
    /// </summary>
    [Fact]
    public async Task A_secret_in_the_context_reaches_the_node_but_not_the_event_log()
    {
        using HttpClient client = _fixture.CreateClient();

        string id = await _fixture.StartAsync(client, "dsl-secret",
            new { orderId = "ORD-SECRET", amount = 10m, cardNumber = Card });

        await _fixture.WaitForStatusAsync(id, InstanceStatus.Completed);

        JsonNode? result = await _fixture.ResultOfAsync(id);
        result!["card"]!.GetValue<string>().Should().Be(Card, "the node is entitled to the value");
        result["cardLength"]!.GetValue<int>().Should().Be(Card.Length, "and to compute over it");

        JsonNode payload = await CustomPayloadAsync(client, id, "custom.handled");

        payload["card"]!.GetValue<string>().Should().Be(RedactionPolicy.Mask,
            "the card is not on the allow-list, so nothing that leaves the process may carry it");
        payload["order"]!.GetValue<string>().Should().Be("ORD-SECRET",
            "an allow-listed field still arrives, or the policy would be a mute button");
    }

    /// <summary>
    /// The sweep, not the sample. A DSL run emits lifecycle events, node events and the workflow's own
    /// notification, and the secret must be in none of them — including anywhere the envelope was
    /// serialized wholesale rather than field by field.
    /// </summary>
    [Fact]
    public async Task No_event_in_the_whole_run_carries_the_secret()
    {
        using HttpClient client = _fixture.CreateClient();

        string id = await _fixture.StartAsync(client, "dsl-secret",
            new { orderId = "ORD-SWEEP", amount = 10m, cardNumber = Card });

        await _fixture.WaitForStatusAsync(id, InstanceStatus.Completed);

        JsonElement history = await client.GetFromJsonAsync<JsonElement>($"/instances/{id}/events/history");
        JsonElement[] items = [.. history.GetProperty("items").EnumerateArray()];

        items.Should().NotBeEmpty("a completed run always logged something");

        foreach (JsonElement item in items)
        {
            item.GetRawText().Should().NotContain(Card,
                $"'{Read(item, "eventType")}' leaked the card number");
        }
    }

    /// <summary>
    /// The instance store is inside the trust boundary and is not redacted — the run has to be able to
    /// resume from its own state. Asserted so the previous test cannot be satisfied by a change that
    /// masks the workflow's data everywhere, which would break resumption instead of protecting it.
    /// </summary>
    [Fact]
    public async Task The_instance_state_still_carries_what_the_run_needs()
    {
        using HttpClient client = _fixture.CreateClient();

        string id = await _fixture.StartAsync(client, "dsl-secret",
            new { orderId = "ORD-STATE", amount = 10m, cardNumber = Card });

        await _fixture.WaitForStatusAsync(id, InstanceStatus.Completed);

        WorkflowInstance instance = (await _fixture.Resolve<IInstanceStore>().GetAsync(id, default))!;
        instance.ContextJson.Should().Contain(Card, "the context is the run's own input");
    }

    private static async Task<JsonNode> CustomPayloadAsync(HttpClient client, string id, string eventType)
    {
        JsonElement history = await client.GetFromJsonAsync<JsonElement>($"/instances/{id}/events/history");

        JsonElement[] matching = [.. history.GetProperty("items").EnumerateArray()
            .Where(e => Read(e, "eventType") == eventType)];

        matching.Should().ContainSingle(
            "the event log held: " + string.Join(", ", history.GetProperty("items").EnumerateArray()
                .Select(e => Read(e, "eventType"))));

        JsonElement raw = matching[0].GetProperty("payloadJson");

        return (raw.ValueKind == JsonValueKind.String
            ? JsonNode.Parse(raw.GetString()!)
            : JsonNode.Parse(raw.GetRawText()))!;
    }

    private static string Read(JsonElement item, string property)
        => item.TryGetProperty(property, out JsonElement value) ? value.GetString() ?? "?" : "?";
}

/// <summary>
/// The envelope's cost. <c>Ctx</c> is cloned once at start and reference-copied after, so a run's
/// checkpoint should be about the size of its context however many nodes it passes through. A
/// regression here — a deep clone per hop, or an envelope that accumulates — shows up as growth.
/// </summary>
public class DslLargeContextTests : IClassFixture<DslHostFixture>
{
    private const int ContextBytes = 128 * 1024;

    private readonly DslHostFixture _fixture;

    public DslLargeContextTests(DslHostFixture fixture) => _fixture = fixture;

    private static object LargeContext(string orderId) => new
    {
        orderId,
        amount = 1m,
        notes = new string('n', ContextBytes)
    };

    [Fact]
    public async Task A_large_context_survives_every_hop_intact()
    {
        using HttpClient client = _fixture.CreateClient();

        string id = await _fixture.StartAsync(client, "dsl-wide", LargeContext("ORD-LARGE"));
        await _fixture.WaitForStatusAsync(id, InstanceStatus.Completed);

        JsonNode? result = await _fixture.ResultOfAsync(id);

        result!["hop"]!.GetValue<int>().Should().Be(4);
        result["order"]!.GetValue<string>().Should().Be("ORD-LARGE");
        result["seen"]!.GetValue<int>().Should().Be(ContextBytes,
            "the fourth node read the context, not a truncated copy of it");
        result["size"]!.GetValue<int>().Should().Be(ContextBytes,
            "and the value the first node measured travelled with the envelope");
    }

    /// <summary>
    /// One copy in flight, not one per node visited. The bound is generous — serialization overhead,
    /// the envelope's own fields and the framework's queue all sit inside it — but it is a small
    /// multiple of the context rather than a multiple of the hop count, which is the claim.
    /// </summary>
    [Fact]
    public async Task The_checkpoint_does_not_grow_per_hop()
    {
        using HttpClient client = _fixture.CreateClient();

        string id = await _fixture.StartAsync(client, "dsl-wide", LargeContext("ORD-CHECKPOINT"));
        await _fixture.WaitForStatusAsync(id, InstanceStatus.Completed);

        IReadOnlyList<CheckpointRecord> checkpoints =
            _fixture.Resolve<OverflowCheckpointStore>().Describe(id);

        checkpoints.Should().NotBeEmpty("the engine checkpoints each superstep");

        int[] sizes = [.. checkpoints.Select(c => c.SizeBytes)];

        sizes.Max().Should().BeLessThan(ContextBytes * 4,
            $"a checkpoint should hold about one context, not one per hop; sizes were {string.Join(", ", sizes)}");

        // The shape that matters: the last superstep is no heavier than the first. Growth here means
        // the envelope started accumulating something.
        sizes[^1].Should().BeLessThan((int)(sizes[0] * 1.5),
            $"checkpoints grew across the run: {string.Join(", ", sizes)}");
    }

    /// <summary>
    /// The same run with a small context, to show the size above is the context's and not the
    /// framework's. Without this the bound could be met by a checkpoint that ignored the context
    /// entirely — which would mean the run could not resume.
    /// </summary>
    [Fact]
    public async Task A_checkpoint_is_the_size_of_its_context()
    {
        using HttpClient client = _fixture.CreateClient();

        string small = await _fixture.StartAsync(client, "dsl-wide",
            new { orderId = "ORD-SMALL", amount = 1m, notes = "n" });
        await _fixture.WaitForStatusAsync(small, InstanceStatus.Completed);

        string large = await _fixture.StartAsync(client, "dsl-wide", LargeContext("ORD-BIG"));
        await _fixture.WaitForStatusAsync(large, InstanceStatus.Completed);

        var store = _fixture.Resolve<OverflowCheckpointStore>();

        int smallest = store.Describe(small).Max(c => c.SizeBytes);
        int biggest = store.Describe(large).Max(c => c.SizeBytes);

        biggest.Should().BeGreaterThan(smallest + ContextBytes / 2,
            "the context is what a checkpoint is mostly made of");
    }
}

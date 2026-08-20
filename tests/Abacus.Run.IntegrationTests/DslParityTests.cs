using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Abacus.Run.Abstractions;
using Abacus.Run.Api;
using Abacus.Run.Core;
using Abacus.Run.Dsl.Hosting;
using Abacus.Run.Dsl.Interpretation;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Abacus.Run.IntegrationTests;

public sealed record ParityContext(string OrderId = "ORD-1", decimal Amount = 100m);

public sealed record ParityResult(string OrderId, decimal Net, decimal Vat, decimal Total);

/// <summary>
/// The compiled half of the parity pair. Deliberately trivial arithmetic: the point is that both
/// front ends reach the same runtime and produce the same answer, not that the sum is interesting.
/// </summary>
public sealed class ParityWorkflow : IWorkflowDefinition<ParityContext, ParityResult>
{
    public string Name => "parity-compiled";
    public string Version => "1.0.0";

    public ValueTask<Workflow> BuildAsync(WorkflowBuildContext context, CancellationToken cancellationToken)
    {
        ExecutorBinding price = context.Node(new Price("price"));
        ExecutorBinding settle = context.Node(new Settle("settle"));

        return new ValueTask<Workflow>(new WorkflowBuilder(price)
            .AddEdge(price, settle)
            .WithOutputFrom(settle)
            .WithName(Name)
            .Build());
    }

    private sealed class Price(string id) : HostExecutor<ParityContext, ParityResult>(id)
    {
        protected override ValueTask<ParityResult> ExecuteCoreAsync(
            ParityContext input, IWorkflowContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(new ParityResult(
                input.OrderId, input.Amount, input.Amount * 0.2m, input.Amount * 1.2m));
    }

    private sealed class Settle(string id) : HostExecutor<ParityResult, ParityResult>(id)
    {
        protected override ValueTask<ParityResult> ExecuteCoreAsync(
            ParityResult input, IWorkflowContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(input);
    }
}

public sealed class ParityHostFixture : WebApplicationFactory<Program>
{
    /// <summary>The same workflow as a document, node for node.</summary>
    internal const string Document = """
    {
      "dsl": "abacus.workflow/1.0",
      "name": "parity-dsl",
      "version": "1.0.0",
      "context": {
        "type": "object",
        "required": ["orderId", "amount"],
        "properties": { "orderId": { "type": "string" }, "amount": { "type": "number" } }
      },
      "start": "price",
      "output": ["settle"],
      "nodes": [
        { "id": "price", "kind": "transform",
          "set": {
            "orderId": "$ctx.orderId",
            "net": "$ctx.amount",
            "vat": "$ctx.amount * 0.2",
            "total": "$ctx.amount * 1.2"
          } },
        { "id": "settle", "kind": "transform",
          "set": {
            "orderId": "$.orderId", "net": "$.net", "vat": "$.vat", "total": "$.total"
          } }
      ],
      "edges": [ { "from": "price", "to": "settle" } ]
    }
    """;

    private readonly string _auditDatabasePath =
        Path.Combine(Path.GetTempPath(), $"abacus-parity-{Guid.NewGuid():N}.db");

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            var host = new WorkflowHostBuilder(services);

            host.AddWorkflow(new ParityWorkflow())
                .ConfigureDsl(registry => registry.EnforceEgress = false)
                .AddDslWorkflowText(Document, "parity.workflow.json");
        });

        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Abacus:AuditRecords:ConnectionString", $"Data Source={_auditDatabasePath}");
        builder.ConfigureServices(services =>
        {
            services.AddHttpClient<Abacus.Run.Service.ControlPlane.Services.WorkflowApiClient>(client =>
            {
                client.BaseAddress = new Uri("http://localhost");
            }).ConfigurePrimaryHttpMessageHandler(() => Server.CreateHandler());
        });
    }

    public T Resolve<T>() where T : notnull => Services.GetRequiredService<T>();

    public async Task<JsonNode?> RunAsync(HttpClient client, string workflow, object context)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/workflows/{workflow}/instances", new { context });

        response.EnsureSuccessStatusCode();

        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        string id = body.GetProperty("instanceId").GetString()!;

        var store = Resolve<IInstanceStore>();
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);

        while (DateTime.UtcNow < deadline)
        {
            WorkflowInstance? instance = await store.GetAsync(id, default);

            if (instance?.Status == InstanceStatus.Completed)
            {
                return instance.ResultJson is { Length: > 0 } json ? JsonNode.Parse(json) : null;
            }

            if (instance is not null && instance.Status.IsTerminal())
            {
                throw new InvalidOperationException(
                    $"'{workflow}' ended {instance.Status}: {instance.TerminalReason}");
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"'{workflow}' did not complete.");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing) return;

        try
        {
            if (File.Exists(_auditDatabasePath)) File.Delete(_auditDatabasePath);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>
/// The clearest statement that the DSL is a front end and not a fork: the same work, authored twice,
/// answering identically on the same host.
/// </summary>
public class DslParityTests : IClassFixture<ParityHostFixture>
{
    private readonly ParityHostFixture _fixture;

    public DslParityTests(ParityHostFixture fixture) => _fixture = fixture;

    [Theory]
    [InlineData("ORD-1", 100)]
    [InlineData("ORD-2", 0)]
    [InlineData("ORD-3", 1)]
    [InlineData("ORD-BIG", 999999)]
    public async Task Both_front_ends_produce_the_same_result(string orderId, decimal amount)
    {
        using HttpClient client = _fixture.CreateClient();
        var context = new { orderId, amount };

        JsonNode? compiled = await _fixture.RunAsync(client, "parity-compiled", context);
        JsonNode? document = await _fixture.RunAsync(client, "parity-dsl", context);

        compiled.Should().NotBeNull();
        document.Should().NotBeNull();

        foreach (string field in new[] { "orderId", "net", "vat", "total" })
        {
            string compiledValue = Read(compiled!, field);
            string documentValue = Read(document!, field);

            documentValue.Should().Be(compiledValue,
                $"'{field}' should match; compiled={compiled!.ToJsonString()} document={document!.ToJsonString()}");
        }
    }

    /// <summary>
    /// Decimal arithmetic on both sides. 0.1 pricing is where a DSL that quietly used binary floating
    /// point would diverge from a compiled workflow that did not.
    /// </summary>
    [Fact]
    public async Task Arithmetic_agrees_on_a_value_binary_floating_point_would_not()
    {
        using HttpClient client = _fixture.CreateClient();
        var context = new { orderId = "ORD-DEC", amount = 0.1m };

        JsonNode? compiled = await _fixture.RunAsync(client, "parity-compiled", context);
        JsonNode? document = await _fixture.RunAsync(client, "parity-dsl", context);

        Read(document!, "vat").Should().Be(Read(compiled!, "vat"));
        Read(document!, "total").Should().Be(Read(compiled!, "total"));
    }

    [Fact]
    public async Task Both_appear_in_the_catalog_side_by_side()
    {
        using HttpClient client = _fixture.CreateClient();

        JsonElement catalog = await client.GetFromJsonAsync<JsonElement>("/workflows");
        string[] names = [.. catalog.EnumerateArray().Select(w => w.GetProperty("name").GetString()!)];

        names.Should().Contain("parity-compiled").And.Contain("parity-dsl");
    }

    /// <summary>
    /// Side by side is not the same as indistinguishable. An operator looking at a running host needs
    /// to know which of two workflows came from a document, and this is the only fixture where both
    /// answers are available from one catalog.
    /// </summary>
    [Fact]
    public async Task The_catalog_says_which_front_end_authored_each_version()
    {
        using HttpClient client = _fixture.CreateClient();

        JsonElement compiled = await client.GetFromJsonAsync<JsonElement>("/workflows/parity-compiled");
        JsonElement version = compiled.GetProperty("versions").EnumerateArray().Single();

        version.GetProperty("source").GetString().Should().Be("compiled");
        version.GetProperty("documentHash").ValueKind.Should().Be(JsonValueKind.Null,
            "a C# definition has no document to hash");

        JsonElement document = await client.GetFromJsonAsync<JsonElement>("/workflows/parity-dsl");
        JsonElement dslVersion = document.GetProperty("versions").EnumerateArray().Single();

        dslVersion.GetProperty("source").GetString().Should().Be("dsl");
        dslVersion.GetProperty("documentHash").GetString().Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// The hash the catalog reports must be the hash of the document that was registered — the same
    /// value the DSL's own route reports, and the same value again after a restart of the same
    /// composition. A hash that only agreed with itself would detect no drift at all.
    /// </summary>
    [Fact]
    public async Task The_catalog_hash_is_the_registered_documents_hash()
    {
        using HttpClient client = _fixture.CreateClient();

        JsonElement documents = await client.GetFromJsonAsync<JsonElement>("/dsl/documents");
        string expected = documents.EnumerateArray()
            .Single(d => d.GetProperty("name").GetString() == "parity-dsl")
            .GetProperty("documentHash").GetString()!;

        JsonElement workflow = await client.GetFromJsonAsync<JsonElement>("/workflows/parity-dsl");

        workflow.GetProperty("versions").EnumerateArray().Single()
            .GetProperty("documentHash").GetString().Should().Be(expected);

        // Deterministic across processes, not merely within one: the point of the hash is that two
        // hosts can be compared.
        DslWorkflowDefinition registered = _fixture.Resolve<DslRegistry>().Find("parity-dsl")!;
        registered.DocumentHash.Should().Be(expected);
        Abacus.Run.Dsl.Validation.DslCanonicalHash
            .Compute(JsonNode.Parse(ParityHostFixture.Document)!)
            .Should().Be(expected, "the hash is of the document text, computable without a host");
    }

    /// <summary>Property casing differs between the two serializers; compare values, not spellings.</summary>
    private static string Read(JsonNode node, string field)
    {
        foreach (string candidate in new[] { field, char.ToUpperInvariant(field[0]) + field[1..] })
        {
            if (node[candidate] is { } value)
            {
                return value.GetValueKind() == JsonValueKind.Number
                    ? value.GetValue<decimal>().ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : value.ToJsonString().Trim('"');
            }
        }

        throw new InvalidOperationException($"'{field}' is absent from {node.ToJsonString()}.");
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Abacus.Run.Abstractions;
using Abacus.Run.Api;
using Abacus.Run.Core;
using Abacus.Run.Dsl.Hosting;
using Abacus.Run.Dsl.Interpretation;
using Abacus.Run.Dsl.Validation;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Abacus.Run.IntegrationTests;

/// <summary>Registration: what reaches the registry, and what fails startup instead.</summary>
public class DslRegistrationTests
{
    private static ServiceProvider Build(Action<WorkflowHostBuilder> configure)
    {
        var services = new ServiceCollection();
        configure(new WorkflowHostBuilder(services));
        return services.BuildServiceProvider();
    }

    [Fact]
    public void A_valid_document_registers_as_a_workflow_definition()
    {
        using ServiceProvider provider = Build(host => host
            .ConfigureDsl(r => r.EnforceEgress = false)
            .AddDslWorkflowText(DslDocuments.Linear, "linear"));

        IWorkflowDefinition[] definitions = [.. provider.GetServices<IWorkflowDefinition>()];

        definitions.Should().ContainSingle();
        definitions[0].Name.Should().Be("dsl-linear");
        definitions[0].Should().BeAssignableTo<DslWorkflowDefinition>();
    }

    /// <summary>
    /// The order composition happens to be written in must not decide whether a document validates,
    /// so resolution is deferred until every AddDslNode call has run.
    /// </summary>
    [Fact]
    public void A_custom_node_registered_after_the_document_still_resolves()
    {
        using ServiceProvider provider = Build(host => host
            .ConfigureDsl(r => r.EnforceEgress = false)
            .AddDslWorkflowText(DslDocuments.Custom, "custom")
            .AddDslNode(new DoublerNodeFactory()));

        Action act = () => provider.GetServices<IWorkflowDefinition>().ToArray();
        act.Should().NotThrow();
    }

    [Fact]
    public void An_invalid_document_fails_startup_with_every_diagnostic()
    {
        using ServiceProvider provider = Build(host => host
            .ConfigureDsl(r => r.EnforceEgress = false)
            .AddDslWorkflowText("""
            {
              "dsl": "abacus.workflow/1.0",
              "name": "broken", "version": "1.0.0", "start": "nope",
              "nodes": [ { "id": "a", "kind": "transform", "set": { "x": "1" } } ],
              "edges": [ { "from": "a", "to": "ghost" } ]
            }
            """, "broken.json"));

        Action act = () => provider.GetServices<IWorkflowDefinition>().ToArray();

        act.Should().Throw<DslValidationException>()
            .Which.Message.Should().Contain("broken.json")
            .And.Contain(DslCodes.StartNotFound)
            .And.Contain(DslCodes.EdgeEndpointNotFound);
    }

    [Fact]
    public void An_unregistered_custom_node_fails_startup()
    {
        using ServiceProvider provider = Build(host => host
            .ConfigureDsl(r => r.EnforceEgress = false)
            .AddDslWorkflowText(DslDocuments.Custom, "custom"));

        Action act = () => provider.GetServices<IWorkflowDefinition>().ToArray();

        act.Should().Throw<DslValidationException>()
            .Which.Message.Should().Contain(DslCodes.UnknownCustomNode);
    }

    /// <summary>A published version is immutable, and two documents claiming one must not both win.</summary>
    [Fact]
    public void Two_documents_claiming_one_version_fail_startup()
    {
        string second = DslDocuments.Linear.Replace("\"start\": \"price\"", "\"start\": \"finish\"",
            StringComparison.Ordinal);

        using ServiceProvider provider = Build(host => host
            .ConfigureDsl(r => r.EnforceEgress = false)
            .AddDslWorkflowText(DslDocuments.Linear, "first.json")
            .AddDslWorkflowText(second, "second.json"));

        Action act = () => provider.GetServices<IWorkflowDefinition>().ToArray();

        act.Should().Throw<DslValidationException>()
            .Which.Message.Should().Contain(DslCodes.HashConflict);
    }

    [Fact]
    public void The_same_document_registered_twice_is_not_a_conflict()
    {
        using ServiceProvider provider = Build(host => host
            .ConfigureDsl(r => r.EnforceEgress = false)
            .AddDslWorkflowText(DslDocuments.Linear, "a.json")
            .AddDslWorkflowText(DslDocuments.Linear, "b.json"));

        Action act = () => provider.GetServices<IWorkflowDefinition>().ToArray();
        act.Should().NotThrow("the documents are byte-identical, so nothing was redefined");
    }

    [Fact]
    public void A_missing_directory_is_refused_at_composition_time()
    {
        var services = new ServiceCollection();
        var host = new WorkflowHostBuilder(services);

        Action act = () => host.AddDslWorkflowsFromDirectory(
            Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}"));

        act.Should().Throw<DirectoryNotFoundException>();
    }

    [Fact]
    public void Documents_load_from_a_directory_in_a_stable_order()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"dsl-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            File.WriteAllText(Path.Combine(directory, "b.workflow.json"), DslDocuments.Linear);
            File.WriteAllText(Path.Combine(directory, "a.workflow.json"), DslDocuments.Branch);
            File.WriteAllText(Path.Combine(directory, "ignored.txt"), "not a document");

            using ServiceProvider provider = Build(host => host
                .ConfigureDsl(r => r.EnforceEgress = false)
                .AddDslWorkflowsFromDirectory(directory));

            string[] names = [.. provider.GetServices<IWorkflowDefinition>().Select(d => d.Name)];

            names.Should().Equal("dsl-branch", "dsl-linear");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_document_from_disk_registers()
    {
        string path = Path.Combine(Path.GetTempPath(), $"dsl-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, DslDocuments.Linear);

        try
        {
            using ServiceProvider provider = Build(host => host
                .ConfigureDsl(r => r.EnforceEgress = false)
                .AddDslWorkflow(path));

            provider.GetServices<IWorkflowDefinition>().Should().ContainSingle();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Registering_a_duplicate_node_name_is_refused()
    {
        var services = new ServiceCollection();
        var host = new WorkflowHostBuilder(services);

        host.AddDslNode(new DoublerNodeFactory());

        Action act = () => host.AddDslNode(new DoublerNodeFactory());
        act.Should().Throw<InvalidOperationException>().WithMessage("*already registered*");
    }
}

/// <summary>The DSL's own control-plane routes.</summary>
public class DslEndpointTests : IClassFixture<DslHostFixture>
{
    private readonly DslHostFixture _fixture;

    public DslEndpointTests(DslHostFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task The_schema_route_serves_the_published_schema()
    {
        using HttpClient client = _fixture.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/dsl/schema");
        response.EnsureSuccessStatusCode();

        string body = await response.Content.ReadAsStringAsync();
        body.Should().Be(DslSchemaValidator.SchemaText);
        body.Should().Contain("abacus.workflow/1.0");
    }

    [Fact]
    public async Task The_node_route_lists_built_in_and_registered_nodes()
    {
        using HttpClient client = _fixture.CreateClient();

        JsonElement nodes = await client.GetFromJsonAsync<JsonElement>("/dsl/nodes");

        string[] builtIn = [.. nodes.GetProperty("builtIn").EnumerateArray().Select(e => e.GetString()!)];
        builtIn.Should().Contain(["transform", "http", "llm", "delay", "approval",
                                  "publish", "wait-event", "fan-in", "custom"]);

        string[] custom = [.. nodes.GetProperty("custom").EnumerateArray()
            .Select(e => e.GetProperty("name").GetString()!)];
        custom.Should().Contain("doubler");
    }

    [Fact]
    public async Task The_custom_node_listing_carries_its_parameter_schema()
    {
        using HttpClient client = _fixture.CreateClient();

        JsonElement nodes = await client.GetFromJsonAsync<JsonElement>("/dsl/nodes");

        JsonElement doubler = nodes.GetProperty("custom").EnumerateArray()
            .First(e => e.GetProperty("name").GetString() == "doubler");

        doubler.GetProperty("parameterSchema").GetRawText().Should().Contain("field");
    }

    [Fact]
    public async Task The_function_route_documents_the_closed_expression_vocabulary()
    {
        using HttpClient client = _fixture.CreateClient();

        JsonElement functions = await client.GetFromJsonAsync<JsonElement>("/dsl/functions");
        string[] names = [.. functions.EnumerateArray().Select(e => e.GetProperty("name").GetString()!)];

        names.Should().Contain(["len", "has", "lower", "upper", "contains",
                                "startsWith", "endsWith", "matches", "coalesce"]);
    }

    [Fact]
    public async Task The_document_route_reports_each_registered_documents_hash()
    {
        using HttpClient client = _fixture.CreateClient();

        JsonElement documents = await client.GetFromJsonAsync<JsonElement>("/dsl/documents");

        JsonElement linear = documents.EnumerateArray()
            .First(d => d.GetProperty("name").GetString() == "dsl-linear");

        linear.GetProperty("documentHash").GetString().Should().MatchRegex("^[0-9a-f]{64}$");
        linear.GetProperty("nodes").GetInt32().Should().Be(2);
    }

    private async Task<JsonElement> ValidateAsync(HttpClient client, string document)
    {
        using var content = new StringContent(document, Encoding.UTF8, "application/json");
        HttpResponseMessage response = await client.PostAsync("/dsl/validate", content);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Validate_accepts_a_good_document_without_registering_it()
    {
        using HttpClient client = _fixture.CreateClient();

        JsonElement result = await ValidateAsync(client, DslDocuments.Branch);

        result.GetProperty("valid").GetBoolean().Should().BeTrue();
        result.GetProperty("name").GetString().Should().Be("dsl-branch");
        result.GetProperty("documentHash").GetString().Should().MatchRegex("^[0-9a-f]{64}$");

        // Validating must not publish: the catalog is unchanged either way.
        JsonElement catalog = await client.GetFromJsonAsync<JsonElement>("/workflows");
        catalog.EnumerateArray().Count(w => w.GetProperty("name").GetString() == "dsl-branch")
            .Should().Be(1);
    }

    [Fact]
    public async Task Validate_returns_pointer_accurate_diagnostics()
    {
        using HttpClient client = _fixture.CreateClient();

        JsonElement result = await ValidateAsync(client, """
        {
          "dsl": "abacus.workflow/1.0",
          "name": "bad", "version": "1.0.0", "start": "a",
          "nodes": [ { "id": "a", "kind": "transform", "set": { "x": "lenn($.y)" } } ],
          "edges": [ { "from": "a", "to": "missing" } ]
        }
        """);

        result.GetProperty("valid").GetBoolean().Should().BeFalse();

        JsonElement[] diagnostics = [.. result.GetProperty("diagnostics").EnumerateArray()];

        diagnostics.Should().Contain(d =>
            d.GetProperty("code").GetString() == DslCodes.UnknownFunction &&
            d.GetProperty("pointer").GetString() == "/nodes/0/set/x");

        diagnostics.Should().Contain(d =>
            d.GetProperty("code").GetString() == DslCodes.EdgeEndpointNotFound &&
            d.GetProperty("pointer").GetString() == "/edges/0/to");

        diagnostics.First(d => d.GetProperty("code").GetString() == DslCodes.UnknownFunction)
            .GetProperty("suggestion").GetString().Should().Contain("len");
    }

    /// <summary>
    /// Validating against the live host is the point of the route — it knows which custom nodes are
    /// registered, which an offline linter cannot.
    /// </summary>
    [Fact]
    public async Task Validate_resolves_custom_nodes_against_the_live_catalog()
    {
        using HttpClient client = _fixture.CreateClient();

        JsonElement good = await ValidateAsync(client, DslDocuments.Custom);
        good.GetProperty("valid").GetBoolean().Should().BeTrue();
        good.GetProperty("skippedChecks").EnumerateArray().Should().BeEmpty();

        JsonElement bad = await ValidateAsync(client,
            DslDocuments.Custom.Replace("\"doubler\"", "\"not-registered\"", StringComparison.Ordinal));

        bad.GetProperty("valid").GetBoolean().Should().BeFalse();
        bad.GetProperty("diagnostics").EnumerateArray()
            .Should().Contain(d => d.GetProperty("code").GetString() == DslCodes.UnknownCustomNode);
    }

    [Fact]
    public async Task Validate_reports_malformed_json_rather_than_failing_the_request()
    {
        using HttpClient client = _fixture.CreateClient();

        JsonElement result = await ValidateAsync(client, "{ not json");

        result.GetProperty("valid").GetBoolean().Should().BeFalse();
        result.GetProperty("diagnostics").EnumerateArray()
            .Should().Contain(d => d.GetProperty("code").GetString() == DslCodes.MalformedJson);
    }

    [Fact]
    public async Task A_root_level_diagnostic_reports_a_usable_pointer()
    {
        using HttpClient client = _fixture.CreateClient();

        JsonElement result = await ValidateAsync(client, "{ not json");

        result.GetProperty("diagnostics").EnumerateArray().First()
            .GetProperty("pointer").GetString().Should().Be("/");
    }
}

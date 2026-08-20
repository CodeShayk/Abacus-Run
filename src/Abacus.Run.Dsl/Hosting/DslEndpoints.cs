using System.Text.Json.Nodes;
using Abacus.Run.Dsl.Interpretation;
using Abacus.Run.Dsl.Validation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Abacus.Run.Dsl.Hosting;

/// <summary>The DSL's own control-plane routes: validate, schema, node catalog.</summary>
public static class DslEndpoints
{
    /// <summary>
    /// Maps the DSL routes. Mounted beside <c>MapWorkflowApi</c>, under the same prefix and the same
    /// authorization — the validate route reflects the host's registered node names back to the
    /// caller, which is information about the host and not something to serve anonymously.
    /// </summary>
    public static IEndpointRouteBuilder MapDslApi(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/dsl/schema", () => Results.Text(
            DslSchemaValidator.SchemaText, "application/schema+json"));

        app.MapGet("/dsl/nodes", (DslRegistry registry) => Results.Ok(new
        {
            builtIn = Model.DslNodeKinds.All,
            custom = registry.Catalog.Describe().Select(entry => new
            {
                name = entry.Key,
                parameterSchema = entry.Value
            })
        }));

        app.MapGet("/dsl/functions", () => Results.Ok(
            Expressions.AbExFunctions.Names
                .Select(name =>
                {
                    Expressions.AbExFunctions.TryGet(name, out Expressions.AbExFunction function);
                    return new { name, arity = function.DescribeArity(), summary = function.Summary };
                })));

        app.MapGet("/dsl/documents", (DslRegistry registry) => Results.Ok(
            registry.Definitions.Select(d => new
            {
                name = d.Name,
                version = d.Version,
                documentHash = d.DocumentHash,
                nodes = d.Document.Nodes.Count,
                edges = d.Document.Edges.Count
            })));

        // What an authoring tool calls. Validates without registering, so a document can be checked
        // against the live host's catalog before anyone commits it.
        app.MapPost("/dsl/validate", async (HttpRequest request, DslRegistry registry, CancellationToken ct) =>
        {
            using var reader = new StreamReader(request.Body);
            string text = await reader.ReadToEndAsync(ct).ConfigureAwait(false);

            DslParseResult result = DslParser.Parse(text, new DslEnvironment
            {
                CustomNodes = registry.Catalog.Describe(),
                EnforceEgress = registry.EnforceEgress,
                Policy = registry.Policy
            });

            return Results.Ok(new
            {
                valid = result.IsValid,
                name = result.Document?.Name,
                version = result.Document?.Version,
                documentHash = result.Document?.Hash,
                skippedChecks = result.Validation.SkippedChecks,
                diagnostics = result.Validation.Diagnostics.Select(Describe)
            });
        });

        return app;
    }

    private static object Describe(DslDiagnostic diagnostic) => new
    {
        code = diagnostic.Code,
        severity = diagnostic.Severity == DslSeverity.Error ? "error" : "warning",

        // Empty means the document as a whole; "/" is what a pointer to the root actually looks
        // like, and an editor matching on it should not have to special-case the empty string.
        pointer = string.IsNullOrEmpty(diagnostic.Pointer) ? "/" : diagnostic.Pointer,
        message = diagnostic.Message,
        suggestion = diagnostic.Suggestion
    };

    /// <summary>Describes a document for the catalog route, when the definition is a DSL one.</summary>
    public static JsonObject? DescribeSource(object? definition)
        => definition is DslWorkflowDefinition dsl
            ? new JsonObject
            {
                ["source"] = "dsl",
                ["documentHash"] = dsl.DocumentHash
            }
            : null;
}

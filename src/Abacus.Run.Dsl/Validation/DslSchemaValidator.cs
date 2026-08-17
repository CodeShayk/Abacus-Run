using System.Reflection;
using System.Text.Json.Nodes;
using Json.Schema;

namespace Abacus.Run.Dsl.Validation;

/// <summary>
/// Phase 1 of validation: structural conformance to the published JSON Schema.
/// </summary>
/// <remarks>
/// The schema is embedded from <c>docs/schema/</c> rather than duplicated, so the document an editor
/// validates against and the one the host enforces are the same bytes.
/// </remarks>
public static class DslSchemaValidator
{
    private const string ResourceName = "Abacus.Run.Dsl.Schema.abacus-workflow-dsl-1.0.json";

    private static readonly Lazy<string> SchemaTextValue = new(LoadSchemaText, isThreadSafe: true);
    private static readonly Lazy<JsonSchema> Schema = new(
        () => JsonSchema.FromText(SchemaTextValue.Value), isThreadSafe: true);

    /// <summary>The published schema, as served by <c>GET /v2/dsl/schema</c>.</summary>
    public static string SchemaText => SchemaTextValue.Value;

    public static IReadOnlyList<DslDiagnostic> Validate(JsonNode? document)
    {
        if (document is null)
        {
            return [DslDiagnostic.Error(DslCodes.MalformedJson, string.Empty, "The document is empty.")];
        }

        EvaluationResults results = Schema.Value.Evaluate(document, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List
        });

        if (results.IsValid)
        {
            return [];
        }

        var diagnostics = new List<DslDiagnostic>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        Collect(results, diagnostics, seen);

        // A failing evaluation always has a reason, but a purely structural failure can report it
        // only at the root; never return "invalid" with nothing to show for it.
        if (diagnostics.Count == 0)
        {
            diagnostics.Add(DslDiagnostic.Error(
                DslCodes.SchemaViolation, string.Empty, "The document does not match the DSL schema."));
        }

        return diagnostics;
    }

    private static void Collect(EvaluationResults results, List<DslDiagnostic> diagnostics, HashSet<string> seen)
    {
        if (results.Errors is { Count: > 0 })
        {
            string pointer = results.InstanceLocation.ToString();

            foreach ((string keyword, string message) in results.Errors)
            {
                string text = Humanise(keyword, message);
                if (seen.Add($"{pointer}|{text}"))
                {
                    diagnostics.Add(DslDiagnostic.Error(DslCodes.SchemaViolation, pointer, text));
                }
            }
        }

        foreach (EvaluationResults detail in results.Details)
        {
            Collect(detail, diagnostics, seen);
        }
    }

    /// <summary>
    /// Schema messages are written for schema authors. These are the three that a workflow author
    /// meets most often, reworded to say what to do about them.
    /// </summary>
    private static string Humanise(string keyword, string message) => keyword switch
    {
        "required" => $"{message} (a required property is missing)",
        "additionalProperties" => $"{message} (unknown property — check the spelling)",
        "pattern" => $"{message} (the value does not match the required format)",
        _ => message
    };

    private static string LoadSchemaText()
    {
        Assembly assembly = typeof(DslSchemaValidator).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream(ResourceName);

        if (stream is null)
        {
            throw new InvalidOperationException(
                $"The DSL schema resource '{ResourceName}' is missing from {assembly.GetName().Name}. " +
                "It is embedded from docs/schema/; check the EmbeddedResource item in the project file.");
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

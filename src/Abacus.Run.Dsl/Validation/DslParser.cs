using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Abacus.Run.Dsl.Model;

namespace Abacus.Run.Dsl.Validation;

/// <summary>A parsed document, the diagnostics found on the way, or both.</summary>
public sealed record DslParseResult(DslDocument? Document, DslValidationResult Validation)
{
    public bool IsValid => Document is not null && Validation.IsValid;
}

/// <summary>
/// The entry point: text in, model and diagnostics out.
/// </summary>
/// <remarks>
/// The phases run in order and stop where continuing would be noise. A document that is not JSON
/// cannot be schema-checked; one that fails the schema cannot be read into the model, and reporting
/// forty type errors from a half-understood document buries the one that matters.
/// </remarks>
public static class DslParser
{
    public static DslParseResult Parse(string text, DslEnvironment? environment = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        DslPolicy policy = environment?.Policy ?? DslPolicy.Default;

        int bytes = Encoding.UTF8.GetByteCount(text);
        if (bytes > policy.MaxDocumentBytes)
        {
            return Failed(DslDiagnostic.Error(
                DslCodes.LimitExceeded, string.Empty,
                $"The document is {bytes} bytes; the limit is {policy.MaxDocumentBytes}."));
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
        }
        catch (JsonException ex)
        {
            return Failed(DslDiagnostic.Error(
                DslCodes.MalformedJson, string.Empty, $"The document is not valid JSON. {ex.Message}"));
        }

        if (node is not JsonObject)
        {
            return Failed(DslDiagnostic.Error(
                DslCodes.MalformedJson, string.Empty, "The document must be a JSON object."));
        }

        IReadOnlyList<DslDiagnostic> structural = DslSchemaValidator.Validate(node);
        if (structural.Any(d => d.Severity == DslSeverity.Error))
        {
            return new DslParseResult(null, new DslValidationResult(structural, []));
        }

        DslDocument document = DslDocumentReader.Read(node);
        DslValidationResult semantic = DslSemanticValidator.Validate(document, environment);

        var diagnostics = new List<DslDiagnostic>(structural);
        diagnostics.AddRange(semantic.Diagnostics);

        return new DslParseResult(
            document, new DslValidationResult(diagnostics, semantic.SkippedChecks));
    }

    /// <summary>Parses a document from disk, naming the file in any diagnostic about reading it.</summary>
    public static DslParseResult ParseFile(string path, DslEnvironment? environment = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            return Parse(File.ReadAllText(path), environment);
        }
        catch (IOException ex)
        {
            return Failed(DslDiagnostic.Error(
                DslCodes.MalformedJson, string.Empty, $"Could not read '{path}'. {ex.Message}"));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Failed(DslDiagnostic.Error(
                DslCodes.MalformedJson, string.Empty, $"Could not read '{path}'. {ex.Message}"));
        }
    }

    /// <summary>
    /// Parses or throws, for call sites that treat an invalid document as a startup failure. The
    /// message carries every diagnostic, because the first one is rarely the only one worth fixing.
    /// </summary>
    public static DslDocument ParseOrThrow(string text, DslEnvironment? environment = null, string? source = null)
    {
        DslParseResult result = Parse(text, environment);

        if (result.IsValid)
        {
            return result.Document!;
        }

        string where = source is null ? "The DSL document" : $"'{source}'";
        throw new DslValidationException(
            $"{where} is not valid:{Environment.NewLine}{result.Validation.Describe()}",
            result.Validation);
    }

    private static DslParseResult Failed(DslDiagnostic diagnostic)
        => new(null, new DslValidationResult([diagnostic], []));
}

/// <summary>Thrown when an invalid document reaches a call site that cannot carry on without one.</summary>
public sealed class DslValidationException : Exception
{
    public DslValidationException(string message, DslValidationResult validation) : base(message)
        => Validation = validation;

    public DslValidationResult Validation { get; }
}

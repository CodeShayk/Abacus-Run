using System.Text.Json;
using Abacus.Run.Abstractions;

namespace Abacus.Run.Core;

public sealed record WorkflowDescriptor(IWorkflowDefinition Definition)
{
    public string Name => Definition.Name;
    public string Version => Definition.Version;
    public Type ContextType => Definition.ContextType;
    public Type ResultType => Definition.ResultType;
}

public sealed record ContextValidationResult(bool IsValid, IReadOnlyDictionary<string, string[]> Errors)
{
    public static ContextValidationResult Valid { get; } =
        new(true, new Dictionary<string, string[]>());

    public static ContextValidationResult Invalid(string field, string error) =>
        new(false, new Dictionary<string, string[]> { [field] = [error] });
}

/// <summary>
/// Implemented by a workflow definition that validates its own start payload beyond binding it to
/// <see cref="IWorkflowDefinition.ContextType"/>.
/// </summary>
/// <remarks>
/// <para>
/// The registry's default check is a type bind, which is the whole story for a definition whose
/// context is a C# record. It is no story at all for one whose context is a JSON schema declared in
/// a document, so such a definition answers for itself.
/// </para>
/// <para>
/// Opt-in and additive: a definition that does not implement this behaves exactly as before, and the
/// hook runs only after the type bind has already succeeded.
/// </para>
/// </remarks>
public interface IContextValidatingWorkflow
{
    ContextValidationResult ValidateContext(JsonElement context);
}

public interface IWorkflowRegistry
{
    IReadOnlyList<WorkflowDescriptor> All { get; }

    WorkflowDescriptor? Resolve(string name, string? version = null);

    IReadOnlyList<WorkflowDescriptor> VersionsOf(string name);

    ContextValidationResult ValidateContext(WorkflowDescriptor descriptor, JsonElement? context);
}

/// <summary>
/// Immutable registry built at startup. Version selection is SemVer-ordered; instances always
/// resolve the exact version they were created with.
/// </summary>
public sealed class WorkflowRegistry : IWorkflowRegistry
{
    private readonly Dictionary<string, List<WorkflowDescriptor>> _byName;

    public WorkflowRegistry(IEnumerable<IWorkflowDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        _byName = definitions
            .Select(d => new WorkflowDescriptor(d))
            .GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(d => ParseVersion(d.Version)).ToList(),
                StringComparer.OrdinalIgnoreCase);

        foreach ((string name, List<WorkflowDescriptor> versions) in _byName)
        {
            IEnumerable<string> duplicates = versions
                .GroupBy(v => v.Version, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key);

            if (duplicates.FirstOrDefault() is { } duplicate)
            {
                throw new InvalidOperationException(
                    $"Workflow '{name}' has more than one definition registered for version '{duplicate}'.");
            }
        }

        All = _byName.Values.SelectMany(v => v).ToArray();
    }

    public IReadOnlyList<WorkflowDescriptor> All { get; }

    public WorkflowDescriptor? Resolve(string name, string? version = null)
    {
        if (!_byName.TryGetValue(name, out List<WorkflowDescriptor>? versions) || versions.Count == 0)
        {
            return null;
        }

        return version is null
            ? versions[0]   // highest SemVer
            : versions.FirstOrDefault(v => string.Equals(v.Version, version, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<WorkflowDescriptor> VersionsOf(string name)
        => _byName.TryGetValue(name, out List<WorkflowDescriptor>? versions) ? versions : [];

    public ContextValidationResult ValidateContext(WorkflowDescriptor descriptor, JsonElement? context)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (context is null || context.Value.ValueKind == JsonValueKind.Undefined)
        {
            return ContextValidationResult.Invalid("context", "A context payload is required.");
        }

        if (context.Value.ValueKind == JsonValueKind.Null)
        {
            return ContextValidationResult.Invalid("context", "Context must not be null.");
        }

        try
        {
            object? deserialized = context.Value.Deserialize(descriptor.ContextType, JsonOptions.Default);
            if (deserialized is null)
            {
                return ContextValidationResult.Invalid(
                    "context", $"Could not bind context to '{descriptor.ContextType.Name}'.");
            }

            // Runs only after the bind succeeded, so a definition's own check never has to repeat
            // what the type system already established.
            return descriptor.Definition is IContextValidatingWorkflow validating
                ? validating.ValidateContext(context.Value)
                : ContextValidationResult.Valid;
        }
        catch (JsonException ex)
        {
            return ContextValidationResult.Invalid("context", ex.Message);
        }
        catch (NotSupportedException ex)
        {
            return ContextValidationResult.Invalid("context", ex.Message);
        }
    }

    internal static Version ParseVersion(string version)
    {
        // Tolerates SemVer pre-release suffixes ("1.2.0-beta.1") by taking the numeric core.
        string core = version.Split('-', '+')[0];
        return Version.TryParse(core, out Version? parsed) ? parsed : new Version(0, 0, 0);
    }
}

public static class JsonOptions
{
    public static JsonSerializerOptions Default { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
}

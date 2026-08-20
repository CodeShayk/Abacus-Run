using System.Text;
using System.Text.Json;

namespace Abacus.Run.Executors;

/// <summary>
/// Implemented by a message type that resolves its own template placeholders.
/// </summary>
/// <remarks>
/// The default resolution walks dotted paths by reflection, which suits a POCO context and nothing
/// else. A message carrying more than one addressable object — the DSL envelope carries the start
/// context alongside the current value — cannot be expressed as one dotted path over one root, so it
/// takes the placeholder text and answers for itself. Opt-in: a type that does not implement this is
/// resolved exactly as before.
/// </remarks>
public interface ITemplateBindingSource
{
    /// <summary>
    /// Resolves the text between <c>{{</c> and <c>}}</c>. Returns null when it resolves to nothing,
    /// which renders as empty — a template must not fail a run over an absent field.
    /// </summary>
    string? Resolve(string expression);
}

/// <summary>Named values available to a template, resolved by dotted path.</summary>
public sealed class TemplateBindings
{
    private readonly object? _root;

    private TemplateBindings(object? root) => _root = root;

    public static TemplateBindings From(object? input) => new(input);

    public static TemplateBindings Empty { get; } = new(null);

    /// <summary>
    /// Resolves "context.Invoice.Id" style paths against the input object, supporting POCOs,
    /// dictionaries, and <see cref="JsonElement"/> alike.
    /// </summary>
    public string? Resolve(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        // Checked before the dotted-path walk so a self-resolving message keeps full control of its
        // own placeholder syntax rather than having it parsed on its behalf first.
        if (_root is ITemplateBindingSource source)
        {
            return source.Resolve(path);
        }

        string[] segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        object? current = _root;

        // "context" and "input" are aliases for the root object.
        int start = segments.Length > 0 && segments[0] is "context" or "input" ? 1 : 0;

        for (int i = start; i < segments.Length; i++)
        {
            current = ResolveSegment(current, segments[i]);
            if (current is null)
            {
                return null;
            }
        }

        return current switch
        {
            null => null,
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
            JsonElement e => e.ToString(),
            IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            _ => current.ToString()
        };
    }

    private static object? ResolveSegment(object? source, string name)
    {
        switch (source)
        {
            case null:
                return null;

            case JsonElement element:
                return element.ValueKind == JsonValueKind.Object &&
                       element.TryGetProperty(name, out JsonElement child)
                    ? child
                    : null;

            case IDictionary<string, object?> dictionary:
                return dictionary.TryGetValue(name, out object? value) ? value : null;

            default:
            {
                System.Reflection.PropertyInfo? property = source.GetType().GetProperty(
                    name,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.IgnoreCase);

                return property?.GetValue(source);
            }
        }
    }
}

/// <summary>
/// Minimal <c>{{ path }}</c> substitution. Deliberately not a general expression language: templates
/// appear in URLs and request bodies, so the surface is kept small enough to reason about.
/// </summary>
public static class TemplateEngine
{
    public static string Render(string template, TemplateBindings bindings)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(bindings);

        if (!template.Contains("{{", StringComparison.Ordinal))
        {
            return template;
        }

        var result = new StringBuilder(template.Length);
        int index = 0;

        while (index < template.Length)
        {
            int open = template.IndexOf("{{", index, StringComparison.Ordinal);
            if (open < 0)
            {
                result.Append(template, index, template.Length - index);
                break;
            }

            int close = template.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0)
            {
                // Unterminated placeholder: emit verbatim rather than silently truncating the URL.
                result.Append(template, index, template.Length - index);
                break;
            }

            result.Append(template, index, open - index);

            string expression = template[(open + 2)..close].Trim();
            string? resolved = bindings.Resolve(expression);
            result.Append(resolved ?? string.Empty);

            index = close + 2;
        }

        return result.ToString();
    }
}

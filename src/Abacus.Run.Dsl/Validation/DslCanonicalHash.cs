using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Abacus.Run.Dsl.Validation;

/// <summary>
/// Canonical SHA-256 of a document, following RFC 8785 (JCS): properties sorted by code unit,
/// no insignificant whitespace, numbers in shortest round-trip form.
/// </summary>
/// <remarks>
/// This is what makes the immutability rule enforceable. A published <c>(name, version)</c> is fixed;
/// reformatting a document must not look like a change, and changing one byte of behaviour must not
/// look like the same document. It also answers the operational question directly: is this instance
/// running the document I am looking at?
/// </remarks>
public static class DslCanonicalHash
{
    public static string Compute(JsonNode? document)
    {
        var builder = new StringBuilder();
        Write(document, builder);

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>The canonical text itself, exposed because a hash mismatch is easier to diagnose with it.</summary>
    public static string Canonicalize(JsonNode? document)
    {
        var builder = new StringBuilder();
        Write(document, builder);
        return builder.ToString();
    }

    private static void Write(JsonNode? node, StringBuilder builder)
    {
        switch (node)
        {
            case null:
                builder.Append("null");
                return;

            case JsonObject obj:
            {
                builder.Append('{');
                bool first = true;

                // Ordinal ordering is the specification's requirement, and it is also what makes the
                // hash stable across writers that preserve source order differently.
                foreach (KeyValuePair<string, JsonNode?> property in
                         obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    if (!first)
                    {
                        builder.Append(',');
                    }

                    first = false;
                    WriteString(property.Key, builder);
                    builder.Append(':');
                    Write(property.Value, builder);
                }

                builder.Append('}');
                return;
            }

            case JsonArray array:
            {
                builder.Append('[');
                for (int i = 0; i < array.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(',');
                    }

                    Write(array[i], builder);
                }

                builder.Append(']');
                return;
            }

            case JsonValue value:
            {
                if (value.TryGetValue(out bool b))
                {
                    builder.Append(b ? "true" : "false");
                    return;
                }

                if (value.TryGetValue(out string? s) && s is not null)
                {
                    WriteString(s, builder);
                    return;
                }

                if (value.TryGetValue(out decimal d))
                {
                    builder.Append(FormatNumber(d));
                    return;
                }

                if (value.TryGetValue(out double dbl))
                {
                    builder.Append(dbl.ToString("R", CultureInfo.InvariantCulture));
                    return;
                }

                builder.Append(value.ToJsonString());
                return;
            }

            default:
                builder.Append(node.ToJsonString());
                return;
        }
    }

    /// <summary>Shortest round-trip form: <c>1.50</c> and <c>1.5</c> hash alike, as JCS requires.</summary>
    private static string FormatNumber(decimal value)
    {
        decimal normalized = value / 1.000000000000000000000000000000000m;
        return normalized == decimal.Truncate(normalized)
            ? decimal.Truncate(normalized).ToString(CultureInfo.InvariantCulture)
            : normalized.ToString(CultureInfo.InvariantCulture);
    }

    private static void WriteString(string value, StringBuilder builder)
    {
        builder.Append('"');

        foreach (char c in value)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (c < 0x20)
                    {
                        builder.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:x4}");
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        builder.Append('"');
    }
}

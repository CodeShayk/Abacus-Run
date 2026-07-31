using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Abacus.Run.Core;

public interface IRedactionPolicy
{
    string RedactBody(string? json);

    IReadOnlyDictionary<string, string> RedactHeaders(IEnumerable<KeyValuePair<string, string>> headers);

    string RedactUrl(Uri uri);
}

/// <summary>
/// Allowlist for bodies, denylist for headers. Applied at write time so the history API can never
/// leak what the live stream withheld.
/// </summary>
public sealed class RedactionPolicy : IRedactionPolicy
{
    public const string Mask = "***";

    private readonly HashSet<string> _bodyAllowList;
    private readonly HashSet<string> _headerDenyList;
    private readonly HashSet<string> _queryAllowList;
    private readonly bool _allowAllBodyFields;

    public static RedactionPolicy Default { get; } = new(allowAllBodyFields: true);

    public RedactionPolicy(
        IEnumerable<string>? bodyAllowList = null,
        IEnumerable<string>? headerDenyList = null,
        IEnumerable<string>? queryAllowList = null,
        bool allowAllBodyFields = false)
    {
        _bodyAllowList = new HashSet<string>(bodyAllowList ?? [], StringComparer.OrdinalIgnoreCase);
        _headerDenyList = new HashSet<string>(
            headerDenyList ?? ["Authorization", "Cookie", "Set-Cookie", "x-api-key", "Proxy-Authorization"],
            StringComparer.OrdinalIgnoreCase);
        _queryAllowList = new HashSet<string>(queryAllowList ?? [], StringComparer.OrdinalIgnoreCase);
        _allowAllBodyFields = allowAllBodyFields;
    }

    public string RedactBody(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return json ?? string.Empty;
        }

        if (_allowAllBodyFields)
        {
            return json;
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            // Not JSON: we cannot reason about its fields, so mask it entirely.
            return Mask;
        }

        if (node is null)
        {
            return json;
        }

        JsonNode? redacted = RedactNode(node);
        return redacted?.ToJsonString() ?? Mask;
    }

    private JsonNode? RedactNode(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var result = new JsonObject();
                foreach (KeyValuePair<string, JsonNode?> property in obj)
                {
                    result[property.Key] = _bodyAllowList.Contains(property.Key)
                        ? property.Value?.DeepClone()
                        : property.Value is JsonObject or JsonArray
                            ? RedactNode(property.Value)
                            : JsonValue.Create(Mask);
                }
                return result;
            }

            case JsonArray array:
            {
                var result = new JsonArray();
                foreach (JsonNode? item in array)
                {
                    result.Add(RedactNode(item));
                }
                return result;
            }

            default:
                return node?.DeepClone();
        }
    }

    public IReadOnlyDictionary<string, string> RedactHeaders(IEnumerable<KeyValuePair<string, string>> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> header in headers)
        {
            result[header.Key] = _headerDenyList.Contains(header.Key) ? Mask : header.Value;
        }
        return result;
    }

    public string RedactUrl(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (string.IsNullOrEmpty(uri.Query))
        {
            return uri.GetLeftPart(UriPartial.Path);
        }

        var builder = new StringBuilder(uri.GetLeftPart(UriPartial.Path));
        bool first = true;
        foreach (string pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=', StringComparison.Ordinal);
            string key = eq < 0 ? pair : pair[..eq];
            string value = eq < 0 ? string.Empty : pair[(eq + 1)..];

            builder.Append(first ? '?' : '&');
            first = false;
            builder.Append(key).Append('=').Append(_queryAllowList.Contains(key) ? value : Mask);
        }

        return builder.ToString();
    }
}

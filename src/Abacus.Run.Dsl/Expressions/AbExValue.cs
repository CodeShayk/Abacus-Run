using System.Globalization;
using System.Text.Json.Nodes;

namespace Abacus.Run.Dsl.Expressions;

public enum AbExValueKind
{
    /// <summary>
    /// The path resolved to nothing. Distinct from <see cref="Null"/>, which is a JSON value the
    /// document actually contains.
    /// </summary>
    Absent,
    Null,
    Boolean,
    Number,
    String,
    Array,
    Object
}

/// <summary>
/// One value in an AbEx evaluation. Absence is a value rather than an exception, which is what makes
/// the evaluator total: no expression over any document can throw.
/// </summary>
public readonly struct AbExValue : IEquatable<AbExValue>
{
    private readonly decimal _number;
    private readonly bool _boolean;
    private readonly string? _string;
    private readonly JsonNode? _node;

    private AbExValue(AbExValueKind kind, decimal number = 0, bool boolean = false,
        string? text = null, JsonNode? node = null)
    {
        Kind = kind;
        _number = number;
        _boolean = boolean;
        _string = text;
        _node = node;
    }

    public AbExValueKind Kind { get; }

    public static AbExValue Absent { get; } = new(AbExValueKind.Absent);
    public static AbExValue Null { get; } = new(AbExValueKind.Null);
    public static AbExValue True { get; } = new(AbExValueKind.Boolean, boolean: true);
    public static AbExValue False { get; } = new(AbExValueKind.Boolean, boolean: false);

    public static AbExValue Bool(bool value) => value ? True : False;
    public static AbExValue Number(decimal value) => new(AbExValueKind.Number, number: value);
    public static AbExValue String(string value) => new(AbExValueKind.String, text: value);

    public bool IsAbsent => Kind == AbExValueKind.Absent;
    public bool IsTruthy => Kind == AbExValueKind.Boolean && _boolean;

    public bool AsBoolean => _boolean;
    public decimal AsNumber => _number;
    public string AsString => _string ?? string.Empty;
    public JsonNode? AsNode => _node;

    /// <summary>
    /// Classifies a <see cref="JsonNode"/> into a value. A null reference is <see cref="Absent"/>
    /// only when the caller means "not there"; a JSON null arrives as <see cref="JsonValue"/> and
    /// maps to <see cref="Null"/>.
    /// </summary>
    public static AbExValue FromNode(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return Null;

            case JsonArray array:
                return new AbExValue(AbExValueKind.Array, node: array);

            case JsonObject obj:
                return new AbExValue(AbExValueKind.Object, node: obj);

            case JsonValue value:
                if (value.TryGetValue(out bool b)) return Bool(b);
                if (value.TryGetValue(out decimal d)) return Number(d);
                if (value.TryGetValue(out string? s) && s is not null) return String(s);

                // A JsonValue wrapping something exotic still has a JSON text form; fall back to it
                // rather than reporting absence for a value that demonstrably exists.
                return String(value.ToJsonString().Trim('"'));

            default:
                return String(node.ToJsonString());
        }
    }

    /// <summary>Length as <c>len()</c> defines it: characters, elements, or properties.</summary>
    public int Length => Kind switch
    {
        AbExValueKind.String => AsString.Length,
        AbExValueKind.Array => ((JsonArray)_node!).Count,
        AbExValueKind.Object => ((JsonObject)_node!).Count,
        _ => 0
    };

    /// <summary>Renders for template interpolation. Absent and null both render as empty.</summary>
    public string ToText() => Kind switch
    {
        AbExValueKind.Absent or AbExValueKind.Null => string.Empty,
        AbExValueKind.Boolean => _boolean ? "true" : "false",
        AbExValueKind.Number => FormatNumber(_number),
        AbExValueKind.String => AsString,
        _ => _node?.ToJsonString() ?? string.Empty
    };

    /// <summary>Converts back to a JSON node for writing into an envelope.</summary>
    public JsonNode? ToNode() => Kind switch
    {
        AbExValueKind.Absent or AbExValueKind.Null => null,
        AbExValueKind.Boolean => JsonValue.Create(_boolean),
        AbExValueKind.Number => JsonValue.Create(_number),
        AbExValueKind.String => JsonValue.Create(AsString),
        _ => _node?.DeepClone()
    };

    /// <summary>
    /// Trailing zeros are dropped so <c>1.50</c> and <c>1.5</c> render alike — decimal preserves
    /// scale, and a template that produced "1.50" where the author wrote arithmetic would look wrong.
    /// </summary>
    internal static string FormatNumber(decimal value)
    {
        decimal normalized = value == decimal.Truncate(value) && Math.Abs(value) < 1e15m
            ? decimal.Truncate(value)
            : value / 1.000000000000000000000000000000000m;

        return normalized.ToString(CultureInfo.InvariantCulture);
    }

    public bool Equals(AbExValue other)
    {
        if (Kind != other.Kind)
        {
            return false;
        }

        return Kind switch
        {
            AbExValueKind.Absent or AbExValueKind.Null => true,
            AbExValueKind.Boolean => _boolean == other._boolean,
            AbExValueKind.Number => _number == other._number,
            AbExValueKind.String => string.Equals(_string, other._string, StringComparison.Ordinal),
            _ => string.Equals(_node?.ToJsonString(), other._node?.ToJsonString(), StringComparison.Ordinal)
        };
    }

    public override bool Equals(object? obj) => obj is AbExValue other && Equals(other);

    public override int GetHashCode() => Kind switch
    {
        AbExValueKind.Boolean => _boolean.GetHashCode(),
        AbExValueKind.Number => _number.GetHashCode(),
        AbExValueKind.String => StringComparer.Ordinal.GetHashCode(_string ?? string.Empty),
        _ => (int)Kind
    };

    public override string ToString() => Kind == AbExValueKind.Absent ? "<absent>" : ToText();
}

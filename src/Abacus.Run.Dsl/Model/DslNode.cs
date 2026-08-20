using System.Text.Json.Nodes;

namespace Abacus.Run.Dsl.Model;

/// <summary>The <c>kind</c> discriminator values the interpreter understands.</summary>
public static class DslNodeKinds
{
    public const string Transform = "transform";
    public const string Http = "http";
    public const string Llm = "llm";
    public const string Delay = "delay";
    public const string Approval = "approval";
    public const string Publish = "publish";
    public const string WaitEvent = "wait-event";
    public const string FanIn = "fan-in";
    public const string Custom = "custom";

    public static IReadOnlyList<string> All { get; } =
        [Transform, Http, Llm, Delay, Approval, Publish, WaitEvent, FanIn, Custom];

    /// <summary>
    /// Whether a node of this kind may carry an approval gate. <c>fan-in</c> cannot, mirroring
    /// <c>RawNode</c> in the compiled API: a barrier target aggregates messages that already
    /// happened, so pausing it would gate nothing that has not already run.
    /// </summary>
    public static bool IsGateable(string kind) => kind != FanIn;
}

/// <summary>One node of the graph, as the document declared it.</summary>
public abstract record DslNode
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public string? Description { get; init; }
    public JsonNode? InputSchema { get; init; }
    public JsonNode? OutputSchema { get; init; }
    public DslGate? Gate { get; init; }
    public DslNotify? Notify { get; init; }

    /// <summary>JSON Pointer this node was read from, e.g. <c>/nodes/3</c>.</summary>
    public string Pointer { get; init; } = string.Empty;

    /// <summary>Every AbEx expression this node declares, with the pointer that located it.</summary>
    public virtual IEnumerable<(string Pointer, string Expression)> Expressions()
    {
        if (Gate?.When is { Length: > 0 } when)
        {
            yield return ($"{Gate.Pointer}/when", when);
        }

        if (Notify is not null)
        {
            foreach ((string key, string expression) in Notify.Payload)
            {
                yield return ($"{Notify.Pointer}/payload/{JsonPointer.Escape(key)}", expression);
            }
        }
    }

    /// <summary>Every <c>{{ }}</c> template this node declares.</summary>
    public virtual IEnumerable<(string Pointer, string Template)> Templates() => [];
}

public sealed record DslTransformNode : DslNode
{
    public IReadOnlyDictionary<string, string> Set { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Replace <c>data</c> outright rather than merging into it.</summary>
    public bool Replace { get; init; }

    public override IEnumerable<(string Pointer, string Expression)> Expressions()
    {
        foreach ((string pointer, string expression) in base.Expressions())
        {
            yield return (pointer, expression);
        }

        foreach ((string target, string expression) in Set)
        {
            yield return ($"{Pointer}/set/{JsonPointer.Escape(target)}", expression);
        }
    }
}

public sealed record DslHttpNode : DslNode
{
    public string Method { get; init; } = "GET";
    public required string Url { get; init; }
    public IReadOnlyDictionary<string, string> Headers { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
    public string? Body { get; init; }
    public int TimeoutSeconds { get; init; } = 30;
    public IReadOnlyList<int> SuccessCodes { get; init; } = [];
    public IReadOnlyList<string> AllowedHosts { get; init; } = [];
    public bool SendIdempotencyKey { get; init; } = true;

    public override IEnumerable<(string Pointer, string Template)> Templates()
    {
        yield return ($"{Pointer}/url", Url);

        if (Body is { Length: > 0 })
        {
            yield return ($"{Pointer}/body", Body);
        }

        foreach ((string header, string value) in Headers)
        {
            yield return ($"{Pointer}/headers/{JsonPointer.Escape(header)}", value);
        }
    }
}

public sealed record DslLlmNode : DslNode
{
    public required string Model { get; init; }
    public string? System { get; init; }
    public required string Prompt { get; init; }
    public string? PromptVersion { get; init; }
    public JsonNode? StructuredOutput { get; init; }
    public float? Temperature { get; init; }
    public int? MaxTokens { get; init; }
    public bool StreamDeltas { get; init; }
    public bool EmitCompletion { get; init; } = true;

    public override IEnumerable<(string Pointer, string Template)> Templates()
    {
        yield return ($"{Pointer}/prompt", Prompt);

        if (System is { Length: > 0 })
        {
            yield return ($"{Pointer}/system", System);
        }
    }
}

public sealed record DslDelayNode : DslNode
{
    public TimeSpan For { get; init; }
}

public sealed record DslApprovalNode : DslNode;

public sealed record DslPublishNode : DslNode
{
    public required string Topic { get; init; }
    public IReadOnlyDictionary<string, string> Payload { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
    public string? CorrelationKey { get; init; }
    public string Scope { get; init; } = "local";

    public override IEnumerable<(string Pointer, string Expression)> Expressions()
    {
        foreach ((string pointer, string expression) in base.Expressions())
        {
            yield return (pointer, expression);
        }

        foreach ((string key, string expression) in Payload)
        {
            yield return ($"{Pointer}/payload/{JsonPointer.Escape(key)}", expression);
        }

        if (CorrelationKey is { Length: > 0 })
        {
            yield return ($"{Pointer}/correlationKey", CorrelationKey);
        }
    }
}

public sealed record DslWaitEventNode : DslNode
{
    public required string Topic { get; init; }
    public string? CorrelationKey { get; init; }
    public TimeSpan? Timeout { get; init; }
    public string OnExpiry { get; init; } = "deadStop";

    public override IEnumerable<(string Pointer, string Expression)> Expressions()
    {
        foreach ((string pointer, string expression) in base.Expressions())
        {
            yield return (pointer, expression);
        }

        if (CorrelationKey is { Length: > 0 })
        {
            yield return ($"{Pointer}/correlationKey", CorrelationKey);
        }
    }
}

public sealed record DslFanInNode : DslNode
{
    /// <summary>Path within <c>data</c> that receives the aggregated array.</summary>
    public string Into { get; init; } = "items";
}

public sealed record DslCustomNode : DslNode
{
    /// <summary>Name registered via <c>AddDslNode</c>.</summary>
    public required string NodeName { get; init; }

    public JsonNode? With { get; init; }
}

/// <summary>RFC 6901 pointer escaping. Two characters, and getting them wrong misplaces a caret.</summary>
public static class JsonPointer
{
    public static string Escape(string segment)
        => segment.Replace("~", "~0", StringComparison.Ordinal)
                  .Replace("/", "~1", StringComparison.Ordinal);
}

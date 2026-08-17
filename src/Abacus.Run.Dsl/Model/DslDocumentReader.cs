using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml;
using Abacus.Run.Dsl.Validation;

namespace Abacus.Run.Dsl.Model;

/// <summary>
/// Builds the typed model from a document that has already passed schema validation.
/// </summary>
/// <remarks>
/// Hand-written rather than driven by <c>System.Text.Json</c> converters, for one reason: every
/// element has to know the JSON Pointer it came from, and a converter cannot see where in the
/// document it is being invoked.
/// </remarks>
public static class DslDocumentReader
{
    public static DslDocument Read(JsonNode document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var root = (JsonObject)document;

        return new DslDocument
        {
            Dsl = Str(root, "dsl") ?? string.Empty,
            Name = Str(root, "name") ?? string.Empty,
            Version = Str(root, "version") ?? string.Empty,
            Description = Str(root, "description"),
            ContextSchema = root["context"]?.DeepClone(),
            ResultSchema = root["result"]?.DeepClone(),
            Strict = Bool(root, "strict") ?? false,
            Start = Str(root, "start") ?? string.Empty,
            Output = StrList(root["output"]),
            Nodes = ReadNodes(root["nodes"] as JsonArray),
            Edges = ReadEdges(root["edges"] as JsonArray),
            Triggers = ReadTriggers(root["triggers"] as JsonArray),
            Notifications = ReadNotifications(root["notifications"] as JsonObject),
            OnFailure = ReadFailureRules(root["onFailure"] as JsonArray),
            Audit = ReadAudit(root["audit"] as JsonObject),
            Limits = ReadLimits(root["limits"] as JsonObject),
            Hash = DslCanonicalHash.Compute(document)
        };
    }

    // ---- nodes ----------------------------------------------------------------------------

    private static IReadOnlyList<DslNode> ReadNodes(JsonArray? array)
    {
        if (array is null)
        {
            return [];
        }

        var nodes = new List<DslNode>(array.Count);

        for (int i = 0; i < array.Count; i++)
        {
            if (array[i] is JsonObject node)
            {
                nodes.Add(ReadNode(node, $"/nodes/{i}"));
            }
        }

        return nodes;
    }

    private static DslNode ReadNode(JsonObject node, string pointer)
    {
        string id = Str(node, "id") ?? string.Empty;
        string kind = Str(node, "kind") ?? string.Empty;

        DslNode built = kind switch
        {
            DslNodeKinds.Transform => new DslTransformNode
            {
                Id = id,
                Kind = kind,
                Set = StrMap(node["set"] as JsonObject),
                Replace = Bool(node, "replace") ?? false
            },

            DslNodeKinds.Http => new DslHttpNode
            {
                Id = id,
                Kind = kind,
                Method = Str(node, "method") ?? "GET",
                Url = Str(node, "url") ?? string.Empty,
                Headers = StrMap(node["headers"] as JsonObject),
                Body = Str(node, "body"),
                TimeoutSeconds = Int(node, "timeoutSeconds") ?? 30,
                SuccessCodes = IntList(node["successCodes"]),
                AllowedHosts = StrList(node["allowedHosts"]),
                SendIdempotencyKey = Bool(node, "sendIdempotencyKey") ?? true
            },

            DslNodeKinds.Llm => new DslLlmNode
            {
                Id = id,
                Kind = kind,
                Model = Str(node, "model") ?? string.Empty,
                System = Str(node, "system"),
                Prompt = Str(node, "prompt") ?? string.Empty,
                PromptVersion = Str(node, "promptVersion"),
                StructuredOutput = node["structuredOutput"]?.DeepClone(),
                Temperature = (float?)Decimal(node, "temperature"),
                MaxTokens = Int(node, "maxTokens"),
                StreamDeltas = Bool(node, "streamDeltas") ?? false,
                EmitCompletion = Bool(node, "emitCompletion") ?? true
            },

            DslNodeKinds.Delay => new DslDelayNode
            {
                Id = id,
                Kind = kind,
                For = Duration(Str(node, "for")) ?? TimeSpan.Zero
            },

            DslNodeKinds.Approval => new DslApprovalNode { Id = id, Kind = kind },

            DslNodeKinds.Publish => new DslPublishNode
            {
                Id = id,
                Kind = kind,
                Topic = Str(node, "topic") ?? string.Empty,
                Payload = StrMap(node["payload"] as JsonObject),
                CorrelationKey = Str(node, "correlationKey"),
                Scope = Str(node, "scope") ?? "local"
            },

            DslNodeKinds.WaitEvent => new DslWaitEventNode
            {
                Id = id,
                Kind = kind,
                Topic = Str(node, "topic") ?? string.Empty,
                CorrelationKey = Str(node, "correlationKey"),
                Timeout = Duration(Str(node, "timeout")),
                OnExpiry = Str(node, "onExpiry") ?? "deadStop"
            },

            DslNodeKinds.FanIn => new DslFanInNode
            {
                Id = id,
                Kind = kind,
                Into = Str(node, "into") ?? "items"
            },

            DslNodeKinds.Custom => new DslCustomNode
            {
                Id = id,
                Kind = kind,
                NodeName = Str(node, "node") ?? string.Empty,
                With = node["with"]?.DeepClone()
            },

            // Unreachable through a schema-validated document; modelled rather than thrown so a
            // future kind added to the schema before the reader fails as a diagnostic, not a crash.
            _ => new DslApprovalNode { Id = id, Kind = kind }
        };

        return built with
        {
            Pointer = pointer,
            Description = Str(node, "description"),
            InputSchema = node["input"]?.DeepClone(),
            OutputSchema = node["output"]?.DeepClone(),
            Gate = ReadGate(node["gate"] as JsonObject, $"{pointer}/gate"),
            Notify = ReadNotify(node["notify"] as JsonObject, $"{pointer}/notify")
        };
    }

    private static DslGate? ReadGate(JsonObject? gate, string pointer)
    {
        if (gate is null)
        {
            return null;
        }

        var expiry = gate["onExpiry"] as JsonObject;

        return new DslGate
        {
            Mode = Str(gate, "mode") ?? "requireApproval",
            When = Str(gate, "when"),
            Reason = Str(gate, "reason"),
            AssignTo = StrList(gate["assignTo"]),
            RequireApprovers = Int(gate, "requireApprovers") ?? 1,
            ExpiresAfter = Duration(Str(gate, "expiresAfter")) ?? TimeSpan.FromHours(24),
            OnExpiryAction = expiry is null ? "deadStop" : Str(expiry, "action") ?? "deadStop",
            EscalateTo = expiry is null ? [] : StrList(expiry["assignTo"]),
            AllowModification = Bool(gate, "allowModification") ?? false,
            RequireSegregationOfDuties = Bool(gate, "requireSegregationOfDuties") ?? false,
            Locked = Bool(gate, "locked") ?? false,
            Pointer = pointer
        };
    }

    private static DslNotify? ReadNotify(JsonObject? notify, string pointer)
        => notify is null
            ? null
            : new DslNotify
            {
                Name = Str(notify, "name") ?? string.Empty,
                Payload = StrMap(notify["payload"] as JsonObject),
                Pointer = pointer
            };

    // ---- edges and the rest ---------------------------------------------------------------

    private static IReadOnlyList<DslEdge> ReadEdges(JsonArray? array)
    {
        if (array is null)
        {
            return [];
        }

        var edges = new List<DslEdge>(array.Count);

        for (int i = 0; i < array.Count; i++)
        {
            if (array[i] is not JsonObject edge)
            {
                continue;
            }

            edges.Add(new DslEdge
            {
                From = OneOrMany(edge["from"]),
                To = OneOrMany(edge["to"]),
                When = Str(edge, "when"),
                Select = Str(edge, "select"),
                Label = Str(edge, "label"),
                Idempotent = Bool(edge, "idempotent") ?? false,
                Pointer = $"/edges/{i}"
            });
        }

        return edges;
    }

    private static IReadOnlyList<DslTrigger> ReadTriggers(JsonArray? array)
    {
        if (array is null)
        {
            return [];
        }

        var triggers = new List<DslTrigger>(array.Count);

        for (int i = 0; i < array.Count; i++)
        {
            if (array[i] is not JsonObject trigger)
            {
                continue;
            }

            triggers.Add(new DslTrigger
            {
                Topic = Str(trigger, "topic") ?? string.Empty,
                CorrelationKey = Str(trigger, "correlationKey"),
                ContextFrom = Str(trigger, "contextFrom"),
                Pointer = $"/triggers/{i}"
            });
        }

        return triggers;
    }

    private static DslNotifications? ReadNotifications(JsonObject? notifications)
        => notifications is null
            ? null
            : new DslNotifications
            {
                Level = Str(notifications, "level") ?? "standard",
                Stream = Bool(notifications, "stream") ?? true,
                ByNode = StrMap(notifications["byNode"] as JsonObject),
                Emits = StrList(notifications["emits"]),
                Pointer = "/notifications"
            };

    private static IReadOnlyList<DslFailureRule> ReadFailureRules(JsonArray? array)
    {
        if (array is null)
        {
            return [];
        }

        var rules = new List<DslFailureRule>(array.Count);

        for (int i = 0; i < array.Count; i++)
        {
            if (array[i] is not JsonObject rule)
            {
                continue;
            }

            var match = rule["match"] as JsonObject;

            rules.Add(new DslFailureRule
            {
                Exception = match is null ? null : Str(match, "exception"),
                Status = match is null ? null : Str(match, "status"),
                Node = match is null ? null : Str(match, "node"),
                Disposition = Str(rule, "disposition") ?? "retry",
                Pointer = $"/onFailure/{i}"
            });
        }

        return rules;
    }

    private static DslAudit? ReadAudit(JsonObject? audit)
        => audit is null
            ? null
            : new DslAudit
            {
                Key = Str(audit, "key"),
                Sections = StrList(audit["sections"]),
                Pointer = "/audit"
            };

    private static DslLimits ReadLimits(JsonObject? limits)
        => limits is null
            ? new DslLimits()
            : new DslLimits
            {
                MaxAttempts = Int(limits, "maxAttempts") ?? 5,
                MaxLifetimeHours = Int(limits, "maxLifetimeHours")
            };

    // ---- primitives -----------------------------------------------------------------------

    private static string? Str(JsonObject obj, string name)
        => obj[name] is JsonValue value && value.TryGetValue(out string? s) ? s : null;

    private static bool? Bool(JsonObject obj, string name)
        => obj[name] is JsonValue value && value.TryGetValue(out bool b) ? b : null;

    private static int? Int(JsonObject obj, string name)
        => obj[name] is JsonValue value && value.TryGetValue(out int i) ? i : null;

    private static decimal? Decimal(JsonObject obj, string name)
        => obj[name] is JsonValue value && value.TryGetValue(out decimal d) ? d : null;

    private static IReadOnlyList<string> StrList(JsonNode? node)
        => node is JsonArray array
            ? array.Select(n => n?.GetValue<string>()).Where(s => s is not null).Select(s => s!).ToArray()
            : [];

    private static IReadOnlyList<int> IntList(JsonNode? node)
        => node is JsonArray array
            ? array.Where(n => n is not null).Select(n => n!.GetValue<int>()).ToArray()
            : [];

    private static IReadOnlyDictionary<string, string> StrMap(JsonObject? obj)
    {
        if (obj is null)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach ((string key, JsonNode? value) in obj)
        {
            if (value is JsonValue jsonValue && jsonValue.TryGetValue(out string? text) && text is not null)
            {
                map[key] = text;
            }
        }

        return map;
    }

    /// <summary>Normalises the two edge shapes — one endpoint or several — into one list.</summary>
    private static IReadOnlyList<string> OneOrMany(JsonNode? node) => node switch
    {
        JsonArray array => StrList(array),
        JsonValue value when value.TryGetValue(out string? s) && s is not null => [s],
        _ => []
    };

    /// <summary>
    /// ISO-8601 duration. The schema has already checked the syntax, so a failure here means the
    /// pattern and this parser disagree — treated as absent rather than thrown, and caught by the
    /// semantic validator's own range checks.
    /// </summary>
    internal static TimeSpan? Duration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return XmlConvert.ToTimeSpan(value);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    internal static string ToIso8601(TimeSpan value)
        => XmlConvert.ToString(value);

    internal static string Number(decimal value)
        => value.ToString(CultureInfo.InvariantCulture);
}

using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using Abacus.Run.Abstractions;
using Abacus.Run.Core;
using Abacus.Run.Dsl.Expressions;
using Abacus.Run.Dsl.Model;
using Abacus.Run.Executors;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace Abacus.Run.Dsl.Interpretation;

/// <summary>
/// Opens the envelope. The workflow's declared input is a <see cref="JsonElement"/> — that is what
/// the runner deserializes the stored context into and sends as the first message — while every DSL
/// node speaks <see cref="DslMessage"/>.
/// </summary>
/// <remarks>
/// A real node rather than a conversion hidden inside the first one: the engine routes by message
/// type, so without something typed to accept the context the first node simply never receives it
/// and the run completes having done nothing at all.
/// </remarks>
internal sealed class DslEntryExecutor : HostExecutor<JsonElement, DslMessage>
{
    /// <summary>
    /// Cannot collide with a declared node id: those must match <c>^[a-z][a-z0-9-]{0,63}$</c>, which
    /// forbids a leading dollar.
    /// </summary>
    internal const string NodeId = "$entry";

    internal DslEntryExecutor() : base(NodeId) { }

    public override IReadOnlyDictionary<string, object?> Metadata =>
        new Dictionary<string, object?> { ["node.kind"] = "entry" };

    protected override ValueTask<DslMessage> ExecuteCoreAsync(
        JsonElement input, IWorkflowContext context, CancellationToken cancellationToken)
        => ValueTask.FromResult(DslMessage.Start(input));
}

/// <summary>
/// Closes the envelope: the workflow's result is the payload, not the envelope that carried it.
/// </summary>
/// <remarks>
/// <para>
/// A node rather than a <c>YieldOutputAsync</c> call inside each output node, because the engine
/// checks a yielded value against the executor's <em>declared</em> output type — a DSL node declares
/// <see cref="DslMessage"/> and may not yield anything else.
/// </para>
/// <para>
/// Without it the caller would get back the whole envelope, including the start context every
/// message carries so that expressions can reach it. That context is machinery, not a result.
/// </para>
/// </remarks>
internal sealed class DslExitExecutor : HostExecutor<DslMessage, JsonObject>
{
    internal const string NodeId = "$exit";

    internal DslExitExecutor() : base(NodeId) { }

    public override IReadOnlyDictionary<string, object?> Metadata =>
        new Dictionary<string, object?> { ["node.kind"] = "exit" };

    protected override ValueTask<JsonObject> ExecuteCoreAsync(
        DslMessage input, IWorkflowContext context, CancellationToken cancellationToken)
        => ValueTask.FromResult(Unwrap(input.Data));

    /// <summary>
    /// Payload data is an object in every shape the built-in nodes produce. A document that ends on
    /// something else still gets a result rather than a failure, wrapped so the shape is predictable.
    /// </summary>
    internal static JsonObject Unwrap(JsonNode? data) => data switch
    {
        JsonObject obj => (JsonObject)obj.DeepClone(),
        null => [],
        _ => new JsonObject { ["value"] = data.DeepClone() }
    };
}

/// <summary>Pure projection. The only node that computes, and it computes only through AbEx.</summary>
internal sealed class DslTransformExecutor : DslExecutor
{
    private readonly DslTransformNode _node;

    internal DslTransformExecutor(DslTransformNode node, TimeProvider? clock = null) : base(node, clock)
        => _node = node;

    protected override ValueTask<DslMessage?> RunAsync(
        DslMessage input, IWorkflowContext context, CancellationToken cancellationToken)
    {
        JsonNode? data = DslExpressions.ApplySet(_node.Set, Context(input), input.Data, _node.Replace);
        return ValueTask.FromResult<DslMessage?>(input.WithData(data));
    }
}

/// <summary>
/// Aggregates a fan-in barrier's inputs. The one node whose input is not a bare envelope, because the
/// engine delivers a barrier's messages as a list.
/// </summary>
internal sealed class DslFanInExecutor : DslExecutor
{
    private readonly DslFanInNode _node;
    private readonly int _expected;
    private readonly List<JsonNode?> _arrived = [];
    private readonly Lock _gate = new();

    internal DslFanInExecutor(DslFanInNode node, int expectedSources, TimeProvider? clock = null)
        : base(node, clock)
    {
        _node = node;
        _expected = Math.Max(1, expectedSources);
    }

    protected override ValueTask<DslMessage?> RunAsync(
        DslMessage input, IWorkflowContext context, CancellationToken cancellationToken)
    {
        // A barrier releases its held messages together, but the engine still delivers them one at a
        // time — it type-checks the target against the individual message, not against a list. So the
        // aggregation lives here: hold each arrival, and emit once the last one lands.
        JsonArray? items = null;

        lock (_gate)
        {
            _arrived.Add(input.Data?.DeepClone());

            if (_arrived.Count >= _expected)
            {
                items = [.. _arrived];
                _arrived.Clear();
            }
        }

        if (items is null)
        {
            // Not the last arrival. Returning null emits nothing — the same mechanism a gated node
            // uses to stay silent, without requesting a halt.
            return ValueTask.FromResult<DslMessage?>(null);
        }

        var data = new JsonObject();
        DslExpressions.Assign(data, _node.Into, items);

        // The context is identical on every branch by construction — it is frozen at start — so
        // continuing with this arrival's envelope is not a choice between differing values.
        return ValueTask.FromResult<DslMessage?>(input.WithData(data));
    }
}

/// <summary>
/// Builds the executor for each built-in <c>kind</c>.
/// </summary>
/// <remarks>
/// Every one of these maps onto an executor the host already ships. Nothing here reimplements HTTP,
/// prompting, egress control, idempotency keys, durable waits or cost accounting — the DSL is a
/// front end, and a front end that forked the execution path would stop being one.
/// </remarks>
internal static class DslBuiltInNodes
{
    internal static IHostExecutor Create(DslNodeContext context, DslNodeCatalog catalog, TimeProvider clock)
        => context.Node switch
        {
            DslTransformNode node => new DslTransformExecutor(node, clock),
            DslFanInNode node => new DslFanInExecutor(node, BarrierSourceCount(context.Document, node.Id), clock),
            DslApprovalNode node => Approval(node, context, clock),
            DslHttpNode node => Http(node, context, clock),
            DslLlmNode node => Llm(node, context, clock),
            DslDelayNode node => Delay(node, context, clock),
            DslPublishNode node => Publish(node, context, clock),
            DslWaitEventNode node => WaitEvent(node, context, clock),
            DslCustomNode node => Custom(node, context, catalog, clock),
            _ => throw new NotSupportedException(
                $"Node '{context.Node.Id}' has kind '{context.Node.Kind}', which the interpreter does not build.")
        };

    /// <summary>
    /// How many messages a barrier will release into this node. Read from the document rather than
    /// counted at run time, because the node has to know when it has them all before the last one
    /// arrives.
    /// </summary>
    private static int BarrierSourceCount(Model.DslDocument document, string nodeId)
        => document.Edges
            .Where(e => e.IsBarrier && e.To.Contains(nodeId, StringComparer.Ordinal))
            .Sum(e => e.From.Count);

    /// <summary>
    /// Identity work. The pause comes from the gate the factory guarantees, so the node is visible in
    /// the graph as the place a human decides rather than as configuration on some other node.
    /// </summary>
    private static IHostExecutor Approval(DslApprovalNode node, DslNodeContext context, TimeProvider clock)
    {
        var inner = new HumanApprovalExecutor<DslMessage>(node.Id);

        return new DslHostedExecutor(
            node, inner, inner.ExecuteTerminalAsync,
            static (output, _) => (DslMessage)output, clock);
    }

    private static IHostExecutor Http(DslHttpNode node, DslNodeContext context, TimeProvider clock)
    {
        var options = new ApiCallOptions
        {
            Method = new HttpMethod(node.Method),
            UrlTemplate = node.Url,
            Headers = new Dictionary<string, string>(node.Headers),
            BodyTemplate = node.Body,
            TimeoutSeconds = node.TimeoutSeconds,
            AllowedHosts = [.. node.AllowedHosts],
            SendIdempotencyKey = node.SendIdempotencyKey
        };

        if (node.SuccessCodes.Count > 0)
        {
            options.SuccessCodes = [.. node.SuccessCodes];
        }

        IHttpClientFactory? factory = context.Optional<IHttpClientFactory>();
        Func<HttpClient> clientFactory = factory is null
            ? static () => new HttpClient()
            : () => factory.CreateClient(ApiCallOptions.HttpClientName);

        var inner = new ApiCallExecutor(node.Id, options, clientFactory);

        return new DslHostedExecutor(node, inner, inner.ExecuteTerminalAsync, ProjectHttp, clock);
    }

    /// <summary>
    /// Status alongside body, so a document can route on either. Both are needed: the body carries
    /// the answer, and the status is how a workflow tells "found nothing" from "not found".
    /// </summary>
    private static DslMessage ProjectHttp(object output, DslMessage input)
    {
        var result = (ApiCallResult)output;

        return input.WithData(new JsonObject
        {
            ["status"] = result.StatusCode,
            ["body"] = ToJson(result.Body, result.RawBody)
        });
    }

    private static JsonNode? ToJson(object? body, string? raw)
    {
        if (body is not null and not string)
        {
            return JsonSerializer.SerializeToNode(body, JsonOptions.Default);
        }

        string? text = body as string ?? raw;
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            // A JSON response is far more useful addressable than as a string, and a non-JSON one
            // must not fail the node for being what it always was.
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return JsonValue.Create(text);
        }
    }

    private static IHostExecutor Llm(DslLlmNode node, DslNodeContext context, TimeProvider clock)
    {
        var options = new LlmOptions
        {
            Model = node.Model,
            SystemPrompt = node.System,
            UserTemplate = node.Prompt,
            PromptVersion = node.PromptVersion,
            Temperature = node.Temperature,
            MaxTokens = node.MaxTokens,
            StreamDeltas = node.StreamDeltas,
            EmitCompletion = node.EmitCompletion
        };

        var resolver = context.Optional<IChatClientResolver>();
        Func<string, IChatClient> clientResolver = resolver is not null
            ? resolver.Resolve
            : _ => context.Require<IChatClient>();

        var inner = new LlmExecutor(node.Id, options, clientResolver, context.Optional<IModelPricing>());

        return new DslHostedExecutor(node, inner, inner.ExecuteTerminalAsync, ProjectLlm, clock);
    }

    /// <summary>
    /// Text and the parsed value, plus the numbers a run is judged by. Cost and tokens are on the
    /// envelope as well as in the log because a document may want to branch on them.
    /// </summary>
    private static DslMessage ProjectLlm(object output, DslMessage input)
    {
        var result = (LlmResult)output;

        return input.WithData(new JsonObject
        {
            ["text"] = result.Text,
            ["value"] = result.Value is null or string
                ? JsonValue.Create(result.Text)
                : JsonSerializer.SerializeToNode(result.Value, JsonOptions.Default),
            ["model"] = result.ModelId,
            ["inputTokens"] = result.InputTokens,
            ["outputTokens"] = result.OutputTokens,
            ["costUsd"] = result.CostUsd,
            ["finishReason"] = result.FinishReason,
            ["elapsedMs"] = (long)result.Elapsed.TotalMilliseconds
        });
    }

    private static IHostExecutor Delay(DslDelayNode node, DslNodeContext context, TimeProvider clock)
    {
        var inner = new DelayExecutor(node.Id, node.For, context.Require<ITimerService>(), clock);

        // The envelope passes through: a delay is about when the next node runs, not about changing
        // what it receives, and losing the payload to a TimerElapsed record would make every delay
        // need a transform after it.
        return new DslHostedExecutor(node, inner, inner.ExecuteTerminalAsync,
            static (_, input) => input, clock);
    }

    private static IHostExecutor Publish(DslPublishNode node, DslNodeContext context, TimeProvider clock)
    {
        var broker = context.Require<IDomainEventBroker>();

        DeliveryScope scope = string.Equals(node.Scope, "distributed", StringComparison.Ordinal)
            ? DeliveryScope.Distributed
            : DeliveryScope.Local;

        var inner = new PublishDomainEventExecutor<DslMessage>(
            node.Id,
            broker,
            node.Topic,
            payload: message => BuildPayload(node, message),
            correlationKey: message => node.CorrelationKey is null
                ? null
                : DslExpressions.Text(node.CorrelationKey, message.ToExpressionContext(message.Run)),
            scope: scope,
            clock: clock);

        // Publishing passes its input through, so the envelope continues unchanged.
        return new DslHostedExecutor(node, inner, inner.ExecuteTerminalAsync,
            static (output, _) => (DslMessage)output, clock);
    }

    /// <summary>
    /// An explicit payload map, or the whole of <c>data</c>. Defaulting to the payload rather than
    /// the envelope matters: a subscriber should receive the message, not this workflow's context.
    /// </summary>
    private static object BuildPayload(DslPublishNode node, DslMessage message)
    {
        if (node.Payload.Count == 0)
        {
            return message.Data ?? (JsonNode)new JsonObject();
        }

        AbExContext context = message.ToExpressionContext(message.Run);
        var payload = new JsonObject();

        foreach ((string key, string expression) in node.Payload)
        {
            AbExValue value = DslExpressions.Evaluate(expression, context);
            payload[key] = value.IsAbsent ? null : value.ToNode();
        }

        return payload;
    }

    private static IHostExecutor WaitEvent(DslWaitEventNode node, DslNodeContext context, TimeProvider clock)
    {
        WaitExpiryAction onExpiry = string.Equals(node.OnExpiry, "resume", StringComparison.Ordinal)
            ? WaitExpiryAction.Resume
            : WaitExpiryAction.DeadStop;

        var inner = new WaitForDomainEventExecutor<DslMessage, JsonNode>(
            node.Id,
            context.Require<IDomainEventSubscriptionStore>(),
            node.Topic,
            correlationKey: message => node.CorrelationKey is null
                ? null
                : DslExpressions.Text(node.CorrelationKey, message.ToExpressionContext(message.Run)),
            timeout: node.Timeout,
            onExpiry: onExpiry,
            clock: clock);

        // Runs twice: the first pass registers the wait and parks (null output, propagated by the
        // hosted executor), the second finds the delivered payload and returns it as the new data.
        return new DslHostedExecutor(node, inner, inner.ExecuteTerminalAsync,
            static (output, input) => input.WithData((JsonNode)output), clock);
    }

    private static IHostExecutor Custom(
        DslCustomNode node, DslNodeContext context, DslNodeCatalog catalog, TimeProvider clock)
    {
        if (!catalog.TryGet(node.NodeName, out IDslNodeFactory factory))
        {
            // Validation refuses this at registration, so reaching here means the catalog changed
            // underneath a document that was already accepted.
            throw new DslInterpretationException(
                $"Node '{node.Id}' names custom node '{node.NodeName}', which is not registered. " +
                $"Known: {(catalog.Names.Count == 0 ? "(none)" : string.Join(", ", catalog.Names))}.");
        }

        IHostExecutor executor = factory.Create(context)
            ?? throw new DslInterpretationException(
                $"The factory for custom node '{node.NodeName}' returned null for node '{node.Id}'.");

        if (executor.InputType != typeof(DslMessage) || executor.OutputType != typeof(DslMessage))
        {
            // Every edge in a DSL graph carries the envelope. A node emitting anything else breaks
            // the next edge rather than its own, so it is refused where the mistake was made.
            throw new DslInterpretationException(
                $"Custom node '{node.NodeName}' produced an executor of " +
                $"{executor.InputType.Name} -> {executor.OutputType.Name}. " +
                $"A DSL node must be HostExecutor<{nameof(DslMessage)}, {nameof(DslMessage)}>.");
        }

        if (!string.Equals(executor.Id, node.Id, StringComparison.Ordinal))
        {
            throw new DslInterpretationException(
                $"Custom node '{node.NodeName}' produced an executor with id '{executor.Id}', " +
                $"but the document declared '{node.Id}'. Gate policy and node state key off the " +
                "declared id, so they must match.");
        }

        // Hosted rather than returned bare, so a custom node gets everything a built-in one gets:
        // the bound expression roots, its declared notification, and the output yield. A factory
        // author writes ExecuteCoreAsync and nothing else.
        var typed = (HostExecutor<DslMessage, DslMessage>)executor;

        return new DslHostedExecutor(
            node, typed, typed.ExecuteTerminalAsync,
            static (output, _) => (DslMessage)output, clock);
    }
}

/// <summary>
/// Resolves a chat client by model name.
/// </summary>
/// <remarks>
/// A document names its model as a string, and a host serving several models needs some way to map
/// that to a client. Optional: a host with one model registers an <see cref="IChatClient"/> and
/// nothing else.
/// </remarks>
public interface IChatClientResolver
{
    IChatClient Resolve(string model);
}

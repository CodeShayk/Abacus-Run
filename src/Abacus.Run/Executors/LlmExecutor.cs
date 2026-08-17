using System.Text.Json;
using Abacus.Run.Abstractions;
using Abacus.Run.Abstractions.Middleware;
using Abacus.Run.Core;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace Abacus.Run.Executors;

public sealed record LlmResult(
    object? Value,
    string? Text,
    long? InputTokens,
    long? OutputTokens,
    string? ModelId,
    string? FinishReason)
{
    /// <summary>Null when the model has no configured price — absent, not free.</summary>
    public decimal? CostUsd { get; init; }

    public TimeSpan Elapsed { get; init; }
}

public sealed class LlmOptions
{
    public required string Model { get; set; }
    public string? SystemPrompt { get; set; }
    public string? PromptVersion { get; set; }
    public required string UserTemplate { get; set; }
    public Type? StructuredOutput { get; set; }
    public float? Temperature { get; set; }
    public int? MaxTokens { get; set; }
    public bool StreamDeltas { get; set; }
    public int MaxReparseAttempts { get; set; } = 2;

    /// <summary>
    /// Emit one <c>llm.completed</c> per invocation carrying model, tokens, cost and latency.
    /// On by default: the cost of a model call is something a consumer of the run should see.
    /// </summary>
    public bool EmitCompletion { get; set; } = true;
}

/// <summary>
/// Invokes a chat model through <see cref="IChatClient"/>, so any provider the Agent Framework
/// supports works unchanged.
/// </summary>
public sealed class LlmExecutor : HostExecutor<object, LlmResult>
{
    private readonly LlmOptions _options;
    private readonly Func<string, IChatClient> _clientResolver;
    private readonly IModelPricing? _pricing;

    public LlmExecutor(
        string id,
        LlmOptions options,
        Func<string, IChatClient> clientResolver,
        IModelPricing? pricing = null)
        : base(id)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clientResolver = clientResolver ?? throw new ArgumentNullException(nameof(clientResolver));
        _pricing = pricing;
    }

    public override IReadOnlyDictionary<string, object?> Metadata => new Dictionary<string, object?>
    {
        ["node.kind"] = "llm",
        ["llm.model"] = _options.Model,
        [MiddlewareContextKeys.PromptVersion] = _options.PromptVersion
    };

    protected override async ValueTask<LlmResult> ExecuteCoreAsync(
        object input, IWorkflowContext context, CancellationToken cancellationToken)
    {
        IChatClient client = _clientResolver(_options.Model);
        TemplateBindings bindings = TemplateBindings.From(input);

        var messages = new List<ChatMessage>();
        if (_options.SystemPrompt is { Length: > 0 } system)
        {
            messages.Add(new ChatMessage(ChatRole.System, system));
        }
        messages.Add(new ChatMessage(ChatRole.User, TemplateEngine.Render(_options.UserTemplate, bindings)));

        var chatOptions = new ChatOptions
        {
            ModelId = _options.Model,
            Temperature = _options.Temperature,
            MaxOutputTokens = _options.MaxTokens
        };

        long start = System.Diagnostics.Stopwatch.GetTimestamp();

        ChatResponse response = _options.StreamDeltas
            ? await StreamAsync(client, messages, chatOptions, context, cancellationToken).ConfigureAwait(false)
            : await client.GetResponseAsync(messages, chatOptions, cancellationToken).ConfigureAwait(false);

        TimeSpan elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(start);
        string text = response.Text ?? string.Empty;

        object? value = _options.StructuredOutput is null
            ? text
            : StructuredOutputParser.Parse(text, _options.StructuredOutput, _options.MaxReparseAttempts);

        long? inputTokens = response.Usage?.InputTokenCount;
        long? outputTokens = response.Usage?.OutputTokenCount;
        string modelId = response.ModelId ?? _options.Model;

        decimal? cost = _pricing?.CostOf(modelId, inputTokens ?? 0, outputTokens ?? 0);

        var result = new LlmResult(
            value, text, inputTokens, outputTokens, modelId, response.FinishReason?.ToString())
        {
            CostUsd = cost,
            Elapsed = elapsed
        };

        await ReportAsync(result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// One event and one log entry per call. The prompt and the response text are deliberately
    /// absent from both: they already travel the executor's input/output path where redaction
    /// applies, and repeating them here would put model output on a stream a UI reads.
    /// </summary>
    private async ValueTask ReportAsync(LlmResult result, CancellationToken cancellationToken)
    {
        if (_options.EmitCompletion && Runtime.Notify is { } notify)
        {
            await notify.EmitReservedAsync(WorkflowEventTypes.LlmCompleted, new
            {
                executorId = Id,
                model = result.ModelId,
                promptVersion = _options.PromptVersion,
                inputTokens = result.InputTokens,
                outputTokens = result.OutputTokens,
                costUsd = result.CostUsd,
                elapsedMs = (long)result.Elapsed.TotalMilliseconds,
                finishReason = result.FinishReason,
                attempt = Runtime.Attempt,
                streamed = _options.StreamDeltas
            }, transient: false, cancellationToken).ConfigureAwait(false);
        }

        // Metrics give the aggregate; this makes the same numbers queryable per instance, next to
        // everything else that instance did.
        if (Runtime.Services?.GetService(typeof(ILogStore)) is ILogStore logs)
        {
            await logs.AppendAsync(new InstanceLogEntry
            {
                InstanceId = Runtime.InstanceId,
                Sequence = 0,
                Level = "Information",
                ExecutorId = Id,
                Superstep = Runtime.CurrentSuperstep,
                Message =
                    $"llm usage model={result.ModelId} in={result.InputTokens} out={result.OutputTokens} " +
                    $"cost={(result.CostUsd is { } c ? c.ToString("F6") : "unpriced")} " +
                    $"ms={(long)result.Elapsed.TotalMilliseconds} finish={result.FinishReason}",
                LoggedAt = DateTimeOffset.UtcNow
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ChatResponse> StreamAsync(
        IChatClient client,
        List<ChatMessage> messages,
        ChatOptions chatOptions,
        IWorkflowContext context,
        CancellationToken cancellationToken)
    {
        var updates = new List<ChatResponseUpdate>();

        await foreach (ChatResponseUpdate update in client
            .GetStreamingResponseAsync(messages, chatOptions, cancellationToken).ConfigureAwait(false))
        {
            updates.Add(update);

            if (update.Text is { Length: > 0 })
            {
                // Surfaced to SSE subscribers as llm.delta by the runner's event translation.
                await context.AddEventAsync(new LlmDeltaWorkflowEvent(Id, update.Text), cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return updates.ToChatResponse();
    }
}


public static class StructuredOutputParser
{
    /// <summary>
    /// Parses model output into <paramref name="targetType"/>, tolerating the usual fenced-code
    /// wrapping. A model that cannot produce the schema will not on a host-level retry, so exhausting
    /// attempts throws <see cref="StructuredOutputException"/> — which classifies as dead-stop.
    /// </summary>
    public static object Parse(string text, Type targetType, int maxAttempts)
    {
        ArgumentNullException.ThrowIfNull(targetType);

        string candidate = Unwrap(text ?? string.Empty);
        Exception? last = null;

        for (int attempt = 1; attempt <= Math.Max(1, maxAttempts); attempt++)
        {
            try
            {
                object? parsed = JsonSerializer.Deserialize(candidate, targetType, JsonOptions.Default);
                if (parsed is not null)
                {
                    return parsed;
                }

                last = new JsonException("Deserialized to null.");
            }
            catch (JsonException ex)
            {
                last = ex;
            }

            // Retry once against the largest embedded JSON object before giving up.
            string extracted = ExtractFirstJsonObject(candidate);
            if (string.Equals(extracted, candidate, StringComparison.Ordinal))
            {
                break;
            }
            candidate = extracted;
        }

        throw new StructuredOutputException(targetType, maxAttempts, last?.Message);
    }

    internal static string Unwrap(string text)
    {
        string trimmed = text.Trim();

        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        int firstNewline = trimmed.IndexOf('\n');
        if (firstNewline < 0)
        {
            return trimmed;
        }

        string body = trimmed[(firstNewline + 1)..];
        int fence = body.LastIndexOf("```", StringComparison.Ordinal);
        return (fence < 0 ? body : body[..fence]).Trim();
    }

    internal static string ExtractFirstJsonObject(string text)
    {
        int start = text.IndexOfAny(['{', '[']);
        if (start < 0)
        {
            return text;
        }

        char open = text[start];
        char close = open == '{' ? '}' : ']';
        int depth = 0;

        for (int i = start; i < text.Length; i++)
        {
            if (text[i] == open)
            {
                depth++;
            }
            else if (text[i] == close)
            {
                depth--;
                if (depth == 0)
                {
                    return text[start..(i + 1)];
                }
            }
        }

        return text;
    }
}

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
    string? FinishReason);

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
}

/// <summary>
/// Invokes a chat model through <see cref="IChatClient"/>, so any provider the Agent Framework
/// supports works unchanged.
/// </summary>
public sealed class LlmExecutor : HostExecutor<object, LlmResult>
{
    private readonly LlmOptions _options;
    private readonly Func<string, IChatClient> _clientResolver;

    public LlmExecutor(string id, LlmOptions options, Func<string, IChatClient> clientResolver)
        : base(id)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clientResolver = clientResolver ?? throw new ArgumentNullException(nameof(clientResolver));
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

        ChatResponse response = _options.StreamDeltas
            ? await StreamAsync(client, messages, chatOptions, context, cancellationToken).ConfigureAwait(false)
            : await client.GetResponseAsync(messages, chatOptions, cancellationToken).ConfigureAwait(false);

        string text = response.Text ?? string.Empty;

        object? value = _options.StructuredOutput is null
            ? text
            : StructuredOutputParser.Parse(text, _options.StructuredOutput, _options.MaxReparseAttempts);

        return new LlmResult(
            value,
            text,
            response.Usage?.InputTokenCount,
            response.Usage?.OutputTokenCount,
            response.ModelId ?? _options.Model,
            response.FinishReason?.ToString());
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

/// <summary>Streaming token event, carried on the normal workflow event stream.</summary>
public sealed class LlmDeltaWorkflowEvent : WorkflowEvent
{
    public LlmDeltaWorkflowEvent(string executorId, string delta) : base(delta)
    {
        ExecutorId = executorId;
        Delta = delta;
    }

    public string ExecutorId { get; }
    public string Delta { get; }
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

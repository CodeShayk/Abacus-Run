using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Abacus.Run.Abstractions;
using Abacus.Run.Abstractions.Middleware;
using Abacus.Run.Core;
using Microsoft.Agents.AI.Workflows;

namespace Abacus.Run.Executors;

public sealed record ApiCallResult(int StatusCode, object? Body, string? RawBody);

public sealed class ApiCallOptions
{
    public const string HttpClientName = "abacus.run.apicall";

    public HttpMethod Method { get; set; } = HttpMethod.Get;
    public required string UrlTemplate { get; set; }
    public Dictionary<string, string> Headers { get; set; } = [];
    public string? BodyTemplate { get; set; }
    public int TimeoutSeconds { get; set; } = 30;
    public HashSet<int> SuccessCodes { get; set; } = [200, 201, 202, 204];
    public Type? ResponseAs { get; set; }
    public List<string> AllowedHosts { get; set; } = [];
    public bool EnforceEgress { get; set; } = true;
    public bool SendIdempotencyKey { get; set; } = true;
}

/// <summary>
/// Declarative HTTP node. Non-success responses surface as <see cref="ApiCallFailureException"/> so the
/// workflow's classifier can decide retry vs. dead-stop from the status code.
/// </summary>
public sealed class ApiCallExecutor : HostExecutor<object, ApiCallResult>
{
    private readonly ApiCallOptions _options;
    private readonly Func<HttpClient> _clientFactory;

    public ApiCallExecutor(string id, ApiCallOptions options, Func<HttpClient> clientFactory)
        : base(id)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
    }

    public override IReadOnlyDictionary<string, object?> Metadata => new Dictionary<string, object?>
    {
        ["node.kind"] = "api-call",
        ["http.method"] = _options.Method.Method
    };

    protected override async ValueTask<ApiCallResult> ExecuteCoreAsync(
        object input, IWorkflowContext context, CancellationToken cancellationToken)
    {
        TemplateBindings bindings = TemplateBindings.From(input);
        string url = TemplateEngine.Render(_options.UrlTemplate, bindings);

        EgressGuard.Assert(url, _options.AllowedHosts, _options.EnforceEgress);

        using var request = new HttpRequestMessage(_options.Method, url);

        foreach ((string key, string value) in _options.Headers)
        {
            request.Headers.TryAddWithoutValidation(key, TemplateEngine.Render(value, bindings));
        }

        if (_options.SendIdempotencyKey)
        {
            // Deterministic within an attempt, distinct across attempts: a retried superstep re-sends
            // the same key, so a well-behaved dependency de-duplicates it.
            object? attempt = Runtime.Descriptor.Metadata.TryGetValue("attempt", out object? a) ? a : 1;
            request.Headers.TryAddWithoutValidation("Idempotency-Key", $"{Runtime.InstanceId}:{Id}:{attempt}");
        }

        if (_options.BodyTemplate is { Length: > 0 } bodyTemplate)
        {
            request.Content = new StringContent(
                TemplateEngine.Render(bodyTemplate, bindings), Encoding.UTF8, "application/json");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

        HttpClient client = _clientFactory();
        using HttpResponseMessage response = await client
            .SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token)
            .ConfigureAwait(false);

        string raw = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

        if (!_options.SuccessCodes.Contains((int)response.StatusCode))
        {
            throw new ApiCallFailureException(
                (int)response.StatusCode,
                Truncate(raw, 2048),
                ReadRetryAfter(response.Headers));
        }

        object? body = _options.ResponseAs is null || string.IsNullOrWhiteSpace(raw)
            ? raw
            : JsonSerializer.Deserialize(raw, _options.ResponseAs, JsonOptions.Default);

        return new ApiCallResult((int)response.StatusCode, body, raw);
    }

    internal static TimeSpan? ReadRetryAfter(HttpResponseHeaders headers)
    {
        if (headers.RetryAfter is null)
        {
            return null;
        }

        if (headers.RetryAfter.Delta is { } delta)
        {
            return delta;
        }

        if (headers.RetryAfter.Date is { } date)
        {
            TimeSpan wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
    }

    internal static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max];
}

/// <summary>Captures the outbound call so middleware can observe or short-circuit it.</summary>
public sealed class OutboundCallCaptureHandler : DelegatingHandler
{
    private readonly Func<ExecutorInvocationContext?> _contextAccessor;

    public OutboundCallCaptureHandler(Func<ExecutorInvocationContext?> contextAccessor, HttpMessageHandler? inner = null)
    {
        _contextAccessor = contextAccessor;
        if (inner is not null)
        {
            InnerHandler = inner;
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ExecutorInvocationContext? context = _contextAccessor();
        var handle = new OutboundCallHandle(request);

        context?.Items[MiddlewareContextKeys.OutboundCall] = handle;

        if (handle.IsShortCircuited)
        {
            return handle.Response!;
        }

        HttpResponseMessage response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        handle.Capture(response);
        return response;
    }
}

internal sealed class OutboundCallHandle : IOutboundCallHandle
{
    public OutboundCallHandle(HttpRequestMessage request) => Request = request;

    public HttpRequestMessage Request { get; }
    public HttpResponseMessage? Response { get; private set; }
    public bool IsShortCircuited { get; private set; }

    public void SetSyntheticResponse(HttpResponseMessage response)
    {
        Response = response ?? throw new ArgumentNullException(nameof(response));
        IsShortCircuited = true;
    }

    internal void Capture(HttpResponseMessage response) => Response = response;
}

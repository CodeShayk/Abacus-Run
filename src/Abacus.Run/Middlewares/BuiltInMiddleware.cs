using System.Diagnostics;
using System.Diagnostics.Metrics;
using Abacus.Run.Abstractions.Middleware;
using Abacus.Run.Core;
using Microsoft.Extensions.Logging;

namespace Abacus.Run.Middlewares;

public static class Telemetry
{
    public const string SourceName = "Abacus.Run";

    public static ActivitySource ActivitySource { get; } = new(SourceName);
    public static Meter Meter { get; } = new(SourceName);
}

/// <summary>Outermost middleware: one span and one duration measurement per executor invocation.</summary>
public sealed class OpenTelemetryExecutorMiddleware : IExecutorMiddleware
{
    private readonly ActivitySource _source;
    private readonly Histogram<double> _duration;
    private readonly Counter<long> _failures;

    public OpenTelemetryExecutorMiddleware(ActivitySource? source = null, Meter? meter = null)
    {
        _source = source ?? Telemetry.ActivitySource;
        Meter m = meter ?? Telemetry.Meter;
        _duration = m.CreateHistogram<double>("workflow.executor.duration", "ms", "Executor invocation duration");
        _failures = m.CreateCounter<long>("workflow.executor.failures", "{failure}", "Executor invocation failures");
    }

    public int Order => -1000;

    public async ValueTask InvokeAsync(
        ExecutorInvocationContext context, ExecutorDelegate next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        using Activity? activity = _source.StartActivity(
            $"executor {context.Descriptor.ExecutorId}", ActivityKind.Internal);

        activity?.SetTag("workflow.instance_id", context.InstanceId);
        activity?.SetTag("workflow.name", context.Descriptor.WorkflowName);
        activity?.SetTag("workflow.version", context.Descriptor.WorkflowVersion);
        activity?.SetTag("workflow.attempt", context.Attempt);
        activity?.SetTag("workflow.superstep", context.Superstep);
        activity?.SetTag("executor.id", context.Descriptor.ExecutorId);
        activity?.SetTag("executor.type", context.Descriptor.ExecutorType.Name);
        activity?.SetTag("executor.mode", context.Descriptor.Mode.ToString());

        long start = Stopwatch.GetTimestamp();
        try
        {
            await next(context, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

            var executorTag = new KeyValuePair<string, object?>("executor.id", context.Descriptor.ExecutorId);
            var workflowTag = new KeyValuePair<string, object?>("workflow.name", context.Descriptor.WorkflowName);

            _duration.Record(elapsed, executorTag, workflowTag);

            if (context.Exception is { } exception)
            {
                _failures.Add(1, executorTag, workflowTag,
                    new KeyValuePair<string, object?>("exception.type", exception.GetType().Name));
                activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
                activity?.AddException(exception);
            }
            else
            {
                activity?.SetStatus(ActivityStatusCode.Ok);
            }
        }
    }
}

public sealed class OpenTelemetryWorkflowMiddleware : IWorkflowMiddleware
{
    private readonly ActivitySource _source;
    private readonly Histogram<double> _duration;

    public OpenTelemetryWorkflowMiddleware(ActivitySource? source = null, Meter? meter = null)
    {
        _source = source ?? Telemetry.ActivitySource;
        _duration = (meter ?? Telemetry.Meter)
            .CreateHistogram<double>("workflow.instance.duration", "ms", "Workflow run duration");
    }

    public int Order => -1000;

    public async ValueTask InvokeAsync(
        WorkflowInvocationContext context, WorkflowDelegate next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        using Activity? activity = _source.StartActivity($"workflow {context.WorkflowName}", ActivityKind.Internal);
        activity?.SetTag("workflow.instance_id", context.InstanceId);
        activity?.SetTag("workflow.name", context.WorkflowName);
        activity?.SetTag("workflow.version", context.WorkflowVersion);
        activity?.SetTag("workflow.attempt", context.Attempt);
        activity?.SetTag("workflow.tenant_id", context.TenantId);

        long start = Stopwatch.GetTimestamp();
        try
        {
            await next(context, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _duration.Record(Stopwatch.GetElapsedTime(start).TotalMilliseconds,
                new KeyValuePair<string, object?>("workflow.name", context.WorkflowName));

            if (context.Exception is { } exception)
            {
                activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
                activity?.AddException(exception);
            }
        }
    }
}

/// <summary>Structured request/response logging for outbound executor calls.</summary>
public sealed class RequestResponseLoggingMiddleware : IExecutorMiddleware
{
    private readonly ILogger _logger;
    private readonly IRedactionPolicy _redaction;
    private readonly LoggingOptions _options;
    private readonly Func<double> _sampler;

    public RequestResponseLoggingMiddleware(
        ILogger<RequestResponseLoggingMiddleware> logger,
        IRedactionPolicy? redaction = null,
        LoggingOptions? options = null,
        Func<double>? sampler = null)
    {
        _logger = logger;
        _redaction = redaction ?? RedactionPolicy.Default;
        _options = options ?? new LoggingOptions();
        _sampler = sampler ?? (() => Random.Shared.NextDouble());
    }

    public int Order => -900;

    public async ValueTask InvokeAsync(
        ExecutorInvocationContext context, ExecutorDelegate next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        await next(context, cancellationToken).ConfigureAwait(false);

        if (!context.Items.TryGetValue(MiddlewareContextKeys.OutboundCall, out object? raw) ||
            raw is not IOutboundCallHandle call)
        {
            return;
        }

        // Errors are always captured; successes are sampled to bound log volume.
        bool sampled = context.Exception is not null || _sampler() < _options.BodySampleRate;

        _logger.LogInformation(
            "Outbound {Method} {Url} -> {StatusCode} in {ElapsedMs}ms (executor {ExecutorId}, instance {InstanceId}) {Body}",
            call.Request.Method.Method,
            call.Request.RequestUri is null ? "(none)" : _redaction.RedactUrl(call.Request.RequestUri),
            call.Response is null ? "(no response)" : ((int)call.Response.StatusCode).ToString(),
            context.Elapsed.TotalMilliseconds,
            context.Descriptor.ExecutorId,
            context.InstanceId,
            sampled ? await ReadBodyAsync(call, _options.MaxBodyBytes, cancellationToken).ConfigureAwait(false) : "(not sampled)");
    }

    private async Task<string> ReadBodyAsync(IOutboundCallHandle call, int maxBytes, CancellationToken cancellationToken)
    {
        if (call.Response?.Content is null)
        {
            return "(no body)";
        }

        try
        {
            string body = await call.Response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (body.Length > maxBytes)
            {
                body = body[..maxBytes];
            }
            return _redaction.RedactBody(body);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or HttpRequestException)
        {
            // A consumed or disposed body must not turn logging into an execution failure.
            return "(body unavailable)";
        }
    }
}

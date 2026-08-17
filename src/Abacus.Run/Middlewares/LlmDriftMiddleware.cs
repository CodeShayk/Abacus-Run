using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Abacus.Run.Abstractions;
using Abacus.Run.Abstractions.Middleware;
using Abacus.Run.Core;
using Abacus.Run.Executors;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Abacus.Run.Middlewares;

public readonly record struct DriftKey(string WorkflowName, string ExecutorId, string ModelId, string? PromptVersion);

public sealed record DriftSample
{
    public required double LatencyMs { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public bool IsRefusal { get; init; }
    public bool SchemaFailed { get; init; }
    public string? FinishReason { get; init; }

    /// <summary>
    /// Null when the model has no configured price. Unpriced samples are excluded from the cost
    /// baseline rather than counted as zero, so an unpriced period cannot make a later rise look
    /// smaller than it is.
    /// </summary>
    public decimal? CostUsd { get; init; }

    public required DateTimeOffset At { get; init; }
}

public sealed record DriftBaseline
{
    public required DriftKey Key { get; init; }
    public required int SampleCount { get; init; }
    public required double LatencyMean { get; init; }
    public required double LatencyStdDev { get; init; }
    public required double OutputTokenMean { get; init; }
    public required double OutputTokenStdDev { get; init; }
    public required double RefusalRate { get; init; }
    public required double SchemaFailureRate { get; init; }
    public string? ModelId { get; init; }

    /// <summary>
    /// Null until enough priced samples exist. Cost is the signal that catches what token counts
    /// miss — a provider silently routing to a pricier model, or a prompt that has quietly grown.
    /// </summary>
    public double? CostMean { get; init; }

    public double? CostStdDev { get; init; }
}

public sealed record DriftBreach(string Signal, double Baseline, double Observed, double Deviation);

public interface IDriftBaselineStore
{
    ValueTask<DriftBaseline?> GetAsync(DriftKey key, CancellationToken cancellationToken);

    ValueTask AddSampleAsync(DriftKey key, DriftSample sample, CancellationToken cancellationToken);
}

public interface IDriftAlertSink
{
    ValueTask RaiseAsync(DriftKey key, DriftBreach breach, CancellationToken cancellationToken);

    ValueTask ModelChangedAsync(DriftKey key, string? previousModel, string? currentModel, CancellationToken cancellationToken);
}

/// <summary>
/// Records LLM quality signals and compares them to a rolling baseline.
/// </summary>
/// <remarks>
/// This middleware never fails an instance. A monitoring defect must not become an execution defect,
/// so every fault inside it is swallowed and logged.
/// </remarks>
public sealed class LlmDriftMiddleware : IExecutorMiddleware
{
    private readonly IDriftBaselineStore _baselines;
    private readonly IDriftAlertSink _alerts;
    private readonly DriftOptions _options;
    private readonly ILogger _logger;
    private readonly TimeProvider _clock;

    private readonly Histogram<double> _latency;
    private readonly Histogram<long> _outputTokens;
    private readonly Counter<long> _refusals;
    private readonly Counter<long> _schemaFailures;
    private readonly Counter<long> _driftDetected;

    public LlmDriftMiddleware(
        IDriftBaselineStore baselines,
        IDriftAlertSink alerts,
        DriftOptions? options = null,
        ILogger<LlmDriftMiddleware>? logger = null,
        Meter? meter = null,
        TimeProvider? clock = null)
    {
        _baselines = baselines;
        _alerts = alerts;
        _options = options ?? new DriftOptions();
        _logger = logger ?? NullLogger<LlmDriftMiddleware>.Instance;
        _clock = clock ?? TimeProvider.System;

        Meter m = meter ?? Telemetry.Meter;
        _latency = m.CreateHistogram<double>("gen_ai.client.operation.duration", "ms", "LLM call duration");
        _outputTokens = m.CreateHistogram<long>("gen_ai.usage.output_tokens", "{token}", "LLM output tokens");
        _refusals = m.CreateCounter<long>("gen_ai.response.refusals", "{refusal}", "LLM refusals");
        _schemaFailures = m.CreateCounter<long>("gen_ai.structured_output.failures", "{failure}", "Schema failures");
        _driftDetected = m.CreateCounter<long>("workflow.llm.drift_detected", "{alert}", "Drift alerts raised");
    }

    public int Order => -800;

    public bool AppliesTo(ExecutorDescriptor descriptor)
        => descriptor is not null && descriptor.ExecutorType == typeof(LlmExecutor);

    public async ValueTask InvokeAsync(
        ExecutorInvocationContext context, ExecutorDelegate next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        long start = Stopwatch.GetTimestamp();
        await next(context, cancellationToken).ConfigureAwait(false);
        TimeSpan elapsed = Stopwatch.GetElapsedTime(start);

        try
        {
            await RecordAsync(context, elapsed, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Deliberate: drift monitoring is observability, not control flow.
            _logger.LogWarning(ex, "LLM drift monitoring failed for executor {ExecutorId}; instance unaffected.",
                context.Descriptor.ExecutorId);
        }
    }

    private async Task RecordAsync(ExecutorInvocationContext context, TimeSpan elapsed, CancellationToken cancellationToken)
    {
        var result = context.Output as LlmResult;

        string modelId = result?.ModelId
            ?? (context.Descriptor.Metadata.TryGetValue("llm.model", out object? configured)
                ? configured?.ToString() ?? "unknown"
                : "unknown");

        string? promptVersion = context.Descriptor.Metadata
            .TryGetValue(MiddlewareContextKeys.PromptVersion, out object? pv)
                ? pv?.ToString()
                : null;

        var key = new DriftKey(context.Descriptor.WorkflowName, context.Descriptor.ExecutorId, modelId, promptVersion);

        var sample = new DriftSample
        {
            LatencyMs = elapsed.TotalMilliseconds,
            InputTokens = result?.InputTokens ?? 0,
            OutputTokens = result?.OutputTokens ?? 0,
            IsRefusal = RefusalDetector.IsRefusal(result?.Text),
            SchemaFailed = context.Exception is StructuredOutputException,
            FinishReason = result?.FinishReason,
            CostUsd = result?.CostUsd,
            At = _clock.GetUtcNow()
        };

        RecordMetrics(key, sample);

        DriftBaseline? baseline = await _baselines.GetAsync(key, cancellationToken).ConfigureAwait(false);
        await _baselines.AddSampleAsync(key, sample, cancellationToken).ConfigureAwait(false);

        if (baseline is null || baseline.SampleCount < _options.MinSamples)
        {
            return;   // still warming up
        }

        if (baseline.ModelId is { Length: > 0 } previous &&
            !string.Equals(previous, modelId, StringComparison.OrdinalIgnoreCase))
        {
            await _alerts.ModelChangedAsync(key, previous, modelId, cancellationToken).ConfigureAwait(false);
        }

        foreach (DriftBreach breach in DriftDetector.Evaluate(baseline, sample, _options.SigmaThreshold))
        {
            _driftDetected.Add(1,
                new KeyValuePair<string, object?>("workflow.name", key.WorkflowName),
                new KeyValuePair<string, object?>("executor.id", key.ExecutorId),
                new KeyValuePair<string, object?>("drift.signal", breach.Signal));

            await _alerts.RaiseAsync(key, breach, cancellationToken).ConfigureAwait(false);
        }
    }

    private void RecordMetrics(DriftKey key, DriftSample sample)
    {
        var modelTag = new KeyValuePair<string, object?>("gen_ai.request.model", key.ModelId);
        var executorTag = new KeyValuePair<string, object?>("executor.id", key.ExecutorId);

        _latency.Record(sample.LatencyMs, modelTag, executorTag);
        _outputTokens.Record(sample.OutputTokens, modelTag, executorTag);

        if (sample.IsRefusal)
        {
            _refusals.Add(1, modelTag, executorTag);
        }
        if (sample.SchemaFailed)
        {
            _schemaFailures.Add(1, modelTag, executorTag);
        }
    }
}

public static class DriftDetector
{
    /// <summary>Signals whose observation exceeds the sigma threshold against the baseline.</summary>
    public static IReadOnlyList<DriftBreach> Evaluate(DriftBaseline baseline, DriftSample sample, double sigma)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(sample);

        var breaches = new List<DriftBreach>();

        AddIfBreached(breaches, "latency", baseline.LatencyMean, baseline.LatencyStdDev, sample.LatencyMs, sigma);
        AddIfBreached(breaches, "output_tokens", baseline.OutputTokenMean, baseline.OutputTokenStdDev, sample.OutputTokens, sigma);

        // Only when both sides are priced. Comparing a priced call against an unpriced baseline, or
        // the reverse, would report a breach that says nothing about the model's behaviour.
        if (sample.CostUsd is { } cost && baseline.CostMean is { } costMean && baseline.CostStdDev is { } costStdDev)
        {
            AddIfBreached(breaches, "cost", costMean, costStdDev, (double)cost, sigma);
        }

        // Rates are compared as absolute deltas: a stddev over a Bernoulli rate is not meaningful
        // at the sample sizes involved.
        if (sample.IsRefusal && baseline.RefusalRate < 0.05)
        {
            breaches.Add(new DriftBreach("refusal_rate", baseline.RefusalRate, 1.0, 1.0 - baseline.RefusalRate));
        }
        if (sample.SchemaFailed && baseline.SchemaFailureRate < 0.05)
        {
            breaches.Add(new DriftBreach("schema_failure_rate", baseline.SchemaFailureRate, 1.0, 1.0 - baseline.SchemaFailureRate));
        }

        return breaches;
    }

    private static void AddIfBreached(
        List<DriftBreach> breaches, string signal, double mean, double stdDev, double observed, double sigma)
    {
        if (stdDev <= double.Epsilon)
        {
            return;   // a degenerate baseline would flag every sample
        }

        double deviation = Math.Abs(observed - mean) / stdDev;
        if (deviation > sigma)
        {
            breaches.Add(new DriftBreach(signal, mean, observed, deviation));
        }
    }
}

public static class RefusalDetector
{
    private static readonly string[] Markers =
    [
        "i can't help",
        "i cannot help",
        "i can't assist",
        "i cannot assist",
        "i'm unable to",
        "i am unable to",
        "i won't be able to",
        "as an ai language model"
    ];

    public static bool IsRefusal(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;   // an empty completion is a failure to answer
        }

        string lower = text.ToLowerInvariant();
        return Markers.Any(marker => lower.Contains(marker, StringComparison.Ordinal));
    }
}

/// <summary>Rolling in-memory baseline over a bounded window.</summary>
public sealed class InMemoryDriftBaselineStore : IDriftBaselineStore
{
    private readonly ConcurrentDictionary<DriftKey, List<DriftSample>> _samples = new();
    private readonly int _windowSize;
    private readonly object _sync = new();

    public InMemoryDriftBaselineStore(int windowSize = 1000) => _windowSize = windowSize;

    public ValueTask<DriftBaseline?> GetAsync(DriftKey key, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (!_samples.TryGetValue(key, out List<DriftSample>? window) || window.Count == 0)
            {
                return ValueTask.FromResult<DriftBaseline?>(null);
            }

            double[] latencies = window.Select(s => s.LatencyMs).ToArray();
            double[] outputs = window.Select(s => (double)s.OutputTokens).ToArray();

            // Unpriced samples are excluded rather than counted as zero: an unpriced stretch would
            // otherwise drag the mean down and hide a genuine cost rise that followed it.
            double[] costs = window
                .Where(s => s.CostUsd is not null)
                .Select(s => (double)s.CostUsd!.Value)
                .ToArray();

            return ValueTask.FromResult<DriftBaseline?>(new DriftBaseline
            {
                CostMean = costs.Length > 0 ? Mean(costs) : null,
                CostStdDev = costs.Length > 0 ? StdDev(costs) : null,
                Key = key,
                SampleCount = window.Count,
                LatencyMean = Mean(latencies),
                LatencyStdDev = StdDev(latencies),
                OutputTokenMean = Mean(outputs),
                OutputTokenStdDev = StdDev(outputs),
                RefusalRate = window.Count(s => s.IsRefusal) / (double)window.Count,
                SchemaFailureRate = window.Count(s => s.SchemaFailed) / (double)window.Count,
                ModelId = key.ModelId
            });
        }
    }

    public ValueTask AddSampleAsync(DriftKey key, DriftSample sample, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            List<DriftSample> window = _samples.GetOrAdd(key, _ => []);
            window.Add(sample);
            if (window.Count > _windowSize)
            {
                window.RemoveRange(0, window.Count - _windowSize);
            }
        }
        return ValueTask.CompletedTask;
    }

    internal static double Mean(IReadOnlyCollection<double> values)
        => values.Count == 0 ? 0 : values.Sum() / values.Count;

    internal static double StdDev(IReadOnlyCollection<double> values)
    {
        if (values.Count < 2)
        {
            return 0;
        }

        double mean = Mean(values);
        double sumSquares = values.Sum(v => (v - mean) * (v - mean));
        return Math.Sqrt(sumSquares / (values.Count - 1));
    }
}

public sealed class CollectingDriftAlertSink : IDriftAlertSink
{
    private readonly List<(DriftKey Key, DriftBreach Breach)> _alerts = [];
    private readonly List<(DriftKey Key, string? Previous, string? Current)> _modelChanges = [];
    private readonly object _sync = new();

    public IReadOnlyList<(DriftKey Key, DriftBreach Breach)> Alerts
    {
        get { lock (_sync) { return [.. _alerts]; } }
    }

    public IReadOnlyList<(DriftKey Key, string? Previous, string? Current)> ModelChanges
    {
        get { lock (_sync) { return [.. _modelChanges]; } }
    }

    public ValueTask RaiseAsync(DriftKey key, DriftBreach breach, CancellationToken cancellationToken)
    {
        lock (_sync) { _alerts.Add((key, breach)); }
        return ValueTask.CompletedTask;
    }

    public ValueTask ModelChangedAsync(DriftKey key, string? previousModel, string? currentModel, CancellationToken cancellationToken)
    {
        lock (_sync) { _modelChanges.Add((key, previousModel, currentModel)); }
        return ValueTask.CompletedTask;
    }
}

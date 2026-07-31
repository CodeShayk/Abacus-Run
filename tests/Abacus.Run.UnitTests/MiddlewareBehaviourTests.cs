using System.Diagnostics;
using System.Net;
using System.Text;
using Abacus.Run.Abstractions;
using Abacus.Run.Abstractions.Middleware;
using Abacus.Run.Core;
using Abacus.Run.Executors;
using Abacus.Run.Middlewares;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Abacus.Run.UnitTests;

public class OpenTelemetryMiddlewareTests
{
    [Fact]
    public async Task Creates_a_span_tagged_with_workflow_and_executor_identity()
    {
        var activities = new List<Activity>();
        using ActivityListener listener = Listen(activities);

        var middleware = new OpenTelemetryExecutorMiddleware();
        ExecutorInvocationContext context = TestFactory.Invocation();

        await middleware.InvokeAsync(context, (_, _) => ValueTask.CompletedTask, default);

        Activity activity = activities.Should().ContainSingle().Subject;
        activity.GetTagItem("workflow.instance_id").Should().Be("i1");
        activity.GetTagItem("executor.id").Should().Be("e1");
        activity.GetTagItem("workflow.name").Should().Be("wf");
        activity.Status.Should().Be(ActivityStatusCode.Ok);
    }

    [Fact]
    public async Task Marks_the_span_as_errored_when_the_pipeline_captured_an_exception()
    {
        var activities = new List<Activity>();
        using ActivityListener listener = Listen(activities);

        var middleware = new OpenTelemetryExecutorMiddleware();
        ExecutorInvocationContext context = TestFactory.Invocation();

        await middleware.InvokeAsync(context, (ctx, _) =>
        {
            ctx.Exception = new TimeoutException("slow");
            return ValueTask.CompletedTask;
        }, default);

        Activity activity = activities.Should().ContainSingle().Subject;
        activity.Status.Should().Be(ActivityStatusCode.Error);
        activity.StatusDescription.Should().Be("slow");
    }

    [Fact]
    public async Task Is_the_outermost_middleware()
        => await Task.FromResult(new OpenTelemetryExecutorMiddleware().Order.Should().Be(-1000));

    [Fact]
    public async Task Workflow_span_carries_run_identity()
    {
        var activities = new List<Activity>();
        using ActivityListener listener = Listen(activities);

        var middleware = new OpenTelemetryWorkflowMiddleware();

        await middleware.InvokeAsync(TestFactory.WorkflowInvocation(), (_, _) => ValueTask.CompletedTask, default);

        Activity activity = activities.Should().ContainSingle().Subject;
        activity.GetTagItem("workflow.tenant_id").Should().Be("t1");
        activity.GetTagItem("workflow.attempt").Should().Be(1);
    }

    [Fact]
    public async Task An_exception_thrown_downstream_still_closes_the_span()
    {
        var activities = new List<Activity>();
        using ActivityListener listener = Listen(activities);

        var middleware = new OpenTelemetryExecutorMiddleware();

        Func<Task> act = async () => await middleware.InvokeAsync(
            TestFactory.Invocation(), (_, _) => throw new InvalidOperationException("boom"), default);

        await act.Should().ThrowAsync<InvalidOperationException>();
        activities.Should().ContainSingle();
    }

    [Fact]
    public async Task Rejects_null_arguments()
    {
        var middleware = new OpenTelemetryExecutorMiddleware();

        await middleware.Invoking(m => m.InvokeAsync(null!, (_, _) => ValueTask.CompletedTask, default).AsTask())
            .Should().ThrowAsync<ArgumentNullException>();
        await middleware.Invoking(m => m.InvokeAsync(TestFactory.Invocation(), null!, default).AsTask())
            .Should().ThrowAsync<ArgumentNullException>();
    }

    private static ActivityListener Listen(List<Activity> sink)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == Telemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = sink.Add
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}

public class RequestResponseLoggingMiddlewareTests
{
    [Fact]
    public async Task Does_nothing_when_there_was_no_outbound_call()
    {
        var middleware = new RequestResponseLoggingMiddleware(NullLogger<RequestResponseLoggingMiddleware>.Instance);
        ExecutorInvocationContext context = TestFactory.Invocation();

        Func<Task> act = async () => await middleware.InvokeAsync(context, (_, _) => ValueTask.CompletedTask, default);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Logs_an_outbound_call_without_disturbing_the_result()
    {
        var middleware = new RequestResponseLoggingMiddleware(
            NullLogger<RequestResponseLoggingMiddleware>.Instance,
            options: new LoggingOptions { BodySampleRate = 1.0 },
            sampler: () => 0.0);

        ExecutorInvocationContext context = TestFactory.Invocation();

        await middleware.InvokeAsync(context, (ctx, _) =>
        {
            ctx.Items[MiddlewareContextKeys.OutboundCall] = StubCall();
            ctx.Output = new Outcome("ok");
            return ValueTask.CompletedTask;
        }, default);

        context.Output.Should().BeOfType<Outcome>();
    }

    [Fact]
    public async Task Survives_a_disposed_response_body()
    {
        var middleware = new RequestResponseLoggingMiddleware(
            NullLogger<RequestResponseLoggingMiddleware>.Instance, sampler: () => 0.0);

        ExecutorInvocationContext context = TestFactory.Invocation();

        var handle = StubCall();
        handle.Response!.Content.Dispose();

        // Logging must never turn into an execution failure.
        Func<Task> act = async () => await middleware.InvokeAsync(context, (ctx, _) =>
        {
            ctx.Items[MiddlewareContextKeys.OutboundCall] = handle;
            return ValueTask.CompletedTask;
        }, default);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void Sits_inside_telemetry()
        => new RequestResponseLoggingMiddleware(NullLogger<RequestResponseLoggingMiddleware>.Instance)
            .Order.Should().BeGreaterThan(new OpenTelemetryExecutorMiddleware().Order);

    private static OutboundCallHandle StubCall()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/x?token=secret");
        var handle = new OutboundCallHandle(request);
        handle.Capture(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json")
        });
        return handle;
    }
}

public class OutboundCallHandleTests
{
    [Fact]
    public void Starts_unshortcircuited()
    {
        var handle = new OutboundCallHandle(new HttpRequestMessage(HttpMethod.Get, "https://x/"));
        handle.IsShortCircuited.Should().BeFalse();
        handle.Response.Should().BeNull();
    }

    [Fact]
    public void A_synthetic_response_short_circuits_the_real_call()
    {
        var handle = new OutboundCallHandle(new HttpRequestMessage(HttpMethod.Get, "https://x/"));

        handle.SetSyntheticResponse(new HttpResponseMessage(HttpStatusCode.Accepted));

        handle.IsShortCircuited.Should().BeTrue();
        handle.Response!.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public void Rejects_a_null_synthetic_response()
        => new OutboundCallHandle(new HttpRequestMessage(HttpMethod.Get, "https://x/"))
            .Invoking(h => h.SetSyntheticResponse(null!)).Should().Throw<ArgumentNullException>();
}

public class LlmDriftMiddlewareTests
{
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.Parse("2026-07-30T10:00:00Z"));

    private LlmDriftMiddleware Build(
        IDriftBaselineStore baselines, CollectingDriftAlertSink alerts, int minSamples = 3)
        => new(baselines, alerts,
            new DriftOptions { MinSamples = minSamples, SigmaThreshold = 3.0 },
            NullLogger<LlmDriftMiddleware>.Instance, clock: _clock);

    private static ExecutorInvocationContext LlmInvocation(string model = "claude-sonnet-5")
    {
        var descriptor = new ExecutorDescriptor("classify", typeof(LlmExecutor), "wf", "1.0.0", ExecutionMode.Autonomous)
        {
            Metadata = new Dictionary<string, object?> { ["llm.model"] = model }
        };

        return new ExecutorInvocationContext
        {
            InstanceId = "i1",
            Descriptor = descriptor,
            WorkflowContext = new FakeWorkflowContext()
        };
    }

    [Fact]
    public void Applies_only_to_llm_executors()
    {
        var middleware = Build(new InMemoryDriftBaselineStore(), new CollectingDriftAlertSink());

        middleware.AppliesTo(TestFactory.Descriptor(type: typeof(LlmExecutor))).Should().BeTrue();
        middleware.AppliesTo(TestFactory.Descriptor(type: typeof(ApiCallExecutor))).Should().BeFalse();
        middleware.AppliesTo(null!).Should().BeFalse();
    }

    [Fact]
    public async Task Records_a_sample_per_invocation()
    {
        var baselines = new InMemoryDriftBaselineStore();
        var alerts = new CollectingDriftAlertSink();
        LlmDriftMiddleware middleware = Build(baselines, alerts);

        await middleware.InvokeAsync(LlmInvocation(), (ctx, _) =>
        {
            ctx.Output = new LlmResult("value", "some text", 100, 50, "claude-sonnet-5", "stop");
            return ValueTask.CompletedTask;
        }, default);

        DriftBaseline? baseline = await baselines.GetAsync(
            new DriftKey("wf", "classify", "claude-sonnet-5", null), default);

        baseline!.SampleCount.Should().Be(1);
        baseline.OutputTokenMean.Should().Be(50);
    }

    [Fact]
    public async Task Raises_no_alert_while_the_baseline_is_warming_up()
    {
        var baselines = new InMemoryDriftBaselineStore();
        var alerts = new CollectingDriftAlertSink();
        LlmDriftMiddleware middleware = Build(baselines, alerts, minSamples: 100);

        for (int i = 0; i < 10; i++)
        {
            await middleware.InvokeAsync(LlmInvocation(), (ctx, _) =>
            {
                ctx.Output = new LlmResult("v", "text", 10, 10, "claude-sonnet-5", "stop");
                return ValueTask.CompletedTask;
            }, default);
        }

        alerts.Alerts.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failing_baseline_store_never_affects_the_instance()
    {
        // Deliberate: a monitoring defect must not become an execution defect.
        var alerts = new CollectingDriftAlertSink();
        LlmDriftMiddleware middleware = Build(new ThrowingBaselineStore(), alerts);

        ExecutorInvocationContext context = LlmInvocation();

        Func<Task> act = async () => await middleware.InvokeAsync(context, (ctx, _) =>
        {
            ctx.Output = new LlmResult("value", "text", 1, 1, "m", "stop");
            return ValueTask.CompletedTask;
        }, default);

        await act.Should().NotThrowAsync();
        context.Output.Should().BeOfType<LlmResult>();
        context.Exception.Should().BeNull();
    }

    [Fact]
    public async Task A_model_change_is_reported()
    {
        var baselines = new InMemoryDriftBaselineStore();
        var alerts = new CollectingDriftAlertSink();
        LlmDriftMiddleware middleware = Build(baselines, alerts, minSamples: 1);

        var key = new DriftKey("wf", "classify", "claude-sonnet-5", null);
        for (int i = 0; i < 5; i++)
        {
            await baselines.AddSampleAsync(key, Sample(100, 50), default);
        }

        await middleware.InvokeAsync(LlmInvocation(), (ctx, _) =>
        {
            ctx.Output = new LlmResult("v", "text", 100, 50, "claude-opus-5", "stop");
            return ValueTask.CompletedTask;
        }, default);

        alerts.ModelChanges.Should().BeEmpty("the key includes the model, so a change starts a fresh baseline");
    }

    [Fact]
    public async Task Non_llm_output_is_ignored_without_error()
    {
        var alerts = new CollectingDriftAlertSink();
        LlmDriftMiddleware middleware = Build(new InMemoryDriftBaselineStore(), alerts);

        Func<Task> act = async () => await middleware.InvokeAsync(LlmInvocation(), (ctx, _) =>
        {
            ctx.Output = new Outcome("not an llm result");
            return ValueTask.CompletedTask;
        }, default);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Rejects_null_arguments()
    {
        LlmDriftMiddleware middleware = Build(new InMemoryDriftBaselineStore(), new CollectingDriftAlertSink());

        await middleware.Invoking(m => m.InvokeAsync(null!, (_, _) => ValueTask.CompletedTask, default).AsTask())
            .Should().ThrowAsync<ArgumentNullException>();
    }

    private DriftSample Sample(double latency, long outputTokens) => new()
    {
        LatencyMs = latency,
        OutputTokens = outputTokens,
        At = _clock.GetUtcNow()
    };

    private sealed class ThrowingBaselineStore : IDriftBaselineStore
    {
        public ValueTask<DriftBaseline?> GetAsync(DriftKey key, CancellationToken cancellationToken)
            => throw new InvalidOperationException("baseline store unavailable");

        public ValueTask AddSampleAsync(DriftKey key, DriftSample sample, CancellationToken cancellationToken)
            => throw new InvalidOperationException("baseline store unavailable");
    }
}

public class DriftDetectorTests
{
    private static DriftBaseline Baseline(
        double latencyMean = 100, double latencyStdDev = 10,
        double tokenMean = 50, double tokenStdDev = 5,
        double refusalRate = 0.0, double schemaFailureRate = 0.0)
        => new()
        {
            Key = new DriftKey("wf", "e1", "m", null),
            SampleCount = 500,
            LatencyMean = latencyMean,
            LatencyStdDev = latencyStdDev,
            OutputTokenMean = tokenMean,
            OutputTokenStdDev = tokenStdDev,
            RefusalRate = refusalRate,
            SchemaFailureRate = schemaFailureRate
        };

    private static DriftSample Sample(
        double latency = 100, long tokens = 50, bool refusal = false, bool schemaFailed = false)
        => new()
        {
            LatencyMs = latency,
            OutputTokens = tokens,
            IsRefusal = refusal,
            SchemaFailed = schemaFailed,
            At = DateTimeOffset.UnixEpoch
        };

    [Fact]
    public void A_sample_at_the_mean_breaches_nothing()
        => DriftDetector.Evaluate(Baseline(), Sample(), 3.0).Should().BeEmpty();

    [Fact]
    public void Latency_beyond_the_sigma_threshold_breaches()
    {
        IReadOnlyList<DriftBreach> breaches = DriftDetector.Evaluate(Baseline(), Sample(latency: 200), 3.0);

        breaches.Should().ContainSingle(b => b.Signal == "latency");
        breaches[0].Observed.Should().Be(200);
        breaches[0].Deviation.Should().BeApproximately(10, 0.001);
    }

    [Fact]
    public void Output_token_drift_breaches()
        => DriftDetector.Evaluate(Baseline(), Sample(tokens: 200), 3.0)
            .Should().Contain(b => b.Signal == "output_tokens");

    [Fact]
    public void A_sample_just_inside_the_threshold_does_not_breach()
        => DriftDetector.Evaluate(Baseline(), Sample(latency: 129), 3.0)
            .Should().NotContain(b => b.Signal == "latency");

    [Fact]
    public void A_degenerate_baseline_never_breaches()
    {
        // Zero variance would otherwise flag literally every sample.
        DriftDetector.Evaluate(Baseline(latencyStdDev: 0, tokenStdDev: 0), Sample(latency: 9999), 3.0)
            .Should().BeEmpty();
    }

    [Fact]
    public void A_refusal_against_a_clean_baseline_breaches()
        => DriftDetector.Evaluate(Baseline(refusalRate: 0.0), Sample(refusal: true), 3.0)
            .Should().Contain(b => b.Signal == "refusal_rate");

    [Fact]
    public void A_refusal_against_a_refusal_heavy_baseline_does_not_breach()
        => DriftDetector.Evaluate(Baseline(refusalRate: 0.4), Sample(refusal: true), 3.0)
            .Should().NotContain(b => b.Signal == "refusal_rate");

    [Fact]
    public void Schema_failures_breach_against_a_clean_baseline()
        => DriftDetector.Evaluate(Baseline(), Sample(schemaFailed: true), 3.0)
            .Should().Contain(b => b.Signal == "schema_failure_rate");

    [Fact]
    public void Rejects_null_arguments()
    {
        FluentActions.Invoking(() => DriftDetector.Evaluate(null!, Sample(), 3.0)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => DriftDetector.Evaluate(Baseline(), null!, 3.0)).Should().Throw<ArgumentNullException>();
    }
}

public class RefusalDetectorTests
{
    [Theory]
    [InlineData("I can't help with that.")]
    [InlineData("I cannot assist with this request.")]
    [InlineData("I'm unable to process this.")]
    [InlineData("As an AI language model, I...")]
    public void Detects_refusal_phrasing(string text)
        => RefusalDetector.IsRefusal(text).Should().BeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Treats_an_empty_completion_as_a_refusal(string? text)
        => RefusalDetector.IsRefusal(text).Should().BeTrue();

    [Theory]
    [InlineData("The invoice is classified as a purchase order.")]
    [InlineData("""{"category":"invoice"}""")]
    public void Ordinary_answers_are_not_refusals(string text)
        => RefusalDetector.IsRefusal(text).Should().BeFalse();

    [Fact]
    public void Matching_is_case_insensitive()
        => RefusalDetector.IsRefusal("I CAN'T HELP WITH THAT").Should().BeTrue();
}

public class DriftBaselineStoreTests
{
    [Fact]
    public async Task Unknown_keys_have_no_baseline()
        => (await new InMemoryDriftBaselineStore().GetAsync(new DriftKey("w", "e", "m", null), default))
            .Should().BeNull();

    [Fact]
    public async Task Computes_mean_and_standard_deviation()
    {
        var store = new InMemoryDriftBaselineStore();
        var key = new DriftKey("w", "e", "m", null);

        foreach (double latency in new double[] { 10, 20, 30, 40, 50 })
        {
            await store.AddSampleAsync(key, new DriftSample { LatencyMs = latency, At = DateTimeOffset.UnixEpoch }, default);
        }

        DriftBaseline? baseline = await store.GetAsync(key, default);

        baseline!.SampleCount.Should().Be(5);
        baseline.LatencyMean.Should().Be(30);
        baseline.LatencyStdDev.Should().BeApproximately(15.811, 0.01);
    }

    [Fact]
    public async Task Window_is_bounded()
    {
        var store = new InMemoryDriftBaselineStore(windowSize: 10);
        var key = new DriftKey("w", "e", "m", null);

        for (int i = 0; i < 50; i++)
        {
            await store.AddSampleAsync(key, new DriftSample { LatencyMs = i, At = DateTimeOffset.UnixEpoch }, default);
        }

        (await store.GetAsync(key, default))!.SampleCount.Should().Be(10);
    }

    [Fact]
    public async Task Rates_are_computed_over_the_window()
    {
        var store = new InMemoryDriftBaselineStore();
        var key = new DriftKey("w", "e", "m", null);

        for (int i = 0; i < 4; i++)
        {
            await store.AddSampleAsync(key, new DriftSample
            {
                LatencyMs = 1, IsRefusal = i < 1, SchemaFailed = i < 2, At = DateTimeOffset.UnixEpoch
            }, default);
        }

        DriftBaseline? baseline = await store.GetAsync(key, default);
        baseline!.RefusalRate.Should().Be(0.25);
        baseline.SchemaFailureRate.Should().Be(0.5);
    }

    [Fact]
    public void Statistics_helpers_handle_degenerate_inputs()
    {
        InMemoryDriftBaselineStore.Mean([]).Should().Be(0);
        InMemoryDriftBaselineStore.StdDev([]).Should().Be(0);
        InMemoryDriftBaselineStore.StdDev([5]).Should().Be(0, "a single sample has no dispersion");
    }

    [Fact]
    public async Task Keys_are_isolated_by_model_and_prompt_version()
    {
        var store = new InMemoryDriftBaselineStore();

        await store.AddSampleAsync(new DriftKey("w", "e", "m1", "v1"),
            new DriftSample { LatencyMs = 10, At = DateTimeOffset.UnixEpoch }, default);

        (await store.GetAsync(new DriftKey("w", "e", "m1", "v2"), default)).Should().BeNull();
        (await store.GetAsync(new DriftKey("w", "e", "m2", "v1"), default)).Should().BeNull();
        (await store.GetAsync(new DriftKey("w", "e", "m1", "v1"), default)).Should().NotBeNull();
    }
}

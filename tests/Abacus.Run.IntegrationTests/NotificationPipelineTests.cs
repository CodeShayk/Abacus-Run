using System.Runtime.CompilerServices;
using Abacus.Run.Abstractions;
using Abacus.Run.Api;
using Abacus.Run.Core;
using Abacus.Run.Executors;
using FluentAssertions;
using Microsoft.Agents.AI.Workflows;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Abacus.Run.IntegrationTests;

public sealed record ClassifyContext(string Text = "an invoice");

public sealed record ClassifyResult(string Text, long? OutputTokens);

/// <summary>Streams four chunks and reports usage, so delta and completion paths are both live.</summary>
public sealed class StreamingChatClient : IChatClient
{
    private static readonly string[] Chunks = ["The ", "inv", "oice ", "is valid"];

    public ChatClientMetadata Metadata { get; } = new("fake", new Uri("http://localhost"), "fake-model");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult(Respond());

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (string chunk in Chunks)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
            await Task.Yield();
        }

        yield return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new UsageContent(new UsageDetails { InputTokenCount = 40, OutputTokenCount = 8 })]
        };
    }

    private static ChatResponse Respond() => new(new ChatMessage(ChatRole.Assistant, string.Concat(Chunks)))
    {
        ModelId = "fake-model",
        Usage = new UsageDetails { InputTokenCount = 40, OutputTokenCount = 8 }
    };

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

public sealed class StreamingLlmWorkflow : IWorkflowDefinition<ClassifyContext, ClassifyResult>
{
    public string Name => "streaming-llm";
    public string Version => "1.0.0";

    public ValueTask<Workflow> BuildAsync(WorkflowBuildContext context, CancellationToken cancellationToken)
    {
        var pricing = context.Services!.GetService<IModelPricing>();

        ExecutorBinding classify = context.Node(new LlmExecutor(
            "classify",
            new LlmOptions
            {
                Model = "fake-model",
                UserTemplate = "{{ context.Text }}",
                StreamDeltas = true
            },
            _ => new StreamingChatClient(),
            pricing));

        ExecutorBinding finish = context.Node(new Finish("finish"));

        return new ValueTask<Workflow>(new WorkflowBuilder(classify)
            .AddEdge(classify, finish)
            .WithOutputFrom(finish)
            .WithName(Name)
            .Build());
    }

    private sealed class Finish(string id) : HostExecutor<LlmResult, ClassifyResult>(id)
    {
        protected override ValueTask<ClassifyResult> ExecuteCoreAsync(
            LlmResult input, IWorkflowContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(new ClassifyResult(input.Text ?? "", input.OutputTokens));
    }
}

/// <summary>Emits a workflow-defined notification, and declares that it does.</summary>
public sealed class NotifyingWorkflow : IWorkflowDefinition<ClassifyContext, ClassifyResult>, INotifyingWorkflow
{
    public string Name => "notifying";
    public string Version => "1.0.0";

    public NotificationPolicy Notifications { get; } = new() { Emits = ["documents.scanned"] };

    public ValueTask<Workflow> BuildAsync(WorkflowBuildContext context, CancellationToken cancellationToken)
    {
        ExecutorBinding scan = context.Node(new Scan("scan"));

        return new ValueTask<Workflow>(new WorkflowBuilder(scan)
            .WithOutputFrom(scan)
            .WithName(Name)
            .Build());
    }

    private sealed class Scan(string id) : HostExecutor<ClassifyContext, ClassifyResult>(id)
    {
        protected override async ValueTask<ClassifyResult> ExecuteCoreAsync(
            ClassifyContext input, IWorkflowContext context, CancellationToken cancellationToken)
        {
            if (Runtime.Notify is { } notify)
            {
                await notify.NotifyAsync("documents.scanned", new { count = 3 }, cancellationToken);
            }

            return new ClassifyResult(input.Text, null);
        }
    }
}

/// <summary>Same shape, but declares that it wants to be quiet.</summary>
public sealed class QuietWorkflow : IWorkflowDefinition<ClassifyContext, ClassifyResult>, INotifyingWorkflow
{
    public string Name => "quiet";
    public string Version => "1.0.0";

    public NotificationPolicy Notifications { get; } = new() { Level = NotificationLevel.Minimal };

    public ValueTask<Workflow> BuildAsync(WorkflowBuildContext context, CancellationToken cancellationToken)
    {
        ExecutorBinding a = context.Node(new Step("step-a"));
        ExecutorBinding b = context.Node(new Step2("step-b"));

        return new ValueTask<Workflow>(new WorkflowBuilder(a)
            .AddEdge(a, b)
            .WithOutputFrom(b)
            .WithName(Name)
            .Build());
    }

    private sealed class Step(string id) : HostExecutor<ClassifyContext, ClassifyContext>(id)
    {
        protected override ValueTask<ClassifyContext> ExecuteCoreAsync(
            ClassifyContext input, IWorkflowContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(input);
    }

    private sealed class Step2(string id) : HostExecutor<ClassifyContext, ClassifyResult>(id)
    {
        protected override ValueTask<ClassifyResult> ExecuteCoreAsync(
            ClassifyContext input, IWorkflowContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(new ClassifyResult(input.Text, null));
    }
}

/// <summary>Opts out of live streaming; its events are read afterwards rather than followed.</summary>
public sealed class LogOnlyWorkflow : IWorkflowDefinition<ClassifyContext, ClassifyResult>, INotifyingWorkflow
{
    public string Name => "log-only";
    public string Version => "1.0.0";

    public NotificationPolicy Notifications { get; } = new() { StreamEvents = false };

    public ValueTask<Workflow> BuildAsync(WorkflowBuildContext context, CancellationToken cancellationToken)
    {
        ExecutorBinding step = context.Node(new Step("batch-step"));

        return new ValueTask<Workflow>(new WorkflowBuilder(step)
            .WithOutputFrom(step)
            .WithName(Name)
            .Build());
    }

    private sealed class Step(string id) : HostExecutor<ClassifyContext, ClassifyResult>(id)
    {
        protected override async ValueTask<ClassifyResult> ExecuteCoreAsync(
            ClassifyContext input, IWorkflowContext context, CancellationToken cancellationToken)
        {
            if (Runtime.Notify is { } notify)
            {
                await notify.NotifyAsync("batch.processed", new { rows = 500 }, cancellationToken);
            }

            return new ClassifyResult(input.Text, null);
        }
    }
}

public sealed class NotificationFixture : WebApplicationFactory<Program>
{
    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IWorkflowDefinition>(new StreamingLlmWorkflow());
            services.AddSingleton<IWorkflowDefinition>(new NotifyingWorkflow());
            services.AddSingleton<IWorkflowDefinition>(new QuietWorkflow());
            services.AddSingleton<IWorkflowDefinition>(new LogOnlyWorkflow());
        });

        return base.CreateHost(builder);
    }

    private readonly string _auditDatabasePath =
        Path.Combine(Path.GetTempPath(), $"abacus-notify-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Abacus:AuditRecords:ConnectionString", $"Data Source={_auditDatabasePath}");

        // A price for the fake model, so the cost path is exercised rather than skipped.
        builder.UseSetting("Abacus:Llm:Pricing:fake-model:InputPerMillion", "3");
        builder.UseSetting("Abacus:Llm:Pricing:fake-model:OutputPerMillion", "15");
    }

    public T Resolve<T>() where T : notnull => Services.GetRequiredService<T>();

    public async Task<WorkflowInstance> RunAsync(string workflow)
    {
        var launcher = Resolve<IInstanceLauncher>();
        StartResult started = await launcher.StartAsync(
            workflow, null,
            new StartInstanceRequest
            {
                Context = System.Text.Json.JsonSerializer.SerializeToElement(
                    new ClassifyContext(), JsonOptions.Default)
            },
            "default", null, default);

        started.IsSuccess.Should().BeTrue();

        var store = Resolve<IInstanceStore>();
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);

        while (DateTime.UtcNow < deadline)
        {
            WorkflowInstance? instance = await store.GetAsync(started.Instance!.InstanceId, default);
            if (instance is not null && instance.Status.IsTerminal())
            {
                return instance;
            }
            await Task.Delay(25);
        }

        throw new TimeoutException($"'{workflow}' never terminated.");
    }

    public async Task<IReadOnlyList<EventEnvelope>> EventsOfAsync(string instanceId)
    {
        var store = Resolve<IEventStore>();
        var events = new List<EventEnvelope>();

        await foreach (EventEnvelope e in store.ReadAsync(instanceId, 0, default))
        {
            events.Add(e);
        }

        return events;
    }
}

public class NotificationPipelineTests : IClassFixture<NotificationFixture>
{
    private readonly NotificationFixture _fixture;

    public NotificationPipelineTests(NotificationFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task An_llm_node_emits_exactly_one_completion_carrying_tokens_and_cost()
    {
        WorkflowInstance instance = await _fixture.RunAsync("streaming-llm");
        instance.Status.Should().Be(InstanceStatus.Completed);

        EventEnvelope[] completions = (await _fixture.EventsOfAsync(instance.InstanceId))
            .Where(e => e.EventType == WorkflowEventTypes.LlmCompleted)
            .ToArray();

        completions.Should().ContainSingle("one model call is one fact, not a stream of them");

        string payload = completions[0].PayloadJson;
        payload.Should().Contain("\"outputTokens\":8");
        payload.Should().Contain("\"inputTokens\":40");
        payload.Should().Contain("costUsd");
        payload.Should().NotContain("\"costUsd\":null", "the fake model is priced in this fixture");
    }

    [Fact]
    public async Task Streamed_tokens_never_reach_the_durable_store()
    {
        WorkflowInstance instance = await _fixture.RunAsync("streaming-llm");

        IReadOnlyList<EventEnvelope> events = await _fixture.EventsOfAsync(instance.InstanceId);

        events.Should().NotContain(e => e.EventType == WorkflowEventTypes.LlmDelta,
            "a token already rendered has no replay value; storing it is pure write amplification");
    }

    [Fact]
    public async Task The_durable_sequence_stays_gapless_despite_the_delta_burst()
    {
        WorkflowInstance instance = await _fixture.RunAsync("streaming-llm");

        long[] sequences = (await _fixture.EventsOfAsync(instance.InstanceId))
            .Select(e => e.Sequence)
            .OrderBy(s => s)
            .ToArray();

        sequences.Should().Equal(Enumerable.Range(1, sequences.Length).Select(i => (long)i),
            "transient events must consume no sequence number, or catch-up would wait for one that never lands");
    }

    [Fact]
    public async Task Llm_usage_is_written_to_the_log_store_for_analysis()
    {
        WorkflowInstance instance = await _fixture.RunAsync("streaming-llm");

        IReadOnlyList<InstanceLogEntry> logs = await _fixture.Resolve<ILogStore>()
            .QueryAsync(instance.InstanceId, null, null, 100, default);

        logs.Should().Contain(l => l.Message.Contains("llm usage", StringComparison.Ordinal)
                                && l.Message.Contains("out=8", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_node_can_put_a_workflow_defined_event_on_the_stream()
    {
        WorkflowInstance instance = await _fixture.RunAsync("notifying");

        IReadOnlyList<EventEnvelope> events = await _fixture.EventsOfAsync(instance.InstanceId);

        EventEnvelope custom = events.Should()
            .ContainSingle(e => e.EventType == "custom.documents.scanned").Subject;

        custom.ExecutorId.Should().Be("scan");
        custom.PayloadJson.Should().Contain("3");
    }

    [Fact]
    public async Task A_quiet_workflow_suppresses_node_chatter_but_still_terminates()
    {
        WorkflowInstance instance = await _fixture.RunAsync("quiet");
        instance.Status.Should().Be(InstanceStatus.Completed);

        IReadOnlyList<EventEnvelope> events = await _fixture.EventsOfAsync(instance.InstanceId);

        events.Should().NotContain(e => e.EventType.StartsWith("executor.", StringComparison.Ordinal));
        events.Should().NotContain(e => e.EventType.StartsWith("superstep.", StringComparison.Ordinal));

        events.Should().Contain(e => e.EventType == WorkflowEventTypes.WorkflowTerminated,
            "suppressing the terminal event would hang every subscriber forever");

        long[] sequences = events.Select(e => e.Sequence).OrderBy(s => s).ToArray();
        sequences.Should().Equal(Enumerable.Range(1, sequences.Length).Select(i => (long)i),
            "filtering happens before the sequence number is taken");
    }

    [Fact]
    public async Task The_catalog_advertises_what_a_workflow_emits()
    {
        HttpClient client = _fixture.CreateClient();

        string body = await client.GetStringAsync("/workflows/notifying");

        body.Should().Contain("custom.documents.scanned",
            "a consumer should discover the vocabulary rather than reverse-engineer it");
    }
}

public class EventLogTests : IClassFixture<NotificationFixture>
{
    private readonly NotificationFixture _fixture;

    public EventLogTests(NotificationFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_log_only_workflow_still_records_a_full_event_history()
    {
        WorkflowInstance instance = await _fixture.RunAsync("log-only");
        instance.Status.Should().Be(InstanceStatus.Completed);

        IReadOnlyList<EventEnvelope> events = await _fixture.EventsOfAsync(instance.InstanceId);

        events.Should().Contain(e => e.EventType == WorkflowEventTypes.WorkflowStarted);
        events.Should().Contain(e => e.EventType == WorkflowEventTypes.WorkflowTerminated);
        events.Should().Contain(e => e.EventType == "custom.batch.processed",
            "opting out of streaming does not opt out of recording");

        events.Should().OnlyContain(e => e.Delivery == EventDeliveryMode.LogOnly);
    }

    [Fact]
    public async Task Every_logged_event_carries_the_workflow_name()
    {
        WorkflowInstance instance = await _fixture.RunAsync("log-only");

        IReadOnlyList<EventEnvelope> events = await _fixture.EventsOfAsync(instance.InstanceId);

        events.Should().OnlyContain(e => e.WorkflowName == "log-only",
            "the log is filterable by workflow without joining back to the instance row");
    }

    [Fact]
    public async Task The_sse_endpoint_refuses_a_log_only_workflow_instead_of_hanging()
    {
        WorkflowInstance instance = await _fixture.RunAsync("log-only");
        HttpClient client = _fixture.CreateClient();

        HttpResponseMessage response = await client.GetAsync($"/instances/{instance.InstanceId}/events");

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Conflict,
            "an empty stream held open is indistinguishable from a stalled run");

        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain($"/v2/workflows/log-only/instances/{instance.InstanceId}/events",
            "a refusal should say where the events actually are");
    }

    [Fact]
    public async Task The_sse_endpoint_still_serves_a_streaming_workflow()
    {
        WorkflowInstance instance = await _fixture.RunAsync("notifying");
        HttpClient client = _fixture.CreateClient();

        // Terminal instance: the stream replays history and closes, so this returns rather than hangs.
        HttpResponseMessage response = await client.GetAsync($"/instances/{instance.InstanceId}/events");

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("custom.documents.scanned");
    }

    [Fact]
    public async Task The_v2_event_log_returns_the_instance_and_workflow_with_its_events()
    {
        WorkflowInstance instance = await _fixture.RunAsync("log-only");
        HttpClient client = _fixture.CreateClient();

        string body = await client.GetStringAsync(
            $"/v2/workflows/log-only/instances/{instance.InstanceId}/events");

        body.Should().Contain(instance.InstanceId);
        body.Should().Contain("\"workflowName\":\"log-only\"");
        body.Should().Contain("custom.batch.processed");

        // The payload is a JSON object in the response, not an escaped string a caller parses twice.
        body.Should().Contain("\"rows\":500");
    }

    [Fact]
    public async Task The_v2_event_log_works_for_a_streaming_workflow_too()
    {
        WorkflowInstance instance = await _fixture.RunAsync("notifying");
        HttpClient client = _fixture.CreateClient();

        string body = await client.GetStringAsync(
            $"/v2/workflows/notifying/instances/{instance.InstanceId}/events");

        body.Should().Contain("custom.documents.scanned",
            "the log is the same rows either way; only the live stream differs");
    }

    [Fact]
    public async Task The_v2_event_log_rejects_a_workflow_that_does_not_own_the_instance()
    {
        WorkflowInstance instance = await _fixture.RunAsync("log-only");
        HttpClient client = _fixture.CreateClient();

        HttpResponseMessage response = await client.GetAsync(
            $"/v2/workflows/notifying/instances/{instance.InstanceId}/events");

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound,
            "naming the wrong workflow is a caller mistake, not a filter");
    }

    [Fact]
    public async Task The_v2_event_log_filters_by_type_and_cursor()
    {
        WorkflowInstance instance = await _fixture.RunAsync("log-only");
        HttpClient client = _fixture.CreateClient();

        string body = await client.GetStringAsync(
            $"/v2/workflows/log-only/instances/{instance.InstanceId}/events?types=custom.batch.processed");

        body.Should().Contain("custom.batch.processed");
        body.Should().NotContain(WorkflowEventTypes.WorkflowStarted);
    }
}

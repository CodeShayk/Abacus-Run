using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Abacus.Run.Abstractions;
using Abacus.Run.Api;
using Abacus.Run.Core;
using Abacus.Run.Dsl.Hosting;
using Abacus.Run.Dsl.Interpretation;
using Abacus.Run.Executors;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Abacus.Run.IntegrationTests;

/// <summary>Canned HTTP responses, so an <c>http</c> node can be exercised without a network.</summary>
public sealed class StubHttpHandler : HttpMessageHandler
{
    public ConcurrentQueue<HttpRequestMessage> Requests { get; } = new();

    public Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> Respond { get; set; } =
        _ => (HttpStatusCode.OK, """{ "ok": true }""");

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Enqueue(request);
        (HttpStatusCode status, string body) = Respond(request);

        return Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        });
    }
}

/// <summary>A chat client that answers with whatever the test told it to.</summary>
public sealed class StubChatClient : IChatClient
{
    public string Reply { get; set; } = "stub reply";

    public List<string> Prompts { get; } = [];

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Prompts.AddRange(messages.Select(m => m.Text ?? string.Empty));

        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Reply))
        {
            ModelId = options?.ModelId ?? "stub",
            Usage = new UsageDetails { InputTokenCount = 11, OutputTokenCount = 7 }
        });
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);
        yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

/// <summary>In-memory timers, since the host ships no <see cref="ITimerService"/> of its own.</summary>
public sealed class InMemoryTimerService : ITimerService
{
    private readonly ConcurrentBag<(string InstanceId, string ExecutorId, DateTimeOffset WakeAt)> _timers = [];

    public IReadOnlyCollection<(string InstanceId, string ExecutorId, DateTimeOffset WakeAt)> Scheduled => _timers;

    public ValueTask ScheduleAsync(
        string instanceId, string executorId, DateTimeOffset wakeAt, CancellationToken cancellationToken)
    {
        _timers.Add((instanceId, executorId, wakeAt));
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<(string InstanceId, string ExecutorId)>> ClaimDueAsync(
        DateTimeOffset now, int max, CancellationToken cancellationToken)
        => ValueTask.FromResult<IReadOnlyList<(string, string)>>(
            [.. _timers.Where(t => t.WakeAt <= now).Take(max).Select(t => (t.InstanceId, t.ExecutorId))]);
}

/// <summary>
/// A custom node, registered by name — the seam that keeps the DSL from having a ceiling. It doubles
/// whatever number the document points it at, which is dull on purpose: the point under test is the
/// registration and parameter contract, not the arithmetic.
/// </summary>
public sealed class DoublerNodeFactory : IDslNodeFactory
{
    public string Name => "doubler";

    public JsonNode? ParameterSchema => JsonNode.Parse("""
    {
      "type": "object",
      "required": ["field"],
      "properties": {
        "field": { "type": "string" },
        "times": { "type": "number" }
      }
    }
    """);

    public IHostExecutor Create(DslNodeContext context)
    {
        string field = context.Parameters["field"]!.GetValue<string>();
        decimal times = context.Parameters["times"]?.GetValue<decimal>() ?? 2m;

        return new Doubler(context.Node.Id, field, times);
    }

    private sealed class Doubler(string id, string field, decimal times)
        : HostExecutor<DslMessage, DslMessage>(id)
    {
        protected override ValueTask<DslMessage> ExecuteCoreAsync(
            DslMessage input, IWorkflowContext context, CancellationToken cancellationToken)
        {
            var data = input.Data as JsonObject ?? [];
            var updated = (JsonObject)data.DeepClone();

            decimal current = updated[field]?.GetValue<decimal>() ?? 0m;
            updated[field] = current * times;

            return ValueTask.FromResult(input.WithData(updated));
        }
    }
}

/// <summary>A custom node that returns the wrong executor shape, to prove the check bites.</summary>
public sealed class WrongShapeNodeFactory : IDslNodeFactory
{
    public string Name => "wrong-shape";

    public IHostExecutor Create(DslNodeContext context)
        => new Wrong(context.Node.Id);

    private sealed class Wrong(string id) : HostExecutor<string, string>(id)
    {
        protected override ValueTask<string> ExecuteCoreAsync(
            string input, IWorkflowContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(input);
    }
}

/// <summary>
/// A host with only DSL workflows registered, so a failure is unambiguously about the DSL rather
/// than about a compiled definition sitting beside it.
/// </summary>
public sealed class DslHostFixture : WebApplicationFactory<Program>
{
    public StubHttpHandler Http { get; } = new();
    public StubChatClient Chat { get; } = new();
    public InMemoryTimerService Timers { get; } = new();

    private readonly string _auditDatabasePath =
        Path.Combine(Path.GetTempPath(), $"abacus-dsl-audit-{Guid.NewGuid():N}.db");

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<ITimerService>(Timers);
            services.AddSingleton<IChatClient>(Chat);

            // The DSL's http node resolves this named client, so a stub handler here reaches every
            // http node without any of them knowing they are under test.
            services.AddHttpClient(ApiCallOptions.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => Http);

            var host = new WorkflowHostBuilder(services);

            host.AddDslNode(new DoublerNodeFactory())
                .AddDslNode(new WrongShapeNodeFactory())
                .ConfigureDsl(registry => registry.EnforceEgress = false);

            foreach ((string name, string text) in DslDocuments.All)
            {
                host.AddDslWorkflowText(text, name);
            }
        });

        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("WorkflowHost:Approvals:SweepIntervalSeconds", "1");
        builder.UseSetting("Abacus:AuditRecords:ConnectionString", $"Data Source={_auditDatabasePath}");
        builder.ConfigureServices(services =>
        {
            services.AddHttpClient<Abacus.Run.Service.ControlPlane.Services.WorkflowApiClient>(client =>
            {
                client.BaseAddress = new Uri("http://localhost");
            }).ConfigurePrimaryHttpMessageHandler(() => Server.CreateHandler());
        });
    }

    public T Resolve<T>() where T : notnull => Services.GetRequiredService<T>();

    public async Task<WorkflowInstance> WaitForStatusAsync(string instanceId, params InstanceStatus[] expected)
    {
        var store = Resolve<IInstanceStore>();
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);

        while (DateTime.UtcNow < deadline)
        {
            WorkflowInstance? instance = await store.GetAsync(instanceId, default);
            if (instance is not null && expected.Contains(instance.Status))
            {
                return instance;
            }

            await Task.Delay(25);
        }

        WorkflowInstance? last = await store.GetAsync(instanceId, default);

        // The status alone says a run stalled but never why, and the reason is almost always in the
        // terminal reason or the instance log. Reading them here turns "expected Completed" into a
        // message that names the actual fault.
        IReadOnlyList<InstanceLogEntry> logs = await Resolve<ILogStore>()
            .QueryAsync(instanceId, null, null, 50, default);

        string detail = logs.Count == 0
            ? "(no log entries)"
            : string.Join(Environment.NewLine, logs.Select(l => $"  [{l.Level}] {l.ExecutorId}: {l.Message}"));

        throw new TimeoutException(
            $"Instance '{instanceId}' was '{last?.Status.ToString() ?? "missing"}', " +
            $"expected one of {string.Join(", ", expected)}.{Environment.NewLine}" +
            $"Terminal reason: {last?.TerminalReason ?? "(none)"}{Environment.NewLine}{detail}");
    }

    public async Task<string> StartAsync(HttpClient client, string workflow, object context)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/workflows/{workflow}/instances", new { context });

        response.EnsureSuccessStatusCode();

        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("instanceId").GetString()!;
    }

    /// <summary>Starts and waits, for the common "did this document run" assertion.</summary>
    public async Task<WorkflowInstance> RunAsync(
        HttpClient client, string workflow, object context, params InstanceStatus[] expected)
    {
        string id = await StartAsync(client, workflow, context);
        return await WaitForStatusAsync(id, expected.Length > 0
            ? expected
            : [InstanceStatus.Completed]);
    }

    /// <summary>Reads the workflow result off a completed instance.</summary>
    public async Task<JsonNode?> ResultOfAsync(string instanceId)
    {
        WorkflowInstance? instance = await Resolve<IInstanceStore>().GetAsync(instanceId, default);
        return instance?.ResultJson is { Length: > 0 } json ? JsonNode.Parse(json) : null;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing) return;

        try
        {
            if (File.Exists(_auditDatabasePath)) File.Delete(_auditDatabasePath);
        }
        catch (IOException)
        {
        }
    }
}

using System.Net.Http.Json;
using System.Text.Json;
using Abacus.Run.Abstractions;
using Abacus.Run.Api;
using Abacus.Run.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Abacus.Run.IntegrationTests;

public sealed record OrderContext(string OrderId = "ORD-1", decimal Amount = 100m, bool Fail = false);

public sealed record OrderResult(string OrderId, string Status);

/// <summary>
/// Records every executor invocation across the whole host so tests can assert exactly-once side
/// effects without a mocking framework.
/// </summary>
public sealed class SideEffectLedger
{
    private readonly List<string> _entries = [];
    private readonly object _sync = new();

    public void Record(string executorId, string instanceId)
    {
        lock (_sync)
        {
            _entries.Add($"{executorId}:{instanceId}");
        }
    }

    public IReadOnlyList<string> Entries
    {
        get { lock (_sync) { return [.. _entries]; } }
    }

    public int CountFor(string executorId)
        => Entries.Count(e => e.StartsWith(executorId + ":", StringComparison.Ordinal));
}

/// <summary>Autonomous two-node workflow; fails on demand so retry and dead-stop paths are reachable.</summary>
public sealed class OrderWorkflow : IWorkflowDefinition<OrderContext, OrderResult>
{
    private readonly SideEffectLedger _ledger;

    public OrderWorkflow(SideEffectLedger ledger) => _ledger = ledger;

    public string Name => "order";
    public string Version => "1.0.0";

    public ValueTask<Workflow> BuildAsync(WorkflowBuildContext context, CancellationToken cancellationToken)
    {
        ExecutorBinding validate = context.Node(new Validate("validate", _ledger, context.InstanceId));
        ExecutorBinding submit = context.Node(new Submit("submit", _ledger, context.InstanceId));

        return new ValueTask<Workflow>(new WorkflowBuilder(validate)
            .AddEdge(validate, submit)
            .WithOutputFrom(submit)
            .WithName(Name)
            .Build());
    }

    private sealed class Validate : HostExecutor<OrderContext, OrderContext>
    {
        private readonly SideEffectLedger _ledger;
        private readonly string _instanceId;

        public Validate(string id, SideEffectLedger ledger, string instanceId) : base(id)
        {
            _ledger = ledger;
            _instanceId = instanceId;
        }

        protected override ValueTask<OrderContext> ExecuteCoreAsync(
            OrderContext input, IWorkflowContext context, CancellationToken cancellationToken)
        {
            _ledger.Record(Id, _instanceId);

            if (input.Fail)
            {
                throw new WorkflowDeadStopException($"Order {input.OrderId} rejected.");
            }

            return ValueTask.FromResult(input);
        }
    }

    private sealed class Submit : HostExecutor<OrderContext, OrderResult>
    {
        private readonly SideEffectLedger _ledger;
        private readonly string _instanceId;

        public Submit(string id, SideEffectLedger ledger, string instanceId) : base(id)
        {
            _ledger = ledger;
            _instanceId = instanceId;
        }

        protected override ValueTask<OrderResult> ExecuteCoreAsync(
            OrderContext input, IWorkflowContext context, CancellationToken cancellationToken)
        {
            _ledger.Record(Id, _instanceId);
            return ValueTask.FromResult(new OrderResult(input.OrderId, "submitted"));
        }
    }
}

/// <summary>Same shape, but the second node is gated above a threshold.</summary>
public sealed class GatedOrderWorkflow : IWorkflowDefinition<OrderContext, OrderResult>
{
    private readonly SideEffectLedger _ledger;

    public GatedOrderWorkflow(SideEffectLedger ledger) => _ledger = ledger;

    public string Name => "gated-order";
    public string Version => "1.0.0";

    public ValueTask<Workflow> BuildAsync(WorkflowBuildContext context, CancellationToken cancellationToken)
    {
        ExecutorBinding validate = context.Node(new Recorder("validate", _ledger, context.InstanceId));

        ExecutorBinding pay = context.Node(
            new Payer("post-payment", _ledger, context.InstanceId),
            gate => gate
                .When<OrderContext>(order => order.Amount > 25_000m)
                .Reason("AmountAboveThreshold")
                .ExpiresAfter(TimeSpan.FromHours(8))
                .OnExpiry(ExpiryAction.DeadStop)
                .AllowModification());

        return new ValueTask<Workflow>(new WorkflowBuilder(validate)
            .AddEdge(validate, pay)
            .WithOutputFrom(pay)
            .WithName(Name)
            .Build());
    }

    private sealed class Recorder : HostExecutor<OrderContext, OrderContext>
    {
        private readonly SideEffectLedger _ledger;
        private readonly string _instanceId;

        public Recorder(string id, SideEffectLedger ledger, string instanceId) : base(id)
        {
            _ledger = ledger;
            _instanceId = instanceId;
        }

        protected override ValueTask<OrderContext> ExecuteCoreAsync(
            OrderContext input, IWorkflowContext context, CancellationToken cancellationToken)
        {
            _ledger.Record(Id, _instanceId);
            return ValueTask.FromResult(input);
        }
    }

    private sealed class Payer : HostExecutor<OrderContext, OrderResult>
    {
        private readonly SideEffectLedger _ledger;
        private readonly string _instanceId;

        public Payer(string id, SideEffectLedger ledger, string instanceId) : base(id)
        {
            _ledger = ledger;
            _instanceId = instanceId;
        }

        protected override ValueTask<OrderResult> ExecuteCoreAsync(
            OrderContext input, IWorkflowContext context, CancellationToken cancellationToken)
        {
            _ledger.Record(Id, _instanceId);
            return ValueTask.FromResult(new OrderResult(input.OrderId, "paid"));
        }
    }
}

public sealed class HostFixture : WebApplicationFactory<Program>
{
    public SideEffectLedger Ledger { get; } = new();

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.AddSingleton(Ledger);
            services.AddSingleton<IWorkflowDefinition>(sp => new OrderWorkflow(sp.GetRequiredService<SideEffectLedger>()));
            services.AddSingleton<IWorkflowDefinition>(sp => new GatedOrderWorkflow(sp.GetRequiredService<SideEffectLedger>()));
        });

        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
        => builder.UseSetting("WorkflowHost:Approvals:SweepIntervalSeconds", "1");

    public T Resolve<T>() where T : notnull => Services.GetRequiredService<T>();

    /// <summary>Polls until the instance reaches one of the expected statuses.</summary>
    public async Task<WorkflowInstance> WaitForStatusAsync(
        string instanceId, params InstanceStatus[] expected)
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
        throw new TimeoutException(
            $"Instance '{instanceId}' was '{last?.Status.ToString() ?? "missing"}', expected one of {string.Join(", ", expected)}.");
    }

    public async Task<string> StartAsync(
        HttpClient client, string workflow, object context, string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/workflows/{workflow}/instances")
        {
            Content = JsonContent.Create(new { context })
        };

        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        HttpResponseMessage response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("instanceId").GetString()!;
    }
}

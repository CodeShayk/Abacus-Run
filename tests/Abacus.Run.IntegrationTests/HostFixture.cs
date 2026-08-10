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

/// <summary>
/// Three nodes with nothing gated by default, so a tenant's configuration is the only thing that
/// can make the run stop — except <c>settle</c>, which the author locked.
/// </summary>
public sealed class TenantOrderWorkflow : IWorkflowDefinition<OrderContext, OrderResult>
{
    private readonly SideEffectLedger _ledger;

    public TenantOrderWorkflow(SideEffectLedger ledger) => _ledger = ledger;

    public string Name => "tenant-order";
    public string Version => "1.0.0";

    public ValueTask<Workflow> BuildAsync(WorkflowBuildContext context, CancellationToken cancellationToken)
    {
        ExecutorBinding prepare = context.Node(new Step("prepare", _ledger, context.InstanceId));
        ExecutorBinding dispatch = context.Node(new Step("dispatch", _ledger, context.InstanceId));

        ExecutorBinding settle = context.Node(
            new Settle("settle", _ledger, context.InstanceId),
            gate => gate
                .When<OrderContext>(order => order.Amount > 25_000m)
                .Reason("RegulatedSettlement")
                .Locked());

        return new ValueTask<Workflow>(new WorkflowBuilder(prepare)
            .AddEdge(prepare, dispatch)
            .AddEdge(dispatch, settle)
            .WithOutputFrom(settle)
            .WithName(Name)
            .Build());
    }

    private sealed class Step : HostExecutor<OrderContext, OrderContext>
    {
        private readonly SideEffectLedger _ledger;
        private readonly string _instanceId;

        public Step(string id, SideEffectLedger ledger, string instanceId) : base(id)
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

    private sealed class Settle : HostExecutor<OrderContext, OrderResult>
    {
        private readonly SideEffectLedger _ledger;
        private readonly string _instanceId;

        public Settle(string id, SideEffectLedger ledger, string instanceId) : base(id)
        {
            _ledger = ledger;
            _instanceId = instanceId;
        }

        protected override ValueTask<OrderResult> ExecuteCoreAsync(
            OrderContext input, IWorkflowContext context, CancellationToken cancellationToken)
        {
            _ledger.Record(Id, _instanceId);
            return ValueTask.FromResult(new OrderResult(input.OrderId, "settled"));
        }
    }
}

/// <summary>
/// Declares an audit record, so the generic state endpoint has a workflow to read back. Deliberately
/// unremarkable otherwise — the point is that a definition gets auditing by declaring it, with no
/// framework code that knows what an "order" is.
/// </summary>
public sealed class AuditedOrderWorkflow : IWorkflowDefinition<OrderContext, OrderResult>, IAuditedWorkflowDefinition
{
    public const string Submission = "submission";
    public const string Step = "step";
    public const string Outcome = "outcome";

    public string Name => "audited-order";
    public string Version => "1.0.0";

    public AuditRecordDefinition AuditRecord { get; } = new(
        "order",
        "One order, as processed.",
        [
            new AuditSectionDefinition(Submission, "What was submitted.", Multiple: false),
            new AuditSectionDefinition(Step, "One processing step."),
            new AuditSectionDefinition(Outcome, "How the run settled.", Multiple: false)
        ]);

    public ValueTask<Workflow> BuildAsync(WorkflowBuildContext context, CancellationToken cancellationToken)
    {
        ExecutorBinding validate = context.Node(new Validate("validate"));
        ExecutorBinding submit = context.Node(new Submit("submit"));

        return new ValueTask<Workflow>(new WorkflowBuilder(validate)
            .AddEdge(validate, submit)
            .WithOutputFrom(submit)
            .WithName(Name)
            .Build());
    }

    private sealed class Validate(string id) : HostExecutor<OrderContext, OrderContext>(id)
    {
        protected override async ValueTask<OrderContext> ExecuteCoreAsync(
            OrderContext input, IWorkflowContext context, CancellationToken cancellationToken)
        {
            if (Runtime.Audit is { } audit)
            {
                await audit.OpenAsync(input.OrderId, new Dictionary<string, object?> { ["amount"] = input.Amount }, cancellationToken);
                await audit.RecordAsync(Submission, null, new { input.OrderId, input.Amount }, cancellationToken);
                await audit.RecordAsync(Step, Id, new { accepted = true }, cancellationToken);
            }

            return input;
        }
    }

    private sealed class Submit(string id) : HostExecutor<OrderContext, OrderResult>(id)
    {
        protected override async ValueTask<OrderResult> ExecuteCoreAsync(
            OrderContext input, IWorkflowContext context, CancellationToken cancellationToken)
        {
            var result = new OrderResult(input.OrderId, "submitted");

            if (Runtime.Audit is { } audit)
            {
                await audit.RecordAsync(Step, Id, new { result.Status }, cancellationToken);
                await audit.RecordAsync(Outcome, null, result, cancellationToken);
                await audit.CloseAsync(AuditRecordStatus.Completed, cancellationToken);
            }

            return result;
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
            services.AddSingleton<IWorkflowDefinition>(sp => new TenantOrderWorkflow(sp.GetRequiredService<SideEffectLedger>()));
            services.AddSingleton<IWorkflowDefinition>(new AuditedOrderWorkflow());
        });

        return base.CreateHost(builder);
    }

    /// <summary>
    /// The host substitutes a SQLite audit-record store for the framework default, so without an
    /// override every fixture would share the deployed database file and inherit records from
    /// previous runs. One file per fixture, deleted on dispose, keeps that state out of the suite.
    /// </summary>
    private readonly string _auditDatabasePath =
        Path.Combine(Path.GetTempPath(), $"abacus-audit-{Guid.NewGuid():N}.db");

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
        HttpClient client, string workflow, object context, string? idempotencyKey = null, string? tenantId = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/workflows/{workflow}/instances")
        {
            Content = JsonContent.Create(new { context })
        };

        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        if (tenantId is not null)
        {
            request.Headers.Add("X-Tenant-Id", tenantId);
        }

        HttpResponseMessage response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("instanceId").GetString()!;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing) return;

        // Best-effort: a file left behind is untidy, not a test failure.
        try
        {
            if (File.Exists(_auditDatabasePath)) File.Delete(_auditDatabasePath);
        }
        catch (IOException)
        {
        }
    }
}

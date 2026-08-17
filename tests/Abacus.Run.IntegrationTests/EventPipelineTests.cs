using System.Net.Http.Json;
using Abacus.Run.Abstractions;
using Abacus.Run.Api;
using Abacus.Run.Core;
using Abacus.Run.Dispatch;
using Abacus.Run.Messaging;
using Abacus.Run.Notifications;
using Abacus.Run.Executors;
using Abacus.Run.Persistence;
using FluentAssertions;
using Microsoft.Agents.AI.Workflows;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Abacus.Run.IntegrationTests;

public sealed record PlaceOrderContext(string OrderId = "ORD-1");

public sealed record OrderPlaced(string OrderId = "ORD-1");

public sealed record ShipmentResult(string OrderId, string Status);

public sealed record AwaitPaymentContext(string OrderId = "ORD-1");

public sealed record PaymentSettled(string OrderId = "ORD-1", decimal Amount = 0m);

public sealed record PaymentResult(string OrderId, string Status, decimal Amount);

/// <summary>Publishes <c>orders.placed</c> as a side effect on the way past.</summary>
public sealed class PlaceOrderWorkflow : IWorkflowDefinition<PlaceOrderContext, OrderPlaced>
{
    public string Name => "place-order";
    public string Version => "1.0.0";

    public ValueTask<Workflow> BuildAsync(WorkflowBuildContext context, CancellationToken cancellationToken)
    {
        var broker = context.Services!.GetRequiredService<IDomainEventBroker>();

        ExecutorBinding prepare = context.Node(new Prepare("prepare"));
        ExecutorBinding publish = context.Node(new PublishDomainEventExecutor<OrderPlaced>(
            "publish-order-placed", broker, "orders.placed", correlationKey: o => o.OrderId));

        return new ValueTask<Workflow>(new WorkflowBuilder(prepare)
            .AddEdge(prepare, publish)
            .WithOutputFrom(publish)
            .WithName(Name)
            .Build());
    }

    private sealed class Prepare(string id) : HostExecutor<PlaceOrderContext, OrderPlaced>(id)
    {
        protected override ValueTask<OrderPlaced> ExecuteCoreAsync(
            PlaceOrderContext input, IWorkflowContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(new OrderPlaced(input.OrderId));
    }
}

/// <summary>Started by a message rather than by an API call.</summary>
public sealed class ShipOrderWorkflow : IWorkflowDefinition<OrderPlaced, ShipmentResult>, IDomainEventTriggeredWorkflow
{
    private readonly SideEffectLedger _ledger;

    public ShipOrderWorkflow(SideEffectLedger ledger) => _ledger = ledger;

    public string Name => "ship-order";
    public string Version => "1.0.0";

    public IReadOnlyList<DomainEventTrigger> Triggers =>
        [new DomainEventTrigger { TopicFilter = "orders.placed" }];

    public ValueTask<Workflow> BuildAsync(WorkflowBuildContext context, CancellationToken cancellationToken)
    {
        ExecutorBinding ship = context.Node(new Ship("ship", _ledger, context.InstanceId));

        return new ValueTask<Workflow>(new WorkflowBuilder(ship)
            .WithOutputFrom(ship)
            .WithName(Name)
            .Build());
    }

    private sealed class Ship(string id, SideEffectLedger ledger, string instanceId)
        : HostExecutor<OrderPlaced, ShipmentResult>(id)
    {
        protected override ValueTask<ShipmentResult> ExecuteCoreAsync(
            OrderPlaced input, IWorkflowContext context, CancellationToken cancellationToken)
        {
            ledger.Record($"ship:{input.OrderId}", instanceId);
            return ValueTask.FromResult(new ShipmentResult(input.OrderId, "shipped"));
        }
    }
}

/// <summary>Parks on <c>payment.settled</c> until a matching correlation key arrives.</summary>
public sealed class AwaitPaymentWorkflow : IWorkflowDefinition<AwaitPaymentContext, PaymentResult>
{
    public string Name => "await-payment";
    public string Version => "1.0.0";

    public ValueTask<Workflow> BuildAsync(WorkflowBuildContext context, CancellationToken cancellationToken)
    {
        var subscriptions = context.Services!.GetRequiredService<IDomainEventSubscriptionStore>();

        ExecutorBinding begin = context.Node(new Begin("begin"));
        ExecutorBinding wait = context.Node(new WaitForDomainEventExecutor<AwaitPaymentContext, PaymentSettled>(
            "await-settlement", subscriptions, "payment.settled", correlationKey: c => c.OrderId));
        ExecutorBinding finish = context.Node(new Finish("finish"));

        return new ValueTask<Workflow>(new WorkflowBuilder(begin)
            .AddEdge(begin, wait)
            .AddEdge(wait, finish)
            .WithOutputFrom(finish)
            .WithName(Name)
            .Build());
    }

    private sealed class Begin(string id) : HostExecutor<AwaitPaymentContext, AwaitPaymentContext>(id)
    {
        protected override ValueTask<AwaitPaymentContext> ExecuteCoreAsync(
            AwaitPaymentContext input, IWorkflowContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(input);
    }

    private sealed class Finish(string id) : HostExecutor<PaymentSettled, PaymentResult>(id)
    {
        protected override ValueTask<PaymentResult> ExecuteCoreAsync(
            PaymentSettled input, IWorkflowContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(new PaymentResult(input.OrderId, "settled", input.Amount));
    }
}

public sealed class EventPipelineFixture : WebApplicationFactory<Program>
{
    public SideEffectLedger Ledger { get; } = new();

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.AddSingleton(Ledger);
            services.AddSingleton<IWorkflowDefinition>(new PlaceOrderWorkflow());
            services.AddSingleton<IWorkflowDefinition>(sp => new ShipOrderWorkflow(sp.GetRequiredService<SideEffectLedger>()));
            services.AddSingleton<IWorkflowDefinition>(new AwaitPaymentWorkflow());
        });

        return base.CreateHost(builder);
    }

    private readonly string _auditDatabasePath =
        Path.Combine(Path.GetTempPath(), $"abacus-events-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Abacus:AuditRecords:ConnectionString", $"Data Source={_auditDatabasePath}");

        // The control plane reaches data only through the public API, so its typed client has to be
        // pointed back at this test server for the pages to render anything.
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
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);

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
            $"Instance {instanceId} never reached {string.Join("/", expected)}; it is {last?.Status.ToString() ?? "missing"}.");
    }

    public async Task<WorkflowInstance> WaitForInstanceOfAsync(string workflowName)
    {
        var store = Resolve<IInstanceStore>();
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);

        while (DateTime.UtcNow < deadline)
        {
            Page<WorkflowInstance> page = await store.QueryAsync(
                new InstanceQuery { WorkflowName = workflowName }, default);

            if (page.Items.Count > 0)
            {
                return page.Items[0];
            }
            await Task.Delay(25);
        }

        throw new TimeoutException($"No instance of '{workflowName}' was ever created.");
    }
}

public class EventPipelineTests : IClassFixture<EventPipelineFixture>
{
    private readonly EventPipelineFixture _fixture;

    public EventPipelineTests(EventPipelineFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_published_event_starts_the_workflow_that_subscribes_to_it()
    {
        var launcher = _fixture.Resolve<IInstanceLauncher>();

        StartResult started = await launcher.StartAsync(
            "place-order", null,
            new StartInstanceRequest { Context = Json(new PlaceOrderContext("ORD-PIPELINE")) },
            "default", null, default);

        started.IsSuccess.Should().BeTrue();
        await _fixture.WaitForStatusAsync(started.Instance!.InstanceId, InstanceStatus.Completed);

        // The publisher completing is not the assertion — the consumer running is.
        WorkflowInstance shipment = await _fixture.WaitForInstanceOfAsync("ship-order");
        await _fixture.WaitForStatusAsync(shipment.InstanceId, InstanceStatus.Completed);

        _fixture.Ledger.CountFor("ship:ORD-PIPELINE").Should()
            .Be(1, "one published message should start exactly one downstream instance");
    }

    [Fact]
    public async Task A_workflow_parks_on_an_event_and_resumes_with_its_payload()
    {
        var launcher = _fixture.Resolve<IInstanceLauncher>();
        var broker = _fixture.Resolve<IDomainEventBroker>();

        StartResult started = await launcher.StartAsync(
            "await-payment", null,
            new StartInstanceRequest { Context = Json(new AwaitPaymentContext("ORD-WAIT")) },
            "default", null, default);

        WorkflowInstance parked = await _fixture.WaitForStatusAsync(
            started.Instance!.InstanceId, InstanceStatus.AwaitingInput);

        parked.Status.Should().Be(InstanceStatus.AwaitingInput,
            "a wait must release its lease rather than block an execution slot");

        // Nothing is holding this in memory: the wait is a row, and so is the instance.
        await broker.PublishAsync(new DomainEventMessage
        {
            MessageId = IdGenerator.NewId("msg"),
            Topic = "payment.settled",
            PayloadJson = """{"orderId":"ORD-WAIT","amount":250.5}""",
            CorrelationKey = "ORD-WAIT"
        }, default);

        WorkflowInstance completed = await _fixture.WaitForStatusAsync(
            started.Instance.InstanceId, InstanceStatus.Completed);

        completed.ResultJson.Should().NotBeNull();
        completed.ResultJson.Should().Contain("250.5", "the delivered payload should reach the workflow's result");
    }

    [Fact]
    public async Task An_event_for_a_different_correlation_key_does_not_resume_the_wait()
    {
        var launcher = _fixture.Resolve<IInstanceLauncher>();
        var broker = _fixture.Resolve<IDomainEventBroker>();

        StartResult started = await launcher.StartAsync(
            "await-payment", null,
            new StartInstanceRequest { Context = Json(new AwaitPaymentContext("ORD-KEYED")) },
            "default", null, default);

        await _fixture.WaitForStatusAsync(started.Instance!.InstanceId, InstanceStatus.AwaitingInput);

        await broker.PublishAsync(new DomainEventMessage
        {
            MessageId = IdGenerator.NewId("msg"),
            Topic = "payment.settled",
            PayloadJson = """{"orderId":"SOMEONE-ELSE","amount":10}""",
            CorrelationKey = "SOMEONE-ELSE"
        }, default);

        await Task.Delay(500);

        WorkflowInstance instance = await _fixture.Resolve<IInstanceStore>()
            .GetAsync(started.Instance.InstanceId, default) ?? throw new InvalidOperationException();

        instance.Status.Should().Be(InstanceStatus.AwaitingInput,
            "a correlated wait must ignore another conversation's message");
    }

    private static System.Text.Json.JsonElement Json<T>(T value)
        => System.Text.Json.JsonSerializer.SerializeToElement(value, JsonOptions.Default);
}

public class EventApiTests : IClassFixture<EventPipelineFixture>
{
    private readonly EventPipelineFixture _fixture;

    public EventApiTests(EventPipelineFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Publishing_over_http_resumes_a_parked_workflow()
    {
        HttpClient client = _fixture.CreateClient();
        var launcher = _fixture.Resolve<IInstanceLauncher>();

        StartResult started = await launcher.StartAsync(
            "await-payment", null,
            new StartInstanceRequest
            {
                Context = System.Text.Json.JsonSerializer.SerializeToElement(
                    new AwaitPaymentContext("ORD-HTTP"), JsonOptions.Default)
            },
            "default", null, default);

        await _fixture.WaitForStatusAsync(started.Instance!.InstanceId, InstanceStatus.AwaitingInput);

        HttpResponseMessage response = await client.PostAsJsonAsync("/events", new
        {
            topic = "payment.settled",
            payload = new { orderId = "ORD-HTTP", amount = 42.0 },
            correlationKey = "ORD-HTTP"
        });

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Accepted);

        WorkflowInstance completed = await _fixture.WaitForStatusAsync(
            started.Instance.InstanceId, InstanceStatus.Completed);

        completed.ResultJson.Should().Contain("42",
            "an external publisher should be able to resume a workflow it knows nothing about");
    }

    [Fact]
    public async Task A_wildcard_topic_is_rejected_on_publish()
    {
        HttpClient client = _fixture.CreateClient();

        HttpResponseMessage response = await client.PostAsJsonAsync("/events", new
        {
            topic = "payment.*",
            payload = new { }
        });

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest,
            "wildcards belong to subscriber filters, not published topics");
    }

    [Fact]
    public async Task Distributed_scope_is_refused_when_no_distributed_broker_is_configured()
    {
        HttpClient client = _fixture.CreateClient();

        HttpResponseMessage response = await client.PostAsJsonAsync("/events", new
        {
            topic = "payment.settled",
            payload = new { },
            scope = "Distributed"
        });

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest,
            "accepting it would mean the other service silently never hears about it");
    }

    [Fact]
    public async Task Subscriptions_show_what_a_parked_instance_is_waiting_for()
    {
        HttpClient client = _fixture.CreateClient();
        var launcher = _fixture.Resolve<IInstanceLauncher>();

        StartResult started = await launcher.StartAsync(
            "await-payment", null,
            new StartInstanceRequest
            {
                Context = System.Text.Json.JsonSerializer.SerializeToElement(
                    new AwaitPaymentContext("ORD-VISIBLE"), JsonOptions.Default)
            },
            "default", null, default);

        await _fixture.WaitForStatusAsync(started.Instance!.InstanceId, InstanceStatus.AwaitingInput);

        string body = await client.GetStringAsync(
            $"/subscriptions?instanceId={started.Instance.InstanceId}&pendingOnly=true");

        body.Should().Contain("payment.settled");
        body.Should().Contain("ORD-VISIBLE");
        body.Should().Contain("await-settlement",
            "an operator must be able to see which node is parked, not just that the instance is");
    }

    [Fact]
    public async Task The_instance_page_shows_what_a_parked_instance_is_waiting_on()
    {
        HttpClient client = _fixture.CreateClient();
        var launcher = _fixture.Resolve<IInstanceLauncher>();

        StartResult started = await launcher.StartAsync(
            "await-payment", null,
            new StartInstanceRequest
            {
                Context = System.Text.Json.JsonSerializer.SerializeToElement(
                    new AwaitPaymentContext("ORD-UI"), JsonOptions.Default)
            },
            "default", null, default);

        await _fixture.WaitForStatusAsync(started.Instance!.InstanceId, InstanceStatus.AwaitingInput);

        string html = await client.GetStringAsync($"/control/Instances/Detail?id={started.Instance.InstanceId}");

        html.Should().Contain("Waiting on");
        html.Should().Contain("payment.settled");
        html.Should().Contain("await-settlement",
            "an operator looking at a parked instance must be told which node is blocked and on what");
    }

    [Fact]
    public async Task Triggers_are_listed_so_an_operator_can_see_what_is_wired_up()
    {
        HttpClient client = _fixture.CreateClient();

        string body = await client.GetStringAsync("/subscriptions?kind=Trigger");

        body.Should().Contain("orders.placed");
        body.Should().Contain("ship-order");
    }
}

/// <summary>
/// The claim the narrative actually makes: an event-driven pipeline survives a restart. Composed by
/// hand rather than through a host, so the "restart" is real — every in-memory component is rebuilt
/// while only the stores persist.
/// </summary>
public class EventPipelineDurabilityTests
{
    [Fact]
    public async Task A_wait_registered_before_a_restart_is_resumed_after_it()
    {
        // Stores are the only thing that survives; everything else is rebuilt below.
        var instances = new InMemoryInstanceStore();
        var subscriptions = new InMemoryDomainEventSubscriptionStore();
        var events = new InMemoryEventStore();
        var sequencer = new NotificationSequencer();
        INotificationSink sink = new DirectNotificationSink(events);
        IWorkflowRegistry registry = new WorkflowRegistry([new AwaitPaymentWorkflow()]);

        WorkflowInstance instance = await instances.CreateAsync(new CreateInstanceRequest
        {
            InstanceId = IdGenerator.NewId(),
            TenantId = "default",
            WorkflowName = "await-payment",
            WorkflowVersion = "1.0.0",
            ContextJson = """{"orderId":"ORD-RESTART"}"""
        }, default);

        await instances.UpdateAsync(instance.InstanceId, m => m.Status = InstanceStatus.AwaitingInput, default);

        await subscriptions.RegisterAsync(new DomainEventSubscription
        {
            SubscriptionId = IdGenerator.NewId("sub"),
            Kind = DomainSubscriptionKind.Wait,
            TopicFilter = "payment.settled",
            CorrelationKey = "ORD-RESTART",
            InstanceId = instance.InstanceId,
            ExecutorId = "await-settlement"
        }, default);

        // --- the process dies here; a completely fresh broker and router come up ---

        await using var broker = new InProcessDomainEventBroker();
        var dispatch = new DomainEventDispatcher(
            broker, subscriptions,
            new InstanceLauncher(registry, instances),
            instances, registry, sink, sequencer,
            NullLogger<DomainEventDispatcher>.Instance);

        await dispatch.HandleAsync(new DomainEventDelivery
        {
            Message = new DomainEventMessage
            {
                MessageId = IdGenerator.NewId("msg"),
                Topic = "payment.settled",
                PayloadJson = """{"orderId":"ORD-RESTART","amount":99}""",
                CorrelationKey = "ORD-RESTART"
            },
            SubscriptionId = "transport"
        }, default);

        WorkflowInstance? resumed = await instances.GetAsync(instance.InstanceId, default);

        resumed!.Status.Should().Be(InstanceStatus.Dispatchable,
            "the wait was a durable row, so a restarted process can still resolve it");

        DomainEventSubscription? wait = await subscriptions
            .FindWaitAsync(instance.InstanceId, "await-settlement", default);

        wait!.DeliveredPayloadJson.Should().Contain("99");
    }

    [Fact]
    public async Task A_redelivered_trigger_message_does_not_start_a_second_instance()
    {
        var instances = new InMemoryInstanceStore();
        var subscriptions = new InMemoryDomainEventSubscriptionStore();
        var sequencer = new NotificationSequencer();
        INotificationSink sink = new DirectNotificationSink(new InMemoryEventStore());
        IWorkflowRegistry registry = new WorkflowRegistry([new ShipOrderWorkflow(new SideEffectLedger())]);

        await using var broker = new InProcessDomainEventBroker();
        var dispatch = new DomainEventDispatcher(
            broker, subscriptions,
            new InstanceLauncher(registry, instances),
            instances, registry, sink, sequencer,
            NullLogger<DomainEventDispatcher>.Instance);

        await dispatch.LoadTriggersAsync(default);

        var message = new DomainEventMessage
        {
            MessageId = IdGenerator.NewId("msg"),
            Topic = "orders.placed",
            PayloadJson = """{"orderId":"ORD-DUP"}"""
        };

        var delivery = new DomainEventDelivery { Message = message, SubscriptionId = "transport" };

        await dispatch.HandleAsync(delivery, default);
        await dispatch.HandleAsync(delivery, default);   // at-least-once: the transport redelivers

        Page<WorkflowInstance> page = await instances.QueryAsync(
            new InstanceQuery { WorkflowName = "ship-order" }, default);

        page.Items.Should().HaveCount(1, "the launcher's idempotency key absorbs a redelivery");
    }

    [Fact]
    public async Task An_unmatched_message_is_counted_rather_than_lost_silently()
    {
        var instances = new InMemoryInstanceStore();
        IWorkflowRegistry registry = new WorkflowRegistry([]);

        await using var broker = new InProcessDomainEventBroker();
        var dispatch = new DomainEventDispatcher(
            broker, new InMemoryDomainEventSubscriptionStore(),
            new InstanceLauncher(registry, instances),
            instances, registry, new DirectNotificationSink(new InMemoryEventStore()), new NotificationSequencer(),
            NullLogger<DomainEventDispatcher>.Instance);

        DeliveryResult result = await dispatch.HandleAsync(new DomainEventDelivery
        {
            Message = new DomainEventMessage
            {
                MessageId = IdGenerator.NewId("msg"),
                Topic = "nobody.listening",
                PayloadJson = "{}"
            },
            SubscriptionId = "transport"
        }, default);

        result.Outcome.Should().Be(DeliveryOutcome.Ack, "there is nothing to retry");
        dispatch.UnroutedCount.Should().Be(1, "an unrouted message must be visible from the outside");
    }
}

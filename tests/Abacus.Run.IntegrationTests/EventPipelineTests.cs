using Abacus.Run.Abstractions;
using Abacus.Run.Api;
using Abacus.Run.Core;
using Abacus.Run.Dispatch;
using Abacus.Run.EventBus;
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
        var broker = context.Services!.GetRequiredService<IEventBroker>();

        ExecutorBinding prepare = context.Node(new Prepare("prepare"));
        ExecutorBinding publish = context.Node(new PublishEventExecutor<OrderPlaced>(
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
public sealed class ShipOrderWorkflow : IWorkflowDefinition<OrderPlaced, ShipmentResult>, IEventTriggeredWorkflow
{
    private readonly SideEffectLedger _ledger;

    public ShipOrderWorkflow(SideEffectLedger ledger) => _ledger = ledger;

    public string Name => "ship-order";
    public string Version => "1.0.0";

    public IReadOnlyList<EventTrigger> Triggers =>
        [new EventTrigger { TopicFilter = "orders.placed" }];

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
        var subscriptions = context.Services!.GetRequiredService<IEventSubscriptionStore>();

        ExecutorBinding begin = context.Node(new Begin("begin"));
        ExecutorBinding wait = context.Node(new WaitForEventExecutor<AwaitPaymentContext, PaymentSettled>(
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
        => builder.UseSetting("Abacus:AuditRecords:ConnectionString", $"Data Source={_auditDatabasePath}");

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
        var broker = _fixture.Resolve<IEventBroker>();

        StartResult started = await launcher.StartAsync(
            "await-payment", null,
            new StartInstanceRequest { Context = Json(new AwaitPaymentContext("ORD-WAIT")) },
            "default", null, default);

        WorkflowInstance parked = await _fixture.WaitForStatusAsync(
            started.Instance!.InstanceId, InstanceStatus.AwaitingInput);

        parked.Status.Should().Be(InstanceStatus.AwaitingInput,
            "a wait must release its lease rather than block an execution slot");

        // Nothing is holding this in memory: the wait is a row, and so is the instance.
        await broker.PublishAsync(new BrokerMessage
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
        var broker = _fixture.Resolve<IEventBroker>();

        StartResult started = await launcher.StartAsync(
            "await-payment", null,
            new StartInstanceRequest { Context = Json(new AwaitPaymentContext("ORD-KEYED")) },
            "default", null, default);

        await _fixture.WaitForStatusAsync(started.Instance!.InstanceId, InstanceStatus.AwaitingInput);

        await broker.PublishAsync(new BrokerMessage
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
        var subscriptions = new InMemoryEventSubscriptionStore();
        var events = new InMemoryEventStore();
        var sequencer = new EventSequencer();
        IEventSink sink = new DirectEventSink(events);
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

        await subscriptions.RegisterAsync(new EventSubscription
        {
            SubscriptionId = IdGenerator.NewId("sub"),
            Kind = SubscriptionKind.Wait,
            TopicFilter = "payment.settled",
            CorrelationKey = "ORD-RESTART",
            InstanceId = instance.InstanceId,
            ExecutorId = "await-settlement"
        }, default);

        // --- the process dies here; a completely fresh broker and router come up ---

        await using var broker = new InProcessEventBroker();
        var dispatch = new BrokerDispatchService(
            broker, subscriptions,
            new InstanceLauncher(registry, instances),
            instances, registry, sink, sequencer,
            NullLogger<BrokerDispatchService>.Instance);

        await dispatch.HandleAsync(new EventDelivery
        {
            Message = new BrokerMessage
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

        EventSubscription? wait = await subscriptions
            .FindWaitAsync(instance.InstanceId, "await-settlement", default);

        wait!.DeliveredPayloadJson.Should().Contain("99");
    }

    [Fact]
    public async Task A_redelivered_trigger_message_does_not_start_a_second_instance()
    {
        var instances = new InMemoryInstanceStore();
        var subscriptions = new InMemoryEventSubscriptionStore();
        var sequencer = new EventSequencer();
        IEventSink sink = new DirectEventSink(new InMemoryEventStore());
        IWorkflowRegistry registry = new WorkflowRegistry([new ShipOrderWorkflow(new SideEffectLedger())]);

        await using var broker = new InProcessEventBroker();
        var dispatch = new BrokerDispatchService(
            broker, subscriptions,
            new InstanceLauncher(registry, instances),
            instances, registry, sink, sequencer,
            NullLogger<BrokerDispatchService>.Instance);

        await dispatch.LoadTriggersAsync(default);

        var message = new BrokerMessage
        {
            MessageId = IdGenerator.NewId("msg"),
            Topic = "orders.placed",
            PayloadJson = """{"orderId":"ORD-DUP"}"""
        };

        var delivery = new EventDelivery { Message = message, SubscriptionId = "transport" };

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

        await using var broker = new InProcessEventBroker();
        var dispatch = new BrokerDispatchService(
            broker, new InMemoryEventSubscriptionStore(),
            new InstanceLauncher(registry, instances),
            instances, registry, new DirectEventSink(new InMemoryEventStore()), new EventSequencer(),
            NullLogger<BrokerDispatchService>.Instance);

        DeliveryResult result = await dispatch.HandleAsync(new EventDelivery
        {
            Message = new BrokerMessage
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

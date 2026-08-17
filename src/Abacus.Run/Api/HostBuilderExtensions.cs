using System.Text.Json;
using Abacus.Run.Abstractions;
using Abacus.Run.Abstractions.Middleware;
using Abacus.Run.Core;
using Abacus.Run.Dispatch;
using Abacus.Run.Messaging;
using Abacus.Run.Notifications;
using Abacus.Run.Middlewares;
using Abacus.Run.Persistence;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Abacus.Run.Api;

public sealed class WorkflowHostBuilder
{
    public WorkflowHostBuilder(IServiceCollection services) => Services = services;

    public IServiceCollection Services { get; }

    public WorkflowHostBuilder AddWorkflow<TDefinition>() where TDefinition : class, IWorkflowDefinition
    {
        Services.AddSingleton<IWorkflowDefinition, TDefinition>();
        return this;
    }

    public WorkflowHostBuilder AddWorkflow(IWorkflowDefinition definition)
    {
        Services.AddSingleton(definition);
        return this;
    }

    public WorkflowHostBuilder AddWorkflowMiddleware<TMiddleware>() where TMiddleware : class, IWorkflowMiddleware
    {
        Services.AddSingleton<IWorkflowMiddleware, TMiddleware>();
        return this;
    }

    public WorkflowHostBuilder AddExecutorMiddleware<TMiddleware>() where TMiddleware : class, IExecutorMiddleware
    {
        Services.AddSingleton<IExecutorMiddleware, TMiddleware>();
        return this;
    }

    public WorkflowHostBuilder AddExecutorMiddleware(IExecutorMiddleware middleware)
    {
        Services.AddSingleton(middleware);
        return this;
    }

    /// <summary>OpenTelemetry, request/response logging, and LLM drift monitoring.</summary>
    public WorkflowHostBuilder AddBuiltInMiddleware()
    {
        Services.AddSingleton<IWorkflowMiddleware, OpenTelemetryWorkflowMiddleware>();
        Services.AddSingleton<IExecutorMiddleware, OpenTelemetryExecutorMiddleware>();
        Services.AddSingleton<IExecutorMiddleware, RequestResponseLoggingMiddleware>();

        Services.TryAddSingleton<IDriftBaselineStore>(_ => new InMemoryDriftBaselineStore());
        Services.TryAddSingleton<IDriftAlertSink, CollectingDriftAlertSink>();
        Services.AddSingleton<IExecutorMiddleware>(sp => new LlmDriftMiddleware(
            sp.GetRequiredService<IDriftBaselineStore>(),
            sp.GetRequiredService<IDriftAlertSink>(),
            sp.GetRequiredService<IOptions<WorkflowHostOptions>>().Value.Drift,
            sp.GetService<ILogger<LlmDriftMiddleware>>(),
            clock: sp.GetRequiredService<TimeProvider>()));

        return this;
    }

    /// <summary>Runs the dispatcher, the expiry sweepers and the broker router in this process.</summary>
    public WorkflowHostBuilder AddBackgroundServices()
    {
        Services.TryAddSingleton<LeaseManager>();
        Services.AddHostedService(sp => sp.GetRequiredService<DispatcherService>());
        Services.AddHostedService<ExpirySweeperService>();
        Services.AddSingleton<DomainEventDispatcher>();
        Services.AddHostedService(sp => sp.GetRequiredService<DomainEventDispatcher>());
        Services.AddHostedService<DomainEventWaitSweeper>();
        Services.AddHostedService<RetentionService>();
        Services.AddSingleton<DrainService>();
        Services.AddHostedService(sp => sp.GetRequiredService<DrainService>());
        return this;
    }

}

public static class HostBuilderExtensions
{
    /// <summary>
    /// Registers the host with in-memory persistence. Every store is behind an interface, so a
    /// relational implementation substitutes without touching the runtime.
    /// </summary>
    public static WorkflowHostBuilder AddWorkflowHost(
        this IServiceCollection services, IConfiguration? configuration = null, Action<WorkflowHostOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (configuration is not null)
        {
            services.Configure<WorkflowHostOptions>(configuration.GetSection(WorkflowHostOptions.SectionName));
        }
        if (configure is not null)
        {
            services.Configure(configure);
        }
        services.AddOptions<WorkflowHostOptions>();

        services.TryAddSingleton(TimeProvider.System);

        // Stores
        services.TryAddSingleton<InMemoryInstanceStore>();
        services.TryAddSingleton<IInstanceStore>(sp => sp.GetRequiredService<InMemoryInstanceStore>());
        services.TryAddSingleton<IEventStore, InMemoryEventStore>();
        services.TryAddSingleton<ILogStore, InMemoryLogStore>();
        services.TryAddSingleton<IApprovalStore, InMemoryApprovalStore>();
        services.TryAddSingleton<IGatePolicyStore, InMemoryGatePolicyStore>();
        services.TryAddSingleton<IAuditStore, InMemoryAuditStore>();
        services.TryAddSingleton<IAuditRecordStore, InMemoryAuditRecordStore>();
        services.TryAddSingleton<IBlobStore, InMemoryBlobStore>();
        services.TryAddSingleton<IDomainEventSubscriptionStore, InMemoryDomainEventSubscriptionStore>();

        services.TryAddSingleton(sp => new OverflowCheckpointStore(
            sp.GetRequiredService<IBlobStore>(),
            sp.GetRequiredService<IOptions<WorkflowHostOptions>>().Value.Checkpoint.InlineThresholdBytes,
            sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<ICheckpointStore<JsonElement>>(sp => sp.GetRequiredService<OverflowCheckpointStore>());
        services.TryAddSingleton<ICheckpointDescriber, CheckpointDescriber>();

        // Events
        services.TryAddSingleton<NotificationSequencer>();
        services.TryAddSingleton<IRedactionPolicy>(_ => RedactionPolicy.Default);
        services.TryAddSingleton<INotificationSink>(sp => new DirectNotificationSink(
            sp.GetRequiredService<IEventStore>(),
            sp.GetService<INotificationBus>(),
            sp.GetRequiredService<IRedactionPolicy>()));

        // Caching, as one substitution. The adapter supplies every cache-shaped capability — the
        // general store and the SSE backplane — so registering Abacus.Adapters.Cache.Redis (or
        // Memcached, or anything else) moves all of them together. In-memory by default, which is
        // correct for a single replica and needs no infrastructure at all.
        services.TryAddSingleton<InMemoryNotificationBus>();
        services.TryAddSingleton<ICacheAdapter>(sp => new InMemoryCacheAdapter(
            sp.GetRequiredService<InMemoryNotificationBus>(),
            sp.GetRequiredService<TimeProvider>()));

        // Both derive from the adapter rather than being registered independently, which is what
        // stops a deployment caching in one technology while streaming through another.
        services.TryAddSingleton<INotificationBus>(sp => sp.GetRequiredService<ICacheAdapter>().NotificationBackplane);
        services.TryAddSingleton<ICacheStore>(sp => sp.GetRequiredService<ICacheAdapter>().Store);

        // Messaging, as one substitution — the mirror of caching above. The adapter supplies every
        // messaging capability, so registering Abacus.Adapters.Messaging.RabbitMQ (or AWS, or
        // anything else) moves domain events and control signalling together. In-process by
        // default, which is correct for a single service.
        services.TryAddSingleton<InProcessDomainEventBroker>(sp => new InProcessDomainEventBroker(
            sp.GetService<ILogger<InProcessDomainEventBroker>>(),
            sp.GetRequiredService<TimeProvider>()));

        services.TryAddSingleton<IMessagingAdapter>(sp => new InProcessMessagingAdapter(
            sp.GetRequiredService<InProcessDomainEventBroker>(),
            sp.GetService<ILoggerFactory>(),
            sp.GetRequiredService<TimeProvider>()));

        services.TryAddSingleton<IDomainEventBroker>(sp => sp.GetRequiredService<IMessagingAdapter>().DomainEventBroker);
        services.TryAddSingleton<IControlChannel>(sp => sp.GetRequiredService<IMessagingAdapter>().ControlChannel);

        // Token prices, bound from Abacus:Llm:Pricing:<model>. Absent by default: a host that has
        // not been told its rates reports cost as unknown rather than as zero.
        services.TryAddSingleton<IModelPricing>(_ => new ModelPricing(
            configuration?.GetSection("Abacus:Llm:Pricing").GetChildren().ToDictionary(
                section => section.Key,
                section => new ModelPrice(
                    section.GetValue<decimal>("InputPerMillion"),
                    section.GetValue<decimal>("OutputPerMillion")),
                StringComparer.OrdinalIgnoreCase)));

        // Runtime
        services.TryAddSingleton<IWorkflowRegistry>(sp =>
        {
            var registry = new WorkflowRegistry(sp.GetServices<IWorkflowDefinition>());

            // A workflow declaring a notification name it could never legally emit should fail
            // startup, not surprise someone in production.
            foreach (WorkflowDescriptor descriptor in registry.All)
            {
                if (descriptor.Definition is INotifyingWorkflow notifying)
                {
                    notifying.Notifications.Validate(descriptor.Name);
                }
            }

            return registry;
        });
        services.TryAddSingleton<IWorkflowInspector>(sp => new WorkflowInspector(sp));
        services.TryAddSingleton<IGateConfigurationService>(sp => new GateConfigurationService(
            sp.GetRequiredService<IWorkflowRegistry>(),
            sp.GetRequiredService<IWorkflowInspector>(),
            sp.GetRequiredService<IGatePolicyStore>(),
            sp.GetService<IAuditStore>(),
            sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton(sp => new MiddlewarePipelineFactory(
            sp.GetServices<IExecutorMiddleware>(), sp.GetServices<IWorkflowMiddleware>()));
        services.TryAddSingleton<IApprovalService, ApprovalCoordinator>();
        services.TryAddSingleton<IApprovalCoordinator>(sp => sp.GetRequiredService<IApprovalService>());
        services.TryAddSingleton<IInstanceControl, InstanceControlService>();
        services.TryAddSingleton<IWorkflowRunnerFactory, WorkflowRunnerFactory>();
        services.TryAddSingleton<IInstanceLauncher, InstanceLauncher>();

        services.TryAddSingleton(sp =>
        {
            WorkflowHostOptions options = sp.GetRequiredService<IOptions<WorkflowHostOptions>>().Value;
            return new ConcurrencyLimiter(options.MaxConcurrentInstances, options.PerWorkflowConcurrency);
        });
        services.TryAddSingleton<DispatcherService>();

        return new WorkflowHostBuilder(services);
    }
}

internal sealed class CheckpointDescriber : ICheckpointDescriber
{
    private readonly OverflowCheckpointStore _store;

    public CheckpointDescriber(OverflowCheckpointStore store) => _store = store;

    public bool Exists(string sessionId, string checkpointId)
        => _store.Describe(sessionId).Any(c => c.CheckpointId == checkpointId);

    public IReadOnlyList<(string CheckpointId, int SizeBytes, DateTimeOffset CommittedAt)> Describe(string sessionId)
        => _store.Describe(sessionId)
            .Select(c => (c.CheckpointId, c.SizeBytes, c.CommittedAt))
            .ToArray();
}

internal sealed class WorkflowRunnerFactory : IWorkflowRunnerFactory
{
    private readonly IServiceProvider _services;

    public WorkflowRunnerFactory(IServiceProvider services) => _services = services;

    public WorkflowRunner Create(WorkflowInstance instance) => new(new WorkflowRunnerDependencies
    {
        Registry = _services.GetRequiredService<IWorkflowRegistry>(),
        Instances = _services.GetRequiredService<IInstanceStore>(),
        Events = _services.GetRequiredService<INotificationSink>(),
        Sequencer = _services.GetRequiredService<NotificationSequencer>(),
        Pipelines = _services.GetRequiredService<MiddlewarePipelineFactory>(),
        Checkpoints = _services.GetRequiredService<ICheckpointStore<JsonElement>>(),
        Approvals = _services.GetRequiredService<IApprovalStore>(),
        ApprovalService = _services.GetRequiredService<IApprovalService>(),
        GatePolicies = _services.GetRequiredService<IGatePolicyStore>(),
        Audit = _services.GetRequiredService<IAuditStore>(),
        AuditRecords = _services.GetRequiredService<IAuditRecordStore>(),
        EventSubscriptions = _services.GetService<IDomainEventSubscriptionStore>(),
        Logs = _services.GetRequiredService<ILogStore>(),
        Services = _services,
        Options = _services.GetRequiredService<IOptions<WorkflowHostOptions>>().Value,
        Clock = _services.GetRequiredService<TimeProvider>(),
        Logger = _services.GetRequiredService<ILoggerFactory>().CreateLogger<WorkflowRunner>()
    });
}

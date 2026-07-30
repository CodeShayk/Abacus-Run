using System.Text.Json;
using Abacus.Run.Abstractions;
using Abacus.Run.Abstractions.Middleware;
using Abacus.Run.Core;
using Abacus.Run.Middleware;
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

    /// <summary>Runs the dispatcher and the expiry sweeper in this process.</summary>
    public WorkflowHostBuilder AddBackgroundServices()
    {
        Services.AddHostedService(sp => sp.GetRequiredService<DispatcherService>());
        Services.AddHostedService<ExpirySweeperService>();
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
        services.TryAddSingleton<IBlobStore, InMemoryBlobStore>();

        services.TryAddSingleton(sp => new OverflowCheckpointStore(
            sp.GetRequiredService<IBlobStore>(),
            sp.GetRequiredService<IOptions<WorkflowHostOptions>>().Value.Checkpoint.InlineThresholdBytes,
            sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<ICheckpointStore<JsonElement>>(sp => sp.GetRequiredService<OverflowCheckpointStore>());
        services.TryAddSingleton<ICheckpointDescriber, CheckpointDescriber>();

        // Events
        services.TryAddSingleton<EventSequencer>();
        services.TryAddSingleton<IRedactionPolicy>(_ => RedactionPolicy.Default);
        services.TryAddSingleton<IEventSink>(sp => new DirectEventSink(
            sp.GetRequiredService<IEventStore>(),
            sp.GetService<IEventBus>(),
            sp.GetRequiredService<IRedactionPolicy>()));

        // Runtime
        services.TryAddSingleton<IWorkflowRegistry>(sp =>
            new WorkflowRegistry(sp.GetServices<IWorkflowDefinition>()));
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
        Events = _services.GetRequiredService<IEventSink>(),
        Sequencer = _services.GetRequiredService<EventSequencer>(),
        Pipelines = _services.GetRequiredService<MiddlewarePipelineFactory>(),
        Checkpoints = _services.GetRequiredService<ICheckpointStore<JsonElement>>(),
        Approvals = _services.GetRequiredService<IApprovalStore>(),
        ApprovalService = _services.GetRequiredService<IApprovalService>(),
        GatePolicies = _services.GetRequiredService<IGatePolicyStore>(),
        Audit = _services.GetRequiredService<IAuditStore>(),
        Logs = _services.GetRequiredService<ILogStore>(),
        Services = _services,
        Options = _services.GetRequiredService<IOptions<WorkflowHostOptions>>().Value,
        Clock = _services.GetRequiredService<TimeProvider>(),
        Logger = _services.GetRequiredService<ILoggerFactory>().CreateLogger<WorkflowRunner>()
    });
}

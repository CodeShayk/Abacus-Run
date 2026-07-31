using Abacus.Run.Api;
using Abacus.Run.Service.ControlPlane;
using Abacus.Run.EventBus;
using Abacus.Run.Service.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Abacus.Run.Service;

public sealed class AbacusServiceOptions
{
    /// <summary>When set, SQL Server replaces the framework's in-memory stores.</summary>
    public string? SqlServerConnectionString { get; set; }

    /// <summary>When set, Redis Streams replaces the framework's in-process event bus.</summary>
    public string? RedisConnectionString { get; set; }

    public bool EnsureDatabaseCreated { get; set; }

    public int RedisMaxStreamLength { get; set; } = 10_000;
}

/// <summary>
/// Composition root for the host: turns on the Abacus.Run framework, then substitutes the concrete
/// infrastructure this deployment uses.
/// </summary>
/// <remarks>
/// Everything reusable — API endpoints, SSE, dispatcher, approvals, control plane — comes from the
/// <c>Abacus.Run</c> library. This project contributes only environment-specific implementations
/// (SQL Server, Redis) and the wiring that selects them. With neither connection string configured
/// the host runs entirely on the library's in-memory defaults, which is what keeps local dev and the
/// integration tests dependency-free.
/// </remarks>
public static class AbacusServiceCollectionExtensions
{
    public static WorkflowHostBuilder AddAbacus(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<AbacusServiceOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new AbacusServiceOptions
        {
            SqlServerConnectionString = configuration["Abacus:SqlServer:ConnectionString"]
                ?? configuration.GetConnectionString("Abacus"),
            RedisConnectionString = configuration["Abacus:Redis:ConnectionString"],
            EnsureDatabaseCreated = configuration.GetValue("Abacus:SqlServer:EnsureDatabaseCreated", false),
            RedisMaxStreamLength = configuration.GetValue("Abacus:Redis:MaxStreamLength", 10_000)
        };
        configure?.Invoke(options);

        // 1. The framework, with its in-memory defaults.
        WorkflowHostBuilder host = services
            .AddWorkflowHost(configuration)
            .AddBuiltInMiddleware()
            .AddBackgroundServices();

        // The control plane is part of this deployable, not the framework.
        services.AddControlPlane();

        // 2. Concrete infrastructure, when configured, displacing those defaults.
        if (!string.IsNullOrWhiteSpace(options.SqlServerConnectionString))
        {
            services.AddSqlServerStores(options.SqlServerConnectionString, options.EnsureDatabaseCreated);
        }

        if (!string.IsNullOrWhiteSpace(options.RedisConnectionString))
        {
            services.AddRedisEventBus(options.RedisConnectionString, options.RedisMaxStreamLength);
        }

        return host;
    }
}

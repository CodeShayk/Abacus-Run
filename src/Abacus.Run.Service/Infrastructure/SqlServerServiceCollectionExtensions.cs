using System.Text.Json;
using Abacus.Run.Api;
using Abacus.Run.Core;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Abacus.Run.Service.Infrastructure;

/// <summary>
/// Registers the SQL Server implementations of the framework's store contracts.
/// </summary>
/// <remarks>
/// The framework library (<c>Abacus.Run</c>) ships in-memory defaults so it runs standalone for
/// local development and tests. Production storage is infrastructure-specific and therefore lives
/// here, in the host, and is swapped in at startup.
/// </remarks>
public static class SqlServerServiceCollectionExtensions
{
    public static IServiceCollection AddSqlServerStores(
        this IServiceCollection services,
        string connectionString,
        bool ensureDatabaseCreated = false)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddDbContextFactory<AbacusDbContext>(builder => builder.UseSqlServer(connectionString));

        // Displace the library's in-memory defaults. RemoveAll before Add so a re-registration is
        // idempotent and the last caller wins deterministically.
        Replace<IInstanceStore, SqlServerInstanceStore>(services);
        Replace<IEventStore, SqlServerEventStore>(services);
        Replace<ILogStore, SqlServerLogStore>(services);
        Replace<IApprovalStore, SqlServerApprovalStore>(services);
        Replace<IGatePolicyStore, SqlServerGatePolicyStore>(services);
        Replace<IAuditStore, SqlServerAuditStore>(services);
        Replace<IBlobStore, SqlServerBlobStore>(services);
        Replace<ICheckpointStore<JsonElement>, SqlServerCheckpointStore>(services);
        Replace<ICheckpointDescriber, SqlServerCheckpointDescriber>(services);

        if (ensureDatabaseCreated)
        {
            services.AddHostedService<AbacusDatabaseInitializer>();
        }

        return services;
    }

    private static void Replace<TService, TImplementation>(IServiceCollection services)
        where TService : class
        where TImplementation : class, TService
    {
        services.RemoveAll<TService>();
        services.AddSingleton<TService, TImplementation>();
    }
}

/// <summary>Creates the schema on startup. Intended for dev and test environments only.</summary>
public sealed class AbacusDatabaseInitializer(IDbContextFactory<AbacusDbContext> factory) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using AbacusDbContext db = await factory.CreateDbContextAsync(cancellationToken);
        await db.Database.EnsureCreatedAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

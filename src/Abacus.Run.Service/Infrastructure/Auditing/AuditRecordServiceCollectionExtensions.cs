using Abacus.Run.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Abacus.Run.Service.Infrastructure.Auditing;

public sealed class AuditRecordOptions
{
    public const string SectionName = "Abacus:AuditRecords";

    /// <summary>SQLite connection string for the generic audit-record store.</summary>
    public string ConnectionString { get; set; } = "Data Source=./data/abacus-audit.db";
}

/// <summary>
/// Replaces the framework's in-memory audit-record store with durable SQLite storage. The store is
/// generic: every workflow that declares an audit record writes here, and the record's meaning stays
/// with the definition that declared it.
/// </summary>
public static class AuditRecordServiceCollectionExtensions
{
    public static IServiceCollection AddSqliteAuditRecords(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<AuditRecordOptions>()
            .Bind(configuration.GetSection(AuditRecordOptions.SectionName));

        services.AddDbContextFactory<AuditRecordDbContext>((sp, builder) =>
        {
            string connectionString = sp.GetRequiredService<IOptions<AuditRecordOptions>>().Value.ConnectionString;
            EnsureDataDirectoryExists(connectionString);
            builder.UseSqlite(connectionString);
        });

        // Displaces the framework default registered by AddAbacus.
        services.RemoveAll<IAuditRecordStore>();
        services.AddSingleton<IAuditRecordStore, SqliteAuditRecordStore>();

        services.AddHostedService<AuditRecordDatabaseInitializer>();

        return services;
    }

    private static void EnsureDataDirectoryExists(string connectionString)
    {
        const string key = "Data Source=";
        int i = connectionString.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return;

        string tail = connectionString[(i + key.Length)..];
        int end = tail.IndexOf(';');
        string path = (end < 0 ? tail : tail[..end]).Trim().Trim('"');

        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }
}

/// <summary>Applies the audit-record migrations at startup.</summary>
public sealed class AuditRecordDatabaseInitializer(IDbContextFactory<AuditRecordDbContext> factory) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using AuditRecordDbContext db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

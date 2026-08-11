using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Abacus.Run.Service.Infrastructure.Auditing;

/// <summary>
/// Root aggregate row. Deliberately workflow-agnostic: <c>RootKind</c> and <c>RootKey</c> carry
/// whatever the workflow declared, so a new workflow needs no schema change.
/// </summary>
public sealed class AuditRecordRootRow
{
    [Key, MaxLength(128)]
    public string InstanceId { get; set; } = default!;

    [Required, MaxLength(128)]
    public string WorkflowName { get; set; } = default!;

    [Required, MaxLength(32)]
    public string WorkflowVersion { get; set; } = default!;

    [Required, MaxLength(64)]
    public string RootKind { get; set; } = default!;

    [Required, MaxLength(256)]
    public string RootKey { get; set; } = default!;

    [Required, MaxLength(32)]
    public string Status { get; set; } = default!;

    public string AttributesJson { get; set; } = "{}";

    public DateTimeOffset OpenedUtc { get; set; }
    public DateTimeOffset? ClosedUtc { get; set; }

    public ICollection<AuditRecordEntryRow> Entries { get; set; } = [];
}

/// <summary>One child construct. The payload is opaque JSON — its shape is the workflow's business.</summary>
public sealed class AuditRecordEntryRow
{
    [Key]
    public Guid Id { get; set; }

    [Required, MaxLength(128)]
    public string InstanceId { get; set; } = default!;
    public AuditRecordRootRow Root { get; set; } = default!;

    [Required, MaxLength(64)]
    public string SectionKind { get; set; } = default!;

    [MaxLength(128)]
    public string? Key { get; set; }

    public string PayloadJson { get; set; } = "null";

    public int Sequence { get; set; }

    public DateTimeOffset RecordedUtc { get; set; }
}

/// <summary>
/// Generic storage for workflow audit records. Owned by the host rather than the framework: the
/// framework defines the contract, a deployment chooses what it is written to.
/// </summary>
public sealed class AuditRecordDbContext(DbContextOptions<AuditRecordDbContext> options) : DbContext(options)
{
    public DbSet<AuditRecordRootRow> AuditRecords => Set<AuditRecordRootRow>();
    public DbSet<AuditRecordEntryRow> AuditRecordEntries => Set<AuditRecordEntryRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // SQLite has no native DateTimeOffset — ORDER BY on the default string mapping throws.
        var dtoConverter = new DateTimeOffsetToBinaryConverter();
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                if (property.ClrType == typeof(DateTimeOffset) || property.ClrType == typeof(DateTimeOffset?))
                {
                    property.SetValueConverter(dtoConverter);
                }
            }
        }

        modelBuilder.Entity<AuditRecordRootRow>(b =>
        {
            b.HasIndex(x => new { x.WorkflowName, x.RootKey });
            b.HasIndex(x => new { x.WorkflowName, x.Status });
        });

        modelBuilder.Entity<AuditRecordEntryRow>(b =>
        {
            b.HasOne(x => x.Root)
                .WithMany(x => x.Entries)
                .HasForeignKey(x => x.InstanceId)
                .OnDelete(DeleteBehavior.Cascade);

            // One entry per (instance, kind, key): a retried executor corrects its record instead of
            // appending a second, contradictory one.
            b.HasIndex(x => new { x.InstanceId, x.SectionKind, x.Key }).IsUnique();
        });
    }
}

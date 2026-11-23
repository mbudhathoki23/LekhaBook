using LekhaBook.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;

namespace LekhaBook.Data.Sqlite;

/// <summary>Device database: the shared model plus device-only sync tables.</summary>
public sealed class SqliteDbContext(DbContextOptions<SqliteDbContext> options) : LekhaBookDbContext(options)
{
    public DbSet<OutboxItem> Outbox => Set<OutboxItem>();

    public DbSet<LocalAttachmentFile> LocalAttachmentFiles => Set<LocalAttachmentFile>();

    public DbSet<SyncCheckpoint> SyncCheckpoints => Set<SyncCheckpoint>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<OutboxItem>(e =>
        {
            e.ToTable("Outbox");
            e.HasKey(x => x.Id);
            e.Property(x => x.EntityType).HasMaxLength(64);
            e.Property(x => x.Operation).HasMaxLength(16);
            e.Property(x => x.LastError).HasMaxLength(1000);
            e.HasIndex(x => new { x.CompanyId, x.Id });
        });

        modelBuilder.Entity<LocalAttachmentFile>(e =>
        {
            e.ToTable("LocalAttachmentFiles");
            e.HasKey(x => x.AttachmentId);
            e.Property(x => x.LocalPath).HasMaxLength(500);
            e.Property(x => x.UploadStatus).HasConversion<string>().HasMaxLength(16);
        });

        modelBuilder.Entity<SyncCheckpoint>(e =>
        {
            e.ToTable("SyncCheckpoints");
            e.HasKey(x => x.CompanyId);
        });
    }
}

/// <summary>
/// A local change waiting to be pushed. Written in the same local transaction as the change itself
/// (AGENT_PLAN § 6.2). Id order is push order.
/// </summary>
public sealed class OutboxItem
{
    public long Id { get; set; }

    public Guid CompanyId { get; set; }

    public required string EntityType { get; set; }

    public Guid EntityId { get; set; }

    /// <summary>Upsert or Delete.</summary>
    public required string Operation { get; set; }

    public required string PayloadJson { get; set; }

    /// <summary>Server version the change was based on, for conflict detection.</summary>
    public long BaseVersion { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public int Attempts { get; set; }

    public string? LastError { get; set; }
}

public enum UploadStatus
{
    Pending,
    Uploading,
    Uploaded,
    Failed,
}

/// <summary>Where an attachment's file is on this device, and whether it has been uploaded.</summary>
public sealed class LocalAttachmentFile
{
    public Guid AttachmentId { get; set; }

    public required string LocalPath { get; set; }

    public UploadStatus UploadStatus { get; set; } = UploadStatus.Pending;
}

/// <summary>Highest server version this device has pulled for a company.</summary>
public sealed class SyncCheckpoint
{
    public Guid CompanyId { get; set; }

    public long LastPulledVersion { get; set; }
}

public static class SqliteServiceCollectionExtensions
{
    /// <summary>
    /// Registers a context factory: Blazor components live longer than a unit of work, so each
    /// operation creates and disposes its own context.
    /// </summary>
    public static IServiceCollection AddLekhaBookSqlite(this IServiceCollection services, string databasePath) =>
        services.AddDbContextFactory<SqliteDbContext>(o => o.UseSqlite($"Data Source={databasePath}"));
}

/// <summary>Used by `dotnet ef migrations add`; doesn't need a real database.</summary>
public sealed class SqliteDesignTimeFactory : IDesignTimeDbContextFactory<SqliteDbContext>
{
    public SqliteDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<SqliteDbContext>().UseSqlite("Data Source=lekhabook-design.db").Options);
}

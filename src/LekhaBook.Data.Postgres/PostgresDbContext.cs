using LekhaBook.Data;
using LekhaBook.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;

namespace LekhaBook.Data.Postgres;

/// <summary>Server database: the shared model plus server-only sync bookkeeping.</summary>
public sealed class PostgresDbContext(DbContextOptions<PostgresDbContext> options) : LekhaBookDbContext(options)
{
    public DbSet<CompanySyncState> CompanySyncStates => Set<CompanySyncState>();

    public DbSet<CompanyMember> CompanyMembers => Set<CompanyMember>();

    public DbSet<Invitation> Invitations => Set<Invitation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<CompanySyncState>(e =>
        {
            e.ToTable("CompanySyncStates");
            e.HasKey(x => x.CompanyId);
        });

        modelBuilder.Entity<CompanyMember>(e =>
        {
            e.ToTable("CompanyMembers");
            e.HasKey(x => x.Id);
            e.Property(x => x.DisplayName).HasMaxLength(200);
            e.Property(x => x.CustomPermissions); // text[] on PostgreSQL
            e.HasOne<Company>().WithMany().HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.CompanyId, x.UserId }).IsUnique();
            e.HasIndex(x => x.UserId);
            // Exactly one Owner per company.
            e.HasIndex(x => x.CompanyId).IsUnique().HasFilter("\"Role\" = 'Owner'").HasDatabaseName("IX_CompanyMembers_OneOwner");
        });

        modelBuilder.Entity<Invitation>(e =>
        {
            e.ToTable("Invitations");
            e.HasKey(x => x.Id);
            e.Property(x => x.NormalizedEmail).HasMaxLength(256);
            e.Property(x => x.DisplayName).HasMaxLength(200);
            e.Property(x => x.TokenHash).HasMaxLength(64);
            e.HasOne<Company>().WithMany().HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => new { x.CompanyId, x.NormalizedEmail });
        });
    }
}

/// <summary>
/// Per-company version counter. A push takes this row with SELECT … FOR UPDATE inside its
/// transaction, so versions become visible in commit order and pulls never skip a row (AGENT_PLAN § 6.2).
/// </summary>
public sealed class CompanySyncState
{
    public Guid CompanyId { get; set; }

    public long LastVersion { get; set; }
}

public static class PostgresServiceCollectionExtensions
{
    /// <summary>Registers the books context and the Identity context on the same database.</summary>
    public static IServiceCollection AddLekhaBookPostgres(this IServiceCollection services, string connectionString) =>
        services.AddLekhaBookPostgres(_ => connectionString);

    /// <summary>
    /// Same, but the connection string is resolved when a context is first created, so configuration
    /// added late (tests, hosting overrides) is honoured.
    /// </summary>
    public static IServiceCollection AddLekhaBookPostgres(this IServiceCollection services, Func<IServiceProvider, string> connectionString) =>
        services
            .AddDbContext<PostgresDbContext>((sp, o) => o.UseNpgsql(connectionString(sp)))
            .AddDbContext<AuthDbContext>((sp, o) => o.UseNpgsql(
                connectionString(sp), n => n.MigrationsHistoryTable(AuthDbContext.MigrationsHistoryTable)));
}

/// <summary>
/// Used by `dotnet ef migrations add`. Creating migrations doesn't connect; `database update`
/// uses the LEKHABOOK_PG environment variable.
/// </summary>
public sealed class PostgresDesignTimeFactory : IDesignTimeDbContextFactory<PostgresDbContext>
{
    public PostgresDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("LEKHABOOK_PG")
            ?? "Host=localhost;Database=lekhabook;Username=lekhabook";
        var options = new DbContextOptionsBuilder<PostgresDbContext>().UseNpgsql(connectionString).Options;
        return new PostgresDbContext(options);
    }
}

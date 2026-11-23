using LekhaBook.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;

namespace LekhaBook.Data.Postgres;

/// <summary>Server database: the shared model plus server-only sync bookkeeping.</summary>
public sealed class PostgresDbContext(DbContextOptions<PostgresDbContext> options) : LekhaBookDbContext(options)
{
    public DbSet<CompanySyncState> CompanySyncStates => Set<CompanySyncState>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<CompanySyncState>(e =>
        {
            e.ToTable("CompanySyncStates");
            e.HasKey(x => x.CompanyId);
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
    public static IServiceCollection AddLekhaBookPostgres(this IServiceCollection services, string connectionString) =>
        services.AddDbContext<PostgresDbContext>(o => o.UseNpgsql(connectionString));
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

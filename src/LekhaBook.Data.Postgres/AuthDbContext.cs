using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace LekhaBook.Data.Postgres;

/// <summary>A person who can sign in. Company access lives in <c>CompanyMembers</c>, not in Identity roles.</summary>
public sealed class AppUser : IdentityUser<Guid>
{
    public AppUser()
    {
        Id = Guid.CreateVersion7();
    }

    public string? DisplayName { get; set; }
}

/// <summary>
/// ASP.NET Core Identity tables (users, logins, tokens). Same database as <see cref="PostgresDbContext"/>,
/// separate context and migrations history so auth can change independently of the books.
/// </summary>
public sealed class AuthDbContext(DbContextOptions<AuthDbContext> options)
    : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>(options)
{
    public const string MigrationsHistoryTable = "__EFMigrationsHistory_Auth";

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<AppUser>().Property(u => u.DisplayName).HasMaxLength(200);
    }
}

public sealed class AuthDesignTimeFactory : IDesignTimeDbContextFactory<AuthDbContext>
{
    public AuthDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("LEKHABOOK_PG")
            ?? "Host=localhost;Database=lekhabook;Username=lekhabook";
        return new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>()
            .UseNpgsql(connectionString, o => o.MigrationsHistoryTable(AuthDbContext.MigrationsHistoryTable))
            .Options);
    }
}

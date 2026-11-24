using LekhaBook.Data.Postgres;
using LekhaBook.Testing;
using Microsoft.EntityFrameworkCore;

namespace LekhaBook.Data.Tests;

public sealed class PostgresDatabaseTests
{
    /// <summary>
    /// Creates a brand-new database, applies all migrations (books + auth), runs the shared checks, then drops it.
    /// This is the phase 3 exit check "migrations apply to a fresh PostgreSQL".
    /// </summary>
    [PostgresFact]
    public async Task Migrations_apply_to_a_fresh_database_and_balances_match()
    {
        var connectionString = TestPostgres.NewDatabase(out var databaseName);
        var options = new DbContextOptionsBuilder<PostgresDbContext>().UseNpgsql(connectionString).Options;
        var authOptions = new DbContextOptionsBuilder<AuthDbContext>()
            .UseNpgsql(connectionString, o => o.MigrationsHistoryTable(AuthDbContext.MigrationsHistoryTable)).Options;

        try
        {
            await using (var db = new PostgresDbContext(options))
            {
                await db.Database.MigrateAsync(); // creates the database, then the schema
                Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            }

            await using (var auth = new AuthDbContext(authOptions))
            {
                await auth.Database.MigrateAsync();
                Assert.Empty(await auth.Database.GetPendingMigrationsAsync());
            }

            await DatabaseChecks.RunAllAsync(() => new PostgresDbContext(options));
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(databaseName);
        }
    }
}

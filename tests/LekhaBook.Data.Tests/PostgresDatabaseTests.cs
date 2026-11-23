using LekhaBook.Data.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace LekhaBook.Data.Tests;

/// <summary>
/// Where to find a PostgreSQL server for tests: the LEKHABOOK_TEST_PG environment variable, or the
/// ConnectionStrings:LekhaBook user secret shared with LekhaBook.Api. The role needs CREATEDB.
/// </summary>
internal static class TestPostgres
{
    public static string? ConnectionString { get; } =
        Environment.GetEnvironmentVariable("LEKHABOOK_TEST_PG") is { Length: > 0 } fromEnv
            ? fromEnv
            : new ConfigurationBuilder().AddUserSecrets(typeof(TestPostgres).Assembly, optional: true).Build()
                .GetConnectionString("LekhaBook");
}

/// <summary>A test that is skipped, with a reason, when no PostgreSQL server is configured.</summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (TestPostgres.ConnectionString is null)
        {
            Skip = "No PostgreSQL configured: set LEKHABOOK_TEST_PG or the ConnectionStrings:LekhaBook user secret (see README).";
        }
    }
}

public sealed class PostgresDatabaseTests
{
    /// <summary>
    /// Creates a brand-new database, applies all migrations, runs the shared checks, then drops it.
    /// This is the phase 3 exit check "migrations apply to a fresh PostgreSQL".
    /// </summary>
    [PostgresFact]
    public async Task Migrations_apply_to_a_fresh_database_and_balances_match()
    {
        var server = new NpgsqlConnectionStringBuilder(TestPostgres.ConnectionString);
        var databaseName = $"lekhabook_test_{Guid.NewGuid():N}";
        var testDb = new NpgsqlConnectionStringBuilder(server.ConnectionString) { Database = databaseName };
        var options = new DbContextOptionsBuilder<PostgresDbContext>().UseNpgsql(testDb.ConnectionString).Options;

        try
        {
            await using (var db = new PostgresDbContext(options))
            {
                await db.Database.MigrateAsync(); // creates the database, then the schema
                Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            }

            await DatabaseChecks.RunAllAsync(() => new PostgresDbContext(options));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            // Connect to the maintenance database: the configured one (e.g. "lekhabook") may not exist yet.
            var maintenance = new NpgsqlConnectionStringBuilder(server.ConnectionString) { Database = "postgres" };
            await using var admin = new NpgsqlConnection(maintenance.ConnectionString);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}

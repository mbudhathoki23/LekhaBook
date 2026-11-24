using Microsoft.Extensions.Configuration;
using Npgsql;

namespace LekhaBook.Testing;

// Linked into every test project that needs PostgreSQL.

/// <summary>
/// Where to find a PostgreSQL server for tests: the LEKHABOOK_TEST_PG environment variable
/// (CI, or scripts/test-postgres.ps1), else the ConnectionStrings:LekhaBook user secret.
/// The role needs CREATEDB: each test fixture creates and drops its own database.
/// </summary>
internal static class TestPostgres
{
    public static string? ServerConnectionString { get; } =
        Environment.GetEnvironmentVariable("LEKHABOOK_TEST_PG") is { Length: > 0 } fromEnv
            ? fromEnv
            : new ConfigurationBuilder().AddUserSecrets(typeof(TestPostgres).Assembly, optional: true).Build()
                .GetConnectionString("LekhaBook");

    public const string SkipReason =
        "No PostgreSQL configured: set LEKHABOOK_TEST_PG (scripts/test-postgres.ps1) or the ConnectionStrings:LekhaBook user secret.";

    /// <summary>Connection string for a new, uniquely named database on the test server (not created yet).</summary>
    public static string NewDatabase(out string databaseName)
    {
        databaseName = $"lekhabook_test_{Guid.NewGuid():N}";
        return new NpgsqlConnectionStringBuilder(ServerConnectionString) { Database = databaseName }.ConnectionString;
    }

    public static async Task DropDatabaseAsync(string databaseName)
    {
        NpgsqlConnection.ClearAllPools();
        // Connect to the maintenance database: the configured one may not exist.
        var admin = new NpgsqlConnectionStringBuilder(ServerConnectionString) { Database = "postgres" };
        await using var connection = new NpgsqlConnection(admin.ConnectionString);
        await connection.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", connection);
        await drop.ExecuteNonQueryAsync();
    }
}

/// <summary>A test that is skipped, with a reason, when no PostgreSQL server is configured.</summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (TestPostgres.ServerConnectionString is null)
        {
            Skip = TestPostgres.SkipReason;
        }
    }
}

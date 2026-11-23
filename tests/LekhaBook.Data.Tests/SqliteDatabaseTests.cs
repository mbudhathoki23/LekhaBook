using LekhaBook.Data.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LekhaBook.Data.Tests;

public sealed class SqliteDatabaseTests : IAsyncLifetime
{
    // An in-memory SQLite database lives as long as its connection stays open.
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    private SqliteDbContext NewContext() =>
        new(new DbContextOptionsBuilder<SqliteDbContext>().UseSqlite(_connection).Options);

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        await using var db = NewContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task Migrations_create_every_table()
    {
        await using var db = NewContext();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        var tables = await db.Database
            .SqlQueryRaw<string>("SELECT name AS \"Value\" FROM sqlite_master WHERE type = 'table'")
            .ToListAsync();
        Assert.Superset(
            new HashSet<string>
            {
                "Companies", "Accounts", "PartyCategories", "Transactions", "Attachments", "AuditLogs",
                "Outbox", "LocalAttachmentFiles", "SyncCheckpoints",
            },
            tables.ToHashSet());
    }

    [Fact]
    public Task Balances_and_values_match_the_domain() => DatabaseChecks.RunAllAsync(NewContext);

    [Fact]
    public async Task Enums_are_stored_as_text()
    {
        await using (var seed = NewContext())
        {
            await SampleBooks.SeedAsync(seed);
        }

        await using var db = NewContext();
        var types = await db.Database
            .SqlQueryRaw<string>("SELECT DISTINCT \"Type\" AS \"Value\" FROM \"Transactions\"")
            .ToListAsync();
        Assert.Contains("PaymentIn", types);
        Assert.Contains("TransferMoney", types);
    }
}

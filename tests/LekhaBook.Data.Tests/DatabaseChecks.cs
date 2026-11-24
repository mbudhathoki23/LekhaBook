using LekhaBook.Data;
using LekhaBook.Domain;
using Microsoft.EntityFrameworkCore;

namespace LekhaBook.Data.Tests;

/// <summary>Checks shared by the SQLite and PostgreSQL tests, run against a freshly migrated database.</summary>
internal static class DatabaseChecks
{
    public static async Task RunAllAsync(Func<LekhaBookDbContext> newContext)
    {
        SampleBooks.Result books;
        await using (var db = newContext())
        {
            books = await SampleBooks.SeedAsync(db);
        }

        await using (var db = newContext())
        {
            await SqlBalancesMatchDomainAsync(db, books);
        }

        await using (var db = newContext())
        {
            await LedgersMatchDomainAsync(db, books);
        }

        await using (var db = newContext())
        {
            await ValuesRoundTripAsync(db, books);
        }
    }

    /// <summary>Statements and single-account balances (used by the party and account screens) agree with the domain.</summary>
    private static async Task LedgersMatchDomainAsync(LekhaBookDbContext db, SampleBooks.Result books)
    {
        foreach (var account in books.Accounts.Where(a => a.Type is AccountType.Party or AccountType.Cash or AccountType.Bank))
        {
            var ledger = await db.GetLedgerAsync(books.Company.Id, account.Id);
            var expected = Balances.Ledger(account, books.Transactions);

            Assert.NotNull(ledger);
            Assert.Equal(expected.Select(l => (l.Transaction.Id, l.Balance)), ledger.Lines.Select(l => (l.Transaction.Id, l.Balance)));
            Assert.Equal(Balances.Of(account, books.Transactions), ledger.ClosingBalance);
            Assert.Equal(ledger.ClosingBalance, await db.GetBalanceAsync(account));
            Assert.All(ledger.Lines, l => Assert.NotNull(ledger.OtherSide(l.Transaction)));
        }

        // Another company's id finds nothing.
        Assert.Null(await db.GetLedgerAsync(Guid.CreateVersion7(), books.Named("Cash").Id));
        Assert.True(await db.IsUsedByTransactionsAsync(books.Company.Id, books.Named("Cash").Id));
    }

    private static async Task SqlBalancesMatchDomainAsync(LekhaBookDbContext db, SampleBooks.Result books)
    {
        var fromDatabase = await db.GetBalancesAsync(books.Company.Id);
        var fromDomain = Balances.All(books.Accounts, books.Transactions);

        Assert.Equal(fromDomain.Count, fromDatabase.Count);
        foreach (var (accountId, expected) in fromDomain)
        {
            Assert.Equal(expected, fromDatabase[accountId]);
        }

        // Spot values from the hand-calculated week (+ two small fuel bills, voided entry ignored).
        Assert.Equal(6699.70m, fromDatabase[books.Named("Cash").Id]);
        Assert.Equal(450.30m, fromDatabase[books.Named("Fuel").Id]);
        Assert.Equal(5300m, fromDatabase[books.Named("Ram Traders").Id]);
        Assert.Equal(-4500m, fromDatabase[books.Named("Sita Suppliers").Id]);
        Assert.Equal(0m, fromDatabase.Values.Sum() + Balances.OpeningEquity(books.Accounts));
    }

    private static async Task ValuesRoundTripAsync(LekhaBookDbContext db, SampleBooks.Result books)
    {
        var customer = await db.Accounts.SingleAsync(a => a.Name == "Ram Traders");
        Assert.Equal(AccountType.Party, customer.Type);
        Assert.Equal(PartyType.Customer, customer.PartyType);
        Assert.Equal(new DateOnly(2026, 11, 1), customer.OpeningAsOf);
        Assert.Equal(DateTimeKind.Utc, customer.CreatedAtUtc.Kind);
        Assert.Equal(SampleBooks.Now, customer.CreatedAtUtc);

        var supplier = await db.Accounts.SingleAsync(a => a.Name == "Sita Suppliers");
        Assert.Equal(BalanceSide.Credit, supplier.OpeningSide);

        // Decimal comparison and ordering run in the database on both providers.
        var bigOnes = await db.Transactions
            .Where(t => t.CompanyId == books.Company.Id && t.Amount >= 3500m)
            .OrderByDescending(t => t.Amount)
            .Select(t => t.Amount)
            .ToListAsync();
        Assert.Equal([7000m, 6000m, 3500m], bigOnes);

        var voided = await db.Transactions.SingleAsync(t => t.Status == TransactionStatus.Void);
        Assert.Equal(999m, voided.Amount);

        // Seeded system accounts are flagged and found by type.
        Assert.True(await db.Accounts.AnyAsync(a => a.Type == AccountType.System && a.IsSystem));
    }
}

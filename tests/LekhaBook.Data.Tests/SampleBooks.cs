using LekhaBook.Data;
using LekhaBook.Domain;

namespace LekhaBook.Data.Tests;

/// <summary>
/// A seeded company with a party, a bank account and a week of entries (the same week as the
/// hand-calculated domain test), used to check each database against the domain calculator.
/// </summary>
internal static class SampleBooks
{
    public static readonly DateTime Now = new(2026, 11, 24, 6, 30, 15, DateTimeKind.Utc);

    public sealed record Result(Company Company, List<Account> Accounts, List<Transaction> Transactions)
    {
        public Account Named(string name) => Accounts.Single(a => a.Name == name);
    }

    public static async Task<Result> SeedAsync(LekhaBookDbContext db)
    {
        var company = new Company { Name = "Test Kirana", CreatedAtUtc = Now, UpdatedAtUtc = Now };
        var accounts = CompanySeeder.DefaultAccounts(company.Id, Now).ToList();

        var customer = NewAccount(company, "Ram Traders", AccountType.Party, a =>
        {
            a.PartyType = PartyType.Customer;
            a.OpeningBalance = 12000m;
            a.OpeningAsOf = new DateOnly(2026, 11, 1);
        });
        var supplier = NewAccount(company, "Sita Suppliers", AccountType.Party, a =>
        {
            a.PartyType = PartyType.Supplier;
            a.OpeningBalance = 8000m;
            a.OpeningSide = BalanceSide.Credit;
        });
        var bank = NewAccount(company, "NIC Asia", AccountType.Bank, a => a.AccountNumber = "0123456789");
        accounts.AddRange([customer, supplier, bank]);

        var cash = accounts.Single(a => a.Name == "Cash");
        cash.OpeningBalance = 5000m;
        var fuel = accounts.Single(a => a.Name == "Fuel");
        var commission = accounts.Single(a => a.Name == "Commission");
        var adjustment = accounts.Single(a => a.Name == CompanySeeder.AdjustmentAccountName);

        var seq = 0;
        Transaction Txn(TransactionType type, Account from, Account to, decimal amount, int day) => new()
        {
            CompanyId = company.Id,
            Type = type,
            VoucherNo = (++seq).ToString(),
            Date = new DateOnly(2026, 11, day),
            Amount = amount,
            FromAccountId = from.Id,
            ToAccountId = to.Id,
            CreatedAtUtc = Now.AddSeconds(seq),
            UpdatedAtUtc = Now.AddSeconds(seq),
        };

        var voided = Txn(TransactionType.PaymentIn, customer, cash, 999m, 6);
        voided.Status = TransactionStatus.Void;

        var transactions = new List<Transaction>
        {
            Txn(TransactionType.PaymentIn, customer, cash, 7000m, 1),
            Txn(TransactionType.TransferMoney, cash, bank, 6000m, 2),
            Txn(TransactionType.PaymentOut, bank, supplier, 3500m, 3),
            Txn(TransactionType.Expense, cash, fuel, 450m, 3),
            Txn(TransactionType.OtherIncome, commission, cash, 1200m, 4),
            Txn(TransactionType.PartyAdjustAdd, adjustment, customer, 300m, 5),
            Txn(TransactionType.ReduceMoney, cash, adjustment, 50m, 5),
            // Small fractions check that decimals add up exactly (no floating-point drift).
            Txn(TransactionType.Expense, cash, fuel, 0.10m, 6),
            Txn(TransactionType.Expense, cash, fuel, 0.20m, 6),
            voided,
        };

        db.Companies.Add(company);
        db.Accounts.AddRange(accounts);
        db.Transactions.AddRange(transactions);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        return new Result(company, accounts, transactions);
    }

    private static Account NewAccount(Company company, string name, AccountType type, Action<Account> configure)
    {
        var account = new Account
        {
            CompanyId = company.Id,
            Name = name,
            Type = type,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        };
        configure(account);
        return account;
    }
}

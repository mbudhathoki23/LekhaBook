using LekhaBook.Domain;

namespace LekhaBook.Domain.Tests;

/// <summary>A small company with one account of each kind, for building test transactions.</summary>
internal sealed class TestBooks
{
    public static readonly DateTime Now = new(2026, 11, 24, 6, 0, 0, DateTimeKind.Utc);

    public Guid CompanyId { get; } = Guid.CreateVersion7();

    public Account Customer { get; }
    public Account Supplier { get; }
    public Account Cash { get; }
    public Account Bank { get; }
    public Account Wallet { get; }
    public Account Fuel { get; }
    public Account Commission { get; }
    public Account Adjustment { get; }

    public TestBooks()
    {
        Customer = New("Ram Traders", AccountType.Party, p => p.PartyType = PartyType.Customer);
        Supplier = New("Sita Suppliers", AccountType.Party, p => p.PartyType = PartyType.Supplier);
        Cash = New("Cash", AccountType.Cash);
        Bank = New("NIC Asia", AccountType.Bank);
        Wallet = New("Online Wallet", AccountType.Wallet);
        Fuel = New("Fuel", AccountType.Expense);
        Commission = New("Commission", AccountType.Income);
        Adjustment = New(CompanySeeder.AdjustmentAccountName, AccountType.System);
    }

    public IEnumerable<Account> All => [Customer, Supplier, Cash, Bank, Wallet, Fuel, Commission, Adjustment];

    public Account New(string name, AccountType type, Action<Account>? configure = null)
    {
        var account = new Account { CompanyId = CompanyId, Name = name, Type = type, CreatedAtUtc = Now, UpdatedAtUtc = Now };
        configure?.Invoke(account);
        return account;
    }

    private int _seq;

    public Transaction Txn(TransactionType type, Account from, Account to, decimal amount, int day = 1) => new()
    {
        CompanyId = CompanyId,
        Type = type,
        VoucherNo = (++_seq).ToString(),
        Date = new DateOnly(2026, 11, day),
        Amount = amount,
        FromAccountId = from.Id,
        ToAccountId = to.Id,
        // Later-created rows sort after earlier ones on the same day.
        CreatedAtUtc = Now.AddSeconds(_seq),
        UpdatedAtUtc = Now.AddSeconds(_seq),
    };
}

namespace LekhaBook.Domain;

/// <summary>
/// Default accounts every new company starts with (AGENT_PLAN § 5.1).
/// Bank accounts are not seeded; users add their own.
/// </summary>
public static class CompanySeeder
{
    public const string AdjustmentAccountName = "Adjustment";

    public static readonly string[] ExpenseHeads =
        ["General", "Health", "Recharge", "Food", "Entertainment", "Fuel", "Shopping", "Travel"];

    public static readonly string[] IncomeHeads = ["Salary", "Investment", "Commission"];

    public static IReadOnlyList<Account> DefaultAccounts(Guid companyId, DateTime nowUtc, Guid? createdBy = null)
    {
        var accounts = new List<Account>
        {
            New("Cash", AccountType.Cash, isSystem: true),
            New("Cheque", AccountType.Cheque, isSystem: true),
            New("Online Wallet", AccountType.Wallet, isSystem: false),
            New(AdjustmentAccountName, AccountType.System, isSystem: true),
        };
        accounts.AddRange(ExpenseHeads.Select(name => New(name, AccountType.Expense, isSystem: false)));
        accounts.AddRange(IncomeHeads.Select(name => New(name, AccountType.Income, isSystem: false)));
        return accounts;

        Account New(string name, AccountType type, bool isSystem) => new()
        {
            CompanyId = companyId,
            Name = name,
            Type = type,
            IsSystem = isSystem,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
            CreatedBy = createdBy,
            UpdatedBy = createdBy,
        };
    }
}

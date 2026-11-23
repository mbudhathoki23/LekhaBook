namespace LekhaBook.Domain;

/// <summary>
/// Which accounts each transaction type may move value between (AGENT_PLAN § 5.2).
/// "From" is credited, "To" is debited.
/// </summary>
public static class PostingRules
{
    /// <summary>Largest amount that fits numeric(18,2).</summary>
    public const decimal MaxAmount = 9_999_999_999_999_999.99m;

    private sealed record Rule(AccountRole From, AccountRole To);

    /// <summary>The kind of account a side of a transaction must be.</summary>
    public enum AccountRole
    {
        Party,
        Money,
        Income,
        Expense,
        Adjustment,
    }

    private static readonly Dictionary<TransactionType, Rule> Rules = new()
    {
        [TransactionType.PaymentIn] = new(AccountRole.Party, AccountRole.Money),
        [TransactionType.PaymentOut] = new(AccountRole.Money, AccountRole.Party),
        [TransactionType.PartyAdjustAdd] = new(AccountRole.Adjustment, AccountRole.Party),
        [TransactionType.PartyAdjustReduce] = new(AccountRole.Party, AccountRole.Adjustment),
        [TransactionType.Expense] = new(AccountRole.Money, AccountRole.Expense),
        [TransactionType.OtherIncome] = new(AccountRole.Income, AccountRole.Money),
        [TransactionType.AddMoney] = new(AccountRole.Adjustment, AccountRole.Money),
        [TransactionType.ReduceMoney] = new(AccountRole.Money, AccountRole.Adjustment),
        [TransactionType.TransferMoney] = new(AccountRole.Money, AccountRole.Money),
    };

    /// <summary>The account kinds a form should offer for each side of <paramref name="type"/>.</summary>
    public static (AccountRole From, AccountRole To) RolesFor(TransactionType type)
    {
        var rule = Rules[type];
        return (rule.From, rule.To);
    }

    public static bool Fits(AccountRole role, Account account) => role switch
    {
        AccountRole.Party => account.Type == AccountType.Party,
        AccountRole.Money => account.Type.IsMoney(),
        AccountRole.Income => account.Type == AccountType.Income,
        AccountRole.Expense => account.Type == AccountType.Expense,
        AccountRole.Adjustment => account.Type == AccountType.System,
        _ => false,
    };

    /// <summary>A positive amount with at most 2 decimal places that fits the database column.</summary>
    public static bool IsValidAmount(decimal amount) =>
        amount > 0 && amount <= MaxAmount && decimal.Round(amount, 2) == amount;

    /// <summary>
    /// Checks a transaction against its accounts. Returns the problems found; empty means valid.
    /// </summary>
    public static IReadOnlyList<string> Validate(Transaction transaction, Account from, Account to)
    {
        var errors = new List<string>();

        if (!Rules.TryGetValue(transaction.Type, out var rule))
        {
            errors.Add($"Unknown transaction type {transaction.Type}.");
            return errors;
        }

        if (!IsValidAmount(transaction.Amount))
        {
            errors.Add("Amount must be more than 0 with at most 2 decimal places.");
        }

        if (string.IsNullOrWhiteSpace(transaction.VoucherNo))
        {
            errors.Add("Voucher number is required.");
        }

        if (from.Id != transaction.FromAccountId || to.Id != transaction.ToAccountId)
        {
            errors.Add("Accounts don't match the transaction.");
        }

        if (from.Id == to.Id)
        {
            errors.Add("From and To must be different accounts.");
        }

        if (from.CompanyId != transaction.CompanyId || to.CompanyId != transaction.CompanyId)
        {
            errors.Add("Both accounts must belong to the transaction's company.");
        }

        if (from.IsDeleted || to.IsDeleted)
        {
            errors.Add("A deleted account can't be used.");
        }

        if (!Fits(rule.From, from))
        {
            errors.Add($"{transaction.Type} must come from a {Describe(rule.From)} account, not {from.Type}.");
        }

        if (!Fits(rule.To, to))
        {
            errors.Add($"{transaction.Type} must go to a {Describe(rule.To)} account, not {to.Type}.");
        }

        return errors;
    }

    private static string Describe(AccountRole role) => role switch
    {
        AccountRole.Money => "money (Cash, Bank, Cheque or Wallet)",
        AccountRole.Adjustment => "system Adjustment",
        _ => role.ToString(),
    };
}

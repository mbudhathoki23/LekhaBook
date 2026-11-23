namespace LekhaBook.Domain;

public enum PartyBalanceSide
{
    ToReceive,
    ToGive,
    Settled,
}

/// <summary>A party's balance as users read it: an amount and "To Receive" / "To Give".</summary>
public readonly record struct PartyBalance(decimal Amount, PartyBalanceSide Side)
{
    public static PartyBalance FromSigned(decimal signed) => signed switch
    {
        > 0 => new(signed, PartyBalanceSide.ToReceive),
        < 0 => new(-signed, PartyBalanceSide.ToGive),
        _ => new(0, PartyBalanceSide.Settled),
    };
}

/// <summary>One row of an account statement, with the balance after it.</summary>
public sealed record LedgerLine(Transaction Transaction, decimal Debit, decimal Credit, decimal Balance);

/// <summary>
/// Balances are always computed from the opening balance and transactions, never stored
/// (AGENT_PLAN § 5.3). Sign convention: debit is positive.
/// <list type="bullet">
/// <item>Party: positive = To Receive, negative = To Give.</item>
/// <item>Money account: positive = money held.</item>
/// <item>Expense: positive = spent. Income: negative = earned.</item>
/// </list>
/// </summary>
public static class Balances
{
    public static decimal SignedOpening(Account account) =>
        account.OpeningSide == BalanceSide.Debit ? account.OpeningBalance : -account.OpeningBalance;

    /// <summary>Opening + everything debited to the account − everything credited from it.</summary>
    public static decimal Of(Account account, IEnumerable<Transaction> transactions)
    {
        var balance = SignedOpening(account);
        foreach (var t in transactions)
        {
            if (!t.CountsTowardBalance)
            {
                continue;
            }

            if (t.ToAccountId == account.Id)
            {
                balance += t.Amount;
            }

            if (t.FromAccountId == account.Id)
            {
                balance -= t.Amount;
            }
        }

        return balance;
    }

    public static PartyBalance OfParty(Account party, IEnumerable<Transaction> transactions) =>
        PartyBalance.FromSigned(Of(party, transactions));

    /// <summary>Balances of all accounts in one pass.</summary>
    public static Dictionary<Guid, decimal> All(IEnumerable<Account> accounts, IEnumerable<Transaction> transactions)
    {
        var result = accounts.ToDictionary(a => a.Id, SignedOpening);
        foreach (var t in transactions)
        {
            if (!t.CountsTowardBalance)
            {
                continue;
            }

            if (result.ContainsKey(t.ToAccountId))
            {
                result[t.ToAccountId] += t.Amount;
            }

            if (result.ContainsKey(t.FromAccountId))
            {
                result[t.FromAccountId] -= t.Amount;
            }
        }

        return result;
    }

    /// <summary>
    /// The balancing figure for all opening balances. Opening equity is implicit (no account row),
    /// so that all account balances plus this always sum to zero.
    /// </summary>
    public static decimal OpeningEquity(IEnumerable<Account> accounts) =>
        -accounts.Where(a => !a.IsDeleted).Sum(SignedOpening);

    /// <summary>
    /// Statement lines for one account, oldest first, with a running balance that starts at the opening
    /// balance. Same-day entries are ordered by creation time, then id.
    /// </summary>
    public static IReadOnlyList<LedgerLine> Ledger(Account account, IEnumerable<Transaction> transactions)
    {
        var running = SignedOpening(account);
        var lines = new List<LedgerLine>();

        var ordered = transactions
            .Where(t => t.CountsTowardBalance && (t.ToAccountId == account.Id || t.FromAccountId == account.Id))
            .OrderBy(t => t.Date)
            .ThenBy(t => t.CreatedAtUtc)
            .ThenBy(t => t.Id);

        foreach (var t in ordered)
        {
            var debit = t.ToAccountId == account.Id ? t.Amount : 0m;
            var credit = t.FromAccountId == account.Id ? t.Amount : 0m;
            running += debit - credit;
            lines.Add(new LedgerLine(t, debit, credit, running));
        }

        return lines;
    }
}

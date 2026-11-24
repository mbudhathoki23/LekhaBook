using LekhaBook.Domain;
using Microsoft.EntityFrameworkCore;

namespace LekhaBook.Data;

/// <summary>An account statement with the names of the accounts on the other side of each entry.</summary>
public sealed record AccountLedger(Account Account, IReadOnlyList<LedgerLine> Lines, IReadOnlyDictionary<Guid, string> AccountNames)
{
    public decimal ClosingBalance => Lines.Count > 0 ? Lines[^1].Balance : Balances.SignedOpening(Account);

    public string? OtherSide(Transaction t)
    {
        var other = t.ToAccountId == Account.Id ? t.FromAccountId : t.ToAccountId;
        return AccountNames.GetValueOrDefault(other);
    }
}

/// <summary>Statement and usage queries shared by the server (PostgreSQL) and devices (SQLite).</summary>
public static class LedgerQueries
{
    /// <summary>The statement for one live account in the company, or null if there is no such account.</summary>
    public static async Task<AccountLedger?> GetLedgerAsync(
        this LekhaBookDbContext db, Guid companyId, Guid accountId, CancellationToken cancellationToken = default)
    {
        var account = await db.Accounts.AsNoTracking()
            .SingleOrDefaultAsync(a => a.Id == accountId && a.CompanyId == companyId && !a.IsDeleted, cancellationToken);
        if (account is null)
        {
            return null;
        }

        // One account's entries fit in memory for now; paging arrives with the transaction screens (phase 6).
        var transactions = await db.Transactions.AsNoTracking()
            .Where(t => t.CompanyId == companyId && !t.IsDeleted && t.Status == TransactionStatus.Posted
                        && (t.FromAccountId == accountId || t.ToAccountId == accountId))
            .ToListAsync(cancellationToken);

        var lines = Balances.Ledger(account, transactions);
        var otherIds = transactions.Select(t => t.FromAccountId).Concat(transactions.Select(t => t.ToAccountId)).Distinct().ToList();
        var names = await db.Accounts.AsNoTracking()
            .Where(a => a.CompanyId == companyId && otherIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => a.Name, cancellationToken);

        return new AccountLedger(account, lines, names);
    }

    /// <summary>Whether any transaction that isn't deleted (posted or void) uses the account.</summary>
    public static Task<bool> IsUsedByTransactionsAsync(
        this LekhaBookDbContext db, Guid companyId, Guid accountId, CancellationToken cancellationToken = default) =>
        db.Transactions.AnyAsync(
            t => t.CompanyId == companyId && !t.IsDeleted && (t.FromAccountId == accountId || t.ToAccountId == accountId),
            cancellationToken);
}

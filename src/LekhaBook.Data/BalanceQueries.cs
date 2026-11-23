using LekhaBook.Domain;
using Microsoft.EntityFrameworkCore;

namespace LekhaBook.Data;

/// <summary>
/// Balance queries that run in the database (SQL SUM), for both SQLite and PostgreSQL.
/// They must agree with <see cref="Balances"/>, which the data tests check.
/// </summary>
public static class BalanceQueries
{
    /// <summary>Balance of every live account in a company, debit positive.</summary>
    public static async Task<Dictionary<Guid, decimal>> GetBalancesAsync(
        this LekhaBookDbContext db, Guid companyId, CancellationToken cancellationToken = default)
    {
        var accounts = await db.Accounts.AsNoTracking()
            .Where(a => a.CompanyId == companyId && !a.IsDeleted)
            .Select(a => new { a.Id, a.OpeningBalance, a.OpeningSide })
            .ToListAsync(cancellationToken);

        var live = db.Transactions.AsNoTracking()
            .Where(t => t.CompanyId == companyId && !t.IsDeleted && t.Status == TransactionStatus.Posted);

        var debits = await live.GroupBy(t => t.ToAccountId)
            .Select(g => new { AccountId = g.Key, Total = g.Sum(t => t.Amount) })
            .ToDictionaryAsync(x => x.AccountId, x => x.Total, cancellationToken);

        var credits = await live.GroupBy(t => t.FromAccountId)
            .Select(g => new { AccountId = g.Key, Total = g.Sum(t => t.Amount) })
            .ToDictionaryAsync(x => x.AccountId, x => x.Total, cancellationToken);

        return accounts.ToDictionary(
            a => a.Id,
            a => (a.OpeningSide == BalanceSide.Debit ? a.OpeningBalance : -a.OpeningBalance)
                 + debits.GetValueOrDefault(a.Id)
                 - credits.GetValueOrDefault(a.Id));
    }
}

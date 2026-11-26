using LekhaBook.Domain;
using Microsoft.EntityFrameworkCore;

namespace LekhaBook.Data.Postgres;

/// <summary>
/// Server change numbers (AGENT_PLAN § 6.2). Every written row gets its own version from the company's
/// counter row. The UPDATE locks that row until the surrounding transaction commits, so writes to one
/// company are serialized and a version can never become visible before a lower one.
/// </summary>
public static class CompanyVersions
{
    /// <summary>
    /// Stamps <paramref name="rows"/> with consecutive new versions and the server's time and user, then saves
    /// everything in one transaction. New rows also get their created fields. <paramref name="stillValid"/> runs once
    /// the counter row is locked (so no other write to the company can slip in); if it returns false nothing is saved.
    /// </summary>
    /// <returns>False if <paramref name="stillValid"/> refused.</returns>
    public static async Task<bool> SaveWithVersionsAsync(
        this PostgresDbContext db, Guid companyId, IReadOnlyList<SyncEntity> rows, Guid userId, DateTime nowUtc,
        Func<Task<bool>>? stillValid = null, CancellationToken cancellationToken = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var last = await ClaimAsync(db, companyId, rows.Count, cancellationToken);
        if (stillValid is not null && !await stillValid())
        {
            return false; // disposing the transaction rolls the claim back
        }

        var next = last - rows.Count + 1;
        foreach (var row in rows)
        {
            if (db.Entry(row).State == EntityState.Added)
            {
                row.CreatedAtUtc = nowUtc;
                row.CreatedBy = userId;
            }

            row.Version = next++;
            row.UpdatedAtUtc = nowUtc;
            row.UpdatedBy = userId;
            row.UpdatedOnDevice = null; // written through the API, not by a device's sync
        }

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>Reserves <paramref name="count"/> versions and returns the highest one. Must run inside a transaction.</summary>
    private static async Task<long> ClaimAsync(PostgresDbContext db, Guid companyId, int count, CancellationToken cancellationToken)
    {
        var updated = await db.CompanySyncStates
            .Where(s => s.CompanyId == companyId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastVersion, x => x.LastVersion + count), cancellationToken);
        if (updated != 1)
        {
            throw new InvalidOperationException($"Company {companyId} has no sync state row.");
        }

        return await db.CompanySyncStates
            .Where(s => s.CompanyId == companyId)
            .Select(s => s.LastVersion)
            .SingleAsync(cancellationToken);
    }
}

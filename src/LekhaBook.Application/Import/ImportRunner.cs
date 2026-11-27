using LekhaBook.Contracts;

namespace LekhaBook.Application.Import;

/// <summary>The answer to one save: no error, or a message. <paramref name="Stop"/> means later saves would fail too.</summary>
public sealed record ImportReply(string? Error = null, bool Stop = false);

/// <summary>
/// Saves the selected rows one by one, in file order. Ids were fixed when the plan was made, so running the same
/// plan again after a lost connection can't add anything twice.
/// </summary>
public static class ImportRunner
{
    /// <summary>
    /// Returns false when it stopped early (connection lost, signed out or cancelled); the remaining rows are
    /// untouched and the plan can be run again.
    /// </summary>
    public static async Task<bool> RunAsync(
        ImportPlan plan,
        Func<SavePartyRequest, Task<ImportReply>> addParty,
        Func<CreateTransactionRequest, Task<ImportReply>> addEntry,
        Action progress,
        CancellationToken cancellationToken = default)
    {
        var added = new HashSet<Guid>(plan.Rows.Where(r => r.Saved && r.NewPartyId is not null).Select(r => r.NewPartyId!.Value));
        foreach (var row in plan.Rows.Where(r => r.Selected && !r.Saved))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            row.SaveError = null;
            ImportReply reply;
            if (row.NewPartyId is { } partyId && !added.Contains(partyId))
            {
                reply = await addParty(plan.NewParties.Single(p => p.Id == partyId));
                if (reply.Error is null)
                {
                    added.Add(partyId);
                }
            }
            else
            {
                reply = new ImportReply();
            }

            if (reply.Error is null)
            {
                reply = row.Party is { } party ? await addParty(party) : await addEntry(row.Entry!);
            }

            row.SaveError = reply.Error;
            row.Saved = reply.Error is null;
            progress();
            if (reply.Stop)
            {
                return false;
            }
        }

        return true;
    }
}

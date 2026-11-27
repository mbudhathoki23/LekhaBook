namespace LekhaBook.UI.Services;

/// <summary>A change made on this device that the server refused, kept until the user decides.</summary>
public sealed record SyncProblem(long Id, string Description, string Message);

/// <summary>Changes that could not be uploaded. Only the apps with a local database have any.</summary>
public interface ISyncProblems
{
    Task<IReadOnlyList<SyncProblem>> ListAsync();

    /// <summary>Sends the change again.</summary>
    Task RetryAsync(long id);

    /// <summary>Drops the change from this device and goes back to what the server has.</summary>
    Task DiscardAsync(long id);
}

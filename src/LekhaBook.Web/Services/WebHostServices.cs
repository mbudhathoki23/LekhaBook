using LekhaBook.UI.Services;

namespace LekhaBook.Web.Services;

public sealed class WebPlatformInfo : IPlatformInfo
{
    public string Platform => "Web";

    public string FormFactor => "Web";
}

/// <summary>
/// The browser app runs on the server (interactive server rendering). If the browser loses its
/// connection, Blazor's reconnect dialog takes over, so from the UI's point of view it is always online.
/// </summary>
public sealed class WebConnectivityService : IConnectivityService
{
    public bool IsOnline => true;

    public event EventHandler<bool>? OnlineChanged
    {
        add { }
        remove { }
    }
}

/// <summary>Writes go straight to the API, so there is never anything waiting to sync.</summary>
public sealed class OnlineOnlySyncService : ISyncService
{
    public SyncState State => SyncState.Synced;

    public int PendingCount => 0;

    public event EventHandler? StateChanged
    {
        add { }
        remove { }
    }

    public Task SyncNowAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

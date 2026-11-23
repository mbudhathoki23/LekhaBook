using LekhaBook.UI.Services;

namespace LekhaBook.App.Services;

public sealed class MauiPlatformInfo : IPlatformInfo
{
    public string Platform => DeviceInfo.Current.Platform == DevicePlatform.WinUI
        ? "Windows"
        : DeviceInfo.Current.Platform.ToString();

    public string FormFactor => DeviceInfo.Current.Idiom == DeviceIdiom.Phone ? "Phone"
        : DeviceInfo.Current.Idiom == DeviceIdiom.Tablet ? "Tablet"
        : "Desktop";
}

/// <summary>Device network state from MAUI Essentials.</summary>
public sealed class MauiConnectivityService : IConnectivityService, IDisposable
{
    public MauiConnectivityService()
    {
        Connectivity.Current.ConnectivityChanged += OnConnectivityChanged;
    }

    public bool IsOnline => Connectivity.Current.NetworkAccess == NetworkAccess.Internet;

    public event EventHandler<bool>? OnlineChanged;

    private void OnConnectivityChanged(object? sender, ConnectivityChangedEventArgs e) =>
        OnlineChanged?.Invoke(this, e.NetworkAccess == NetworkAccess.Internet);

    public void Dispose() => Connectivity.Current.ConnectivityChanged -= OnConnectivityChanged;
}

/// <summary>
/// Placeholder until the sync engine is built (phase 8): nothing is stored locally yet,
/// so there is never anything pending.
/// </summary>
public sealed class PendingSyncEngine(IConnectivityService connectivity) : ISyncService
{
    public SyncState State => connectivity.IsOnline ? SyncState.Synced : SyncState.Offline;

    public int PendingCount => 0;

    public event EventHandler? StateChanged
    {
        add { }
        remove { }
    }

    public Task SyncNowAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

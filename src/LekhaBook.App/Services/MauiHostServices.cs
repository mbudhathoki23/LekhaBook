using LekhaBook.Offline;
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

/// <summary>Whether the device has any network, from MAUI Essentials. A local network counts: the server may be on it.</summary>
public sealed class MauiNetworkMonitor : INetworkMonitor, IDisposable
{
    public MauiNetworkMonitor() => Connectivity.Current.ConnectivityChanged += OnConnectivityChanged;

    public bool HasNetwork => Connectivity.Current.NetworkAccess != NetworkAccess.None;

    public event EventHandler? Changed;

    private void OnConnectivityChanged(object? sender, ConnectivityChangedEventArgs e) => Changed?.Invoke(this, EventArgs.Empty);

    public void Dispose() => Connectivity.Current.ConnectivityChanged -= OnConnectivityChanged;
}

namespace LekhaBook.UI.Services;

// The shared UI talks to the device or server only through these interfaces
// (AGENT_PLAN § 3.2). Each host registers its own implementations:
// LekhaBook.App (MAUI, offline-first) and LekhaBook.Web (browser, online-only).
// IDataService, IAttachmentService and IShareService are added in later phases.

/// <summary>Where the UI is running, so layouts can adapt.</summary>
public interface IPlatformInfo
{
    /// <summary>"Android", "iOS", "Windows" or "Web".</summary>
    string Platform { get; }

    /// <summary>"Phone", "Tablet", "Desktop" or "Web".</summary>
    string FormFactor { get; }
}

/// <summary>Network state of the device.</summary>
public interface IConnectivityService
{
    bool IsOnline { get; }

    event EventHandler<bool>? OnlineChanged;
}

public enum SyncState
{
    Synced,
    Pending,
    Syncing,
    Offline,
    Error,
}

/// <summary>Background sync between the device database and the server.</summary>
public interface ISyncService
{
    SyncState State { get; }

    /// <summary>Local changes not yet sent to the server.</summary>
    int PendingCount { get; }

    event EventHandler? StateChanged;

    Task SyncNowAsync(CancellationToken cancellationToken = default);
}

/// <summary>The signed-in user and the company they are working in.</summary>
public interface ISessionService
{
    string? UserName { get; }

    string? CompanyName { get; }
}

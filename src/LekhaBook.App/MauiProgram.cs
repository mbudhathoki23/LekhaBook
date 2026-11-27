using LekhaBook.App.Services;
using LekhaBook.Client;
using LekhaBook.Data.Sqlite;
using LekhaBook.Offline;
using LekhaBook.UI;
using LekhaBook.UI.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LekhaBook.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            });

        builder.Services.AddMauiBlazorWebView();

        // Shared UI + device implementations (offline-first host).
        builder.Services.AddLekhaBookUI();
        builder.Services.AddSingleton<IPlatformInfo, MauiPlatformInfo>();
        builder.Services.AddSingleton<IDocumentCapture, MauiDocumentCapture>();
        builder.Services.AddSingleton<ILanguageService, DeviceLanguageService>();

        // Sign-in: one user per app install, so the session is a singleton. Tokens live in SecureStorage.
        builder.Services.AddLekhaBookClient(ApiEndpoint.BaseAddress, perUserScope: false);
        builder.Services.AddSingleton<IDeviceUnlock, DeviceUnlock>();
        builder.Services.AddSingleton<ITokenStore, SecureTokenStore>();
        builder.Services.AddSingleton<ILocalSettings, PreferencesSettings>();

        // Real server-reachability connectivity monitor (probes /health and fails fast when server is unreachable).
        builder.Services.AddSingleton<INetworkMonitor, MauiNetworkMonitor>();
        builder.Services.AddHttpClient(ServerConnectivity.ProbeClient, c =>
        {
            c.BaseAddress = ApiEndpoint.BaseAddress;
            c.Timeout = TimeSpan.FromSeconds(4);
        });
        builder.Services.AddSingleton(sp => new ServerConnectivity(
            sp.GetRequiredService<INetworkMonitor>(),
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<ServerConnectivity>>()));
        builder.Services.AddSingleton<IConnectivityService>(sp => sp.GetRequiredService<ServerConnectivity>());
        builder.Services.AddHttpClient("lekhabook-api")
            .AddHttpMessageHandler(sp => new OfflineGate(sp.GetRequiredService<ServerConnectivity>()));

        // Device database: one file per signed-in user (plus a dedicated Offline Local Mode database),
        // opened and migrated automatically on sign-in or Offline Local Mode start.
        var databases = new UserDatabaseFactory(FileSystem.AppDataDirectory);
        var filesRoot = Path.Combine(FileSystem.AppDataDirectory, "attachments");
        var localStore = new LocalBooksStore(databases, filesRoot);

        builder.Services.AddSingleton(databases);
        builder.Services.AddSingleton<IDbContextFactory<SqliteDbContext>>(databases);
        builder.Services.AddSingleton(localStore);
        builder.Services.AddSingleton<IUserChangeListener, DeviceDatabaseOpener>();
        builder.Services.AddSingleton<ILocalSessionProvider, DeviceLocalSessionProvider>();

        // Full offline-first sync engine + SQLite services supporting both Offline Local Mode and Online-Synced Mode.
        builder.Services.AddSingleton<ISyncService, DeviceSyncEngine>();
        builder.Services.AddSingleton<IDataService, SqliteDataService>();
        builder.Services.AddSingleton<ICompanyApi, HybridCompanyService>();
        builder.Services.AddSingleton<IBookExtrasApi, SqliteBookExtrasService>();
        builder.Services.AddSingleton<IAttachmentService, SqliteAttachmentService>();
        builder.Services.AddSingleton<IBackupService, LocalBackupService>();
        builder.Services.AddSingleton<IOfflineUpgradeService>(sp => new OfflineUpgradeService(
            databases,
            filesRoot,
            sp.GetRequiredService<ISessionService>(),
            sp.GetRequiredService<CompanyApi>(),
            sp.GetRequiredService<ApiAttachmentService>(),
            sp.GetRequiredService<ISyncService>()));

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}

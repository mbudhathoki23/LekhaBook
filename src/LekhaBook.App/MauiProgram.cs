using LekhaBook.App.Services;
using LekhaBook.Client;
using LekhaBook.Data.Sqlite;
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
        builder.Services.AddSingleton<IConnectivityService, MauiConnectivityService>();
        builder.Services.AddSingleton<ISyncService, MauiSyncEngine>();
        builder.Services.AddSingleton<ILanguageService, DeviceLanguageService>();

        // Sign-in: one user per app install, so the session is a singleton. Tokens live in SecureStorage.
        builder.Services.AddLekhaBookClient(ApiEndpoint.BaseAddress, perUserScope: false);
        builder.Services.AddSingleton<IDeviceUnlock, DeviceUnlock>();
        builder.Services.AddSingleton<ITokenStore, SecureTokenStore>();
        builder.Services.AddSingleton<ILocalSettings, PreferencesSettings>();

        // Device database: one file per signed-in user, opened (and migrated) at sign-in.
        var databases = new UserDatabaseFactory(FileSystem.AppDataDirectory);
        builder.Services.AddSingleton(databases);
        builder.Services.AddSingleton<IDbContextFactory<SqliteDbContext>>(databases);
        builder.Services.AddSingleton<IUserChangeListener, DeviceDatabaseOpener>();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}

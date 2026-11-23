using LekhaBook.App.Services;
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
        builder.Services.AddSingleton<IConnectivityService, MauiConnectivityService>();
        builder.Services.AddSingleton<ISyncService, PendingSyncEngine>();

        // Device database. One file per signed-in user arrives with sign-in (phase 4).
        builder.Services.AddLekhaBookSqlite(Path.Combine(FileSystem.AppDataDirectory, "lekhabook.db"));

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        var app = builder.Build();

        // Create or upgrade the device database before the first screen loads.
        using (var db = app.Services.GetRequiredService<IDbContextFactory<SqliteDbContext>>().CreateDbContext())
        {
            db.Database.Migrate();
        }

        return app;
    }
}

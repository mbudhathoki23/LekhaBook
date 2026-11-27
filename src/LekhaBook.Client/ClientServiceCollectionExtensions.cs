using LekhaBook.UI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace LekhaBook.Client;

public static class ClientServiceCollectionExtensions
{
    /// <summary>
    /// Registers the API client and session. <paramref name="perUserScope"/> must be true on the web host
    /// (one session per browser circuit); the MAUI app has one user, so it uses singletons.
    /// The host registers <see cref="ITokenStore"/> and <see cref="ILocalSettings"/> with the same lifetime.
    /// </summary>
    public static IServiceCollection AddLekhaBookClient(this IServiceCollection services, Uri apiBaseAddress, bool perUserScope)
    {
        services.AddHttpClient("lekhabook-api", c =>
        {
            c.BaseAddress = apiBaseAddress;
            c.Timeout = TimeSpan.FromSeconds(30);
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            // All clients use explicit bearer tokens. A pooled handler must never share Identity
            // cookies (including pending two-factor cookies) between web users or devices.
            UseCookies = false,
        });
        services.AddSingleton(TimeProvider.System);

        Func<IServiceProvider, LekhaBookApi> api = sp => new LekhaBookApi(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("lekhabook-api"),
            sp.GetRequiredService<ITokenStore>(),
            sp.GetRequiredService<TimeProvider>());

        if (perUserScope)
        {
            services.AddScoped<IAccountSecurity, AccountSecurity>();
            services.AddScoped<IDeviceUnlock, UnsupportedDeviceUnlock>();
            services.AddScoped<IOfflineUpgradeService, UnsupportedOfflineUpgradeService>();
            services.AddScoped<IBackupService, UnsupportedBackupService>();
            services.AddScoped<IThemeService, LocalThemeService>();
            services.AddScoped(api);
            services.AddScoped<SessionService>();
            services.AddScoped<ISessionService>(sp => sp.GetRequiredService<SessionService>());
            services.AddScoped<CompanyApi>();
            services.AddScoped<ICompanyApi>(sp => sp.GetRequiredService<CompanyApi>());
            services.AddScoped<ApiDataService>();
            services.AddScoped<IDataService>(sp => sp.GetRequiredService<ApiDataService>());
            services.AddScoped<ApiAttachmentService>();
            services.AddScoped<IAttachmentService>(sp => sp.GetRequiredService<ApiAttachmentService>());
            services.AddScoped<IReportLinkApi, ReportLinkApi>();
            services.AddScoped<BookExtrasApi>();
            services.AddScoped<IBookExtrasApi>(sp => sp.GetRequiredService<BookExtrasApi>());
            services.AddScoped<IImportService, ImportApi>();
        }
        else
        {
            services.AddSingleton<IAccountSecurity, AccountSecurity>();
            services.AddSingleton<IDeviceUnlock, UnsupportedDeviceUnlock>();
            services.AddSingleton<IOfflineUpgradeService, UnsupportedOfflineUpgradeService>();
            services.AddSingleton<IBackupService, UnsupportedBackupService>();
            services.AddSingleton<IThemeService, LocalThemeService>();
            services.AddSingleton(api);
            services.AddSingleton<SessionService>();
            services.AddSingleton<ISessionService>(sp => sp.GetRequiredService<SessionService>());
            services.AddSingleton<CompanyApi>();
            services.AddSingleton<ICompanyApi>(sp => sp.GetRequiredService<CompanyApi>());
            services.AddSingleton<ApiDataService>();
            services.AddSingleton<IDataService>(sp => sp.GetRequiredService<ApiDataService>());
            services.AddSingleton<ApiAttachmentService>();
            services.AddSingleton<IAttachmentService>(sp => sp.GetRequiredService<ApiAttachmentService>());
            services.AddSingleton<IReportLinkApi, ReportLinkApi>();
            services.AddSingleton<BookExtrasApi>();
            services.AddSingleton<IBookExtrasApi>(sp => sp.GetRequiredService<BookExtrasApi>());
            services.AddSingleton<IImportService, ImportApi>();
        }

        return services;
    }
}

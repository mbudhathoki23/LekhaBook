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
            services.AddScoped(api);
            services.AddScoped<SessionService>();
            services.AddScoped<ISessionService>(sp => sp.GetRequiredService<SessionService>());
            services.AddScoped<ICompanyApi, CompanyApi>();
            services.AddScoped<IDataService, ApiDataService>();
            services.AddScoped<IAttachmentService, ApiAttachmentService>();
            services.AddScoped<IReportLinkApi, ReportLinkApi>();
            services.AddScoped<IBookExtrasApi, BookExtrasApi>();
            services.AddScoped<IImportService, ImportApi>();
        }
        else
        {
            services.AddSingleton<IAccountSecurity, AccountSecurity>();
            services.AddSingleton<IDeviceUnlock, UnsupportedDeviceUnlock>();
            services.AddSingleton(api);
            services.AddSingleton<SessionService>();
            services.AddSingleton<ISessionService>(sp => sp.GetRequiredService<SessionService>());
            services.AddSingleton<ICompanyApi, CompanyApi>();
            services.AddSingleton<IDataService, ApiDataService>();
            services.AddSingleton<IAttachmentService, ApiAttachmentService>();
            services.AddSingleton<IReportLinkApi, ReportLinkApi>();
            services.AddSingleton<IBookExtrasApi, BookExtrasApi>();
            services.AddSingleton<IImportService, ImportApi>();
        }

        return services;
    }
}

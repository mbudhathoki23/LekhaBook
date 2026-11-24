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
        });
        services.AddSingleton(TimeProvider.System);

        Func<IServiceProvider, LekhaBookApi> api = sp => new LekhaBookApi(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("lekhabook-api"),
            sp.GetRequiredService<ITokenStore>(),
            sp.GetRequiredService<TimeProvider>());

        if (perUserScope)
        {
            services.AddScoped(api);
            services.AddScoped<SessionService>();
            services.AddScoped<ISessionService>(sp => sp.GetRequiredService<SessionService>());
            services.AddScoped<ICompanyApi, CompanyApi>();
            services.AddScoped<IDataService, ApiDataService>();
        }
        else
        {
            services.AddSingleton(api);
            services.AddSingleton<SessionService>();
            services.AddSingleton<ISessionService>(sp => sp.GetRequiredService<SessionService>());
            services.AddSingleton<ICompanyApi, CompanyApi>();
            services.AddSingleton<IDataService, ApiDataService>();
        }

        return services;
    }
}

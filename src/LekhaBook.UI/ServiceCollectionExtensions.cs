using LekhaBook.UI.Localization;
using LekhaBook.UI.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace LekhaBook.UI;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers what the shared UI needs in every host. The host must also register its own
    /// <see cref="IPlatformInfo"/>, <see cref="IConnectivityService"/>, <see cref="ISyncService"/>,
    /// and the session/API services (see LekhaBook.Client).
    /// </summary>
    public static IServiceCollection AddLekhaBookUI(this IServiceCollection services)
    {
        services.AddLocalization();
        services.AddMudServices();
        // The app's translations also serve MudBlazor's built-in text. Registered after AddMudServices so it wins.
        services.AddTransient<MudLocalizer, ResxMudLocalizer>();
        // Attachment bytes shown as thumbnails; scoped, so each user session or window has its own.
        services.AddScoped<AttachmentContentCache>();
        // Back button of a phone (the MAUI router injects it; the browser has its own).
        services.AddScoped<BackNavigator>();
        return services;
    }
}

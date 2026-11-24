using LekhaBook.UI.Services;
using Microsoft.Extensions.DependencyInjection;
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
        services.AddMudServices();
        return services;
    }
}

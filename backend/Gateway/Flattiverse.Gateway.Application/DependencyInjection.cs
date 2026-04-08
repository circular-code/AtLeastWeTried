using Flattiverse.Gateway.Application.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Flattiverse.Gateway.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(TimeProvider.System);
        services.Configure<Options.GatewayOptions>(configuration.GetSection("Gateway"));
        services.AddSingleton<GatewaySessionOrchestrator>();

        return services;
    }
}
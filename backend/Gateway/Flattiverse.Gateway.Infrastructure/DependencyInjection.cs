using Flattiverse.Gateway.Application.Abstractions;
using Flattiverse.Gateway.Infrastructure.Connector;
using Flattiverse.Gateway.Infrastructure.Runtime;
using Flattiverse.Gateway.Infrastructure.Sessions;
using Flattiverse.Gateway.Infrastructure.Transport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Flattiverse.Gateway.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ConnectorOptions>(configuration.GetSection("Connector"));
        services.AddSingleton<IBrowserSessionStore, InMemoryBrowserSessionStore>();
        services.AddSingleton<IGatewayConnectionMessageQueue, InMemoryGatewayConnectionMessageQueue>();
        services.AddSingleton<IConnectorEventPipeline, ConnectorGatewayEventPipeline>();
        services.AddSingleton<ConnectorPlayerSessionPool>();
        services.AddSingleton<IPlayerSessionPool>(static serviceProvider => serviceProvider.GetRequiredService<ConnectorPlayerSessionPool>());
        services.AddSingleton<IConnectorPlayerSessionStore>(static serviceProvider => serviceProvider.GetRequiredService<ConnectorPlayerSessionPool>());
        services.AddSingleton<IGatewayMessageSerializer, SystemTextJsonGatewayMessageSerializer>();
        services.AddSingleton<IGatewayRuntime, ConnectorGatewayRuntime>();
        services.AddHostedService<GatewayTickWorker>();

        return services;
    }
}
using Flattiverse.Gateway.Application.Abstractions;
using Flattiverse.Gateway.Application.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Flattiverse.Gateway.Infrastructure.Runtime;

public sealed class GatewayTickWorker : BackgroundService
{
    private readonly IGatewayRuntime gatewayRuntime;
    private readonly GatewayOptions gatewayOptions;

    public GatewayTickWorker(IGatewayRuntime gatewayRuntime, IOptions<GatewayOptions> gatewayOptions)
    {
        this.gatewayRuntime = gatewayRuntime;
        this.gatewayOptions = gatewayOptions.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMilliseconds(Math.Max(1, gatewayOptions.TickIntervalMs));

        while (!stoppingToken.IsCancellationRequested)
        {
            await gatewayRuntime.TickAsync(stoppingToken);
            await Task.Delay(interval, stoppingToken);
        }
    }
}
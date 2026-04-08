using Flattiverse.Gateway.Application.Options;
using Flattiverse.Gateway.Contracts.Health;
using Flattiverse.Gateway.Host.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Flattiverse.Gateway.Host.Endpoints;

public static class GatewayEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapGatewayEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
            "/api/health",
            (IOptions<GatewayOptions> options, TimeProvider timeProvider) => TypedResults.Ok(
                new GatewayHealthResponse(
                    "ok",
                    options.Value.ProtocolVersion,
                    timeProvider.GetUtcNow())));

        endpoints.Map(
            "/ws",
            async context =>
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await context.Response.WriteAsync("Expected a WebSocket upgrade request.");
                    return;
                }

                var handler = context.RequestServices.GetRequiredService<WebSocketGatewayConnectionHandler>();
                await handler.HandleAsync(context, context.RequestAborted);
            });

        return endpoints;
    }
}
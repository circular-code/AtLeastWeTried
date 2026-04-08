using System.Net.Http.Json;
using Flattiverse.Gateway.Contracts.Health;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Flattiverse.Gateway.Tests.Integration;

public sealed class HealthEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> factory;

    public HealthEndpointTests(WebApplicationFactory<Program> factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task HealthEndpoint_ReturnsProtocolMetadata()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/health");
        var payload = await response.Content.ReadFromJsonAsync<GatewayHealthResponse>();

        response.EnsureSuccessStatusCode();
        Assert.NotNull(payload);
        Assert.Equal("ok", payload.Status);
        Assert.Equal("0.2.0", payload.ProtocolVersion);
    }
}
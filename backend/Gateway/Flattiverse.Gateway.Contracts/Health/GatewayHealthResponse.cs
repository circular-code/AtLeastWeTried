namespace Flattiverse.Gateway.Contracts.Health;

public sealed record GatewayHealthResponse(
    string Status,
    string ProtocolVersion,
    DateTimeOffset ServerTimeUtc);
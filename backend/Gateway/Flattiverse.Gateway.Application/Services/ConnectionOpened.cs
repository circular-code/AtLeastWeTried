using Flattiverse.Gateway.Contracts.Messages.Server;

namespace Flattiverse.Gateway.Application.Services;

public sealed record ConnectionOpened(
    string ConnectionId,
    IReadOnlyList<GatewayMessage> Messages);
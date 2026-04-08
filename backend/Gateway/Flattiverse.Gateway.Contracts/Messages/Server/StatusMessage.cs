namespace Flattiverse.Gateway.Contracts.Messages.Server;

public sealed record StatusMessage(
    GatewayStatusKind Kind,
    string Code,
    string Message,
    bool Recoverable) : GatewayMessage("status");
namespace Flattiverse.Gateway.Contracts.Messages.Server;

public sealed record OwnerDeltaMessage(
    string PlayerSessionId,
    IReadOnlyList<OverlayEvent> Events) : GatewayMessage("owner.delta");
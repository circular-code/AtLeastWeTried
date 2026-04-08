namespace Flattiverse.Gateway.Contracts.Messages.Server;

public sealed record SessionReadyMessage(
    string ConnectionId,
    string ProtocolVersion,
    bool ObserverOnly,
    IReadOnlyList<PlayerSessionSummary> PlayerSessions) : GatewayMessage("session.ready");
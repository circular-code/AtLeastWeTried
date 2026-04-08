namespace Flattiverse.Gateway.Contracts.Messages.Server;

public sealed record SnapshotFullMessage(WorldSnapshot Snapshot) : GatewayMessage("snapshot.full");
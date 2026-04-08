using System.Text.Json.Nodes;

namespace Flattiverse.Gateway.Contracts.Messages.Server;

public sealed record WorldDelta(
    string EventType,
    string EntityId,
    JsonObject? Changes);

public sealed record WorldDeltaMessage(IReadOnlyList<WorldDelta> Events) : GatewayMessage("world.delta");
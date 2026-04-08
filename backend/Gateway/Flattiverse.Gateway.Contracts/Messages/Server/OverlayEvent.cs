using System.Text.Json.Nodes;

namespace Flattiverse.Gateway.Contracts.Messages.Server;

public sealed record OverlayEvent(
    string EventType,
    string? ControllableId,
    JsonObject? Changes);
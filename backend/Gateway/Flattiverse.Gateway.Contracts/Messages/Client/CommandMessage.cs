using System.Text.Json.Nodes;

namespace Flattiverse.Gateway.Contracts.Messages.Client;

public sealed record CommandMessage(
    string MessageType,
    string? CommandId,
    JsonObject? Payload) : ClientMessage(MessageType);
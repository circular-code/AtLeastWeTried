namespace Flattiverse.Gateway.Contracts.Messages.Client;

public sealed record PongMessage() : ClientMessage("pong");
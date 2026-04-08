namespace Flattiverse.Gateway.Contracts.Messages.Client;

public sealed record PlayerSelectMessage(string? PlayerSessionId) : ClientMessage("player.select");
namespace Flattiverse.Gateway.Contracts.Messages.Client;

public sealed record ConnectionDetachMessage(string? PlayerSessionId) : ClientMessage("connection.detach");
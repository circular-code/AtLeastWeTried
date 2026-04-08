namespace Flattiverse.Gateway.Contracts.Messages.Server;

public sealed record ChatEntry(
    string MessageId,
    string Scope,
    string SenderDisplayName,
    string? PlayerSessionId,
    string Message,
    DateTimeOffset SentAtUtc);

public sealed record ChatReceivedMessage(ChatEntry Entry) : GatewayMessage("chat.received");
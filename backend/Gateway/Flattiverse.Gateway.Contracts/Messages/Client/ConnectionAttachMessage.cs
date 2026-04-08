namespace Flattiverse.Gateway.Contracts.Messages.Client;

public sealed record ConnectionAttachMessage(AttachPayload? Payload) : ClientMessage("connection.attach");
namespace Flattiverse.Gateway.Contracts.Messages.Client;

public sealed record AttachPayload(
    string? ApiKey,
    string? TeamName);
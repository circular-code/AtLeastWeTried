namespace Flattiverse.Gateway.Contracts.Messages.Server;

public sealed record PlayerSessionSummary(
    string PlayerSessionId,
    string DisplayName,
    bool Connected,
    bool Selected,
    string? TeamName);
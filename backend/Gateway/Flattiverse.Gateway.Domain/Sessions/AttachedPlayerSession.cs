namespace Flattiverse.Gateway.Domain.Sessions;

public sealed record AttachedPlayerSession(
    string PlayerSessionId,
    string DisplayName,
    string? TeamName,
    bool Connected);
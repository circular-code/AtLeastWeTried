namespace Flattiverse.Gateway.Contracts.Messages.Server;

public sealed record CommandError(
    string Code,
    string Message,
    bool Recoverable);
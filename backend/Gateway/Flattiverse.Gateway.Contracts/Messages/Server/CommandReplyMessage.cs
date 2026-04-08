namespace Flattiverse.Gateway.Contracts.Messages.Server;

public sealed record CommandReplyMessage(
    string CommandId,
    string Status,
    object? Result,
    CommandError? Error) : GatewayMessage("command.reply");
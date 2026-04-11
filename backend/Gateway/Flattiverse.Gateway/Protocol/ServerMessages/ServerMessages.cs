using Flattiverse.Gateway.Protocol.Dtos;

namespace Flattiverse.Gateway.Protocol.ServerMessages;

public abstract class ServerMessage
{
    public abstract string Type { get; set; }
}

public sealed class SessionReadyMessage : ServerMessage
{
    public override string Type { get; set; } = "session.ready";
    public string ConnectionId { get; set; } = "";
    public string ProtocolVersion { get; set; } = "0.2.0";
    public bool ObserverOnly { get; set; }
    public List<PlayerSessionSummaryDto> PlayerSessions { get; set; } = new();
}

public sealed class SnapshotFullMessage : ServerMessage
{
    public override string Type { get; set; } = "snapshot.full";
    public GalaxySnapshotDto Snapshot { get; set; } = new();
}

public sealed class WorldDeltaMessage : ServerMessage
{
    public override string Type { get; set; } = "world.delta";
    public List<WorldDeltaDto> Events { get; set; } = new();
}

public sealed class OwnerDeltaMessage : ServerMessage
{
    public override string Type { get; set; } = "owner.delta";
    public string PlayerSessionId { get; set; } = "";
    public List<OwnerOverlayDeltaDto> Events { get; set; } = new();
}

public sealed class ChatReceivedMessage : ServerMessage
{
    public override string Type { get; set; } = "chat.received";
    public ChatEntryDto Entry { get; set; } = new();
}

public sealed class CommandReplyMessage : ServerMessage
{
    public override string Type { get; set; } = "command.reply";
    public string CommandId { get; set; } = "";
    public string Status { get; set; } = "completed";
    public Dictionary<string, object?>? Result { get; set; }
    public ErrorInfoDto? Error { get; set; }
}

public sealed class ServerStatusMessage : ServerMessage
{
    public override string Type { get; set; } = "status";
    public string Kind { get; set; } = "info";
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
    public bool Recoverable { get; set; } = true;
}

public sealed class PingMessage : ServerMessage
{
    public override string Type { get; set; } = "ping";
}

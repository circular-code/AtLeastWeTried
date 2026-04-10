namespace Flattiverse.Gateway.Protocol.Dtos;

public sealed class TeamSnapshotDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int Score { get; set; }
    public string ColorHex { get; set; } = "#808080";
    public bool Playable { get; set; }
}

public sealed class ClusterSnapshotDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsStart { get; set; }
    public bool Respawns { get; set; }
}

public sealed class UnitSnapshotDto
{
    public string UnitId { get; set; } = "";
    public int ClusterId { get; set; }
    public string Kind { get; set; } = "";
    public bool FullStateKnown { get; set; }
    public bool IsStatic { get; set; }
    /// <summary>
    /// When false, the unit does not block navigation (e.g. mission targets). Null means unknown (legacy JSON) and is treated as solid for pathfinding.
    /// </summary>
    public bool? IsSolid { get; set; }
    public bool IsSeen { get; set; } = true;
    public uint LastSeenTick { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Angle { get; set; }
    public float Radius { get; set; }
    /// <summary>
    /// Connector gravity strength; used for navigation clearance (higher → stay farther). Omitted in legacy JSON deserializes as 0.
    /// </summary>
    public float Gravity { get; set; }
    public string? TeamName { get; set; }
    public float? SunEnergy { get; set; }
    public float? SunIons { get; set; }
    public float? SunNeutrinos { get; set; }
    public float? SunHeat { get; set; }
    public float? SunDrain { get; set; }
    public float? PlanetMetal { get; set; }
    public float? PlanetCarbon { get; set; }
    public float? PlanetHydrogen { get; set; }
    public float? PlanetSilicon { get; set; }
}

public sealed class PublicControllableSnapshotDto
{
    public string ControllableId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string TeamName { get; set; } = "";
    public bool Alive { get; set; }
    public int Score { get; set; }
}

public sealed class GalaxySnapshotDto
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string? GameMode { get; set; }
    public List<TeamSnapshotDto> Teams { get; set; } = new();
    public List<ClusterSnapshotDto> Clusters { get; set; } = new();
    public List<UnitSnapshotDto> Units { get; set; } = new();
    public List<PublicControllableSnapshotDto> Controllables { get; set; } = new();
}

public sealed class PlayerSessionSummaryDto
{
    public string PlayerSessionId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool Connected { get; set; }
    public bool Selected { get; set; }
    public string? TeamName { get; set; }
}

public sealed class ChatEntryDto
{
    public string MessageId { get; set; } = "";
    public string Scope { get; set; } = "galaxy";
    public string SenderDisplayName { get; set; } = "";
    public string? PlayerSessionId { get; set; }
    public string Message { get; set; } = "";
    public string SentAtUtc { get; set; } = "";
}

public sealed class ErrorInfoDto
{
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
    public bool Recoverable { get; set; }
}

public sealed class WorldDeltaDto
{
    public string EventType { get; set; } = "";
    public string EntityId { get; set; } = "";
    public Dictionary<string, object?>? Changes { get; set; }
}

public sealed class OwnerOverlayDeltaDto
{
    public string EventType { get; set; } = "";
    public string ControllableId { get; set; } = "";
    public Dictionary<string, object?>? Changes { get; set; }
}

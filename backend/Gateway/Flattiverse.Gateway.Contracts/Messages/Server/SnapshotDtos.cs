namespace Flattiverse.Gateway.Contracts.Messages.Server;

public sealed record TeamSnapshot(
    int Id,
    string Name,
    int Score,
    string ColorHex);

public sealed record ClusterSnapshot(
    int Id,
    string Name,
    bool IsStart,
    bool Respawns);

public sealed record UnitSnapshot(
    string UnitId,
    int ClusterId,
    string Kind,
    double X,
    double Y,
    double Angle,
    double Radius,
    string? TeamName,
    double? SunEnergy,
    double? SunIons,
    double? SunNeutrinos,
    double? SunHeat,
    double? SunDrain);

public sealed record PublicControllableSnapshot(
    string ControllableId,
    string DisplayName,
    string TeamName,
    bool Alive,
    int Score);
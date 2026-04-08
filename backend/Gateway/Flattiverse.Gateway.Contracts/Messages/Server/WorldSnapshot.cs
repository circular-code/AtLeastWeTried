namespace Flattiverse.Gateway.Contracts.Messages.Server;

public sealed record WorldSnapshot(
    string Name,
    string? Description,
    string GameMode,
    IReadOnlyList<TeamSnapshot> Teams,
    IReadOnlyList<ClusterSnapshot> Clusters,
    IReadOnlyList<UnitSnapshot> Units,
    IReadOnlyList<PublicControllableSnapshot> Controllables);
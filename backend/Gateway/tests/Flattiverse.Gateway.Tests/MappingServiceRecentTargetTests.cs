using Flattiverse.Gateway.Protocol.Dtos;
using Flattiverse.Gateway.Services;

namespace Flattiverse.Gateway.Tests;

public sealed class MappingServiceRecentTargetTests
{
    [Fact]
    public void Recent_target_snapshot_is_returned_for_matching_cluster()
    {
        var galaxyId = $"test-galaxy-{Guid.NewGuid():N}";
        var mapping = new MappingService(() => new MappingService.MappingScopeContext(galaxyId, 7, null));
        var snapshot = BuildTargetSnapshot("p2-c4", clusterId: 7, x: 42f, y: -18f);

        MappingService.RecordRecentTargetSnapshot(galaxyId, snapshot, currentTick: 125u);

        Assert.True(mapping.TryGetRecentGalaxyUnitSnapshot(snapshot.UnitId, clusterId: 7, out var resolved));
        Assert.NotNull(resolved);
        Assert.False(resolved!.IsSeen);
        Assert.False(resolved.IsStatic);
        Assert.Equal(snapshot.ClusterId, resolved.ClusterId);
        Assert.Equal(snapshot.X, resolved.X, precision: 3);
        Assert.Equal(snapshot.Y, resolved.Y, precision: 3);
    }

    [Fact]
    public void Recent_target_snapshot_respects_cluster_filter()
    {
        var galaxyId = $"test-galaxy-{Guid.NewGuid():N}";
        var mapping = new MappingService(() => new MappingService.MappingScopeContext(galaxyId, 3, null));
        var snapshot = BuildTargetSnapshot("p9-c1", clusterId: 3, x: 10f, y: 20f);

        MappingService.RecordRecentTargetSnapshot(galaxyId, snapshot, currentTick: 40u);

        Assert.False(mapping.TryGetRecentGalaxyUnitSnapshot(snapshot.UnitId, clusterId: 8, out _));
    }

    [Fact]
    public void Recent_target_snapshot_expires_after_retention_window()
    {
        var galaxyId = $"test-galaxy-{Guid.NewGuid():N}";
        var snapshot = BuildTargetSnapshot("p5-c2", clusterId: 6, x: 5f, y: 15f);

        MappingService.RecordRecentTargetSnapshot(galaxyId, snapshot, currentTick: 10u);

        Assert.True(MappingService.TryGetRecentTargetSnapshot(galaxyId, snapshot.UnitId, clusterId: 6, currentTick: 82u, out var retained));
        Assert.NotNull(retained);
        Assert.False(MappingService.TryGetRecentTargetSnapshot(galaxyId, snapshot.UnitId, clusterId: 6, currentTick: 83u, out _));
    }

    [Fact]
    public void Marking_a_player_unit_unseen_preserves_its_live_snapshot()
    {
        var snapshot = BuildTargetSnapshot("p4-c9", clusterId: 2, x: -12f, y: 8f);
        Dictionary<string, UnitSnapshotDto> unitsById = new(StringComparer.Ordinal)
        {
            [snapshot.UnitId] = snapshot
        };

        Assert.True(MappingService.TryMarkUnitUnseen(unitsById, snapshot.UnitId, currentTick: 55u, out var updated));
        Assert.NotNull(updated);
        Assert.Same(snapshot, updated);
        Assert.True(unitsById.ContainsKey(snapshot.UnitId));
        Assert.False(updated!.IsSeen);
        Assert.False(updated.IsStatic);
    }

    private static UnitSnapshotDto BuildTargetSnapshot(string unitId, int clusterId, float x, float y)
    {
        return new UnitSnapshotDto
        {
            UnitId = unitId,
            ClusterId = clusterId,
            Kind = "classic-ship",
            FullStateKnown = true,
            IsStatic = false,
            IsSeen = true,
            LastSeenTick = 9,
            X = x,
            Y = y,
            MovementX = 1.5f,
            MovementY = -0.5f,
            Angle = 15f,
            Radius = 4f,
            CurrentThrust = 3f,
            MaximumThrust = 6f
        };
    }
}

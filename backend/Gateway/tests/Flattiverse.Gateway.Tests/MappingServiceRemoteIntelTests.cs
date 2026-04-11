using Flattiverse.Gateway.Protocol.Dtos;
using Flattiverse.Gateway.Services;

namespace Flattiverse.Gateway.Tests;

public sealed class MappingServiceRemoteIntelTests
{
    [Fact]
    public void Remote_intel_is_visible_in_collaborative_snapshot_but_not_local_export()
    {
        var galaxyId = $"remote-galaxy-{Guid.NewGuid():N}";
        var teamName = $"team-{Guid.NewGuid():N}";
        var mapping = new MappingService(() => new MappingService.MappingScopeContext(galaxyId, 5, 11, teamName));
        var remoteSnapshot = BuildRemoteSnapshot("enemy-ship-7", clusterId: 5, x: 120f, y: -42f);

        mapping.ApplyRemoteIntel(
            sourcePlayerKey: "17",
            changes: new[]
            {
                new FriendlyIntelSyncService.IntelChange(
                    FriendlyIntelSyncService.IntelChangeKind.Upsert,
                    remoteSnapshot.UnitId,
                    remoteSnapshot,
                    "sig-1")
            });

        var collaborative = mapping.BuildGalaxyUnitSnapshots();
        var localOnly = mapping.BuildLocalGalaxyUnitSnapshots();

        Assert.Single(collaborative);
        Assert.Empty(localOnly);
        Assert.True(mapping.TryGetGalaxyUnitSnapshot(remoteSnapshot.UnitId, out var resolved));
        Assert.NotNull(resolved);
        Assert.False(resolved!.IsSeen);
        Assert.Equal(remoteSnapshot.X, resolved.X, precision: 3);
        Assert.Equal(remoteSnapshot.Y, resolved.Y, precision: 3);
        Assert.Equal(remoteSnapshot.PredictedTrajectory!.Count, resolved.PredictedTrajectory!.Count);
    }

    [Fact]
    public void Remote_intel_remove_deletes_source_snapshot()
    {
        var galaxyId = $"remote-galaxy-{Guid.NewGuid():N}";
        var teamName = $"team-{Guid.NewGuid():N}";
        var mapping = new MappingService(() => new MappingService.MappingScopeContext(galaxyId, 8, 9, teamName));
        var remoteSnapshot = BuildRemoteSnapshot("target-1", clusterId: 8, x: 5f, y: 6f);

        mapping.ApplyRemoteIntel(
            sourcePlayerKey: "22",
            changes: new[]
            {
                new FriendlyIntelSyncService.IntelChange(
                    FriendlyIntelSyncService.IntelChangeKind.Upsert,
                    remoteSnapshot.UnitId,
                    remoteSnapshot,
                    "sig-a")
            });

        mapping.ApplyRemoteIntel(
            sourcePlayerKey: "22",
            changes: new[]
            {
                new FriendlyIntelSyncService.IntelChange(
                    FriendlyIntelSyncService.IntelChangeKind.Remove,
                    remoteSnapshot.UnitId,
                    null,
                    null)
            });

        Assert.Empty(mapping.BuildGalaxyUnitSnapshots());
        Assert.False(mapping.TryGetGalaxyUnitSnapshot(remoteSnapshot.UnitId, out _));
    }

    private static UnitSnapshotDto BuildRemoteSnapshot(string unitId, int clusterId, float x, float y)
    {
        return new UnitSnapshotDto
        {
            UnitId = unitId,
            ClusterId = clusterId,
            Kind = "classic-ship",
            FullStateKnown = true,
            IsStatic = false,
            IsSolid = true,
            IsSeen = false,
            LastSeenTick = 144,
            X = x,
            Y = y,
            MovementX = 1.25f,
            MovementY = -0.75f,
            Angle = 24f,
            Radius = 3f,
            Gravity = 0f,
            CurrentThrust = 2f,
            MaximumThrust = 4f,
            TeamName = "Enemies",
            PredictedTrajectory = new List<TrajectoryPointDto>
            {
                new TrajectoryPointDto { X = x + 3f, Y = y + 1f },
                new TrajectoryPointDto { X = x + 6f, Y = y + 2f }
            }
        };
    }
}

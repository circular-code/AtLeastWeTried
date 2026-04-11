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

    [Fact]
    public void Collaborative_cluster_snapshot_uses_shared_team_intel_outside_current_scope()
    {
        var galaxyId = $"remote-galaxy-{Guid.NewGuid():N}";
        var teamName = $"team-{Guid.NewGuid():N}";
        var mapping = new MappingService(() => new MappingService.MappingScopeContext(galaxyId, 5, 11, teamName));
        var remoteSnapshot = BuildRemoteSnapshot("cluster-8-rock", clusterId: 8, x: 300f, y: -120f);
        remoteSnapshot.Kind = "meteoroid";
        remoteSnapshot.IsStatic = true;
        remoteSnapshot.IsSeen = true;
        remoteSnapshot.Radius = 18f;
        remoteSnapshot.Gravity = 0.25f;

        mapping.ApplyRemoteIntel(
            sourcePlayerKey: "17",
            changes: new[]
            {
                new FriendlyIntelSyncService.IntelChange(
                    FriendlyIntelSyncService.IntelChangeKind.Upsert,
                    remoteSnapshot.UnitId,
                    remoteSnapshot,
                    "sig-cluster-8")
            });

        Assert.Empty(mapping.BuildUnitSnapshots());

        var sharedCluster = mapping.BuildCollaborativeClusterUnitSnapshots(clusterId: 8);

        var resolved = Assert.Single(sharedCluster);
        Assert.Equal(remoteSnapshot.UnitId, resolved.UnitId);
        Assert.Equal(8, resolved.ClusterId);
        Assert.True(resolved.IsSeen);
        Assert.True(resolved.IsStatic);
        Assert.Equal(remoteSnapshot.Gravity, resolved.Gravity, precision: 3);
    }

    [Fact]
    public void Remote_intel_remove_only_drops_the_removed_source()
    {
        var galaxyId = $"remote-galaxy-{Guid.NewGuid():N}";
        var teamName = $"team-{Guid.NewGuid():N}";
        var mapping = new MappingService(() => new MappingService.MappingScopeContext(galaxyId, 5, 11, teamName));
        var firstSnapshot = BuildRemoteSnapshot("shared-target", clusterId: 5, x: 10f, y: 20f);
        var secondSnapshot = BuildRemoteSnapshot("shared-target", clusterId: 5, x: 12f, y: 22f);
        secondSnapshot.LastSeenTick = firstSnapshot.LastSeenTick + 5;

        mapping.ApplyRemoteIntel(
            sourcePlayerKey: "17",
            changes: new[]
            {
                new FriendlyIntelSyncService.IntelChange(
                    FriendlyIntelSyncService.IntelChangeKind.Upsert,
                    firstSnapshot.UnitId,
                    firstSnapshot,
                    "sig-17")
            });

        mapping.ApplyRemoteIntel(
            sourcePlayerKey: "21",
            changes: new[]
            {
                new FriendlyIntelSyncService.IntelChange(
                    FriendlyIntelSyncService.IntelChangeKind.Upsert,
                    secondSnapshot.UnitId,
                    secondSnapshot,
                    "sig-21")
            });

        mapping.ApplyRemoteIntel(
            sourcePlayerKey: "17",
            changes: new[]
            {
                new FriendlyIntelSyncService.IntelChange(
                    FriendlyIntelSyncService.IntelChangeKind.Remove,
                    firstSnapshot.UnitId,
                    null,
                    null)
            });

        Assert.True(mapping.TryGetGalaxyUnitSnapshot(firstSnapshot.UnitId, out var resolved));
        Assert.NotNull(resolved);
        Assert.Equal(secondSnapshot.X, resolved!.X, precision: 3);
        Assert.Equal(secondSnapshot.Y, resolved.Y, precision: 3);
    }

    [Fact]
    public void Remote_intel_cluster_move_rehomes_the_source_snapshot()
    {
        var galaxyId = $"remote-galaxy-{Guid.NewGuid():N}";
        var teamName = $"team-{Guid.NewGuid():N}";
        var mapping = new MappingService(() => new MappingService.MappingScopeContext(galaxyId, 5, 11, teamName));
        var firstSnapshot = BuildRemoteSnapshot("moving-target", clusterId: 5, x: 40f, y: 50f);
        var movedSnapshot = BuildRemoteSnapshot("moving-target", clusterId: 8, x: 140f, y: -10f);
        movedSnapshot.LastSeenTick = firstSnapshot.LastSeenTick + 1;

        mapping.ApplyRemoteIntel(
            sourcePlayerKey: "17",
            changes: new[]
            {
                new FriendlyIntelSyncService.IntelChange(
                    FriendlyIntelSyncService.IntelChangeKind.Upsert,
                    firstSnapshot.UnitId,
                    firstSnapshot,
                    "sig-start")
            });

        mapping.ApplyRemoteIntel(
            sourcePlayerKey: "17",
            changes: new[]
            {
                new FriendlyIntelSyncService.IntelChange(
                    FriendlyIntelSyncService.IntelChangeKind.Upsert,
                    movedSnapshot.UnitId,
                    movedSnapshot,
                    "sig-moved")
            });

        Assert.Empty(mapping.BuildCollaborativeClusterUnitSnapshots(clusterId: 5)
            .Where(unit => unit.UnitId == firstSnapshot.UnitId));

        var moved = Assert.Single(mapping.BuildCollaborativeClusterUnitSnapshots(clusterId: 8)
            .Where(unit => unit.UnitId == movedSnapshot.UnitId));

        Assert.Equal(8, moved.ClusterId);
        Assert.Equal(movedSnapshot.X, moved.X, precision: 3);
        Assert.Equal(movedSnapshot.Y, moved.Y, precision: 3);
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

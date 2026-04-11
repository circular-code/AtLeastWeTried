using Flattiverse.Gateway.Protocol.Dtos;
using Flattiverse.Gateway.Services;

namespace Flattiverse.Gateway.Tests;

public sealed class FriendlyIntelSyncServiceTests
{
    [Fact]
    public void Codec_round_trips_upserts_and_removals()
    {
        var galaxyId = $"codec-galaxy-{Guid.NewGuid():N}";
        var snapshot = new UnitSnapshotDto
        {
            UnitId = "enemy-14",
            ClusterId = 3,
            Kind = "modern-ship",
            FullStateKnown = true,
            IsStatic = false,
            IsSolid = true,
            IsSeen = false,
            LastSeenTick = 91,
            X = 15.5f,
            Y = -8.25f,
            MovementX = 0.4f,
            MovementY = 1.8f,
            Angle = 270f,
            Radius = 4.5f,
            Gravity = 0.2f,
            SpeedLimit = 12f,
            CurrentThrust = 3f,
            MaximumThrust = 6f,
            TeamName = "Red",
            PredictedTrajectory = new List<TrajectoryPointDto>
            {
                new TrajectoryPointDto { X = 16f, Y = -7f },
                new TrajectoryPointDto { X = 17f, Y = -5.5f }
            },
            MissionTargetSequenceNumber = 7,
            MissionTargetVectorCount = 2,
            MissionTargetVectors = new List<TrajectoryPointDto>
            {
                new TrajectoryPointDto { X = 18f, Y = -4f },
                new TrajectoryPointDto { X = 19f, Y = -3f }
            },
            WormHoleTargetClusterName = "Echo Rift",
            WormHoleTargetLeft = -12f,
            WormHoleTargetTop = 44f,
            WormHoleTargetRight = 8f,
            WormHoleTargetBottom = 60f,
            CurrentFieldMode = "spiral",
            CurrentFieldFlowX = 0.25f,
            CurrentFieldFlowY = -0.75f,
            PowerUpAmount = 2f,
            ScannedSubsystems = new List<ScannedSubsystemDto>
            {
                new ScannedSubsystemDto
                {
                    Id = "scanner-a",
                    Name = "Main Scanner",
                    Exists = true,
                    Status = "Worked",
                    Stats = new List<ScannedSubsystemStatDto>
                    {
                        new ScannedSubsystemStatDto { Label = "Range", Value = "42" }
                    }
                }
            }
        };

        var payload = FriendlyIntelSyncService.EncodeForTests(
            galaxyId,
            new[]
            {
                new FriendlyIntelSyncService.IntelChange(
                    FriendlyIntelSyncService.IntelChangeKind.Upsert,
                    snapshot.UnitId,
                    snapshot,
                    "sig-upsert"),
                new FriendlyIntelSyncService.IntelChange(
                    FriendlyIntelSyncService.IntelChangeKind.Remove,
                    "obsolete-unit",
                    null,
                    null)
            });

        Assert.True(FriendlyIntelSyncService.TryDecodeForTests(galaxyId, payload, out var decoded));
        Assert.Equal(2, decoded.Count);

        var upsert = decoded[0];
        Assert.Equal(FriendlyIntelSyncService.IntelChangeKind.Upsert, upsert.Kind);
        Assert.NotNull(upsert.Snapshot);
        Assert.False(upsert.Snapshot!.IsSeen);
        Assert.Equal(snapshot.ClusterId, upsert.Snapshot.ClusterId);
        Assert.Equal(snapshot.X, upsert.Snapshot.X, precision: 3);
        Assert.Equal(snapshot.Y, upsert.Snapshot.Y, precision: 3);
        Assert.Equal(snapshot.PredictedTrajectory!.Count, upsert.Snapshot.PredictedTrajectory!.Count);
        Assert.Equal(snapshot.MissionTargetSequenceNumber, upsert.Snapshot.MissionTargetSequenceNumber);
        Assert.Equal(snapshot.MissionTargetVectors!.Count, upsert.Snapshot.MissionTargetVectors!.Count);
        Assert.Equal(snapshot.WormHoleTargetClusterName, upsert.Snapshot.WormHoleTargetClusterName);
        Assert.Equal(snapshot.CurrentFieldMode, upsert.Snapshot.CurrentFieldMode);
        Assert.Equal(snapshot.ScannedSubsystems![0].Name, upsert.Snapshot.ScannedSubsystems![0].Name);

        var remove = decoded[1];
        Assert.Equal(FriendlyIntelSyncService.IntelChangeKind.Remove, remove.Kind);
        Assert.Equal("obsolete-unit", remove.UnitId);
        Assert.Null(remove.Snapshot);
    }

    [Fact]
    public void Local_team_registry_uses_lowest_player_id_as_scope_leader()
    {
        var scopeKey = $"scope-{Guid.NewGuid():N}";
        const string firstSessionId = "ps-high";
        const string secondSessionId = "ps-low";

        try
        {
            LocalTeamSessionRegistry.Register(scopeKey, firstSessionId, playerId: 14);
            LocalTeamSessionRegistry.Register(scopeKey, secondSessionId, playerId: 6);

            Assert.True(LocalTeamSessionRegistry.IsLocallyManagedPlayer(scopeKey, 14));
            Assert.True(LocalTeamSessionRegistry.IsLocallyManagedPlayer(scopeKey, 6));
            Assert.False(LocalTeamSessionRegistry.IsLeader(scopeKey, firstSessionId));
            Assert.True(LocalTeamSessionRegistry.IsLeader(scopeKey, secondSessionId));
        }
        finally
        {
            LocalTeamSessionRegistry.RemoveSession(firstSessionId);
            LocalTeamSessionRegistry.RemoveSession(secondSessionId);
        }
    }

    [Fact]
    public void Shareable_unit_merge_prefers_owned_snapshot_without_duplication()
    {
        var mappedSnapshot = new UnitSnapshotDto
        {
            UnitId = "p4-c2",
            ClusterId = 5,
            Kind = "classic-ship",
            FullStateKnown = true,
            IsStatic = false,
            IsSolid = true,
            IsSeen = true,
            LastSeenTick = 10,
            X = 12f,
            Y = 18f,
            Radius = 3f,
            Gravity = 0.1f
        };

        var asteroidSnapshot = new UnitSnapshotDto
        {
            UnitId = "cluster/5/unit/asteroid-1",
            ClusterId = 5,
            Kind = "meteoroid",
            FullStateKnown = true,
            IsStatic = true,
            IsSolid = true,
            IsSeen = true,
            LastSeenTick = 10,
            X = 40f,
            Y = 41f,
            Radius = 7f,
            Gravity = 0.5f
        };

        var ownedSnapshot = new UnitSnapshotDto
        {
            UnitId = "p4-c2",
            ClusterId = 7,
            Kind = "classic-ship",
            FullStateKnown = true,
            IsStatic = false,
            IsSolid = true,
            IsSeen = true,
            LastSeenTick = 42,
            X = 99f,
            Y = -4f,
            Radius = 3f,
            Gravity = 0.1f,
            TeamName = "Blue"
        };

        var merged = FriendlyIntelSyncService.MergeShareableUnits(
            new[] { mappedSnapshot, asteroidSnapshot },
            new[] { ownedSnapshot });

        Assert.Equal(2, merged.Count);

        var sharedOwned = Assert.Single(merged.Where(unit => unit.UnitId == "p4-c2"));
        Assert.Equal(7, sharedOwned.ClusterId);
        Assert.Equal(99f, sharedOwned.X);
        Assert.Equal(-4f, sharedOwned.Y);

        Assert.Contains(merged, unit => unit.UnitId == asteroidSnapshot.UnitId);
    }
}

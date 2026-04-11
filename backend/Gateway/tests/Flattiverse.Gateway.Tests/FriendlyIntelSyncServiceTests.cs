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
}

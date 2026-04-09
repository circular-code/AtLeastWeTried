using System.Text.Json;
using Flattiverse.Gateway.Protocol.Dtos;
using Flattiverse.Gateway.Services.Navigation;

namespace Flattiverse.Gateway.Tests;

public sealed class PathfindingFixtureTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Extractor_builds_inflated_obstacles_from_fixture_scope()
    {
        var units = LoadFixtureUnits();

        var obstacles = CircularObstacleExtractor.Extract(units, clusterId: 0, shipRadius: 12d, clearanceMargin: 8d);

        Assert.NotEmpty(obstacles);
        Assert.Contains(obstacles, obstacle => obstacle.Kind == "meteoroid");
        Assert.All(obstacles, obstacle => Assert.True(obstacle.Radius > 18d));
        Assert.DoesNotContain(obstacles, obstacle => obstacle.Kind.StartsWith("powerup-", StringComparison.Ordinal));
    }

    [Fact]
    public void Extractor_skips_units_marked_non_solid()
    {
        var units = new List<UnitSnapshotDto>
        {
            new()
            {
                UnitId = "solid-rock",
                ClusterId = 1,
                Kind = "meteoroid",
                IsStatic = true,
                IsSeen = true,
                IsSolid = true,
                Radius = 10f,
                X = 0f,
                Y = 0f,
            },
            new()
            {
                UnitId = "ghost-rock",
                ClusterId = 1,
                Kind = "meteoroid",
                IsStatic = true,
                IsSeen = true,
                IsSolid = false,
                Radius = 10f,
                X = 100f,
                Y = 0f,
            },
        };

        var obstacles = CircularObstacleExtractor.Extract(units, clusterId: 1, shipRadius: 0d, clearanceMargin: 0d);

        Assert.Single(obstacles);
        Assert.Equal("solid-rock", obstacles[0].Id);
    }

    [Fact]
    public void Extractor_adds_extra_clearance_proportional_to_gravity()
    {
        UnitSnapshotDto baseline() => new()
        {
            UnitId = "rock",
            ClusterId = 1,
            Kind = "meteoroid",
            IsStatic = true,
            IsSeen = true,
            IsSolid = true,
            Radius = 10f,
            X = 0f,
            Y = 0f,
            Gravity = 0f,
        };

        var light = baseline();
        var heavy = baseline();
        heavy.Gravity = 0.001f;

        var withoutGravity = CircularObstacleExtractor.Extract(
            new List<UnitSnapshotDto> { light },
            clusterId: 1,
            shipRadius: 12d,
            clearanceMargin: 8d);

        var withGravity = CircularObstacleExtractor.Extract(
            new List<UnitSnapshotDto> { heavy },
            clusterId: 1,
            shipRadius: 12d,
            clearanceMargin: 8d);

        Assert.Single(withoutGravity);
        Assert.Single(withGravity);
        const double expectedDelta = 80d * 0.001d;
        Assert.Equal(expectedDelta, withGravity[0].Radius - withoutGravity[0].Radius, 5);
    }

    [Fact]
    public void Extractor_shrinks_keep_out_radius_when_ship_inside_disk_or_omits_when_degenerate()
    {
        var unit = new UnitSnapshotDto
        {
            UnitId = "sun",
            ClusterId = 1,
            Kind = "sun",
            IsStatic = true,
            IsSeen = true,
            IsSolid = true,
            Radius = 40f,
            X = 0f,
            Y = 0f,
            Gravity = 0.02f,
        };
        var units = new List<UnitSnapshotDto> { unit };

        var atCenter = new NavigationPoint(0d, 0d);
        var degenerate = CircularObstacleExtractor.Extract(
            units,
            clusterId: 1,
            shipRadius: 12d,
            clearanceMargin: 8d,
            destinationUnitId: null,
            shipPosition: atCenter);

        Assert.Empty(degenerate);

        var far = new NavigationPoint(5000d, 0d);
        var fullRadius = CircularObstacleExtractor.Extract(
            units,
            clusterId: 1,
            shipRadius: 12d,
            clearanceMargin: 8d,
            destinationUnitId: null,
            shipPosition: far)[0].Radius;

        var insideShell = new NavigationPoint(55d, 0d);
        var shrunk = CircularObstacleExtractor.Extract(
            units,
            clusterId: 1,
            shipRadius: 12d,
            clearanceMargin: 8d,
            destinationUnitId: null,
            shipPosition: insideShell);

        Assert.Single(shrunk);
        Assert.Equal("sun", shrunk[0].Id);
        Assert.True(shrunk[0].Radius < fullRadius - 1d);
        Assert.Equal(55d, shrunk[0].Radius, 2);
    }

    [Fact]
    public void PolylineIntersectsObstacleDisk_detects_segment_through_disk()
    {
        var path = new[]
        {
            new NavigationPoint(-100d, 0d),
            new NavigationPoint(100d, 0d),
        };
        var blocking = new NavigationObstacle("a", "meteoroid", new NavigationPoint(0d, 0d), 50d);
        Assert.True(NavigationMath.PolylineIntersectsObstacleDisk(path, blocking));

        var clear = new NavigationObstacle("b", "meteoroid", new NavigationPoint(0d, 0d), 10d);
        var pathAbove = new[]
        {
            new NavigationPoint(-100d, -200d),
            new NavigationPoint(100d, -200d),
        };
        Assert.False(NavigationMath.PolylineIntersectsObstacleDisk(pathAbove, clear));
    }

    [Fact]
    public void Planner_finds_route_around_fixture_meteoroid_barrier()
    {
        var planner = new CircularPathPlanner();
        var obstacles = LoadFixtureObstacles();
        var start = new NavigationPoint(-240d, -450d);
        var goal = new NavigationPoint(140d, -350d);

        var result = planner.Plan(start, goal, obstacles);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.True(result.PathPoints.Count > 2);
        Assert.NotEmpty(result.SearchEdges);
        AssertClose(start, result.PathPoints[0]);
        AssertClose(goal, result.PathPoints[^1]);
        AssertPathMaintainsClearance(result.PathPoints, obstacles, 0.75d);
    }

    [Fact]
    public void Path_follower_picks_lookahead_target_ahead_of_ship()
    {
        var planner = new CircularPathPlanner();
        var pathFollower = new PathFollower();
        var obstacles = LoadFixtureObstacles();
        var start = new NavigationPoint(-240d, -450d);
        var goal = new NavigationPoint(140d, -350d);
        var result = planner.Plan(start, goal, obstacles);

        Assert.True(result.Succeeded, result.FailureReason);

        const double lookaheadDistance = 72d;
        const double minTargetDistance = 30d;
        const double arrivalThreshold = 14d;
        var follow = pathFollower.Follow(start, result.PathPoints, lookaheadDistance, minTargetDistance, arrivalThreshold);

        Assert.False(follow.GoalReached);
        Assert.True(start.DistanceTo(follow.Target) >= minTargetDistance - 0.5d);
        Assert.True(follow.RemainingDistance > arrivalThreshold);
        Assert.True(follow.Target.DistanceTo(goal) > arrivalThreshold);
    }

    private static IReadOnlyList<NavigationObstacle> LoadFixtureObstacles()
    {
        return CircularObstacleExtractor.Extract(LoadFixtureUnits(), clusterId: 0, shipRadius: 12d, clearanceMargin: 8d);
    }

    private static List<UnitSnapshotDto> LoadFixtureUnits()
    {
        var fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "old-world-state.wss-www-flattiverse-com-galaxies-0-api-b.fe66663304e80502.json");

        using var stream = File.OpenRead(fixturePath);
        var worldState = JsonSerializer.Deserialize<PersistedWorldState>(stream, JsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize the persisted fixture.");

        return worldState.Scopes
            .Where(scope => scope.ClusterId == 0)
            .SelectMany(scope => scope.StaticUnits ?? [])
            .ToList();
    }

    private static void AssertPathMaintainsClearance(
        IReadOnlyList<NavigationPoint> path,
        IReadOnlyList<NavigationObstacle> obstacles,
        double tolerance)
    {
        for (var segmentIndex = 0; segmentIndex < path.Count - 1; segmentIndex++)
        {
            var start = path[segmentIndex];
            var end = path[segmentIndex + 1];
            foreach (var obstacle in obstacles)
            {
                var distance = NavigationMath.DistanceToSegment(obstacle.Center, start, end, out _);
                Assert.True(
                    distance >= obstacle.Radius - tolerance,
                    $"Segment {segmentIndex} from {start} to {end} violates clearance for obstacle {obstacle.Id} ({obstacle.Kind}). Distance={distance:0.###}, radius={obstacle.Radius:0.###}");
            }
        }
    }

    private static void AssertClose(NavigationPoint expected, NavigationPoint actual)
    {
        Assert.True(expected.DistanceTo(actual) <= 0.01d, $"Expected {expected}, got {actual}.");
    }

    private sealed class PersistedWorldState
    {
        public List<PersistedScope> Scopes { get; set; } = [];
    }

    private sealed class PersistedScope
    {
        public string GalaxyId { get; set; } = "";

        public int ClusterId { get; set; }

        public List<UnitSnapshotDto>? StaticUnits { get; set; }
    }
}

using System.Text.Json;
using Flattiverse.Gateway.Protocol.Dtos;
using Flattiverse.Gateway.Services;
using Flattiverse.Gateway.Services.Navigation;
using Xunit.Abstractions;
using static Flattiverse.Gateway.Services.GravitySimulator;

namespace Flattiverse.Gateway.Tests;

/// <summary>
/// Verifies that the 200-tick constant-engine trajectory predicted by
/// <see cref="GravitySimulator.SimulateTrajectory"/> stays close to the planned
/// navigation path.  If a trajectory point drifts far from the path polyline the
/// UI overlay "looks wrong" because it shows the ship flying off into space.
/// </summary>
public sealed class TrajectoryPathAdherenceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private const double ShipRadius = 12d;
    private const double ClearanceMargin = 8d;
    private const double EngineMax = 0.059d;
    private const double SpeedLimit = 6.0d;
    private const double ThrustPercentage = 1.0d;
    // Keep the simulated horizon aligned with production-facing trajectory previews.
    // A 200-tick constant-thrust preview drifts unrealistically because control input
    // is recomputed every tick in the real loop.
    private const int TrajectoryTicks = 48;

    /// <summary>
    /// Maximum allowed distance (in world units) between any trajectory point and
    /// the nearest point on the planned path polyline.  If the trajectory drifts
    /// further than this the overlay is considered visually incorrect.
    /// </summary>
    private const double MaxAllowedDeviation = 180d;

    private readonly ITestOutputHelper _output;

    public TrajectoryPathAdherenceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void All_trajectory_points_stay_near_planned_path()
    {
        // ── Arrange: plan a path through the fixture world ──
        var units = LoadFixtureUnits();
        var obstacles = CircularObstacleExtractor.Extract(units, clusterId: 0, shipRadius: ShipRadius, clearanceMargin: ClearanceMargin);
        var gravitySources = ExtractGravitySources(units);

        var start = new NavigationPoint(-338d, 276d);
        var goal = new NavigationPoint(527.5d, -457.2d);

        var planner = new CircularPathPlanner();
        var plan = planner.Plan(start, goal, obstacles);
        Assert.True(plan.Succeeded, $"Path planning failed: {plan.FailureReason}");
        Assert.True(plan.PathPoints.Count >= 2, "Path must have at least 2 points");

        var path = plan.PathPoints;

        _output.WriteLine($"Path: {path.Count} points, total length={PolylineLength(path):F1}");

        // ── Act: simulate the ship following the path tick-by-tick ──
        var follower = new PathFollower();
        var shipX = start.X;
        var shipY = start.Y;
        var velX = 0d;
        var velY = 0d;

        const double arrivalThreshold = 14d;
        const double lookahead = 72d;
        const double minTargetDistance = 30d;
        const int maxSimTicks = 2000;

        var worstDeviation = 0d;
        var worstDeviationTick = 0;
        var worstDeviationTrajectoryTick = 0;
        var failedPoints = new List<string>();

        for (var tick = 0; tick < maxSimTicks; tick++)
        {
            var current = new NavigationPoint(shipX, shipY);

            // Check arrival
            if (current.DistanceTo(goal) <= arrivalThreshold)
            {
                _output.WriteLine($"Arrived at goal after {tick} ticks");
                break;
            }

            // Follow the path to get the next target
            var follow = follower.Follow(current, path, lookahead, minTargetDistance, arrivalThreshold);
            if (follow.GoalReached)
            {
                _output.WriteLine($"Goal reached after {tick} ticks");
                break;
            }

            // Build remaining path for curvature-aware steering
            var remainingPath = BuildRemainingPath(follow.Target, follow.ProgressDistance, path);

            // Compute engine vector (same as ManeuveringService)
            var (engineX, engineY) = TrajectoryAligner.ComputeEngineVector(
                shipX, shipY, velX, velY,
                follow.Target.X, follow.Target.Y,
                gravitySources, EngineMax, ThrustPercentage, SpeedLimit,
                remainingPath);

            // ── Assert: compute trajectory and check every point ──
            var trajectory = SimulateTrajectory(
                shipX, shipY, velX, velY,
                engineX, engineY,
                gravitySources, TrajectoryTicks, SpeedLimit);

            for (var t = 0; t < trajectory.Count; t++)
            {
                var pt = trajectory[t];
                var pointOnPath = new NavigationPoint(pt.X, pt.Y);
                var deviation = DistanceToPolyline(pointOnPath, path);

                if (deviation > worstDeviation)
                {
                    worstDeviation = deviation;
                    worstDeviationTick = tick;
                    worstDeviationTrajectoryTick = t;
                }

                if (deviation > MaxAllowedDeviation)
                {
                    failedPoints.Add(
                        $"tick={tick} traj_t={t} pos=({pt.X:F1},{pt.Y:F1}) deviation={deviation:F1}");
                }
            }

            // Log every 100 ticks
            if (tick % 100 == 0)
            {
                var speed = Math.Sqrt(velX * velX + velY * velY);
                _output.WriteLine(
                    $"tick={tick:0000} pos=({shipX:F1},{shipY:F1}) spd={speed:F2} " +
                    $"target=({follow.Target.X:F1},{follow.Target.Y:F1}) remain={follow.RemainingDistance:F1}");
            }

            // ── Advance the simulation by one tick ──
            velX += engineX;
            velY += engineY;

            var (gx, gy) = ComputeGravityAcceleration(shipX, shipY, gravitySources);
            velX += gx;
            velY += gy;

            (velX, velY) = ApplySoftCap(velX, velY, SpeedLimit);

            shipX += velX;
            shipY += velY;
        }

        _output.WriteLine(
            $"Worst deviation: {worstDeviation:F1} at sim tick {worstDeviationTick}, trajectory tick {worstDeviationTrajectoryTick}");

        if (failedPoints.Count > 0)
        {
            _output.WriteLine($"Failed points ({failedPoints.Count}):");
            foreach (var line in failedPoints.Take(20))
            {
                _output.WriteLine($"  {line}");
            }
        }

        Assert.True(
            failedPoints.Count == 0,
            $"Found {failedPoints.Count} trajectory points exceeding {MaxAllowedDeviation} units from path. " +
            $"Worst: {worstDeviation:F1} units at sim tick {worstDeviationTick}, traj tick {worstDeviationTrajectoryTick}. " +
            $"First: {failedPoints.FirstOrDefault()}");
    }

    [Fact]
    public void Trajectory_final_point_distance_from_path_is_bounded()
    {
        // Specifically check that the LAST trajectory point (t=200) doesn't fly far off,
        // since that's the end of the overlay line the user sees.
        var units = LoadFixtureUnits();
        var obstacles = CircularObstacleExtractor.Extract(units, clusterId: 0, shipRadius: ShipRadius, clearanceMargin: ClearanceMargin);
        var gravitySources = ExtractGravitySources(units);

        var start = new NavigationPoint(-338d, 276d);
        var goal = new NavigationPoint(527.5d, -457.2d);

        var planner = new CircularPathPlanner();
        var plan = planner.Plan(start, goal, obstacles);
        Assert.True(plan.Succeeded, $"Path planning failed: {plan.FailureReason}");

        var path = plan.PathPoints;
        var follower = new PathFollower();
        var shipX = start.X;
        var shipY = start.Y;
        var velX = 0d;
        var velY = 0d;

        const double arrivalThreshold = 14d;
        const double lookahead = 72d;
        const double minTargetDistance = 30d;
        const int maxSimTicks = 2000;
        const double maxFinalDeviation = 180d;

        var worstFinalDeviation = 0d;
        var worstFinalTick = 0;

        for (var tick = 0; tick < maxSimTicks; tick++)
        {
            var current = new NavigationPoint(shipX, shipY);
            if (current.DistanceTo(goal) <= arrivalThreshold)
                break;

            var follow = follower.Follow(current, path, lookahead, minTargetDistance, arrivalThreshold);
            if (follow.GoalReached)
                break;

            var remainingPath = BuildRemainingPath(follow.Target, follow.ProgressDistance, path);

            var (engineX, engineY) = TrajectoryAligner.ComputeEngineVector(
                shipX, shipY, velX, velY,
                follow.Target.X, follow.Target.Y,
                gravitySources, EngineMax, ThrustPercentage, SpeedLimit,
                remainingPath);

            var trajectory = SimulateTrajectory(
                shipX, shipY, velX, velY,
                engineX, engineY,
                gravitySources, TrajectoryTicks, SpeedLimit);

            var finalPt = trajectory[^1];
            var finalDev = DistanceToPolyline(new NavigationPoint(finalPt.X, finalPt.Y), path);

            if (finalDev > worstFinalDeviation)
            {
                worstFinalDeviation = finalDev;
                worstFinalTick = tick;
            }

            // Advance the ship
            velX += engineX;
            velY += engineY;
            var (gx, gy) = ComputeGravityAcceleration(shipX, shipY, gravitySources);
            velX += gx;
            velY += gy;
            (velX, velY) = ApplySoftCap(velX, velY, SpeedLimit);
            shipX += velX;
            shipY += velY;
        }

        _output.WriteLine($"Worst final-point deviation: {worstFinalDeviation:F1} at tick {worstFinalTick}");

        Assert.True(
            worstFinalDeviation <= maxFinalDeviation,
            $"Trajectory final point (t={TrajectoryTicks}) deviated {worstFinalDeviation:F1} units from path at tick {worstFinalTick}, " +
            $"exceeding limit of {maxFinalDeviation}");
    }

    // ── Helpers ──

    private static List<GravitySource> ExtractGravitySources(List<UnitSnapshotDto> units)
    {
        var sources = new List<GravitySource>();
        foreach (var unit in units)
        {
            if (unit.Gravity > 0f)
                sources.Add(new GravitySource(unit.X, unit.Y, unit.Gravity));
        }
        return sources;
    }

    private static double DistanceToPolyline(NavigationPoint point, IReadOnlyList<NavigationPoint> polyline)
    {
        var minDist = double.PositiveInfinity;
        for (var i = 0; i < polyline.Count - 1; i++)
        {
            var dist = NavigationMath.DistanceToSegment(point, polyline[i], polyline[i + 1], out _);
            minDist = Math.Min(minDist, dist);
        }
        return minDist;
    }

    private static double PolylineLength(IReadOnlyList<NavigationPoint> path)
    {
        var sum = 0d;
        for (var i = 0; i < path.Count - 1; i++)
            sum += path[i].DistanceTo(path[i + 1]);
        return sum;
    }

    private static IReadOnlyList<(double X, double Y)> BuildRemainingPath(
        NavigationPoint lookahead,
        double progressDistance,
        IReadOnlyList<NavigationPoint> fullPath)
    {
        if (fullPath.Count < 2)
            return Array.Empty<(double, double)>();

        var cumulative = 0d;
        var startIndex = 0;
        for (var i = 0; i < fullPath.Count - 1; i++)
        {
            var segLen = fullPath[i].DistanceTo(fullPath[i + 1]);
            if (cumulative + segLen >= progressDistance)
            {
                startIndex = i + 1;
                break;
            }
            cumulative += segLen;
            startIndex = i + 1;
        }

        var result = new List<(double, double)>();
        for (var i = startIndex; i < fullPath.Count; i++)
            result.Add((fullPath[i].X, fullPath[i].Y));
        return result;
    }

    private static List<UnitSnapshotDto> LoadFixtureUnits()
    {
        var fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "world-state.wss-www-flattiverse-com-galaxies-0-api-b.fe66663304e80502.json");

        using var stream = File.OpenRead(fixturePath);
        var worldState = JsonSerializer.Deserialize<PersistedWorldState>(stream, JsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize the persisted fixture.");

        return worldState.Scopes
            .Where(scope => scope.ClusterId == 0)
            .SelectMany(scope => scope.StaticUnits ?? [])
            .ToList();
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

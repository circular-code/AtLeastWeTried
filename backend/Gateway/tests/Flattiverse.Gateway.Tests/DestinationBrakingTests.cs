using System.Text.Json;
using Flattiverse.Gateway.Protocol.Dtos;
using Flattiverse.Gateway.Services;
using Flattiverse.Gateway.Services.Navigation;
using Xunit.Abstractions;
using static Flattiverse.Gateway.Services.GravitySimulator;

namespace Flattiverse.Gateway.Tests;

/// <summary>
/// Verifies that the navigation system decelerates before reaching the destination
/// so the ship arrives with near-zero speed and holds position.
/// </summary>
public sealed class DestinationBrakingTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private const double ShipRadius = 12d;
    private const double ClearanceMargin = 8d;
    private const double EngineMax = 0.059d;
    private const double SpeedLimit = 6.0d;
    private const double ThrustPercentage = 1.0d;

    private const double ArrivalThreshold = 14d;
    private const double Lookahead = 72d;
    private const double MinTargetDistance = 30d;
    private const int MaxSimTicks = 3000;

    /// <summary>Maximum acceptable speed when the ship first enters the arrival zone.</summary>
    private const double MaxArrivalSpeed = 0.5d;

    /// <summary>Maximum acceptable speed after holding position for a settling period.</summary>
    private const double MaxSettledSpeed = 0.15d;

    /// <summary>Ticks to continue simulating after arrival to verify station-keeping.</summary>
    private const int SettlingTicks = 100;

    /// <summary>Maximum allowed drift from goal during the settling period.</summary>
    private const double MaxSettledDrift = 20d;

    private readonly ITestOutputHelper _output;

    public DestinationBrakingTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Ship_brakes_to_near_zero_speed_at_destination()
    {
        // ── Arrange ──
        var units = LoadFixtureUnits();
        var obstacles = CircularObstacleExtractor.Extract(
            units, clusterId: 0, shipRadius: ShipRadius, clearanceMargin: ClearanceMargin);
        var gravitySources = ExtractGravitySources(units);

        var start = new NavigationPoint(-338d, 276d);
        var goal = new NavigationPoint(-378.9d, -274.8d);

        var planner = new CircularPathPlanner();
        var plan = planner.Plan(start, goal, obstacles);
        Assert.True(plan.Succeeded, $"Path planning failed: {plan.FailureReason}");

        var path = plan.PathPoints;
        _output.WriteLine($"Path: {path.Count} points, total length={PolylineLength(path):F1}");
        for (var i = 0; i < path.Count; i++)
            _output.WriteLine($"  [{i:000}] ({path[i].X:F1}, {path[i].Y:F1})");

        // ── Act: simulate tick-by-tick navigation ──
        var follower = new PathFollower();
        var shipX = start.X;
        var shipY = start.Y;
        var velX = 0d;
        var velY = 0d;

        var arrived = false;
        var arrivalTick = -1;
        var arrivalSpeed = double.NaN;

        for (var tick = 0; tick < MaxSimTicks; tick++)
        {
            var current = new NavigationPoint(shipX, shipY);
            var distToGoal = current.DistanceTo(goal);
            var speed = Math.Sqrt(velX * velX + velY * velY);

            // Log periodically and near the end
            if (tick % 50 == 0 || (distToGoal < 60d && tick % 5 == 0))
            {
                _output.WriteLine(
                    $"tick={tick:0000} pos=({shipX:F1},{shipY:F1}) vel=({velX:F3},{velY:F3}) " +
                    $"spd={speed:F3} distToGoal={distToGoal:F1}");
            }

            if (distToGoal <= ArrivalThreshold && !arrived)
            {
                arrived = true;
                arrivalTick = tick;
                arrivalSpeed = speed;
                _output.WriteLine(
                    $"*** ARRIVED tick={tick} pos=({shipX:F1},{shipY:F1}) " +
                    $"spd={speed:F3} distToGoal={distToGoal:F1}");
            }

            // Once arrived, continue for settling period then stop
            if (arrived && tick >= arrivalTick + SettlingTicks)
                break;

            // Navigate: follow path toward goal
            var follow = follower.Follow(current, path, Lookahead, MinTargetDistance, ArrivalThreshold);

            if (follow.GoalReached)
            {
                // Goal reached by PathFollower — but we still need to brake.
                // Steer toward goal directly to decelerate.
                var remainingPath = Array.Empty<(double X, double Y)>();
                var (engineX, engineY) = TrajectoryAligner.ComputeEngineVector(
                    shipX, shipY, velX, velY,
                    goal.X, goal.Y,
                    gravitySources, EngineMax, ThrustPercentage, SpeedLimit,
                    remainingPath);

                velX += engineX;
                velY += engineY;
            }
            else
            {
                var remainingPath = BuildRemainingPath(follow.Target, follow.ProgressDistance, path);

                var (engineX, engineY) = TrajectoryAligner.ComputeEngineVector(
                    shipX, shipY, velX, velY,
                    follow.Target.X, follow.Target.Y,
                    gravitySources, EngineMax, ThrustPercentage, SpeedLimit,
                    remainingPath);

                velX += engineX;
                velY += engineY;
            }

            // Apply gravity
            var (gx, gy) = ComputeGravityAcceleration(shipX, shipY, gravitySources);
            velX += gx;
            velY += gy;

            // Apply soft cap
            (velX, velY) = ApplySoftCap(velX, velY, SpeedLimit);

            // Update position
            shipX += velX;
            shipY += velY;
        }

        // ── Assert ──
        Assert.True(arrived, $"Ship did not reach goal within {MaxSimTicks} ticks");

        _output.WriteLine($"Arrival speed: {arrivalSpeed:F3} (max allowed: {MaxArrivalSpeed})");

        Assert.True(
            arrivalSpeed <= MaxArrivalSpeed,
            $"Ship arrived too fast: speed={arrivalSpeed:F3}, max allowed={MaxArrivalSpeed}. " +
            $"The ship is not braking before reaching the destination.");

        // Verify the ship settles near the goal
        var finalPos = new NavigationPoint(shipX, shipY);
        var finalDist = finalPos.DistanceTo(goal);
        var finalSpeed = Math.Sqrt(velX * velX + velY * velY);

        _output.WriteLine(
            $"After settling: pos=({shipX:F1},{shipY:F1}) dist={finalDist:F1} spd={finalSpeed:F3}");

        Assert.True(
            finalDist <= MaxSettledDrift,
            $"Ship drifted {finalDist:F1} units from goal after settling (max: {MaxSettledDrift})");

        Assert.True(
            finalSpeed <= MaxSettledSpeed,
            $"Ship still moving at {finalSpeed:F3} after {SettlingTicks} settling ticks (max: {MaxSettledSpeed})");
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

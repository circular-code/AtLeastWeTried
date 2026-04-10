using System.Text.Json;
using Flattiverse.Gateway.Protocol.Dtos;
using Flattiverse.Gateway.Services;
using static Flattiverse.Gateway.Services.GravitySimulator;

namespace Flattiverse.Gateway.Tests;

public sealed class ManeuveringServiceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // ──────────────────────────────────────────────────────────────────────
    //  GravitySimulator.ComputeGravityAcceleration
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gravity_single_source_far_away_uses_inverse_square()
    {
        // Distance > 60 (d² > 3600): formula is (dx, dy) * gravity * 60 / d²
        var source = new GravitySource(200d, 0d, 0.1d);
        var (ax, ay) = GravitySimulator.ComputeGravityAcceleration(0d, 0d, new[] { source });

        // dx=200, d²=40000, expected ax = 200 * 0.1 * 60 / 40000 = 0.03
        Assert.Equal(0.03d, ax, 6);
        Assert.Equal(0d, ay, 6);
    }

    [Fact]
    public void Gravity_single_source_close_uses_full_magnitude()
    {
        // Distance < 60 (d² ≤ 3600): formula is normalize(dx, dy) * gravity
        var source = new GravitySource(10d, 0d, 0.5d);
        var (ax, ay) = GravitySimulator.ComputeGravityAcceleration(0d, 0d, new[] { source });

        // Direction is (1, 0), magnitude is 0.5
        Assert.Equal(0.5d, ax, 6);
        Assert.Equal(0d, ay, 6);
    }

    [Fact]
    public void Gravity_source_at_same_position_returns_fallback()
    {
        var source = new GravitySource(100d, 200d, 0.3d);
        var (ax, ay) = GravitySimulator.ComputeGravityAcceleration(100d, 200d, new[] { source });

        // Fallback: (gravity, 0)
        Assert.Equal(0.3d, ax, 6);
        Assert.Equal(0d, ay, 6);
    }

    [Fact]
    public void Gravity_multiple_sources_sum_vectors()
    {
        var sources = new[]
        {
            new GravitySource(100d, 0d, 0.1d),   // far: dx=100, d²=10000, ax = 100*0.1*60/10000 = 0.06
            new GravitySource(0d, 100d, 0.1d),    // far: dy=100, d²=10000, ay = 100*0.1*60/10000 = 0.06
        };

        var (ax, ay) = GravitySimulator.ComputeGravityAcceleration(0d, 0d, sources);

        Assert.Equal(0.06d, ax, 5);
        Assert.Equal(0.06d, ay, 5);
    }

    [Fact]
    public void Gravity_close_diagonal_source_has_correct_magnitude()
    {
        // Place source at (30, 40) from origin → distance = 50, d² = 2500 (≤ 3600, close range)
        var source = new GravitySource(30d, 40d, 0.2d);
        var (ax, ay) = GravitySimulator.ComputeGravityAcceleration(0d, 0d, new[] { source });

        // normalize(30,40) = (0.6, 0.8), magnitude = 0.2
        Assert.Equal(0.6d * 0.2d, ax, 6);
        Assert.Equal(0.8d * 0.2d, ay, 6);
    }

    [Fact]
    public void Gravity_empty_sources_returns_zero()
    {
        var (ax, ay) = GravitySimulator.ComputeGravityAcceleration(0d, 0d, Array.Empty<GravitySource>());

        Assert.Equal(0d, ax);
        Assert.Equal(0d, ay);
    }

    // ──────────────────────────────────────────────────────────────────────
    //  GravitySimulator.ApplySoftCap
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void SoftCap_below_limit_unchanged()
    {
        var (vx, vy) = GravitySimulator.ApplySoftCap(3d, 4d, 10d);

        Assert.Equal(3d, vx, 6);
        Assert.Equal(4d, vy, 6);
    }

    [Fact]
    public void SoftCap_above_limit_dampened()
    {
        // Speed = 10, limit = 6: newSpeed = 6 + 0.9 * (10 - 6) = 6 + 3.6 = 9.6
        var (vx, vy) = GravitySimulator.ApplySoftCap(10d, 0d, 6d);

        Assert.Equal(9.6d, vx, 6);
        Assert.Equal(0d, vy, 6);
    }

    [Fact]
    public void SoftCap_preserves_direction()
    {
        // Velocity (6, 8), speed = 10, limit = 5
        // newSpeed = 5 + 0.9 * 5 = 9.5, scale = 9.5/10 = 0.95
        var (vx, vy) = GravitySimulator.ApplySoftCap(6d, 8d, 5d);

        Assert.Equal(6d * 0.95d, vx, 6);
        Assert.Equal(8d * 0.95d, vy, 6);
    }

    [Fact]
    public void SoftCap_at_zero_velocity_unchanged()
    {
        var (vx, vy) = GravitySimulator.ApplySoftCap(0d, 0d, 6d);

        Assert.Equal(0d, vx);
        Assert.Equal(0d, vy);
    }

    // ──────────────────────────────────────────────────────────────────────
    //  GravitySimulator.SimulateTrajectory
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Trajectory_no_gravity_constant_velocity_straight_line()
    {
        var points = GravitySimulator.SimulateTrajectory(
            startX: 0d, startY: 0d,
            velX: 1d, velY: 0d,
            engineX: 0d, engineY: 0d,
            sources: Array.Empty<GravitySource>(),
            ticks: 10,
            speedLimit: 100d);

        Assert.Equal(11, points.Count); // start + 10 ticks
        Assert.Equal(0d, points[0].X, 6);
        Assert.Equal(0d, points[0].Y, 6);

        for (var i = 1; i < points.Count; i++)
        {
            Assert.Equal(i * 1d, points[i].X, 6);
            Assert.Equal(0d, points[i].Y, 6);
        }
    }

    [Fact]
    public void Trajectory_with_engine_accelerates()
    {
        var points = GravitySimulator.SimulateTrajectory(
            startX: 0d, startY: 0d,
            velX: 0d, velY: 0d,
            engineX: 0.1d, engineY: 0d,
            sources: Array.Empty<GravitySource>(),
            ticks: 5,
            speedLimit: 100d);

        Assert.Equal(6, points.Count);

        // Each tick: vx += 0.1, x += vx → accelerating rightward
        Assert.True(points[5].X > points[4].X);
        Assert.True(points[4].X > points[3].X);
    }

    [Fact]
    public void Trajectory_sun_attracts_ship()
    {
        var sun = new GravitySource(100d, 0d, 0.1d);
        var points = GravitySimulator.SimulateTrajectory(
            startX: 0d, startY: 0d,
            velX: 0d, velY: 0d,
            engineX: 0d, engineY: 0d,
            sources: new[] { sun },
            ticks: 100,
            speedLimit: 100d);

        // Ship should move toward the sun (increasing X)
        Assert.True(points[100].X > points[0].X);
        Assert.True(points[100].X > 10d, $"Ship should have moved significantly toward sun; ended at X={points[100].X}");
    }

    [Fact]
    public void Trajectory_soft_cap_limits_speed()
    {
        // With no soft cap (very high limit), no-engine ship at constant velocity
        // travels a known distance. Soft cap with lower limit should reduce distance.
        var uncapped = GravitySimulator.SimulateTrajectory(
            startX: 0d, startY: 0d,
            velX: 10d, velY: 0d,
            engineX: 0d, engineY: 0d,
            sources: Array.Empty<GravitySource>(),
            ticks: 20,
            speedLimit: 1000d);

        var capped = GravitySimulator.SimulateTrajectory(
            startX: 0d, startY: 0d,
            velX: 10d, velY: 0d,
            engineX: 0d, engineY: 0d,
            sources: Array.Empty<GravitySource>(),
            ticks: 20,
            speedLimit: 3d);

        // The capped trajectory should have the ship travel less far
        Assert.True(capped[20].X < uncapped[20].X,
            $"Capped distance ({capped[20].X:F1}) should be less than uncapped ({uncapped[20].X:F1})");

        // Velocity should converge toward the speed limit over time
        var cappedVelocity = capped[20].X - capped[19].X;
        Assert.True(cappedVelocity < 10d,
            $"Capped velocity ({cappedVelocity:F1}) should be less than initial velocity 10");
    }

    [Fact]
    public void Trajectory_returns_correct_count()
    {
        var points = GravitySimulator.SimulateTrajectory(
            0d, 0d, 0d, 0d, 0d, 0d,
            Array.Empty<GravitySource>(), ticks: 200, speedLimit: 6d);

        Assert.Equal(201, points.Count);
    }

    // ──────────────────────────────────────────────────────────────────────
    //  TrajectoryAligner.ComputeEngineVector
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Aligner_no_gravity_points_toward_target()
    {
        var (ex, ey) = TrajectoryAligner.ComputeEngineVector(
            shipX: 0d, shipY: 0d,
            velX: 0d, velY: 0d,
            targetX: 100d, targetY: 0d,
            sources: Array.Empty<GravitySource>(),
            engineMax: 1d, thrustPercentage: 1d,
            speedLimit: 6d);

        // Engine should point in +X direction
        Assert.True(ex > 0d, $"Engine X should be positive, got {ex}");
        Assert.Equal(0d, ey, 3);
    }

    [Fact]
    public void Aligner_compensates_for_gravity()
    {
        // Gravity pulling left, target to the right → engine should push harder right
        var leftPull = new GravitySource(-100d, 0d, 0.5d);
        // At ship position (0,0), source at (-100,0): d=100, d²=10000 > 3600
        // gravity X = (-100)*0.5*60/10000 = -0.3 (pulling left)

        // Use high engine max so the result isn't clamped to the same ceiling
        var (exWithGravity, _) = TrajectoryAligner.ComputeEngineVector(
            0d, 0d, 0d, 0d, 100d, 0d,
            new[] { leftPull },
            engineMax: 10d, thrustPercentage: 1d,
            speedLimit: 6d);

        var (exNoGravity, _) = TrajectoryAligner.ComputeEngineVector(
            0d, 0d, 0d, 0d, 100d, 0d,
            Array.Empty<GravitySource>(),
            engineMax: 10d, thrustPercentage: 1d,
            speedLimit: 6d);

        Assert.True(exWithGravity > exNoGravity,
            $"Engine should compensate for leftward gravity: with={exWithGravity}, without={exNoGravity}");
    }

    [Fact]
    public void Aligner_clamps_to_engine_maximum()
    {
        var (ex, ey) = TrajectoryAligner.ComputeEngineVector(
            0d, 0d, -10d, -10d, 1000d, 1000d,
            Array.Empty<GravitySource>(),
            engineMax: 0.5d, thrustPercentage: 1d,
            speedLimit: 6d);

        var magnitude = Math.Sqrt(ex * ex + ey * ey);
        Assert.True(magnitude <= 0.5d + 1e-6d, $"Engine magnitude {magnitude} exceeds max 0.5");
    }

    [Fact]
    public void Aligner_respects_thrust_percentage()
    {
        var (exFull, eyFull) = TrajectoryAligner.ComputeEngineVector(
            0d, 0d, 0d, 0d, 100d, 100d,
            Array.Empty<GravitySource>(),
            engineMax: 2d, thrustPercentage: 1d,
            speedLimit: 6d);

        var (exHalf, eyHalf) = TrajectoryAligner.ComputeEngineVector(
            0d, 0d, 0d, 0d, 100d, 100d,
            Array.Empty<GravitySource>(),
            engineMax: 2d, thrustPercentage: 0.5d,
            speedLimit: 6d);

        var magFull = Math.Sqrt(exFull * exFull + eyFull * eyFull);
        var magHalf = Math.Sqrt(exHalf * exHalf + eyHalf * eyHalf);

        Assert.True(magHalf <= magFull + 1e-6d,
            $"Half thrust magnitude {magHalf} should not exceed full {magFull}");
    }

    [Fact]
    public void Aligner_zero_thrust_returns_zero()
    {
        var (ex, ey) = TrajectoryAligner.ComputeEngineVector(
            0d, 0d, 0d, 0d, 100d, 0d,
            Array.Empty<GravitySource>(),
            engineMax: 2d, thrustPercentage: 0d,
            speedLimit: 6d);

        Assert.Equal(0d, ex);
        Assert.Equal(0d, ey);
    }

    [Fact]
    public void Aligner_at_target_returns_zero()
    {
        var (ex, ey) = TrajectoryAligner.ComputeEngineVector(
            50d, 50d, 1d, 0d, 50d, 50d,
            Array.Empty<GravitySource>(),
            engineMax: 2d, thrustPercentage: 1d,
            speedLimit: 6d);

        Assert.Equal(0d, ex);
        Assert.Equal(0d, ey);
    }

    [Fact]
    public void Aligner_cancels_cross_track_drift_when_moving_perpendicular()
    {
        // Ship at origin moving fast downward (-Y), target is to the right (+X).
        // The old algorithm would mostly point toward the target (diagonally),
        // but the new one should prioritize canceling the downward drift.
        var (ex, ey) = TrajectoryAligner.ComputeEngineVector(
            shipX: 0d, shipY: 0d,
            velX: 0d, velY: -5d,      // moving fast downward
            targetX: 200d, targetY: 0d, // target is to the right
            sources: Array.Empty<GravitySource>(),
            engineMax: 1.2d, thrustPercentage: 1d,
            speedLimit: 6d);

        // Engine Y should be positive (braking the downward velocity)
        Assert.True(ey > 0d, $"Engine should brake downward drift; ey={ey}");

        // Engine should have significant upward component relative to rightward
        // because cross-track correction is high priority
        Assert.True(ey > ex * 0.5d,
            $"Cross-track correction (ey={ey:F3}) should be significant relative to along-track (ex={ex:F3})");
    }

    [Fact]
    public void Aligner_trajectory_follows_right_turn_without_overshooting()
    {
        // Simulate the crash scenario: ship moving fast at ~45° up-right,
        // then waypoint shifts far right. After several ticks of correction,
        // the ship's cross-track drift should be reduced.
        var sources = Array.Empty<GravitySource>();

        double shipX = 640d, shipY = 960d;
        double velX = 1d, velY = -4d; // moving mostly downward
        double targetX = 795d, targetY = 298d; // far right target (like the planned path arc)

        // Simulate 30 ticks of the aligner correcting course
        for (var tick = 0; tick < 30; tick++)
        {
            var (engineX, engineY) = TrajectoryAligner.ComputeEngineVector(
                shipX, shipY, velX, velY,
                targetX, targetY,
                sources,
                engineMax: 1.2d, thrustPercentage: 1d,
                speedLimit: 6d);

            velX += engineX;
            velY += engineY;
            var (newVx, newVy) = GravitySimulator.ApplySoftCap(velX, velY, 6d);
            velX = newVx;
            velY = newVy;
            shipX += velX;
            shipY += velY;
        }

        // After correction, ship X should be meaningfully closer to target X (not stuck at ~640).
        // Target is diagonal (155, -662) so only ~23% of thrust goes rightward.
        Assert.True(shipX > 670d,
            $"Ship should have steered right toward target; final X={shipX:F1} (target X=795)");
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Fixture-based simulation tests
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Fixture_trajectory_near_xami_sun_curves_toward_it()
    {
        var gravitySources = LoadFixtureGravitySources();

        // Xami sun is at (-66.28, 585.31) with gravity 0.11
        // Place ship at (0, 400) moving upward — trajectory should curve left toward Xami
        var points = GravitySimulator.SimulateTrajectory(
            startX: 0d, startY: 400d,
            velX: 0d, velY: 1d,
            engineX: 0d, engineY: 0d,
            sources: gravitySources,
            ticks: 200,
            speedLimit: 6d);

        Assert.Equal(201, points.Count);

        // Ship should have moved toward Xami's X position (negative X)
        Assert.True(points[200].X < points[0].X,
            $"Ship should curve toward Xami (leftward); start X={points[0].X}, end X={points[200].X}");

        // Verify no NaN or infinite values
        Assert.All(points, p =>
        {
            Assert.False(double.IsNaN(p.X), "X is NaN");
            Assert.False(double.IsNaN(p.Y), "Y is NaN");
            Assert.False(double.IsInfinity(p.X), "X is infinite");
            Assert.False(double.IsInfinity(p.Y), "Y is infinite");
        });
    }

    [Fact]
    public void Fixture_trajectory_from_open_space_stays_bounded()
    {
        var gravitySources = LoadFixtureGravitySources();

        var points = GravitySimulator.SimulateTrajectory(
            startX: 200d, startY: 400d,
            velX: 1d, velY: 0d,
            engineX: 0d, engineY: 0d,
            sources: gravitySources,
            ticks: 200,
            speedLimit: 6d);

        Assert.All(points, p =>
        {
            Assert.False(double.IsNaN(p.X));
            Assert.False(double.IsNaN(p.Y));
            Assert.InRange(p.X, -10000d, 10000d);
            Assert.InRange(p.Y, -10000d, 10000d);
        });
    }

    [Fact]
    public void Fixture_trajectory_with_engine_toward_target_approaches_initially()
    {
        var gravitySources = LoadFixtureGravitySources();

        // Ship at (200, 400), target at (400, 600), with engine thrust aligned
        var (engineX, engineY) = TrajectoryAligner.ComputeEngineVector(
            200d, 400d, 0d, 0d,
            400d, 600d,
            gravitySources,
            engineMax: 1.2d, thrustPercentage: 1d,
            speedLimit: 6d);

        var points = GravitySimulator.SimulateTrajectory(
            200d, 400d, 0d, 0d,
            engineX, engineY,
            gravitySources,
            ticks: 200,
            speedLimit: 6d);

        // In the first ~30 ticks the ship should approach the target before overshooting
        // (constant engine eventually carries it past the target)
        var startDist = Distance(points[0].X, points[0].Y, 400d, 600d);
        var minDist = Enumerable.Range(0, 60)
            .Select(i => Distance(points[i].X, points[i].Y, 400d, 600d))
            .Min();

        Assert.True(minDist < startDist * 0.5d,
            $"Ship should approach target within first 60 ticks; start={startDist:F1}, closest={minDist:F1}");
    }

    [Fact]
    public void Fixture_extract_gravity_sources_finds_known_bodies()
    {
        var sources = LoadFixtureGravitySources();

        // The fixture contains Xami (gravity 0.11), Rhombus (0.05), Exster (0.1), Shaowei (0.0125), etc.
        Assert.True(sources.Count >= 4, $"Expected at least 4 gravity sources, got {sources.Count}");

        // Check for Xami's gravity specifically
        var xami = sources.FirstOrDefault(s =>
            Math.Abs(s.X - (-66.27759d)) < 1d && Math.Abs(s.Y - 585.3143d) < 1d);
        Assert.True(xami.Gravity > 0d, "Xami sun should be a gravity source");
        Assert.Equal(0.11d, xami.Gravity, 3);
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Helpers
    // ──────────────────────────────────────────────────────────────────────

    private static double Distance(double x1, double y1, double x2, double y2)
    {
        var dx = x2 - x1;
        var dy = y2 - y1;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static List<GravitySource> LoadFixtureGravitySources()
    {
        var units = LoadFixtureUnits("world-state.wss-www-flattiverse-com-galaxies-0-api-b.fe66663304e80502.json");
        return units
            .Where(u => u.Gravity > 0f)
            .Select(u => new GravitySource(u.X, u.Y, u.Gravity))
            .ToList();
    }

    private static List<UnitSnapshotDto> LoadFixtureUnits(string fixtureFileName)
    {
        var fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            fixtureFileName);

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

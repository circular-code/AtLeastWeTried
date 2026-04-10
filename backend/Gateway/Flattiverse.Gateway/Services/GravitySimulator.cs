namespace Flattiverse.Gateway.Services;

/// <summary>
/// Pure-math gravity and trajectory simulation. No mutable state — all methods are static.
/// Implements the game gravity formula from docs/game.md.
/// </summary>
public static class GravitySimulator
{
    /// <summary>Distance² threshold: beyond this, gravity falls off with inverse-square law.</summary>
    private const double FarThresholdSquared = 3600d;

    /// <summary>Scaling constant used in the far-field gravity formula: <c>gravity * 60 / d²</c>.</summary>
    private const double FarFieldScale = 60d;

    /// <summary>Soft-cap decay factor: excess speed above the limit is multiplied by this.</summary>
    private const double SoftCapDecay = 0.9d;

    public readonly record struct GravitySource(double X, double Y, double Gravity);

    public readonly record struct TrajectoryPoint(double X, double Y);

    /// <summary>
    /// Compute the total gravitational acceleration on a ship at (<paramref name="shipX"/>, <paramref name="shipY"/>)
    /// from all <paramref name="sources"/>.
    /// </summary>
    public static (double Ax, double Ay) ComputeGravityAcceleration(
        double shipX,
        double shipY,
        IReadOnlyList<GravitySource> sources)
    {
        var ax = 0d;
        var ay = 0d;

        for (var i = 0; i < sources.Count; i++)
        {
            var source = sources[i];
            var dx = source.X - shipX;
            var dy = source.Y - shipY;
            var d2 = dx * dx + dy * dy;

            double deltaX, deltaY;

            if (d2 > FarThresholdSquared)
            {
                // Inverse-square falloff: (dx, dy) * gravity * 60 / d²
                var factor = source.Gravity * FarFieldScale / d2;
                deltaX = dx * factor;
                deltaY = dy * factor;
            }
            else if (d2 > 0d)
            {
                // Close range: full gravity magnitude in normalized direction
                var dist = Math.Sqrt(d2);
                deltaX = (dx / dist) * source.Gravity;
                deltaY = (dy / dist) * source.Gravity;
            }
            else
            {
                // Exact overlap: fallback to (gravity, 0)
                deltaX = source.Gravity;
                deltaY = 0d;
            }

            ax += deltaX;
            ay += deltaY;
        }

        return (ax, ay);
    }

    /// <summary>
    /// Apply the post-movement soft speed cap. Speeds exceeding <paramref name="speedLimit"/> are dampened:
    /// <c>newSpeed = speedLimit + 0.9 * (speed - speedLimit)</c>.
    /// </summary>
    public static (double Vx, double Vy) ApplySoftCap(double vx, double vy, double speedLimit)
    {
        var speed = Math.Sqrt(vx * vx + vy * vy);
        if (speed <= speedLimit || speed <= 0d)
            return (vx, vy);

        var newSpeed = speedLimit + SoftCapDecay * (speed - speedLimit);
        var scale = newSpeed / speed;
        return (vx * scale, vy * scale);
    }

    /// <summary>
    /// Forward-simulate position for <paramref name="ticks"/> ticks, applying constant engine thrust,
    /// gravity from all sources, and the soft speed cap each tick.
    /// Returns a list of <paramref name="ticks"/> + 1 points (starting position included).
    /// </summary>
    public static List<TrajectoryPoint> SimulateTrajectory(
        double startX,
        double startY,
        double velX,
        double velY,
        double engineX,
        double engineY,
        IReadOnlyList<GravitySource> sources,
        int ticks,
        double speedLimit)
    {
        return SimulateTrajectory(
            startX,
            startY,
            velX,
            velY,
            engineX,
            engineY,
            (shipX, shipY) => ComputeGravityAcceleration(shipX, shipY, sources),
            ticks,
            speedLimit);
    }

    /// <summary>
    /// Forward-simulate position for <paramref name="ticks"/> ticks using a gravity sampler callback.
    /// Returns a list of simulated points starting with the initial position.
    /// </summary>
    public static List<TrajectoryPoint> SimulateTrajectory(
        double startX,
        double startY,
        double velX,
        double velY,
        double engineX,
        double engineY,
        Func<double, double, (double Ax, double Ay)> gravitySampler,
        int ticks,
        double speedLimit,
        Func<TrajectoryPoint, bool>? shouldStop = null)
    {
        var points = new List<TrajectoryPoint>(ticks + 1);

        var x = startX;
        var y = startY;
        var vx = velX;
        var vy = velY;

        points.Add(new TrajectoryPoint(x, y));

        for (var t = 0; t < ticks; t++)
        {
            // 1. Apply engine thrust
            vx += engineX;
            vy += engineY;

            // 2. Apply gravity
            var (gx, gy) = gravitySampler(x, y);
            vx += gx;
            vy += gy;

            // 3. Apply soft cap
            (vx, vy) = ApplySoftCap(vx, vy, speedLimit);

            // 4. Update position
            x += vx;
            y += vy;

            var point = new TrajectoryPoint(x, y);
            points.Add(point);
            if (shouldStop?.Invoke(point) == true)
                break;
        }

        return points;
    }
}

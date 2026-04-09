namespace Flattiverse.Gateway.Services.Navigation;

internal readonly record struct NavigationPoint(double X, double Y)
{
    public static NavigationPoint operator +(NavigationPoint left, NavigationPoint right) => new(left.X + right.X, left.Y + right.Y);

    public static NavigationPoint operator -(NavigationPoint left, NavigationPoint right) => new(left.X - right.X, left.Y - right.Y);

    public static NavigationPoint operator *(NavigationPoint point, double factor) => new(point.X * factor, point.Y * factor);

    public static NavigationPoint operator /(NavigationPoint point, double divisor) => new(point.X / divisor, point.Y / divisor);

    public double LengthSquared => X * X + Y * Y;

    public double Length => Math.Sqrt(LengthSquared);

    public NavigationPoint Normalized()
    {
        var length = Length;
        return length <= NavigationMath.Epsilon
            ? new NavigationPoint(1d, 0d)
            : this / length;
    }

    public double DistanceTo(NavigationPoint other) => (other - this).Length;

    public double Angle => Math.Atan2(Y, X);

    public static NavigationPoint FromAngle(double angle, double length) => new(Math.Cos(angle) * length, Math.Sin(angle) * length);

    public static double Dot(NavigationPoint left, NavigationPoint right) => left.X * right.X + left.Y * right.Y;

    public static double Cross(NavigationPoint left, NavigationPoint right) => left.X * right.Y - left.Y * right.X;

    public static NavigationPoint PerpendicularLeft(NavigationPoint point) => new(-point.Y, point.X);
}

internal readonly record struct NavigationObstacle(
    string Id,
    string Kind,
    NavigationPoint Center,
    double Radius);

internal readonly record struct NavigationPreviewPoint(double X, double Y);

internal readonly record struct NavigationPreviewSegment(double StartX, double StartY, double EndX, double EndY);

internal static class NavigationMath
{
    public const double Epsilon = 1e-6;
    public const double CoordinateRounding = 1e5;

    public static double NormalizeAngle(double angle)
    {
        var normalized = angle % (Math.PI * 2d);
        return normalized < 0d
            ? normalized + Math.PI * 2d
            : normalized;
    }

    /// <summary>
    /// Signed CCW sweep along the <b>shorter</b> arc from <paramref name="fromAngle"/> to <paramref name="toAngle"/>.
    /// Range is (-π, π]: positive = CCW, negative = CW. Use for arc sampling between two obstacle contacts.
    /// </summary>
    /// <remarks>
    /// Without this, sorting atan2 angles in (-π, π] can place two contacts near ±π consecutively; the naive
    /// CCW difference in [0, 2π) then picks the <b>long</b> way around the circle (~2π − gap), producing wrong
    /// arcs and spurious tangent-like overlay segments.
    /// </remarks>
    public static double ShortArcSignedSweep(double fromAngle, double toAngle)
    {
        var ccw = NormalizeAngle(toAngle - fromAngle);
        if (ccw > Math.PI)
        {
            return ccw - Math.PI * 2d;
        }

        return ccw;
    }

    public static double DistanceToSegment(NavigationPoint point, NavigationPoint start, NavigationPoint end, out double t)
    {
        var delta = end - start;
        var lengthSquared = delta.LengthSquared;
        if (lengthSquared <= Epsilon)
        {
            t = 0d;
            return point.DistanceTo(start);
        }

        t = Math.Clamp(NavigationPoint.Dot(point - start, delta) / lengthSquared, 0d, 1d);
        var projection = start + delta * t;
        return point.DistanceTo(projection);
    }

    public static NavigationPoint PointOnSegment(NavigationPoint start, NavigationPoint end, double t)
    {
        return start + (end - start) * t;
    }

    /// <summary>
    /// External tangent touch points from <paramref name="point"/> to <paramref name="circle"/>.
    /// Returns empty if the point lies inside or on the inflated circle.
    /// </summary>
    public static IEnumerable<NavigationPoint> PointToCircleTangentTouchPoints(
        NavigationPoint point,
        NavigationObstacle circle)
    {
        var offset = point - circle.Center;
        var distance = offset.Length;
        if (distance <= circle.Radius + Epsilon)
        {
            yield break;
        }

        var baseAngle = Math.Atan2(offset.Y, offset.X);
        var halfSpan = Math.Acos(circle.Radius / distance);
        foreach (var sign in new[] { -1d, 1d })
        {
            var touchAngle = baseAngle + sign * halfSpan;
            yield return circle.Center + NavigationPoint.FromAngle(touchAngle, circle.Radius);
        }
    }

    public static string BuildPointKey(int obstacleIndex, NavigationPoint point)
    {
        var roundedX = Math.Round(point.X * CoordinateRounding) / CoordinateRounding;
        var roundedY = Math.Round(point.Y * CoordinateRounding) / CoordinateRounding;
        return $"{obstacleIndex}:{roundedX:0.#####}:{roundedY:0.#####}";
    }

    /// <summary>
    /// True if any polyline segment passes within <paramref name="obstacle"/>'s inflated radius of its center
    /// (segment enters the obstacle disk).
    /// </summary>
    public static bool PolylineIntersectsObstacleDisk(
        IReadOnlyList<NavigationPoint> path,
        NavigationObstacle obstacle)
    {
        if (path.Count < 2)
        {
            return false;
        }

        for (var i = 0; i < path.Count - 1; i++)
        {
            var distance = DistanceToSegment(obstacle.Center, path[i], path[i + 1], out _);
            if (distance < obstacle.Radius - Epsilon)
            {
                return true;
            }
        }

        return false;
    }
}

using Flattiverse.Gateway.Protocol.Dtos;

namespace Flattiverse.Gateway.Services.Navigation;

internal static class CircularObstacleExtractor
{
    /// <summary>
    /// Extra world-space clearance (beyond ship radius + base margin) per unit of connector gravity.
    /// Typical stellar gravity is ~1e-3..1e-2, giving tens to hundreds of units of standoff.
    /// </summary>
    private const double ClearancePerGravity = 80d;

    private static readonly HashSet<string> BlockingKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "black-hole",
        "buoy",
        "meteoroid",
        "moon",
        "planet",
        "sun",
        "wormhole",
    };

    /// <param name="shipPosition">
    /// When set and this point lies inside a unit's inflated keep-out disk, that obstacle's effective radius is temporarily
    /// reduced so the ship lies just outside the disk (enabling escape routes). Degenerate zero-radius obstacles are omitted.
    /// </param>
    public static List<NavigationObstacle> Extract(
        IReadOnlyList<UnitSnapshotDto> units,
        int clusterId,
        double shipRadius,
        double clearanceMargin,
        string? destinationUnitId = null,
        NavigationPoint? shipPosition = null)
    {
        var inflatedRadius = Math.Max(0d, shipRadius) + Math.Max(0d, clearanceMargin);
        var obstacles = new List<NavigationObstacle>();

        foreach (var unit in units)
        {
            if (unit.ClusterId != clusterId || !unit.IsStatic || !unit.IsSeen || unit.Radius <= 0f)
            {
                continue;
            }

            if (unit.IsSolid == false)
            {
                continue;
            }

            if (!BlockingKinds.Contains(unit.Kind) || string.Equals(unit.UnitId, destinationUnitId, StringComparison.Ordinal))
            {
                continue;
            }

            var gravity = unit.Gravity <= 0f ? 0d : unit.Gravity;
            var gravityMargin = ClearancePerGravity * gravity;
            var effectiveRadius = unit.Radius + inflatedRadius + gravityMargin;

            if (shipPosition.HasValue)
            {
                var center = new NavigationPoint(unit.X, unit.Y);
                var distanceToShip = shipPosition.Value.DistanceTo(center);
                if (distanceToShip < effectiveRadius - NavigationMath.Epsilon)
                {
                    effectiveRadius = Math.Max(0d, distanceToShip - NavigationMath.Epsilon);
                }
            }

            if (effectiveRadius <= NavigationMath.Epsilon)
            {
                continue;
            }

            obstacles.Add(new NavigationObstacle(
                unit.UnitId,
                unit.Kind,
                new NavigationPoint(unit.X, unit.Y),
                effectiveRadius));
        }

        return obstacles;
    }
}

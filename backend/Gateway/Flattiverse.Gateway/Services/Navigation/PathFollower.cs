namespace Flattiverse.Gateway.Services.Navigation;

internal sealed class PathFollower
{
    public readonly record struct FollowResult(
        NavigationPoint Target,
        bool GoalReached,
        double ProgressDistance,
        double RemainingDistance);

    public FollowResult Follow(
        NavigationPoint currentPosition,
        IReadOnlyList<NavigationPoint> path,
        double lookaheadDistance,
        double minTargetDistance,
        double arrivalThreshold)
    {
        if (path.Count == 0)
        {
            return new FollowResult(currentPosition, true, 0d, 0d);
        }

        if (path.Count == 1)
        {
            var destination = path[0];
            var distance = currentPosition.DistanceTo(destination);
            return new FollowResult(destination, distance <= arrivalThreshold, 0d, distance);
        }

        var cumulative = BuildCumulativeDistances(path);
        var totalLength = cumulative[^1];
        var goal = path[^1];
        var directGoalDistance = currentPosition.DistanceTo(goal);
        if (directGoalDistance <= arrivalThreshold)
        {
            return new FollowResult(goal, true, totalLength, 0d);
        }

        var closestProgress = ResolveClosestProgress(currentPosition, path, cumulative);
        var remaining = Math.Max(0d, totalLength - closestProgress);
        if (remaining <= arrivalThreshold)
        {
            return new FollowResult(goal, true, totalLength, 0d);
        }

        var desiredProgress = Math.Min(totalLength, closestProgress + Math.Max(lookaheadDistance, arrivalThreshold));
        var target = PointAtProgress(path, cumulative, desiredProgress);
        var targetDistance = currentPosition.DistanceTo(target);

        while (desiredProgress < totalLength && targetDistance < minTargetDistance)
        {
            desiredProgress = Math.Min(totalLength, desiredProgress + Math.Max(minTargetDistance * 0.5d, arrivalThreshold));
            target = PointAtProgress(path, cumulative, desiredProgress);
            targetDistance = currentPosition.DistanceTo(target);
        }

        return new FollowResult(target, desiredProgress >= totalLength && targetDistance <= arrivalThreshold, closestProgress, remaining);
    }

    private static double[] BuildCumulativeDistances(IReadOnlyList<NavigationPoint> path)
    {
        var cumulative = new double[path.Count];
        for (var index = 1; index < path.Count; index++)
        {
            cumulative[index] = cumulative[index - 1] + path[index - 1].DistanceTo(path[index]);
        }

        return cumulative;
    }

    private static double ResolveClosestProgress(
        NavigationPoint currentPosition,
        IReadOnlyList<NavigationPoint> path,
        IReadOnlyList<double> cumulative)
    {
        var closestDistance = double.MaxValue;
        var closestProgress = 0d;

        for (var index = 0; index < path.Count - 1; index++)
        {
            var distance = NavigationMath.DistanceToSegment(currentPosition, path[index], path[index + 1], out var t);
            if (distance >= closestDistance)
            {
                continue;
            }

            closestDistance = distance;
            closestProgress = cumulative[index] + path[index].DistanceTo(path[index + 1]) * t;
        }

        return closestProgress;
    }

    private static NavigationPoint PointAtProgress(
        IReadOnlyList<NavigationPoint> path,
        IReadOnlyList<double> cumulative,
        double progress)
    {
        if (path.Count == 0)
        {
            return default;
        }

        if (progress <= 0d)
        {
            return path[0];
        }

        var totalLength = cumulative[^1];
        if (progress >= totalLength)
        {
            return path[^1];
        }

        for (var index = 0; index < path.Count - 1; index++)
        {
            var segmentStart = cumulative[index];
            var segmentEnd = cumulative[index + 1];
            if (progress > segmentEnd)
            {
                continue;
            }

            var segmentLength = segmentEnd - segmentStart;
            if (segmentLength <= NavigationMath.Epsilon)
            {
                return path[index + 1];
            }

            var t = (progress - segmentStart) / segmentLength;
            return NavigationMath.PointOnSegment(path[index], path[index + 1], t);
        }

        return path[^1];
    }
}

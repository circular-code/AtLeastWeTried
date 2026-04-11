using Flattiverse.Gateway.Protocol.Dtos;
using System.Linq;
using static Flattiverse.Gateway.Services.GravitySimulator;

namespace Flattiverse.Gateway.Services;

internal static class TrajectoryPredictionService
{
    internal readonly record struct PredictionOptions(
        int LookaheadTicks,
        int MaximumTicks,
        int Downsample,
        double MinimumPointDistance);

    private sealed class SimulatedBody
    {
        public required string UnitId { get; init; }
        public required double Radius { get; init; }
        public required double Gravity { get; init; }
        public required bool IsObstacle { get; init; }
        public required bool IsDynamic { get; init; }
        public required bool IsTarget { get; init; }
        public required double SpeedLimit { get; init; }
        public required PropulsionRuntimeState? Propulsion { get; init; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Vx { get; set; }
        public double Vy { get; set; }
    }

    private sealed class PropulsionRuntimeState
    {
        public PropulsionPredictionMode Mode { get; init; }
        public double CurrentX { get; set; }
        public double CurrentY { get; set; }
        public double TargetX { get; init; }
        public double TargetY { get; init; }
        public double MaximumMagnitude { get; init; }
        public double MaximumChangePerTick { get; init; }
        public ThrusterRuntimeState[]? Thrusters { get; init; }
    }

    private sealed class ThrusterRuntimeState
    {
        public double WorldAngleDegrees { get; init; }
        public double CurrentThrust { get; set; }
        public double TargetThrust { get; init; }
        public double MaximumThrust { get; init; }
        public double MaximumThrustChangePerTick { get; init; }
    }

    public static List<TrajectoryPointDto>? BuildHiddenTrajectory(
        UnitSnapshotDto unit,
        IEnumerable<UnitSnapshotDto> scopeUnits,
        uint currentTick,
        PredictionOptions options)
    {
        if (unit.IsStatic || unit.IsSeen || !SupportsHiddenTrajectory(unit))
            return null;

        var elapsedTicks = currentTick > unit.LastSeenTick
            ? (int)Math.Min(currentTick - unit.LastSeenTick, (uint)options.MaximumTicks)
            : 0;
        var totalTicks = Math.Min(options.MaximumTicks, elapsedTicks + options.LookaheadTicks);
        if (totalTicks <= 0)
            return null;

        var relevantUnits = scopeUnits
            .Where(candidate => candidate.ClusterId == unit.ClusterId)
            .Where(candidate =>
                string.Equals(candidate.UnitId, unit.UnitId, StringComparison.Ordinal) ||
                candidate.IsStatic ||
                candidate.IsSeen)
            .ToArray();

        var bodies = BuildSimulationBodies(unit, relevantUnits);
        if (bodies.Length == 0)
            return null;

        var targetBody = bodies.FirstOrDefault(body => body.IsTarget);
        if (targetBody is null)
            return null;

        var simulated = SimulateTargetPath(targetBody, bodies, totalTicks);
        if (simulated.Count < 2)
            return null;

        var predicted = new List<TrajectoryPointDto>(simulated.Count);
        AppendTrajectoryPoint(predicted, simulated[0], options.MinimumPointDistance);

        var presentIndex = Math.Min(elapsedTicks, simulated.Count - 1);
        if (presentIndex > 0)
            AppendTrajectoryPoint(predicted, simulated[presentIndex], options.MinimumPointDistance);

        for (var index = presentIndex + Math.Max(1, options.Downsample); index < simulated.Count; index += Math.Max(1, options.Downsample))
            AppendTrajectoryPoint(predicted, simulated[index], options.MinimumPointDistance);

        AppendTrajectoryPoint(predicted, simulated[^1], options.MinimumPointDistance);
        return predicted.Count >= 2 ? predicted : null;
    }

    internal static bool SupportsHiddenTrajectory(UnitSnapshotDto unit)
    {
        return IsPlayerShipKind(unit.Kind);
    }

    private static SimulatedBody[] BuildSimulationBodies(UnitSnapshotDto targetUnit, IReadOnlyList<UnitSnapshotDto> relevantUnits)
    {
        var bodies = new List<SimulatedBody>(relevantUnits.Count);

        for (var index = 0; index < relevantUnits.Count; index++)
        {
            var unit = relevantUnits[index];
            var isTarget = string.Equals(unit.UnitId, targetUnit.UnitId, StringComparison.Ordinal);
            var isObstacle = unit.IsSolid != false && unit.Radius > 0f;
            var isGravitySource = unit.Gravity > 0f;
            if (!isTarget && !isObstacle && !isGravitySource)
                continue;

            bodies.Add(new SimulatedBody
            {
                UnitId = unit.UnitId,
                X = unit.X,
                Y = unit.Y,
                Vx = unit.MovementX.GetValueOrDefault(),
                Vy = unit.MovementY.GetValueOrDefault(),
                Radius = Math.Max(0f, unit.Radius),
                Gravity = Math.Max(0f, unit.Gravity),
                IsObstacle = isObstacle,
                IsDynamic = !unit.IsStatic,
                IsTarget = isTarget,
                SpeedLimit = unit.SpeedLimit is float speedLimit && speedLimit > 0f
                    ? speedLimit
                    : double.PositiveInfinity,
                Propulsion = CreatePropulsionRuntime(unit.PropulsionPrediction)
            });
        }

        return bodies.ToArray();
    }

    private static List<TrajectoryPoint> SimulateTargetPath(SimulatedBody targetBody, IReadOnlyList<SimulatedBody> bodies, int ticks)
    {
        var points = new List<TrajectoryPoint>(ticks + 1)
        {
            new(targetBody.X, targetBody.Y)
        };

        for (var tick = 0; tick < ticks; tick++)
        {
            var engineVectors = new (double X, double Y)[bodies.Count];
            for (var index = 0; index < bodies.Count; index++)
            {
                var body = bodies[index];
                if (!body.IsDynamic)
                    continue;

                engineVectors[index] = SampleEngineVector(body.Propulsion);
            }

            var gravityVectors = new (double X, double Y)[bodies.Count];
            for (var index = 0; index < bodies.Count; index++)
            {
                var body = bodies[index];
                if (!body.IsDynamic)
                    continue;

                gravityVectors[index] = ComputeGravityAcceleration(body, bodies);
            }

            for (var index = 0; index < bodies.Count; index++)
            {
                var body = bodies[index];
                if (!body.IsDynamic)
                    continue;

                var engine = engineVectors[index];
                var gravity = gravityVectors[index];
                body.Vx += engine.X + gravity.X;
                body.Vy += engine.Y + gravity.Y;
                (body.Vx, body.Vy) = ApplySoftCap(body.Vx, body.Vy, body.SpeedLimit);
            }

            for (var index = 0; index < bodies.Count; index++)
            {
                var body = bodies[index];
                if (!body.IsDynamic)
                    continue;

                body.X += body.Vx;
                body.Y += body.Vy;
            }

            var point = new TrajectoryPoint(targetBody.X, targetBody.Y);
            points.Add(point);
            if (IntersectsObstacle(targetBody, bodies))
                break;
        }

        return points;
    }

    private static (double X, double Y) ComputeGravityAcceleration(SimulatedBody body, IReadOnlyList<SimulatedBody> bodies)
    {
        var gravitySources = new List<GravitySource>(bodies.Count);
        for (var index = 0; index < bodies.Count; index++)
        {
            var candidate = bodies[index];
            if (ReferenceEquals(candidate, body) || candidate.Gravity <= 0d)
                continue;

            gravitySources.Add(new GravitySource(candidate.X, candidate.Y, candidate.Gravity));
        }

        return gravitySources.Count == 0
            ? (0d, 0d)
            : GravitySimulator.ComputeGravityAcceleration(body.X, body.Y, gravitySources);
    }

    private static bool IntersectsObstacle(SimulatedBody targetBody, IReadOnlyList<SimulatedBody> bodies)
    {
        for (var index = 0; index < bodies.Count; index++)
        {
            var obstacle = bodies[index];
            if (!obstacle.IsObstacle || obstacle.IsTarget)
                continue;

            var dx = targetBody.X - obstacle.X;
            var dy = targetBody.Y - obstacle.Y;
            var minimumDistance = targetBody.Radius + obstacle.Radius;
            if ((dx * dx) + (dy * dy) <= minimumDistance * minimumDistance)
                return true;
        }

        return false;
    }

    private static PropulsionRuntimeState? CreatePropulsionRuntime(PropulsionPredictionSnapshotDto? snapshot)
    {
        if (snapshot is null || snapshot.Mode == PropulsionPredictionMode.None)
            return null;

        return new PropulsionRuntimeState
        {
            Mode = snapshot.Mode,
            CurrentX = snapshot.CurrentX,
            CurrentY = snapshot.CurrentY,
            TargetX = snapshot.TargetX,
            TargetY = snapshot.TargetY,
            MaximumMagnitude = snapshot.MaximumMagnitude,
            MaximumChangePerTick = snapshot.MaximumChangePerTick,
            Thrusters = snapshot.Thrusters?
                .Select(thruster => new ThrusterRuntimeState
                {
                    WorldAngleDegrees = thruster.WorldAngleDegrees,
                    CurrentThrust = thruster.CurrentThrust,
                    TargetThrust = thruster.TargetThrust,
                    MaximumThrust = thruster.MaximumThrust,
                    MaximumThrustChangePerTick = thruster.MaximumThrustChangePerTick
                })
                .ToArray()
        };
    }

    private static (double X, double Y) SampleEngineVector(PropulsionRuntimeState? propulsion)
    {
        if (propulsion is null)
            return (0d, 0d);

        if (propulsion.Mode == PropulsionPredictionMode.DirectVector)
        {
            if (propulsion.MaximumChangePerTick > 0d)
            {
                (propulsion.CurrentX, propulsion.CurrentY) = MoveVectorTowards(
                    propulsion.CurrentX,
                    propulsion.CurrentY,
                    propulsion.TargetX,
                    propulsion.TargetY,
                    propulsion.MaximumChangePerTick);
            }
            else
            {
                propulsion.CurrentX = propulsion.TargetX;
                propulsion.CurrentY = propulsion.TargetY;
            }

            if (propulsion.MaximumMagnitude > 0d)
                (propulsion.CurrentX, propulsion.CurrentY) = ClampMagnitude(propulsion.CurrentX, propulsion.CurrentY, propulsion.MaximumMagnitude);

            return (propulsion.CurrentX, propulsion.CurrentY);
        }

        var totalX = 0d;
        var totalY = 0d;
        if (propulsion.Thrusters is null)
            return (0d, 0d);

        for (var index = 0; index < propulsion.Thrusters.Length; index++)
        {
            var thruster = propulsion.Thrusters[index];
            thruster.CurrentThrust = MoveScalarTowards(
                thruster.CurrentThrust,
                thruster.TargetThrust,
                thruster.MaximumThrustChangePerTick > 0d ? thruster.MaximumThrustChangePerTick : double.PositiveInfinity);

            if (thruster.MaximumThrust > 0d)
                thruster.CurrentThrust = Math.Clamp(thruster.CurrentThrust, -thruster.MaximumThrust, thruster.MaximumThrust);

            var angleRadians = thruster.WorldAngleDegrees * (Math.PI / 180d);
            totalX += Math.Cos(angleRadians) * thruster.CurrentThrust;
            totalY += Math.Sin(angleRadians) * thruster.CurrentThrust;
        }

        propulsion.CurrentX = totalX;
        propulsion.CurrentY = totalY;
        return (totalX, totalY);
    }

    private static (double X, double Y) MoveVectorTowards(double currentX, double currentY, double targetX, double targetY, double maximumDelta)
    {
        if (!(maximumDelta > 0d) || double.IsInfinity(maximumDelta))
            return (targetX, targetY);

        var deltaX = targetX - currentX;
        var deltaY = targetY - currentY;
        var distance = Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
        if (distance <= maximumDelta || distance <= 0d)
            return (targetX, targetY);

        var scale = maximumDelta / distance;
        return (currentX + (deltaX * scale), currentY + (deltaY * scale));
    }

    private static double MoveScalarTowards(double current, double target, double maximumDelta)
    {
        if (!(maximumDelta > 0d) || double.IsInfinity(maximumDelta))
            return target;

        var delta = target - current;
        if (Math.Abs(delta) <= maximumDelta)
            return target;

        return current + Math.Sign(delta) * maximumDelta;
    }

    private static (double X, double Y) ClampMagnitude(double x, double y, double maximumMagnitude)
    {
        var magnitudeSquared = (x * x) + (y * y);
        if (magnitudeSquared <= 0d || magnitudeSquared <= maximumMagnitude * maximumMagnitude)
            return (x, y);

        var scale = maximumMagnitude / Math.Sqrt(magnitudeSquared);
        return (x * scale, y * scale);
    }

    private static void AppendTrajectoryPoint(List<TrajectoryPointDto> points, TrajectoryPoint candidate, double minimumPointDistance)
    {
        if (points.Count > 0)
        {
            var previous = points[^1];
            var dx = candidate.X - previous.X;
            var dy = candidate.Y - previous.Y;
            if ((dx * dx) + (dy * dy) < minimumPointDistance * minimumPointDistance)
                return;
        }

        points.Add(new TrajectoryPointDto
        {
            X = (float)candidate.X,
            Y = (float)candidate.Y
        });
    }

    private static bool IsPlayerShipKind(string? kind)
    {
        var normalizedKind = CanonicalizeKind(kind);
        return string.Equals(normalizedKind, "classicship", StringComparison.Ordinal) ||
            string.Equals(normalizedKind, "classicshipplayerunit", StringComparison.Ordinal) ||
            string.Equals(normalizedKind, "modernship", StringComparison.Ordinal) ||
            string.Equals(normalizedKind, "modernshipplayerunit", StringComparison.Ordinal);
    }

    private static string CanonicalizeKind(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
            return string.Empty;

        return string.Concat(kind.Where(char.IsLetterOrDigit)).ToLowerInvariant();
    }
}

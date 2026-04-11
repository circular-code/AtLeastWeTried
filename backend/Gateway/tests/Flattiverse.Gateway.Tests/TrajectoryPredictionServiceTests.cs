using Flattiverse.Gateway.Protocol.Dtos;
using Flattiverse.Gateway.Services;

namespace Flattiverse.Gateway.Tests;

public sealed class TrajectoryPredictionServiceTests
{
    private static readonly TrajectoryPredictionService.PredictionOptions DefaultOptions = new(
        LookaheadTicks: 20,
        MaximumTicks: 72,
        Downsample: 1,
        MinimumPointDistance: 0.01d);

    [Fact]
    public void Hidden_trajectory_applies_known_direct_thrust_vector()
    {
        var driftingTarget = CreateTargetUnit();
        var acceleratedTarget = CreateTargetUnit();
        acceleratedTarget.PropulsionPrediction = new PropulsionPredictionSnapshotDto
        {
            Mode = PropulsionPredictionMode.DirectVector,
            CurrentX = 0.5f,
            CurrentY = 0f,
            TargetX = 0.5f,
            TargetY = 0f,
            MaximumMagnitude = 1f
        };

        var withoutThrust = TrajectoryPredictionService.BuildHiddenTrajectory(
            driftingTarget,
            new[] { driftingTarget },
            currentTick: 100,
            DefaultOptions);

        var withThrust = TrajectoryPredictionService.BuildHiddenTrajectory(
            acceleratedTarget,
            new[] { acceleratedTarget },
            currentTick: 100,
            DefaultOptions);

        Assert.NotNull(withoutThrust);
        Assert.NotNull(withThrust);
        Assert.True(withThrust![^1].X > withoutThrust![^1].X + 25f,
            $"Expected direct thrust to materially extend the hidden path ({withoutThrust[^1].X:F2} -> {withThrust[^1].X:F2}).");
    }

    [Fact]
    public void Hidden_trajectory_accounts_for_moving_obstacles()
    {
        var target = CreateTargetUnit();
        target.MovementX = 2f;
        target.Radius = 2f;

        var movingObstacle = new UnitSnapshotDto
        {
            UnitId = "moving-obstacle",
            ClusterId = 1,
            Kind = "classic-ship",
            IsSeen = true,
            IsStatic = false,
            IsSolid = true,
            LastSeenTick = 100,
            X = 10f,
            Y = 4f,
            MovementX = 0f,
            MovementY = -1f,
            Radius = 2f,
            Gravity = 0f
        };

        var predicted = TrajectoryPredictionService.BuildHiddenTrajectory(
            target,
            new[] { target, movingObstacle },
            currentTick: 100,
            DefaultOptions);

        Assert.NotNull(predicted);
        Assert.True(predicted![^1].X <= 8.1f,
            $"Expected prediction to stop once the moving obstacle intersects the path, got final X={predicted[^1].X:F2}.");
        Assert.True(predicted.Count <= 6, $"Expected early stop after collision, got {predicted.Count} points.");
    }

    [Fact]
    public void Hidden_trajectory_ramps_directional_thrusters_over_time()
    {
        var target = CreateTargetUnit();
        target.MovementX = 0f;
        target.PropulsionPrediction = new PropulsionPredictionSnapshotDto
        {
            Mode = PropulsionPredictionMode.DirectionalThrusters,
            Thrusters = new List<PropulsionThrusterSnapshotDto>
            {
                new()
                {
                    WorldAngleDegrees = 0f,
                    CurrentThrust = 0f,
                    TargetThrust = 1f,
                    MaximumThrust = 1f,
                    MaximumThrustChangePerTick = 0.25f
                }
            }
        };

        var predicted = TrajectoryPredictionService.BuildHiddenTrajectory(
            target,
            new[] { target },
            currentTick: 100,
            DefaultOptions);

        Assert.NotNull(predicted);
        Assert.True(predicted!.Count >= 4, $"Expected several ramp samples, got {predicted.Count} points.");

        var firstStep = predicted[1].X - predicted[0].X;
        var secondStep = predicted[2].X - predicted[1].X;
        var thirdStep = predicted[3].X - predicted[2].X;

        Assert.True(secondStep > firstStep, $"Expected ramping thrust to increase displacement ({firstStep:F2} -> {secondStep:F2}).");
        Assert.True(thirdStep >= secondStep, $"Expected continued ramp-up or hold ({secondStep:F2} -> {thirdStep:F2}).");
    }

    private static UnitSnapshotDto CreateTargetUnit()
    {
        return new UnitSnapshotDto
        {
            UnitId = "target",
            ClusterId = 1,
            Kind = "classic-ship",
            IsSeen = false,
            IsStatic = false,
            IsSolid = true,
            LastSeenTick = 100,
            X = 0f,
            Y = 0f,
            MovementX = 0.5f,
            MovementY = 0f,
            Radius = 1f,
            Gravity = 0f
        };
    }
}

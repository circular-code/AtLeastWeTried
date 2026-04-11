using System.Numerics;
using Flattiverse.Gateway.Services;

namespace Flattiverse.Gateway.Tests;

public sealed class ScanningServiceTests
{
    [Fact]
    public void Targeting_solution_follows_unseen_predicted_trajectory_arc()
    {
        var predictedTrajectory = new[]
        {
            new ScanningService.TargetPathPoint(120f, 0f),
            new ScanningService.TargetPathPoint(120f, 60f),
            new ScanningService.TargetPathPoint(120f, 120f)
        };

        var target = new ScanningService.TargetSnapshot(
            X: 120f,
            Y: 0f,
            VelocityX: 0f,
            VelocityY: 0f,
            HasVelocity: true,
            IsSeen: false,
            LastSeenTick: 100,
            CurrentTick: 104,
            CurrentThrust: 0f,
            MaximumThrust: 1f,
            PredictedTrajectory: predictedTrajectory);

        var resolved = ScanningService.TryResolveTargetingSolution(
            scannerOrigin: Vector2.Zero,
            desiredWidth: 20f,
            minimumWidth: 5f,
            maximumWidth: 90f,
            target: target,
            resolvedVelocityX: 0f,
            resolvedVelocityY: 0f,
            out var solution);

        Assert.True(resolved);
        Assert.InRange(solution.Angle, 15f, 35f);
        Assert.True(solution.Width > 35f, $"Expected the predicted path to widen the cone beyond the requested width, got {solution.Width:F2}.");
    }

    [Fact]
    public void Targeting_solution_widens_with_thrust_uncertainty_for_unseen_ship()
    {
        var targetWithoutThrust = new ScanningService.TargetSnapshot(
            X: 200f,
            Y: 0f,
            VelocityX: 0f,
            VelocityY: 0f,
            HasVelocity: true,
            IsSeen: false,
            LastSeenTick: 200,
            CurrentTick: 208,
            CurrentThrust: 0f,
            MaximumThrust: 2f,
            PredictedTrajectory: new[] { new ScanningService.TargetPathPoint(200f, 0f) });

        var targetWithThrust = targetWithoutThrust with
        {
            CurrentThrust = 1.5f
        };

        Assert.True(ScanningService.TryResolveTargetingSolution(
            scannerOrigin: Vector2.Zero,
            desiredWidth: 5f,
            minimumWidth: 5f,
            maximumWidth: 90f,
            target: targetWithoutThrust,
            resolvedVelocityX: 0f,
            resolvedVelocityY: 0f,
            out var withoutThrust));

        Assert.True(ScanningService.TryResolveTargetingSolution(
            scannerOrigin: Vector2.Zero,
            desiredWidth: 5f,
            minimumWidth: 5f,
            maximumWidth: 90f,
            target: targetWithThrust,
            resolvedVelocityX: 0f,
            resolvedVelocityY: 0f,
            out var withThrust));

        Assert.Equal(0f, withoutThrust.Angle, precision: 3);
        Assert.Equal(0f, withThrust.Angle, precision: 3);
        Assert.True(withThrust.Width > withoutThrust.Width,
            $"Expected thrust uncertainty to widen the cone ({withoutThrust.Width:F2} -> {withThrust.Width:F2}).");
    }

    [Fact]
    public void Targeting_solution_ignores_stale_last_seen_point_for_unseen_target()
    {
        var predictedTrajectory = new[]
        {
            new ScanningService.TargetPathPoint(-120f, 0f),
            new ScanningService.TargetPathPoint(120f, 0f),
            new ScanningService.TargetPathPoint(120f, 60f)
        };

        var target = new ScanningService.TargetSnapshot(
            X: -120f,
            Y: 0f,
            VelocityX: 0f,
            VelocityY: 0f,
            HasVelocity: false,
            IsSeen: false,
            LastSeenTick: 100,
            CurrentTick: 104,
            CurrentThrust: 0f,
            MaximumThrust: 1f,
            PredictedTrajectory: predictedTrajectory);

        var resolved = ScanningService.TryResolveTargetingSolution(
            scannerOrigin: Vector2.Zero,
            desiredWidth: 5f,
            minimumWidth: 5f,
            maximumWidth: 90f,
            target: target,
            resolvedVelocityX: 0f,
            resolvedVelocityY: 0f,
            out var solution);

        Assert.True(resolved);
        Assert.InRange(solution.Angle, 0f, 20f);
        Assert.InRange(solution.Width, 20f, 40f);
    }
}

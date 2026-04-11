using System.Reflection;
using Flattiverse.Connector.Units;
using Flattiverse.Gateway.Sessions;

namespace Flattiverse.Gateway.Tests;

public sealed class PlayerSessionWeaponAimingTests
{
    private static readonly MethodInfo BuildDirectPointShotFallbackMovementMethod = typeof(PlayerSession).GetMethod(
        "BuildDirectPointShotFallbackMovement",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("BuildDirectPointShotFallbackMovement method not found.");

    private static readonly MethodInfo ShouldFireRailgunBackMethod = typeof(PlayerSession).GetMethod(
        "ShouldFireRailgunBack",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("ShouldFireRailgunBack method not found.");

    private static readonly MethodInfo BuildCompensatedPointShotMovementMethod = typeof(PlayerSession).GetMethod(
        "BuildCompensatedPointShotMovement",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("BuildCompensatedPointShotMovement method not found.");

    private static readonly MethodInfo ComputePointShotMissDistanceMethod = typeof(PlayerSession).GetMethod(
        "ComputePointShotMissDistance",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("ComputePointShotMissDistance method not found.");

    [Fact]
    public void Direct_point_fallback_aiming_supports_backward_shots()
    {
        Vector shot = InvokeFallbackAiming(
            sourceX: 0f,
            sourceY: 0f,
            fallbackAngle: 0f,
            targetX: -100f,
            targetY: 0f,
            relativeSpeed: 2f);

        Assert.True(shot.X < -1.99f, $"Expected strong negative X for backward shot, got {shot.X:0.###}");
        Assert.True(MathF.Abs(shot.Y) < 0.01f, $"Expected near-zero Y for backward shot, got {shot.Y:0.###}");
    }

    [Fact]
    public void Direct_point_fallback_aiming_supports_vertical_shots()
    {
        Vector shot = InvokeFallbackAiming(
            sourceX: 0f,
            sourceY: 0f,
            fallbackAngle: 0f,
            targetX: 0f,
            targetY: 100f,
            relativeSpeed: 2f);

        Assert.True(MathF.Abs(shot.X) < 0.01f, $"Expected near-zero X for vertical shot, got {shot.X:0.###}");
        Assert.True(shot.Y > 1.99f, $"Expected strong positive Y for vertical shot, got {shot.Y:0.###}");
    }

    [Theory]
    [InlineData(0f, false)]
    [InlineData(45f, false)]
    [InlineData(90f, false)]
    [InlineData(180f, true)]
    [InlineData(-120f, true)]
    [InlineData(270f, false)]
    [InlineData(300f, false)]
    public void Railgun_back_decision_uses_normalized_relative_angle(float relativeAngle, bool expectedBack)
    {
        bool shouldFireBack = (bool)(ShouldFireRailgunBackMethod.Invoke(null, new object[] { relativeAngle })
            ?? throw new InvalidOperationException("Railgun direction invocation returned null."));

        Assert.Equal(expectedBack, shouldFireBack);
    }

    [Fact]
    public void Compensated_point_shot_reduces_miss_for_moving_ship()
    {
        const float sourceX = 0f;
        const float sourceY = 0f;
        const float sourceMovementX = 0f;
        const float sourceMovementY = 1f;
        const float targetX = 100f;
        const float targetY = 50f;
        const float relativeSpeed = 2f;
        const int ticks = 50;

        Vector compensated = InvokeCompensatedPointShotMovement(
            sourceX,
            sourceY,
            sourceMovementX,
            sourceMovementY,
            targetX,
            targetY,
            relativeSpeed,
            ticks,
            launchOffsetDistance: 0f);

        Vector naive = InvokeFallbackAiming(
            sourceX,
            sourceY,
            fallbackAngle: 0f,
            targetX,
            targetY,
            relativeSpeed);

        float compensatedMiss = InvokePointShotMissDistance(
            sourceX,
            sourceY,
            sourceMovementX,
            sourceMovementY,
            launchOffsetDistance: 0f,
            compensated,
            ticks,
            targetX,
            targetY);

        float naiveMiss = InvokePointShotMissDistance(
            sourceX,
            sourceY,
            sourceMovementX,
            sourceMovementY,
            launchOffsetDistance: 0f,
            naive,
            ticks,
            targetX,
            targetY);

        Assert.True(compensatedMiss < 1f, $"Expected very low compensated miss, got {compensatedMiss:0.###}");
        Assert.True(compensatedMiss < naiveMiss * 0.2f,
            $"Expected compensated miss << naive miss. compensated={compensatedMiss:0.###}, naive={naiveMiss:0.###}");
    }

    private static Vector InvokeFallbackAiming(
        float sourceX,
        float sourceY,
        float fallbackAngle,
        float targetX,
        float targetY,
        float relativeSpeed)
    {
        return (Vector)(BuildDirectPointShotFallbackMovementMethod.Invoke(
                null,
                new object[] { sourceX, sourceY, fallbackAngle, targetX, targetY, relativeSpeed })
            ?? throw new InvalidOperationException("Fallback aiming invocation returned null."));
    }

    private static Vector InvokeCompensatedPointShotMovement(
        float sourceX,
        float sourceY,
        float sourceMovementX,
        float sourceMovementY,
        float targetX,
        float targetY,
        float relativeSpeed,
        int ticks,
        float launchOffsetDistance)
    {
        return (Vector)(BuildCompensatedPointShotMovementMethod.Invoke(
                null,
                new object[]
                {
                    sourceX,
                    sourceY,
                    sourceMovementX,
                    sourceMovementY,
                    targetX,
                    targetY,
                    relativeSpeed,
                    ticks,
                    launchOffsetDistance
                })
            ?? throw new InvalidOperationException("Compensated point-shot invocation returned null."));
    }

    private static float InvokePointShotMissDistance(
        float sourceX,
        float sourceY,
        float sourceMovementX,
        float sourceMovementY,
        float launchOffsetDistance,
        Vector relativeMovement,
        int ticks,
        float targetX,
        float targetY)
    {
        return (float)(ComputePointShotMissDistanceMethod.Invoke(
                null,
                new object[]
                {
                    sourceX,
                    sourceY,
                    sourceMovementX,
                    sourceMovementY,
                    launchOffsetDistance,
                    relativeMovement,
                    ticks,
                    targetX,
                    targetY
                })
            ?? throw new InvalidOperationException("Point-shot miss-distance invocation returned null."));
    }
}

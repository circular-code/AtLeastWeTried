using Flattiverse.Gateway.Services;

namespace Flattiverse.Gateway.Tests;

public sealed class ManeuveringServiceTests
{
    [Fact]
    public void ComputeStoppingDistance_returns_zero_for_non_positive_inputs()
    {
        Assert.Equal(0f, ManeuveringService.ComputeStoppingDistance(0f, 10f));
        Assert.Equal(0f, ManeuveringService.ComputeStoppingDistance(8f, 0f));
        Assert.Equal(0f, ManeuveringService.ComputeStoppingDistance(-4f, 10f));
    }

    [Fact]
    public void ComputeStoppingDistance_matches_kinematics_formula()
    {
        var distance = ManeuveringService.ComputeStoppingDistance(30f, 20f);
        Assert.Equal(22.5f, distance, 3);
    }

    [Fact]
    public void ApplyStoppingBrake_overrides_forward_thrust_when_overshoot_is_likely()
    {
        var magnitude = ManeuveringService.ApplyStoppingBrake(
            targetMagnitude: 8f,
            distanceError: 40f,
            closingSpeed: 30f,
            undesiredSpeed: 0f,
            desiredVectorLengthLimit: 10f,
            maximumVectorLength: 20f,
            brakeDistance: 20f,
            driftBrakeDistanceFactor: 10f);

        Assert.True(magnitude < 0f, $"Expected braking (negative thrust), got {magnitude:0.###}.");
    }

    [Fact]
    public void ApplyStoppingBrake_keeps_forward_thrust_when_ship_is_not_yet_in_braking_zone()
    {
        var magnitude = ManeuveringService.ApplyStoppingBrake(
            targetMagnitude: 8f,
            distanceError: 250f,
            closingSpeed: 20f,
            undesiredSpeed: 0f,
            desiredVectorLengthLimit: 10f,
            maximumVectorLength: 20f,
            brakeDistance: 20f,
            driftBrakeDistanceFactor: 10f);

        Assert.Equal(8f, magnitude, 3);
    }

    [Fact]
    public void ApplyStoppingBrake_brakes_earlier_when_entering_extended_buffer_zone()
    {
        var magnitude = ManeuveringService.ApplyStoppingBrake(
            targetMagnitude: 8f,
            distanceError: 47f,
            closingSpeed: 30f,
            undesiredSpeed: 0f,
            desiredVectorLengthLimit: 10f,
            maximumVectorLength: 20f,
            brakeDistance: 20f,
            driftBrakeDistanceFactor: 10f);

        Assert.True(magnitude < 0f, $"Expected earlier braking near target buffer, got {magnitude:0.###}.");
    }
}

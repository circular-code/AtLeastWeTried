using System.Reflection;
using System.Runtime.Serialization;
using Flattiverse.Connector.GalaxyHierarchy;
using Flattiverse.Connector.Units;
using Flattiverse.Gateway.Services;

namespace Flattiverse.Gateway.Tests;

public sealed class TacticalServiceTests
{
    [Fact]
    public void Scan_mode_candidate_filter_accepts_hostile_mobile_units_only()
    {
        var friendlyTeam = CreateTeam(1);
        var hostileTeam = CreateTeam(2);

        var hostileAiShip = CreateNpcUnit<AiShip>(hostileTeam);
        var friendlyProbe = CreateNpcUnit<AiProbe>(friendlyTeam);
        var hostilePlayerShip = CreatePlayerUnit(hostileTeam);
        var projectile = (Projectile)FormatterServices.GetUninitializedObject(typeof(Projectile));
        var missionTarget = (MissionTarget)FormatterServices.GetUninitializedObject(typeof(MissionTarget));

        Assert.True(TacticalService.IsScanModeTargetCandidate(hostileAiShip, friendlyTeam));
        Assert.True(TacticalService.IsScanModeTargetCandidate(hostilePlayerShip, friendlyTeam));
        Assert.False(TacticalService.IsScanModeTargetCandidate(friendlyProbe, friendlyTeam));
        Assert.False(TacticalService.IsScanModeTargetCandidate(projectile, friendlyTeam));
        Assert.False(TacticalService.IsScanModeTargetCandidate(missionTarget, friendlyTeam));
    }

    [Fact]
    public void Overlay_reports_scan_mode()
    {
        var service = new TacticalService();

        service.SetMode("p1-c4", TacticalService.TacticalMode.Scan);

        var overlay = service.BuildOverlay("p1-c4");

        Assert.Equal("scan", overlay["mode"]);
        Assert.Equal(false, overlay["hasTarget"]);
        Assert.Null(overlay["targetId"]);
    }

    [Fact]
    public void Speed_only_intercept_hits_moving_target_when_solution_exists()
    {
        var hasSolution = TacticalService.TryComputeSpeedOnlyIntercept(
            shipX: 0f,
            shipY: 0f,
            shipVx: 0f,
            shipVy: 0f,
            shotSpeed: 2f,
            spawnOffset: 0f,
            minTicks: 1,
            maxTicks: 120,
            targetX: 70f,
            targetY: 0f,
            targetVx: -0.8f,
            targetVy: 0f,
            targetRadius: 1f,
            out var movement,
            out var ticks,
            out var missDistance);

        Assert.True(hasSolution);
        Assert.True(ticks > 0);
        Assert.InRange(movement.Length, 1.999f, 2.001f);
        Assert.InRange(missDistance, 0f, 2f);
    }

    [Fact]
    public void Speed_only_intercept_accounts_for_target_speed()
    {
        var movingAway = TacticalService.TryComputeSpeedOnlyIntercept(
            shipX: 0f,
            shipY: 0f,
            shipVx: 0f,
            shipVy: 0f,
            shotSpeed: 2f,
            spawnOffset: 0f,
            minTicks: 1,
            maxTicks: 120,
            targetX: 50f,
            targetY: 0f,
            targetVx: 0.8f,
            targetVy: 0f,
            targetRadius: 1f,
            out _,
            out var awayTicks,
            out _);

        var movingToward = TacticalService.TryComputeSpeedOnlyIntercept(
            shipX: 0f,
            shipY: 0f,
            shipVx: 0f,
            shipVy: 0f,
            shotSpeed: 2f,
            spawnOffset: 0f,
            minTicks: 1,
            maxTicks: 120,
            targetX: 50f,
            targetY: 0f,
            targetVx: -0.8f,
            targetVy: 0f,
            targetRadius: 1f,
            out _,
            out var towardTicks,
            out _);

        Assert.True(movingAway);
        Assert.True(movingToward);
        Assert.True(awayTicks > towardTicks);
    }

    private static T CreateNpcUnit<T>(Team team)
        where T : MobileNpcUnit
    {
        var unit = (T)FormatterServices.GetUninitializedObject(typeof(T));
        SetField(typeof(NpcUnit), unit, "_team", team);
        return unit;
    }

    private static PlayerUnit CreatePlayerUnit(Team team)
    {
        var player = (Player)FormatterServices.GetUninitializedObject(typeof(Player));
        SetField(typeof(Player), player, "Team", team);

        var unit = (PlayerUnit)FormatterServices.GetUninitializedObject(typeof(PlayerUnit));
        SetField(typeof(PlayerUnit), unit, "Player", player);
        return unit;
    }

    private static Team CreateTeam(byte id)
    {
        var team = (Team)FormatterServices.GetUninitializedObject(typeof(Team));
        SetField(typeof(Team), team, "Id", id);
        return team;
    }

    private static void SetField(Type declaringType, object instance, string fieldName, object? value)
    {
        var field = declaringType.GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field {declaringType.FullName}.{fieldName} was not found.");
        field.SetValue(instance, value);
    }
}

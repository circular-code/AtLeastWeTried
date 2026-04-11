using System.Reflection;
using Flattiverse.Gateway.Services;
using Flattiverse.Gateway.Sessions;

namespace Flattiverse.Gateway.Tests;

public sealed class PlayerSessionTargetResolutionTests
{
    private static readonly MethodInfo TryParseControllableIdForPlayerMethod = typeof(PlayerSession).GetMethod(
        "TryParseControllableIdForPlayer",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("TryParseControllableIdForPlayer method not found.");
    private static readonly MethodInfo TryBuildOverlayTargetSnapshotMethod = typeof(PlayerSession).GetMethod(
        "TryBuildOverlayTargetSnapshot",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("TryBuildOverlayTargetSnapshot method not found.");

    [Fact]
    public void Current_player_controllable_parser_requires_exact_player_match()
    {
        Assert.True(InvokeTryParseControllableIdForPlayer("p7-c12", playerId: 7, out var localControllableId));
        Assert.Equal(12, localControllableId);

        Assert.False(InvokeTryParseControllableIdForPlayer("p8-c12", playerId: 7, out _));
    }

    [Fact]
    public void Overlay_target_snapshot_reads_marker_position_and_velocity()
    {
        Dictionary<string, object?> changes = new(StringComparer.Ordinal)
        {
            ["position"] = new Dictionary<string, object?>
            {
                ["x"] = 125f,
                ["y"] = -45f,
                ["angle"] = 90f
            },
            ["movement"] = new Dictionary<string, object?>
            {
                ["x"] = 2.5f,
                ["y"] = -1.5f
            },
            ["engine"] = new Dictionary<string, object?>
            {
                ["maximum"] = 8f,
                ["currentX"] = 3f,
                ["currentY"] = 4f
            }
        };

        Assert.True(InvokeTryBuildOverlayTargetSnapshot(changes, currentTick: 321u, out var target));
        Assert.Equal(125f, target.X, precision: 3);
        Assert.Equal(-45f, target.Y, precision: 3);
        Assert.True(target.HasVelocity);
        Assert.Equal(2.5f, target.VelocityX, precision: 3);
        Assert.Equal(-1.5f, target.VelocityY, precision: 3);
        Assert.Equal(5f, target.CurrentThrust, precision: 3);
        Assert.Equal(8f, target.MaximumThrust, precision: 3);
        Assert.True(target.IsSeen);
    }

    private static bool InvokeTryParseControllableIdForPlayer(string value, int playerId, out int controllableId)
    {
        object?[] arguments = [value, playerId, 0];
        var resolved = (bool)(TryParseControllableIdForPlayerMethod.Invoke(null, arguments)
            ?? throw new InvalidOperationException("TryParseControllableIdForPlayer returned null."));
        controllableId = (int)arguments[2];
        return resolved;
    }

    private static bool InvokeTryBuildOverlayTargetSnapshot(
        Dictionary<string, object?> changes,
        uint currentTick,
        out ScanningService.TargetSnapshot target)
    {
        object?[] arguments = [changes, currentTick, default(ScanningService.TargetSnapshot)];
        var resolved = (bool)(TryBuildOverlayTargetSnapshotMethod.Invoke(null, arguments)
            ?? throw new InvalidOperationException("TryBuildOverlayTargetSnapshot returned null."));
        target = (ScanningService.TargetSnapshot)(arguments[2]
            ?? throw new InvalidOperationException("TryBuildOverlayTargetSnapshot target was null."));
        return resolved;
    }
}

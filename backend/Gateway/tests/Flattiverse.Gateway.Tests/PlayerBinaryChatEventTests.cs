using System.Reflection;
using System.Runtime.Serialization;
using Flattiverse.Connector.Events;
using Flattiverse.Connector.GalaxyHierarchy;

namespace Flattiverse.Gateway.Tests;

public sealed class PlayerBinaryChatEventTests
{
    [Fact]
    public void ToString_tolerates_missing_player_team_and_destination()
    {
        var player = CreatePlayer(name: "Scout", team: null);
        var @event = CreateBinaryEvent(player, destination: null, message: [0xDE, 0xAD, 0xBE, 0xEF]);

        var text = @event.ToString();

        Assert.Contains("[?]Scout->?", text);
        Assert.Contains("0xDEADBEEF", text);
    }

    private static PlayerBinaryChatEvent CreateBinaryEvent(Player? player, Player? destination, byte[]? message)
    {
        var @event = (PlayerBinaryChatEvent)FormatterServices.GetUninitializedObject(typeof(PlayerBinaryChatEvent));
        SetInstanceField(typeof(FlattiverseEvent), @event, "Stamp", new DateTime(2026, 4, 11, 12, 0, 0, DateTimeKind.Utc));
        SetInstanceField(typeof(PlayerEvent), @event, "Player", player);
        SetInstanceField(typeof(PlayerBinaryChatEvent), @event, "Destination", destination);
        SetInstanceField(typeof(PlayerBinaryChatEvent), @event, "Message", message);
        return @event;
    }

    private static Player CreatePlayer(string name, Team? team)
    {
        var player = (Player)FormatterServices.GetUninitializedObject(typeof(Player));
        SetInstanceField(typeof(Player), player, "_name", name);
        SetInstanceField(typeof(Player), player, "Team", team);
        return player;
    }

    private static void SetInstanceField(Type declaringType, object target, string fieldName, object? value)
    {
        var field = declaringType.GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field '{fieldName}' not found on {declaringType.FullName}.");
        field.SetValue(target, value);
    }
}

using System.Text.Json;

namespace Flattiverse.Gateway.Protocol.ClientMessages;

public sealed class ClientMessage
{
    public string Type { get; set; } = "";
    public string? CommandId { get; set; }
    public string? PlayerSessionId { get; set; }
    public JsonElement? Payload { get; set; }

    public static ClientMessage? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeProp))
                return null;

            var msg = new ClientMessage
            {
                Type = typeProp.GetString() ?? ""
            };

            if (root.TryGetProperty("commandId", out var cmdId))
                msg.CommandId = cmdId.GetString();

            if (root.TryGetProperty("playerSessionId", out var psId))
                msg.PlayerSessionId = psId.GetString();

            if (root.TryGetProperty("payload", out var payload))
                msg.Payload = payload.Clone();

            return msg;
        }
        catch
        {
            return null;
        }
    }
}

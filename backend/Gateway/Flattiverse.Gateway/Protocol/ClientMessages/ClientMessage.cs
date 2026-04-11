namespace Flattiverse.Gateway.Protocol.ClientMessages;

public sealed class ClientMessage
{
    public string Type { get; set; } = "";
    public string? CommandId { get; set; }
    public string? PlayerSessionId { get; set; }
    public PayloadElement? Payload { get; set; }

    public static ClientMessage? Parse(object? raw)
    {
        try
        {
            if (raw is not IDictionary<object, object> dict)
                return null;

            if (!dict.TryGetValue("type", out var typeValue) || typeValue is not string typeStr)
                return null;

            var msg = new ClientMessage
            {
                Type = typeStr
            };

            if (dict.TryGetValue("commandId", out var cmdVal) && cmdVal is string cmdStr)
                msg.CommandId = cmdStr;

            if (dict.TryGetValue("playerSessionId", out var psVal) && psVal is string psStr)
                msg.PlayerSessionId = psStr;

            if (dict.TryGetValue("payload", out var payloadVal) && payloadVal is not null)
                msg.Payload = new PayloadElement(payloadVal);

            return msg;
        }
        catch
        {
            return null;
        }
    }
}

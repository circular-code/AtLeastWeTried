using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Flattiverse.Gateway.Application.Abstractions;
using Flattiverse.Gateway.Application.Exceptions;
using Flattiverse.Gateway.Contracts.Messages.Client;
using Flattiverse.Gateway.Contracts.Messages.Server;

namespace Flattiverse.Gateway.Infrastructure.Transport;

public sealed class SystemTextJsonGatewayMessageSerializer : IGatewayMessageSerializer
{
    private readonly JsonSerializerOptions serializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public ClientMessage DeserializeClientMessage(string json)
    {
        JsonObject root;

        try
        {
            root = JsonNode.Parse(json)?.AsObject()
                ?? throw new GatewayClientMessageException("invalid_message_shape", "The gateway expects a JSON object payload.", true);
        }
        catch (JsonException exception)
        {
            throw new GatewayClientMessageException("invalid_json", exception.Message, true);
        }

        if (!root.TryGetPropertyValue("type", out var typeNode) || typeNode?.GetValue<string>() is not { Length: > 0 } messageType)
        {
            throw new GatewayClientMessageException("missing_type", "Every message must include a string 'type' discriminator.", true);
        }

        return messageType switch
        {
            "connection.attach" => root.Deserialize<ConnectionAttachMessage>(serializerOptions)
                ?? throw new GatewayClientMessageException("invalid_message_shape", "connection.attach has an invalid message shape.", true),
            "connection.detach" => root.Deserialize<ConnectionDetachMessage>(serializerOptions)
                ?? throw new GatewayClientMessageException("invalid_message_shape", "connection.detach has an invalid message shape.", true),
            "player.select" => root.Deserialize<PlayerSelectMessage>(serializerOptions)
                ?? throw new GatewayClientMessageException("invalid_message_shape", "player.select has an invalid message shape.", true),
            "pong" => new PongMessage(),
            _ when messageType.StartsWith("command.", StringComparison.Ordinal) => DeserializeCommandMessage(root, messageType),
            _ => throw new GatewayClientMessageException("unknown_message_type", $"The gateway does not recognize message type '{messageType}'.", true)
        };
    }

    public byte[] SerializeServerMessage(GatewayMessage message)
    {
        return JsonSerializer.SerializeToUtf8Bytes(message, message.GetType(), serializerOptions);
    }

    private static CommandMessage DeserializeCommandMessage(JsonObject root, string messageType)
    {
        var commandId = root.TryGetPropertyValue("commandId", out var commandIdNode)
            ? commandIdNode?.GetValue<string>()
            : null;

        var payload = root.TryGetPropertyValue("payload", out var payloadNode)
            ? payloadNode as JsonObject
            : null;

        return new CommandMessage(messageType, commandId, payload);
    }
}
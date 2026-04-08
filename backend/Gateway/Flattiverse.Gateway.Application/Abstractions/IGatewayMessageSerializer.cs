using Flattiverse.Gateway.Contracts.Messages.Client;
using Flattiverse.Gateway.Contracts.Messages.Server;

namespace Flattiverse.Gateway.Application.Abstractions;

public interface IGatewayMessageSerializer
{
    ClientMessage DeserializeClientMessage(string json);

    byte[] SerializeServerMessage(GatewayMessage message);
}
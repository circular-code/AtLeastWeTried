using Flattiverse.Gateway.Contracts.Messages.Server;

namespace Flattiverse.Gateway.Application.Abstractions;

public interface IGatewayConnectionMessageQueue
{
    ValueTask RegisterConnectionAsync(string connectionId, CancellationToken cancellationToken);

    ValueTask EnqueueAsync(string connectionId, IReadOnlyList<GatewayMessage> messages, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<GatewayMessage>> DequeueAsync(string connectionId, CancellationToken cancellationToken);

    ValueTask CompleteConnectionAsync(string connectionId, CancellationToken cancellationToken);
}
using Flattiverse.Gateway.Contracts.Messages.Client;
using Flattiverse.Gateway.Contracts.Messages.Server;
using Flattiverse.Gateway.Domain.Sessions;

namespace Flattiverse.Gateway.Application.Abstractions;

public interface IGatewayRuntime
{
    ValueTask EnsurePlayerSessionAsync(AttachedPlayerSession playerSession, CancellationToken cancellationToken);

    ValueTask<WorldSnapshot> GetSnapshotAsync(string playerSessionId, CancellationToken cancellationToken);

    ValueTask<OwnerDeltaMessage> GetOwnerOverlayAsync(string playerSessionId, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<GatewayMessage>> ExecuteCommandAsync(
        string playerSessionId,
        CommandMessage command,
        CancellationToken cancellationToken);

    ValueTask TickAsync(CancellationToken cancellationToken);
}
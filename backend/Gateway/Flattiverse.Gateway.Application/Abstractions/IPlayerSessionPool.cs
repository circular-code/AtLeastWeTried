using Flattiverse.Gateway.Domain.Sessions;

namespace Flattiverse.Gateway.Application.Abstractions;

public interface IPlayerSessionPool
{
    ValueTask<AttachedPlayerSession> AttachAsync(
        string connectionId,
        string apiKey,
        string? teamName,
        CancellationToken cancellationToken);

    ValueTask ReleaseAsync(string connectionId, string playerSessionId, CancellationToken cancellationToken);
}
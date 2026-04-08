using Flattiverse.Gateway.Domain.Sessions;

namespace Flattiverse.Gateway.Application.Abstractions;

public interface IBrowserSessionStore
{
    ValueTask<BrowserSession?> GetAsync(string connectionId, CancellationToken cancellationToken);

    ValueTask<IReadOnlyCollection<BrowserSession>> ListAsync(CancellationToken cancellationToken);

    ValueTask RemoveAsync(string connectionId, CancellationToken cancellationToken);

    ValueTask UpsertAsync(BrowserSession session, CancellationToken cancellationToken);
}
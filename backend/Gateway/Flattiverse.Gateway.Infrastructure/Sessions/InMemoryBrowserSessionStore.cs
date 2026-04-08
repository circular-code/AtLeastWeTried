using System.Collections.Concurrent;
using Flattiverse.Gateway.Application.Abstractions;
using Flattiverse.Gateway.Domain.Sessions;

namespace Flattiverse.Gateway.Infrastructure.Sessions;

public sealed class InMemoryBrowserSessionStore : IBrowserSessionStore
{
    private readonly ConcurrentDictionary<string, BrowserSession> sessions = new(StringComparer.Ordinal);

    public ValueTask<BrowserSession?> GetAsync(string connectionId, CancellationToken cancellationToken)
    {
        sessions.TryGetValue(connectionId, out var session);
        return ValueTask.FromResult(session);
    }

    public ValueTask<IReadOnlyCollection<BrowserSession>> ListAsync(CancellationToken cancellationToken)
    {
        IReadOnlyCollection<BrowserSession> snapshot = sessions.Values.ToArray();
        return ValueTask.FromResult(snapshot);
    }

    public ValueTask RemoveAsync(string connectionId, CancellationToken cancellationToken)
    {
        sessions.TryRemove(connectionId, out _);
        return ValueTask.CompletedTask;
    }

    public ValueTask UpsertAsync(BrowserSession session, CancellationToken cancellationToken)
    {
        sessions[session.ConnectionId] = session;
        return ValueTask.CompletedTask;
    }
}
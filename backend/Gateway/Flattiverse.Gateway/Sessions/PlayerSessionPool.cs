using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Flattiverse.Gateway.Sessions;

public sealed class PlayerSessionPool : IDisposable
{
    private readonly ConcurrentDictionary<string, PlayerSession> _sessions = new();
    private readonly SemaphoreSlim _createLock = new(1, 1);
    private readonly ILoggerFactory _loggerFactory;
    private readonly string _galaxyUrl;

    public PlayerSessionPool(string galaxyUrl, ILoggerFactory loggerFactory)
    {
        _galaxyUrl = galaxyUrl;
        _loggerFactory = loggerFactory;
    }

    public async Task<PlayerSession> GetOrCreateAsync(string apiKey, string? teamName)
    {
        if (_sessions.TryGetValue(apiKey, out var existing) && existing.Connected)
            return existing;

        await _createLock.WaitAsync();
        try
        {
            // Double-check after acquiring lock
            if (_sessions.TryGetValue(apiKey, out existing) && existing.Connected)
                return existing;

            // Dispose old disconnected session if present
            if (existing is not null)
            {
                _sessions.TryRemove(apiKey, out _);
                existing.Dispose();
            }

            var id = $"ps-{Guid.NewGuid():N}";
            var logger = _loggerFactory.CreateLogger<PlayerSession>();
            var session = new PlayerSession(id, apiKey, teamName, _galaxyUrl, logger);
            await session.ConnectAsync();

            _sessions[apiKey] = session;
            return session;
        }
        finally
        {
            _createLock.Release();
        }
    }

    public IEnumerable<PlayerSession> GetActiveSessions()
    {
        return _sessions.Values.Where(s => s.Connected);
    }

    public void Dispose()
    {
        foreach (var kvp in _sessions)
            kvp.Value.Dispose();

        _sessions.Clear();
        _createLock.Dispose();
    }
}

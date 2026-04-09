using System.Collections.Concurrent;
using Flattiverse.Gateway.Options;
using Flattiverse.Gateway.Services.Navigation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Flattiverse.Gateway.Sessions;

public sealed class PlayerSessionPool : IDisposable
{
    private readonly ConcurrentDictionary<string, PlayerSession> _sessions = new();
    private readonly SemaphoreSlim _createLock = new(1, 1);
    private readonly ILoggerFactory _loggerFactory;
    private readonly string _galaxyUrl;
    private readonly IOptions<PathfindingOptions> _pathfindingOptions;

    public PlayerSessionPool(string galaxyUrl, ILoggerFactory loggerFactory, IOptions<PathfindingOptions> pathfindingOptions)
    {
        _galaxyUrl = galaxyUrl;
        _loggerFactory = loggerFactory;
        _pathfindingOptions = pathfindingOptions;
    }

    public async Task<PlayerSession> GetOrCreateAsync(string apiKey, string? teamName)
    {
        if (_sessions.TryGetValue(apiKey, out var existing))
        {
            await existing.EnsureConnectedAsync();
            return existing;
        }

        await _createLock.WaitAsync();
        try
        {
            // Double-check after acquiring lock
            if (_sessions.TryGetValue(apiKey, out existing))
            {
                await existing.EnsureConnectedAsync();
                return existing;
            }

            var id = $"ps-{Guid.NewGuid():N}";
            var logger = _loggerFactory.CreateLogger<PlayerSession>();
            var pathfindingLogger = _loggerFactory.CreateLogger<PathfindingService>();
            var session = new PlayerSession(id, apiKey, teamName, _galaxyUrl, logger, pathfindingLogger, _pathfindingOptions);
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

using Flattiverse.Gateway.Sessions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Flattiverse.Gateway.Services;

/// <summary>
/// Singleton hosted service that fires every 40 ms.
/// On each tick it iterates all active player sessions,
/// collects pending world deltas and overlay updates,
/// and delivers them to attached browser connections.
/// </summary>
public sealed class TickService : BackgroundService
{
    private readonly PlayerSessionPool _sessionPool;
    private readonly ILogger<TickService> _logger;
    private readonly TimeSpan _tickInterval;

    public TickService(PlayerSessionPool sessionPool, ILogger<TickService> logger, TimeSpan? tickInterval = null)
    {
        _sessionPool = sessionPool;
        _logger = logger;
        _tickInterval = tickInterval ?? TimeSpan.FromMilliseconds(40);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TickService started with {Interval}ms interval", _tickInterval.TotalMilliseconds);

        using var timer = new PeriodicTimer(_tickInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    Tick();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during tick");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }

        _logger.LogInformation("TickService stopped");
    }

    private void Tick()
    {
        foreach (var session in _sessionPool.GetActiveSessions())
        {
            try
            {
                session.FlushTickDeltas();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error flushing tick deltas for session {SessionId}", session.Id);
            }
        }
    }
}

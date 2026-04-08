using Flattiverse.Connector.GalaxyHierarchy;
using Microsoft.Extensions.Logging;

namespace Flattiverse.Gateway.Connector;

/// <summary>
/// Runs a single task per Galaxy that calls <c>galaxy.NextEvent()</c> in a tight loop
/// and dispatches each event to registered <see cref="IConnectorEventHandler"/> instances.
/// 
/// Critical: only one consumer per Galaxy — this loop is the single reader.
/// Handlers are called synchronously so they can mutate state without locking.
/// </summary>
public sealed class ConnectorEventLoop : IDisposable
{
    private readonly Galaxy _galaxy;
    private readonly IReadOnlyList<IConnectorEventHandler> _handlers;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loopTask;

    /// <summary>
    /// Fired when the event loop terminates (connection lost, error, or cancellation).
    /// </summary>
    public event Action? Terminated;

    public ConnectorEventLoop(Galaxy galaxy, IReadOnlyList<IConnectorEventHandler> handlers, ILogger logger)
    {
        _galaxy = galaxy;
        _handlers = handlers;
        _logger = logger;
        _loopTask = Task.Run(RunLoop);
    }

    private async Task RunLoop()
    {
        try
        {
            while (_galaxy.Active && !_cts.Token.IsCancellationRequested)
            {
                var @event = await _galaxy.NextEvent();

                foreach (var handler in _handlers)
                {
                    try
                    {
                        handler.Handle(@event);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Event handler {Handler} threw on {EventType}",
                            handler.GetType().Name, @event.GetType().Name);
                    }
                }
            }
        }
        catch (Flattiverse.Connector.Network.ConnectionTerminatedGameException)
        {
            _logger.LogWarning("Galaxy connection terminated");
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Event loop crashed");
        }

        Terminated?.Invoke();
    }

    /// <summary>
    /// Wait for the event loop task to finish (for clean shutdown).
    /// </summary>
    public Task WaitAsync() => _loopTask;

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}

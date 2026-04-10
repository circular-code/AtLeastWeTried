using Flattiverse.Connector.GalaxyHierarchy;
using Microsoft.Extensions.Logging;

namespace Flattiverse.Gateway.Connector;

/// <summary>
/// Owns the Connector <see cref="Galaxy"/> instance for a player session.
/// Manages the connection lifecycle: connect, start event loop, and dispose.
/// </summary>
public sealed class GalaxyConnectionManager : IDisposable
{
    private readonly string _galaxyUrl;
    private readonly string _apiKey;
    private readonly string? _teamName;
    private readonly RuntimeDisclosure? _runtimeDisclosure;
    private readonly BuildDisclosure? _buildDisclosure;
    private readonly ILogger _logger;

    private Galaxy? _galaxy;
    private ConnectorEventLoop? _eventLoop;

    public Galaxy? Galaxy => _galaxy;
    public bool Connected => _galaxy?.Active == true;

    /// <summary>
    /// Fired when the underlying connection is lost.
    /// </summary>
    public event Action? ConnectionLost;

    public GalaxyConnectionManager(
        string galaxyUrl,
        string apiKey,
        string? teamName,
        RuntimeDisclosure? runtimeDisclosure,
        BuildDisclosure? buildDisclosure,
        ILogger logger)
    {
        _galaxyUrl = galaxyUrl;
        _apiKey = apiKey;
        _teamName = teamName;
        _runtimeDisclosure = runtimeDisclosure;
        _buildDisclosure = buildDisclosure;
        _logger = logger;
    }

    /// <summary>
    /// Connect to the Galaxy and start the event loop that dispatches to the given handlers.
    /// </summary>
    public async Task ConnectAsync(IReadOnlyList<IConnectorEventHandler> eventHandlers)
    {
        _galaxy = await Galaxy.Connect(_galaxyUrl, _apiKey, _teamName, _runtimeDisclosure, _buildDisclosure);
        _logger.LogInformation("Connected to Galaxy as {Player}", _galaxy.Player.Name);

        _eventLoop = new ConnectorEventLoop(_galaxy, eventHandlers, _logger);
        _eventLoop.Terminated += OnEventLoopTerminated;
    }

    private void OnEventLoopTerminated()
    {
        _logger.LogWarning("Event loop terminated");
        ConnectionLost?.Invoke();
    }

    public void Dispose()
    {
        if (_eventLoop is not null)
        {
            _eventLoop.Terminated -= OnEventLoopTerminated;
            _eventLoop.Dispose();
        }

        _galaxy?.Dispose();
    }
}

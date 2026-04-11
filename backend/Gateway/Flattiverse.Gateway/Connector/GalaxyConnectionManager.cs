using Flattiverse.Connector.GalaxyHierarchy;
using Microsoft.Extensions.Logging;

namespace Flattiverse.Gateway.Connector;

/// <summary>
/// Owns the Connector <see cref="Galaxy"/> instance for a player session.
/// Manages the connection lifecycle: connect, start event loop, and dispose.
/// </summary>
public sealed class GalaxyConnectionManager : IDisposable
{
    private static readonly RuntimeDisclosure RuntimeSelfDisclosure = new(
        RuntimeDisclosureLevel.Automated,
        RuntimeDisclosureLevel.Automated,
        RuntimeDisclosureLevel.Automated,
        RuntimeDisclosureLevel.Automated,
        RuntimeDisclosureLevel.AiControlled,
        RuntimeDisclosureLevel.Manual,
        RuntimeDisclosureLevel.Unsupported,
        RuntimeDisclosureLevel.Unsupported,
        RuntimeDisclosureLevel.Manual,
        RuntimeDisclosureLevel.Manual);

    private static readonly BuildDisclosure BuildSelfDisclosure = new(
        BuildDisclosureLevel.AgenticTool,
        BuildDisclosureLevel.IntegratedLlm,
        BuildDisclosureLevel.SearchOnly,
        BuildDisclosureLevel.SearchOnly,
        BuildDisclosureLevel.AgenticTool,
        BuildDisclosureLevel.AgenticTool,
        BuildDisclosureLevel.AgenticTool,
        BuildDisclosureLevel.AgenticTool,
        BuildDisclosureLevel.SearchOnly,
        BuildDisclosureLevel.None,
        BuildDisclosureLevel.None,
        BuildDisclosureLevel.None);

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
        BuildDisclosure buildDisclosure = new(
            softwareDesign:    BuildDisclosureLevel.IntegratedLlm,
            ui:                BuildDisclosureLevel.IntegratedLlm,
            universeRendering: BuildDisclosureLevel.IntegratedLlm,
            input:             BuildDisclosureLevel.IntegratedLlm,
            engineControl:     BuildDisclosureLevel.IntegratedLlm,
            navigation:        BuildDisclosureLevel.IntegratedLlm,
            scannerControl:    BuildDisclosureLevel.IntegratedLlm,
            weaponSystems:     BuildDisclosureLevel.IntegratedLlm,
            resourceControl:   BuildDisclosureLevel.IntegratedLlm,
            fleetControl:      BuildDisclosureLevel.None,
            missionControl:    BuildDisclosureLevel.None,
            chat:              BuildDisclosureLevel.IntegratedLlm
        );

        RuntimeDisclosure runtimeDisclosure = new(
            engineControl:         RuntimeDisclosureLevel.Automated,
            navigation:            RuntimeDisclosureLevel.Autonomous,
            scannerControl:        RuntimeDisclosureLevel.Automated,
            weaponAiming:          RuntimeDisclosureLevel.Automated,
            weaponTargetSelection: RuntimeDisclosureLevel.Automated,
            resourceControl:       RuntimeDisclosureLevel.Automated,
            fleetControl:          RuntimeDisclosureLevel.Unsupported,
            missionControl:        RuntimeDisclosureLevel.Unsupported,
            loadoutControl:        RuntimeDisclosureLevel.Manual,
            chat:                  RuntimeDisclosureLevel.Manual
        );
        _galaxy = await Galaxy.Connect(_galaxyUrl, _apiKey, _teamName, runtimeDisclosure, buildDisclosure);
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

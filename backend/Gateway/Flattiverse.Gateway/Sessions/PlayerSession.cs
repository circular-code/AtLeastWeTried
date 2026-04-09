using Flattiverse.Connector;
using Flattiverse.Connector.Events;
using Flattiverse.Connector.GalaxyHierarchy;
using Flattiverse.Connector.Network;
using Flattiverse.Connector.Units;
using Flattiverse.Gateway.Connector;
using Flattiverse.Gateway.Protocol.Dtos;
using Flattiverse.Gateway.Protocol.ServerMessages;
using Flattiverse.Gateway.Services;
using Microsoft.Extensions.Logging;

namespace Flattiverse.Gateway.Sessions;

public sealed class PlayerSession : IConnectorEventHandler, IDisposable
{
    private readonly string _apiKey;
    private readonly string? _teamName;
    private readonly ILogger _logger;
    private readonly string _id;
    private GalaxyConnectionManager? _connectionManager;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly HashSet<BrowserConnection> _attachedConnections = new();
    private string _displayName = "";
    private bool _connected;
    private string _galaxyUrl;
    private readonly MappingService _mappingService;
    private readonly ScanningService _scanningService = new();

    public string Id => _id;
    public string DisplayName => _displayName;
    public bool Connected => _connected;
    public string? TeamName => _teamName;
    public Galaxy? Galaxy => _connectionManager?.Galaxy;
    public MappingService MappingService => _mappingService;
    public ScanningService ScanningService => _scanningService;

    public PlayerSession(string id, string apiKey, string? teamName, string galaxyUrl, ILogger logger)
    {
        _id = id;
        _apiKey = apiKey;
        _teamName = teamName;
        _galaxyUrl = galaxyUrl;
        _logger = logger;
        _mappingService = new MappingService(BuildMappingScopeContext);
    }

    public async Task ConnectAsync()
    {
        await EnsureConnectedAsync();
    }

    public async Task EnsureConnectedAsync()
    {
        await _connectLock.WaitAsync();
        try
        {
            if (_connected && _connectionManager?.Connected == true && _connectionManager.Galaxy is not null)
                return;

            if (_connectionManager is not null)
            {
                _connectionManager.ConnectionLost -= OnConnectionLost;
                _connectionManager.Dispose();
            }

            _connectionManager = new GalaxyConnectionManager(_galaxyUrl, _apiKey, _teamName, _logger);
            _connectionManager.ConnectionLost += OnConnectionLost;

            var handlers = new List<IConnectorEventHandler> { _mappingService, _scanningService, this };
            await _connectionManager.ConnectAsync(handlers);

            _connected = true;
            _displayName = _connectionManager.Galaxy!.Player.Name;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect player session {Id}", _id);
            throw;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private void OnConnectionLost()
    {
        _connected = false;

        List<BrowserConnection> connections;
        lock (_lock)
            connections = _attachedConnections.ToList();

        var statusMsg = new ServerStatusMessage
        {
            Kind = "error",
            Code = "session_lost",
            Message = "Connection lost",
            Recoverable = false
        };
        foreach (var conn in connections)
            conn.EnqueueMessage(statusMsg);
    }

    public void AttachConnection(BrowserConnection connection)
    {
        lock (_lock)
            _attachedConnections.Add(connection);
    }

    public void DetachConnection(BrowserConnection connection)
    {
        lock (_lock)
            _attachedConnections.Remove(connection);
    }

    public int AttachedConnectionCount
    {
        get { lock (_lock) return _attachedConnections.Count; }
    }

    public GalaxySnapshotDto BuildSnapshot()
    {
        var galaxy = Galaxy;
        if (galaxy is null)
            return new GalaxySnapshotDto();

        var dto = new GalaxySnapshotDto
        {
            Name = galaxy.Name,
            Description = galaxy.Description,
            GameMode = galaxy.GameMode.ToString()
        };

        foreach (var team in galaxy.Teams)
        {
            if (team is null) continue;
            dto.Teams.Add(new TeamSnapshotDto
            {
                Id = team.Id,
                Name = team.Name,
                Score = team.Score.Mission,
                ColorHex = $"#{team.Red:X2}{team.Green:X2}{team.Blue:X2}"
            });
        }

        foreach (var cluster in galaxy.Clusters)
        {
            if (cluster is null) continue;
            dto.Clusters.Add(new ClusterSnapshotDto
            {
                Id = cluster.Id,
                Name = cluster.Name,
                IsStart = cluster.Start,
                Respawns = cluster.Respawn
            });
        }

        dto.Units = _mappingService.BuildUnitSnapshots();

        foreach (var player in galaxy.Players)
        {
            if (player is null) continue;
            foreach (var info in player.ControllableInfos)
            {
                if (info is null) continue;
                dto.Controllables.Add(new PublicControllableSnapshotDto
                {
                    ControllableId = $"p{player.Id}-c{info.Id}",
                    DisplayName = info.Name,
                    TeamName = player.Team?.Name ?? "",
                    Alive = info.Alive,
                    Score = info.Score.Mission
                });
            }
        }

        return dto;
    }

    public List<OwnerOverlayDeltaDto> BuildOverlaySnapshot()
    {
        var galaxy = Galaxy;
        if (galaxy is null)
            return new List<OwnerOverlayDeltaDto>();

        var events = new List<OwnerOverlayDeltaDto>();

        foreach (var controllable in galaxy.Controllables)
        {
            if (controllable is null) continue;
            events.Add(BuildControllableOverlay(controllable));
        }

        return events;
    }

    private OwnerOverlayDeltaDto BuildControllableOverlay(Controllable controllable)
    {
        var changes = new Dictionary<string, object?>();
        changes["displayName"] = controllable.Name;
        changes["kind"] = MappingService.MapUnitKind(controllable.Kind);
        changes["clusterId"] = controllable.Cluster?.Id ?? 0;
        changes["clusterName"] = controllable.Cluster?.Name ?? "Unknown";
        changes["radius"] = controllable.Size;
        changes["teamName"] = Galaxy?.Player?.Team?.Name ?? "";
        changes["alive"] = controllable.Alive;
        changes["active"] = controllable.Active;

        if (controllable is ClassicShipControllable classic)
        {
            changes["ammo"] = (int)classic.ShotMagazine.CurrentShots;
            changes["position"] = new Dictionary<string, object>
            {
                { "x", controllable.Position.X },
                { "y", controllable.Position.Y },
                { "angle", controllable.Angle }
            };
            changes["movement"] = new Dictionary<string, object>
            {
                { "x", controllable.Movement.X },
                { "y", controllable.Movement.Y }
            };
            changes["engine"] = new Dictionary<string, object>
            {
                { "maximum", classic.Engine.Maximum },
                { "currentX", classic.Engine.Current.X },
                { "currentY", classic.Engine.Current.Y }
            };
            changes["scanner"] = _scanningService.BuildOverlay(classic.Id);
            changes["shield"] = new Dictionary<string, object>
            {
                { "active", controllable.Shield.Active },
                { "current", controllable.Shield.Current },
                { "maximum", controllable.Shield.Maximum }
            };
            changes["hull"] = new Dictionary<string, object>
            {
                { "current", controllable.Hull.Current },
                { "maximum", controllable.Hull.Maximum }
            };
            changes["energyBattery"] = new Dictionary<string, object>
            {
                { "current", controllable.EnergyBattery.Current },
                { "maximum", controllable.EnergyBattery.Maximum }
            };
            var cId = $"p{Galaxy!.Player.Id}-c{controllable.Id}";
            var navTarget = GetNavigationTarget(cId);
            changes["navigation"] = new Dictionary<string, object>
            {
                { "active", navTarget.HasValue },
                { "targetX", navTarget?.x ?? 0f },
                { "targetY", navTarget?.y ?? 0f }
            };
        }

        return new OwnerOverlayDeltaDto
        {
            EventType = "overlay.snapshot",
            ControllableId = $"p{Galaxy!.Player.Id}-c{controllable.Id}",
            Changes = changes
        };
    }

    public async Task<CommandReplyMessage> HandleCommandAsync(string commandType, string commandId, System.Text.Json.JsonElement? payload)
    {
        if (Galaxy is null || !_connected)
            return Rejected(commandId, "not_connected", "Player session is not connected.");

        try
        {
            return commandType switch
            {
                "command.chat" => await HandleChat(commandId, payload),
                "command.create_ship" => await HandleCreateShip(commandId, payload),
                "command.set_engine" => await HandleSetEngine(commandId, payload),
                "command.set_navigation_target" => HandleSetNavigationTarget(commandId, payload),
                "command.clear_navigation_target" => HandleClearNavigationTarget(commandId, payload),
                "command.fire_weapon" => await HandleFireWeapon(commandId, payload),
                "command.set_subsystem_mode" => await HandleSetSubsystemMode(commandId, payload),
                "command.destroy_ship" => await HandleDestroyShip(commandId, payload),
                "command.continue_ship" => await HandleContinueShip(commandId, payload),
                "command.remove_ship" => HandleRemoveShip(commandId, payload),
                _ => Rejected(commandId, "unknown_command", $"Unknown command type: {commandType}")
            };
        }
        catch (GameException ex)
        {
            return Rejected(commandId, ex.GetType().Name, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling command {Type}", commandType);
            return Rejected(commandId, "internal_error", ex.Message);
        }
    }

    private async Task<CommandReplyMessage> HandleChat(string commandId, System.Text.Json.JsonElement? payload)
    {
        var message = payload?.GetProperty("message").GetString() ?? "";
        var scope = payload?.GetProperty("scope").GetString() ?? "galaxy";

        switch (scope)
        {
            case "galaxy":
                await Galaxy!.Chat(message);
                break;
            case "team":
                await Galaxy!.Player.Team.Chat(message);
                break;
            case "private":
                var recipientId = payload?.GetProperty("recipientPlayerSessionId").GetString();
                // Find player by session ID and send private chat
                // For MVP, fall back to galaxy chat
                await Galaxy!.Chat(message);
                break;
        }

        return Completed(commandId);
    }

    private async Task<CommandReplyMessage> HandleCreateShip(string commandId, System.Text.Json.JsonElement? payload)
    {
        var name = payload?.GetProperty("name").GetString() ?? "Ship";
        var shipClass = payload?.GetProperty("shipClass").GetString() ?? "classic";
        string[] crystalNames = Array.Empty<string>();

        if (payload?.TryGetProperty("crystalNames", out var crystalsEl) == true)
        {
            crystalNames = crystalsEl.EnumerateArray()
                .Select(e => e.GetString() ?? "")
                .Where(s => s.Length > 0)
                .ToArray();
        }

        Controllable controllable;
        if (shipClass == "modern")
        {
            controllable = await Galaxy!.CreateModernShip(name,
                crystalNames.ElementAtOrDefault(0) ?? "",
                crystalNames.ElementAtOrDefault(1) ?? "",
                crystalNames.ElementAtOrDefault(2) ?? "");
        }
        else
        {
            controllable = await Galaxy!.CreateClassicShip(name,
                crystalNames.ElementAtOrDefault(0) ?? "",
                crystalNames.ElementAtOrDefault(1) ?? "",
                crystalNames.ElementAtOrDefault(2) ?? "");
        }

        var controllableId = $"p{Galaxy.Player.Id}-c{controllable.Id}";

        return new CommandReplyMessage
        {
            CommandId = commandId,
            Status = "completed",
            Result = new Dictionary<string, object?>
            {
                { "controllableId", controllableId }
            }
        };
    }

    private async Task<CommandReplyMessage> HandleSetEngine(string commandId, System.Text.Json.JsonElement? payload)
    {
        var controllableId = payload?.GetProperty("controllableId").GetString() ?? "";
        var controllable = FindControllable(controllableId);
        if (controllable is not ClassicShipControllable classic)
            return Rejected(commandId, "invalid_controllable", "Controllable not found or not a classic ship.");

        var thrust = payload?.GetProperty("thrust").GetSingle() ?? 0f;

        if (payload?.TryGetProperty("x", out var xEl) == true && payload?.TryGetProperty("y", out var yEl) == true &&
            xEl.ValueKind != System.Text.Json.JsonValueKind.Null && yEl.ValueKind != System.Text.Json.JsonValueKind.Null)
        {
            var x = xEl.GetSingle();
            var y = yEl.GetSingle();
            var vec = new Vector(x * thrust, y * thrust);
            if (vec.Length > classic.Engine.Maximum)
                vec.Length = classic.Engine.Maximum;
            await classic.Engine.Set(vec);
        }
        else
        {
            if (thrust == 0f)
                await classic.Engine.Off();
            else
            {
                var angle = classic.Angle;
                var vec = Vector.FromAngleLength(angle, thrust * classic.Engine.Maximum);
                await classic.Engine.Set(vec);
            }
        }

        return Completed(commandId);
    }

    // Navigation state per controllable (gateway-only, not in connector)
    private readonly Dictionary<string, (float x, float y)> _navigationTargets = new();

    private CommandReplyMessage HandleSetNavigationTarget(string commandId, System.Text.Json.JsonElement? payload)
    {
        var controllableId = payload?.GetProperty("controllableId").GetString() ?? "";
        var targetX = payload?.GetProperty("targetX").GetSingle() ?? 0f;
        var targetY = payload?.GetProperty("targetY").GetSingle() ?? 0f;

        _navigationTargets[controllableId] = (targetX, targetY);

        return Completed(commandId);
    }

    private CommandReplyMessage HandleClearNavigationTarget(string commandId, System.Text.Json.JsonElement? payload)
    {
        var controllableId = payload?.GetProperty("controllableId").GetString() ?? "";
        _navigationTargets.Remove(controllableId);
        return Completed(commandId);
    }

    public (float x, float y)? GetNavigationTarget(string controllableId)
    {
        return _navigationTargets.TryGetValue(controllableId, out var target) ? target : null;
    }

    private async Task<CommandReplyMessage> HandleFireWeapon(string commandId, System.Text.Json.JsonElement? payload)
    {
        var controllableId = payload?.GetProperty("controllableId").GetString() ?? "";
        var weaponId = payload?.GetProperty("weaponId").GetString() ?? "shot";
        var controllable = FindControllable(controllableId);
        if (controllable is not ClassicShipControllable classic)
            return Rejected(commandId, "invalid_controllable", "Controllable not found or not a classic ship.");

        float? relativeAngle = null;
        if (payload?.TryGetProperty("relativeAngle", out var angleEl) == true && angleEl.ValueKind != System.Text.Json.JsonValueKind.Null)
            relativeAngle = angleEl.GetSingle();

        switch (weaponId)
        {
            case "shot":
            case "ShotLauncher":
                var angle = relativeAngle ?? 0f;
                var movement = Vector.FromAngleLength(classic.Angle + angle, 2f);
                await classic.ShotLauncher.Shoot(movement, 80, 12f, 8f);
                break;
            case "railgun":
            case "Railgun":
                if (relativeAngle.HasValue && relativeAngle.Value > 90f)
                    await classic.Railgun.FireBack();
                else
                    await classic.Railgun.FireFront();
                break;
        }

        return Completed(commandId);
    }

    private async Task<CommandReplyMessage> HandleSetSubsystemMode(string commandId, System.Text.Json.JsonElement? payload)
    {
        var controllableId = payload?.GetProperty("controllableId").GetString() ?? "";
        var subsystemId = payload?.GetProperty("subsystemId").GetString() ?? "";
        var mode = payload?.GetProperty("mode").GetString() ?? "";
        var controllable = FindControllable(controllableId);
        if (controllable is not ClassicShipControllable classic)
            return Rejected(commandId, "invalid_controllable", "Controllable not found or not a classic ship.");

        switch (subsystemId)
        {
            case "MainScanner":
            case "scanner":
                if (mode == "off")
                {
                    await _scanningService.ApplyModeAsync(classic, ScanningService.ScannerMode.Off);
                }
                else if (mode == "on")
                {
                    await _scanningService.ApplyModeAsync(classic, ScanningService.ScannerMode.Forward);
                }
                else if (mode == "set")
                {
                    float width = 90f;
                    if (payload?.TryGetProperty("value", out var valEl) == true && valEl.ValueKind != System.Text.Json.JsonValueKind.Null)
                        width = valEl.GetSingle();
                    var scanMode = width >= 180f
                        ? ScanningService.ScannerMode.Full
                        : ScanningService.ScannerMode.Forward;
                    await _scanningService.ApplyModeAsync(classic, scanMode);
                }
                break;
            case "ShotFabricator":
            case "shotFabricator":
                if (mode == "on") await classic.ShotFabricator.On();
                else if (mode == "off") await classic.ShotFabricator.Off();
                break;
            case "InterceptorFabricator":
            case "interceptorFabricator":
                if (mode == "on") await classic.InterceptorFabricator.On();
                else if (mode == "off") await classic.InterceptorFabricator.Off();
                break;
        }

        return Completed(commandId);
    }

    private async Task<CommandReplyMessage> HandleDestroyShip(string commandId, System.Text.Json.JsonElement? payload)
    {
        var controllableId = payload?.GetProperty("controllableId").GetString() ?? "";
        var controllable = FindControllable(controllableId);
        if (controllable is null)
            return Rejected(commandId, "invalid_controllable", "Controllable not found.");

        await controllable.Suicide();
        return Completed(commandId);
    }

    private async Task<CommandReplyMessage> HandleContinueShip(string commandId, System.Text.Json.JsonElement? payload)
    {
        var controllableId = payload?.GetProperty("controllableId").GetString() ?? "";
        var controllable = FindControllable(controllableId);
        if (controllable is null)
            return Rejected(commandId, "invalid_controllable", "Controllable not found.");

        await controllable.Continue();
        return Completed(commandId);
    }

    private CommandReplyMessage HandleRemoveShip(string commandId, System.Text.Json.JsonElement? payload)
    {
        var controllableId = payload?.GetProperty("controllableId").GetString() ?? "";
        var controllable = FindControllable(controllableId);
        if (controllable is null)
            return Rejected(commandId, "invalid_controllable", "Controllable not found.");

        controllable.RequestClose();
        return Completed(commandId);
    }

    private Controllable? FindControllable(string controllableId)
    {
        // controllableId format: "p{playerId}-c{controllableId}"
        var galaxy = Galaxy;
        if (galaxy is null) return null;

        foreach (var c in galaxy.Controllables)
        {
            if (c is null) continue;
            if ($"p{galaxy.Player.Id}-c{c.Id}" == controllableId)
                return c;
        }

        return null;
    }

    private MappingService.MappingScopeContext? BuildMappingScopeContext()
    {
        var galaxy = Galaxy;
        if (galaxy is null)
            return null;

        var clusterId = ResolveCurrentClusterId(galaxy);
        var galaxyId = $"{_galaxyUrl}|{galaxy.Name}";
        return new MappingService.MappingScopeContext(galaxyId, clusterId);
    }

    private static int ResolveCurrentClusterId(Galaxy galaxy)
    {
        foreach (var controllable in galaxy.Controllables)
        {
            if (controllable is { Active: true, Cluster: not null })
                return controllable.Cluster.Id;
        }

        foreach (var controllable in galaxy.Controllables)
        {
            if (controllable?.Cluster is not null)
                return controllable.Cluster.Id;
        }

        return 0;
    }

    /// <summary>
    /// Called by TickService every 40ms. Collects pending deltas from
    /// MappingService and overlay state, then delivers to browser connections.
    /// </summary>
    public void FlushTickDeltas()
    {
        List<BrowserConnection> connections;
        lock (_lock)
            connections = _attachedConnections.ToList();

        if (connections.Count == 0) return;

        // Collect and broadcast pending world deltas
        var worldDeltas = _mappingService.CollectPendingDeltas();
        BroadcastWorldDelta(connections, worldDeltas);

        // Broadcast owner overlay
        BroadcastOwnerOverlay(connections);
    }

    /// <summary>
    /// IConnectorEventHandler — receives events from the ConnectorEventLoop.
    /// Handles session-specific events (chat, controllable lifecycle, connection terminated).
    /// Unit events are handled by MappingService (registered as a separate handler).
    /// </summary>
    public void Handle(FlattiverseEvent @event)
    {
        List<BrowserConnection> connections;
        lock (_lock)
            connections = _attachedConnections.ToList();

        switch (@event)
        {

            case ChatEvent chatEvent:
                var scope = chatEvent switch
                {
                    TeamChatEvent => "team",
                    PlayerChatEvent => "private",
                    _ => "galaxy"
                };
                var chatMsg = new ChatReceivedMessage
                {
                    Entry = new ChatEntryDto
                    {
                        MessageId = Guid.NewGuid().ToString("N"),
                        Scope = scope,
                        SenderDisplayName = chatEvent.Player.Name,
                        PlayerSessionId = _id,
                        Message = chatEvent.Message,
                        SentAtUtc = DateTime.UtcNow.ToString("O")
                    }
                };
                foreach (var conn in connections)
                    conn.EnqueueMessage(chatMsg);
                break;

            case RegisteredControllableInfoEvent registered:
                var regDelta = new WorldDeltaDto
                {
                    EventType = "controllable.created",
                    EntityId = $"p{registered.Player.Id}-c{registered.ControllableInfo.Id}",
                    Changes = new Dictionary<string, object?>
                    {
                        { "controllableId", $"p{registered.Player.Id}-c{registered.ControllableInfo.Id}" },
                        { "displayName", registered.ControllableInfo.Name },
                        { "teamName", registered.Player.Team?.Name ?? "" },
                        { "alive", registered.ControllableInfo.Alive },
                        { "score", registered.ControllableInfo.Score.Mission }
                    }
                };
                BroadcastWorldDelta(connections, new List<WorldDeltaDto> { regDelta });
                break;

            case ContinuedControllableInfoEvent continued:
            {
                var cDelta = new WorldDeltaDto
                {
                    EventType = "controllable.created",
                    EntityId = $"p{continued.Player.Id}-c{continued.ControllableInfo.Id}",
                    Changes = new Dictionary<string, object?>
                    {
                        { "controllableId", $"p{continued.Player.Id}-c{continued.ControllableInfo.Id}" },
                        { "displayName", continued.ControllableInfo.Name },
                        { "teamName", continued.Player.Team?.Name ?? "" },
                        { "alive", continued.ControllableInfo.Alive },
                        { "score", continued.ControllableInfo.Score.Mission }
                    }
                };
                BroadcastWorldDelta(connections, new List<WorldDeltaDto> { cDelta });

                // Re-apply remembered scanner mode on respawn for own controllables
                if (continued.Player.Id == Galaxy?.Player?.Id)
                {
                    foreach (var c in Galaxy!.Controllables)
                    {
                        if (c is ClassicShipControllable respawnedShip && c.Id == continued.ControllableInfo.Id)
                        {
                            _ = _scanningService.ReapplyModeAsync(respawnedShip);
                            break;
                        }
                    }
                }
                break;
            }

            case DestroyedControllableInfoEvent:
            case ClosedControllableInfoEvent:
            case UpdatedControllableInfoScoreEvent:
                // Re-broadcast full controllable state on these events; the tick overlay will handle owner side
                if (@event is ControllableInfoEvent ciEvent)
                {
                    var evtType = @event switch
                    {
                        ClosedControllableInfoEvent => "unit.removed",
                        _ => "controllable.created"
                    };
                    var cDelta2 = new WorldDeltaDto
                    {
                        EventType = evtType,
                        EntityId = $"p{ciEvent.Player.Id}-c{ciEvent.ControllableInfo.Id}",
                        Changes = new Dictionary<string, object?>
                        {
                            { "controllableId", $"p{ciEvent.Player.Id}-c{ciEvent.ControllableInfo.Id}" },
                            { "displayName", ciEvent.ControllableInfo.Name },
                            { "teamName", ciEvent.Player.Team?.Name ?? "" },
                            { "alive", ciEvent.ControllableInfo.Alive },
                            { "score", ciEvent.ControllableInfo.Score.Mission }
                        }
                    };
                    BroadcastWorldDelta(connections, new List<WorldDeltaDto> { cDelta2 });
                }
                break;
        }
    }

    private void BroadcastWorldDelta(List<BrowserConnection> connections, List<WorldDeltaDto> deltas)
    {
        if (deltas.Count == 0) return;
        var msg = new WorldDeltaMessage { Events = deltas };
        foreach (var conn in connections)
            conn.EnqueueMessage(msg);
    }

    private void BroadcastOwnerOverlay(List<BrowserConnection> connections)
    {
        var galaxy = Galaxy;
        if (galaxy is null) return;

        var events = new List<OwnerOverlayDeltaDto>();

        foreach (var controllable in galaxy.Controllables)
        {
            if (controllable is null) continue;
            events.Add(BuildControllableOverlay(controllable));
        }

        if (events.Count == 0) return;

        var msg = new OwnerDeltaMessage
        {
            PlayerSessionId = _id,
            Events = events
        };

        foreach (var conn in connections)
        {
            if (conn.SelectedSessionId == _id)
                conn.EnqueueMessage(msg);
        }
    }

    private static CommandReplyMessage Completed(string commandId)
    {
        return new CommandReplyMessage { CommandId = commandId, Status = "completed" };
    }

    private static CommandReplyMessage Rejected(string commandId, string code, string message)
    {
        return new CommandReplyMessage
        {
            CommandId = commandId,
            Status = "rejected",
            Error = new ErrorInfoDto { Code = code, Message = message, Recoverable = true }
        };
    }

    public void Dispose()
    {
        if (_connectionManager is not null)
        {
            _connectionManager.ConnectionLost -= OnConnectionLost;
            _connectionManager.Dispose();
        }

        _connectLock.Dispose();
    }
}

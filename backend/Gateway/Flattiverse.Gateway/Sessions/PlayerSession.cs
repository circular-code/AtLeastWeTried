using Flattiverse.Connector;
using Flattiverse.Connector.Events;
using Flattiverse.Connector.GalaxyHierarchy;
using Flattiverse.Connector.Network;
using Flattiverse.Connector.Units;
using Flattiverse.Gateway.Protocol.Dtos;
using Flattiverse.Gateway.Protocol.ServerMessages;
using Microsoft.Extensions.Logging;

namespace Flattiverse.Gateway.Sessions;

public sealed class PlayerSession : IDisposable
{
    private readonly string _apiKey;
    private readonly string? _teamName;
    private readonly ILogger _logger;
    private readonly string _id;
    private Galaxy? _galaxy;
    private Task? _eventLoopTask;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();
    private readonly HashSet<BrowserConnection> _attachedConnections = new();
    private string _displayName = "";
    private bool _connected;
    private string _galaxyUrl;

    public string Id => _id;
    public string DisplayName => _displayName;
    public bool Connected => _connected;
    public string? TeamName => _teamName;
    public Galaxy? Galaxy => _galaxy;

    public PlayerSession(string id, string apiKey, string? teamName, string galaxyUrl, ILogger logger)
    {
        _id = id;
        _apiKey = apiKey;
        _teamName = teamName;
        _galaxyUrl = galaxyUrl;
        _logger = logger;
    }

    public async Task ConnectAsync()
    {
        try
        {
            _galaxy = await Connector.GalaxyHierarchy.Galaxy.Connect(_galaxyUrl, _apiKey, _teamName);
            _connected = true;
            _displayName = _galaxy.Player.Name;
            _eventLoopTask = Task.Run(EventLoop);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect player session {Id}", _id);
            throw;
        }
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
        if (_galaxy is null)
            return new GalaxySnapshotDto();

        var dto = new GalaxySnapshotDto
        {
            Name = _galaxy.Name,
            Description = _galaxy.Description,
            GameMode = _galaxy.GameMode.ToString()
        };

        foreach (var team in _galaxy.Teams)
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

        foreach (var cluster in _galaxy.Clusters)
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

        foreach (var cluster in _galaxy.Clusters)
        {
            if (cluster is null) continue;
            foreach (var unit in cluster.Units)
            {
                var unitDto = MapUnit(unit, cluster.Id);
                if (unitDto is not null)
                    dto.Units.Add(unitDto);
            }
        }

        foreach (var player in _galaxy.Players)
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
        if (_galaxy is null)
            return new List<OwnerOverlayDeltaDto>();

        var events = new List<OwnerOverlayDeltaDto>();

        foreach (var controllable in _galaxy.Controllables)
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
        changes["kind"] = MapUnitKind(controllable.Kind);
        changes["clusterId"] = controllable.Cluster?.Id ?? 0;
        changes["clusterName"] = controllable.Cluster?.Name ?? "Unknown";
        changes["radius"] = controllable.Size;
        changes["teamName"] = _galaxy?.Player?.Team?.Name ?? "";
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
            changes["scanner"] = new Dictionary<string, object>
            {
                { "active", classic.MainScanner.Active }
            };
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
            var cId = $"p{_galaxy!.Player.Id}-c{controllable.Id}";
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
            ControllableId = $"p{_galaxy!.Player.Id}-c{controllable.Id}",
            Changes = changes
        };
    }

    public async Task<CommandReplyMessage> HandleCommandAsync(string commandType, string commandId, System.Text.Json.JsonElement? payload)
    {
        if (_galaxy is null || !_connected)
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
                await _galaxy!.Chat(message);
                break;
            case "team":
                await _galaxy!.Player.Team.Chat(message);
                break;
            case "private":
                var recipientId = payload?.GetProperty("recipientPlayerSessionId").GetString();
                // Find player by session ID and send private chat
                // For MVP, fall back to galaxy chat
                await _galaxy!.Chat(message);
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
            controllable = await _galaxy!.CreateModernShip(name,
                crystalNames.ElementAtOrDefault(0) ?? "",
                crystalNames.ElementAtOrDefault(1) ?? "",
                crystalNames.ElementAtOrDefault(2) ?? "");
        }
        else
        {
            controllable = await _galaxy!.CreateClassicShip(name,
                crystalNames.ElementAtOrDefault(0) ?? "",
                crystalNames.ElementAtOrDefault(1) ?? "",
                crystalNames.ElementAtOrDefault(2) ?? "");
        }

        var controllableId = $"p{_galaxy.Player.Id}-c{controllable.Id}";

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
                if (mode == "on") await classic.MainScanner.On();
                else if (mode == "off") await classic.MainScanner.Off();
                else if (mode == "set")
                {
                    float width = 90f, length = 220f, angle = 0f;
                    if (payload?.TryGetProperty("value", out var valEl) == true && valEl.ValueKind != System.Text.Json.JsonValueKind.Null)
                        angle = valEl.GetSingle();
                    await classic.MainScanner.Set(width, length, angle);
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
        if (_galaxy is null) return null;

        foreach (var c in _galaxy.Controllables)
        {
            if (c is null) continue;
            if ($"p{_galaxy.Player.Id}-c{c.Id}" == controllableId)
                return c;
        }

        return null;
    }

    private async Task EventLoop()
    {
        var galaxy = _galaxy;
        if (galaxy is null) return;

        try
        {
            while (galaxy.Active && !_cts.Token.IsCancellationRequested)
            {
                var @event = await galaxy.NextEvent();
                DispatchEvent(@event);
            }
        }
        catch (ConnectionTerminatedGameException)
        {
            _logger.LogWarning("Galaxy connection terminated for session {Id}", _id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Event loop error for session {Id}", _id);
        }
        finally
        {
            _connected = false;
        }
    }

    private void DispatchEvent(FlattiverseEvent @event)
    {
        List<BrowserConnection> connections;
        lock (_lock)
            connections = _attachedConnections.ToList();

        switch (@event)
        {
            case NewUnitFlattiverseEvent newUnit:
                var created = MapUnit(newUnit.Unit, newUnit.Unit.Cluster?.Id ?? 0);
                if (created is null) break;
                var createDelta = new WorldDeltaDto
                {
                    EventType = "unit.created",
                    EntityId = created.UnitId,
                    Changes = UnitToChanges(created)
                };
                BroadcastWorldDelta(connections, new List<WorldDeltaDto> { createDelta });
                break;

            case UpdatedUnitFlattiverseEvent updatedUnit:
                var updated = MapUnit(updatedUnit.Unit, updatedUnit.Unit.Cluster?.Id ?? 0);
                if (updated is null) break;
                var updateDelta = new WorldDeltaDto
                {
                    EventType = "unit.updated",
                    EntityId = updated.UnitId,
                    Changes = UnitToChanges(updated)
                };
                BroadcastWorldDelta(connections, new List<WorldDeltaDto> { updateDelta });
                break;

            case RemovedUnitFlattiverseEvent removedUnit:
                var removedId = removedUnit.Unit.Name;
                var removeDelta = new WorldDeltaDto
                {
                    EventType = "unit.removed",
                    EntityId = removedId
                };
                BroadcastWorldDelta(connections, new List<WorldDeltaDto> { removeDelta });
                break;

            case GalaxyTickEvent:
                BroadcastOwnerOverlay(connections);
                break;

            case ChatPlayerEvent chatEvent:
                var scope = chatEvent.Kind switch
                {
                    EventKind.ChatTeam => "team",
                    EventKind.ChatPlayer => "private",
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

            case RegisteredControllableInfoPlayerEvent registered:
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

            case ContinuedControllableInfoPlayerEvent continued:
            case DestroyedControllableInfoPlayerEvent:
            case ClosedControllableInfoPlayerEvent:
            case ControllableInfoScoreUpdatedEvent:
                // Re-broadcast full controllable state on these events; the tick overlay will handle owner side
                if (@event is ControllableInfoPlayerEvent ciEvent)
                {
                    var evtType = @event switch
                    {
                        ContinuedControllableInfoPlayerEvent => "controllable.created",
                        ClosedControllableInfoPlayerEvent => "unit.removed",
                        _ => "controllable.created"
                    };
                    var cDelta = new WorldDeltaDto
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
                    BroadcastWorldDelta(connections, new List<WorldDeltaDto> { cDelta });
                }
                break;

            case ConnectionTerminatedEvent terminated:
                _connected = false;
                var statusMsg = new ServerStatusMessage
                {
                    Kind = "error",
                    Code = "session_lost",
                    Message = terminated.Message ?? "Connection lost",
                    Recoverable = false
                };
                foreach (var conn in connections)
                    conn.EnqueueMessage(statusMsg);
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
        if (_galaxy is null) return;

        var events = new List<OwnerOverlayDeltaDto>();

        foreach (var controllable in _galaxy.Controllables)
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

    private static Dictionary<string, object?> UnitToChanges(UnitSnapshotDto unit)
    {
        var changes = new Dictionary<string, object?>
        {
            { "unitId", unit.UnitId },
            { "clusterId", unit.ClusterId },
            { "kind", unit.Kind },
            { "x", unit.X },
            { "y", unit.Y },
            { "angle", unit.Angle },
            { "radius", unit.Radius }
        };
        if (unit.TeamName is not null) changes["teamName"] = unit.TeamName;
        if (unit.SunEnergy.HasValue) changes["sunEnergy"] = unit.SunEnergy;
        if (unit.SunIons.HasValue) changes["sunIons"] = unit.SunIons;
        if (unit.SunNeutrinos.HasValue) changes["sunNeutrinos"] = unit.SunNeutrinos;
        if (unit.SunHeat.HasValue) changes["sunHeat"] = unit.SunHeat;
        if (unit.SunDrain.HasValue) changes["sunDrain"] = unit.SunDrain;
        return changes;
    }

    internal static UnitSnapshotDto? MapUnit(Unit unit, int clusterId)
    {
        var dto = new UnitSnapshotDto
        {
            UnitId = unit.Name,
            ClusterId = clusterId,
            Kind = MapUnitKind(unit.Kind),
            X = unit.Position.X,
            Y = unit.Position.Y,
            Angle = unit.Angle,
            Radius = unit.Radius,
            TeamName = unit.Team?.Name
        };

        if (unit is Sun sun)
        {
            dto.SunEnergy = sun.Energy;
            dto.SunIons = sun.Ions;
            dto.SunNeutrinos = sun.Neutrinos;
            dto.SunHeat = sun.Heat;
            dto.SunDrain = sun.Drain;
        }

        return dto;
    }

    internal static string MapUnitKind(UnitKind kind)
    {
        return kind switch
        {
            UnitKind.Sun => "sun",
            UnitKind.BlackHole => "black-hole",
            UnitKind.Planet => "planet",
            UnitKind.Moon => "moon",
            UnitKind.Meteoroid => "meteoroid",
            UnitKind.Buoy => "buoy",
            UnitKind.WormHole => "wormhole",
            UnitKind.MissionTarget => "mission-target",
            UnitKind.Flag => "flag",
            UnitKind.DominationPoint => "domination-point",
            UnitKind.CurrentField => "current-field",
            UnitKind.Nebula => "nebula",
            UnitKind.Storm => "storm",
            UnitKind.StormCommencingWhirl => "storm-whirl",
            UnitKind.StormActiveWhirl => "storm-whirl-active",
            UnitKind.ClassicShipPlayerUnit => "classic-ship",
            UnitKind.ModernShipPlayerUnit => "modern-ship",
            UnitKind.EnergyChargePowerUp => "powerup-energy",
            UnitKind.IonChargePowerUp => "powerup-ion",
            UnitKind.NeutrinoChargePowerUp => "powerup-neutrino",
            UnitKind.MetalCargoPowerUp => "powerup-metal",
            UnitKind.CarbonCargoPowerUp => "powerup-carbon",
            UnitKind.HydrogenCargoPowerUp => "powerup-hydrogen",
            UnitKind.SiliconCargoPowerUp => "powerup-silicon",
            UnitKind.ShieldChargePowerUp => "powerup-shield",
            _ => kind.ToString().ToLowerInvariant()
        };
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
        _cts.Cancel();
        _galaxy?.Dispose();
        _cts.Dispose();
    }
}

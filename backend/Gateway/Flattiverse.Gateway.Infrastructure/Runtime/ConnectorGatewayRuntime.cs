using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Flattiverse.Connector.GalaxyHierarchy;
using Flattiverse.Connector.Units;
using Flattiverse.Gateway.Application.Abstractions;
using Flattiverse.Gateway.Contracts.Messages.Client;
using Flattiverse.Gateway.Contracts.Messages.Server;
using Flattiverse.Gateway.Domain.Sessions;
using Flattiverse.Gateway.Infrastructure.Connector;

namespace Flattiverse.Gateway.Infrastructure.Runtime;

internal sealed class ConnectorGatewayRuntime : IGatewayRuntime
{
    private const double NavigationArrivalDistance = 24;
    private static readonly TimeSpan NavigationDispatchInterval = TimeSpan.FromMilliseconds(200);
    private readonly IConnectorPlayerSessionStore sessionStore;
    private readonly TimeProvider timeProvider;
    private readonly ConcurrentDictionary<string, PlayerRuntimeState> runtimeStates = new(StringComparer.Ordinal);

    public ConnectorGatewayRuntime(IConnectorPlayerSessionStore sessionStore, TimeProvider timeProvider)
    {
        this.sessionStore = sessionStore;
        this.timeProvider = timeProvider;
    }

    public ValueTask EnsurePlayerSessionAsync(AttachedPlayerSession playerSession, CancellationToken cancellationToken)
    {
        runtimeStates.GetOrAdd(playerSession.PlayerSessionId, static _ => new PlayerRuntimeState());
        return ValueTask.CompletedTask;
    }

    public ValueTask<WorldSnapshot> GetSnapshotAsync(string playerSessionId, CancellationToken cancellationToken)
    {
        var lease = GetRequiredLease(playerSessionId);
        return ValueTask.FromResult(ReadRetry(() => BuildSnapshot(lease)));
    }

    public ValueTask<OwnerDeltaMessage> GetOwnerOverlayAsync(string playerSessionId, CancellationToken cancellationToken)
    {
        var lease = GetRequiredLease(playerSessionId);
        return ValueTask.FromResult(ReadRetry(() => BuildOwnerOverlay(lease)));
    }

    public async ValueTask<IReadOnlyList<GatewayMessage>> ExecuteCommandAsync(
        string playerSessionId,
        CommandMessage command,
        CancellationToken cancellationToken)
    {
        if (!sessionStore.TryGetLease(playerSessionId, out var lease))
        {
            return
            [
                BuildRejectedReply(command.CommandId, "player_session_unavailable", "The selected player session is not available in the gateway runtime.")
            ];
        }

        var commandId = string.IsNullOrWhiteSpace(command.CommandId)
            ? Guid.NewGuid().ToString("N")
            : command.CommandId;

        await lease.CommandGate.WaitAsync(cancellationToken);

        try
        {
            return command.Type switch
            {
                "command.chat" => await HandleChatAsync(lease, commandId, command.Payload),
                "command.create_ship" => await HandleCreateShipAsync(lease, commandId, command.Payload),
                "command.destroy_ship" => await HandleDestroyShipAsync(lease, commandId, command.Payload),
                "command.continue_ship" => await HandleContinueShipAsync(lease, commandId, command.Payload),
                "command.remove_ship" => await HandleRemoveShipAsync(lease, commandId, command.Payload),
                "command.set_engine" => await HandleSetEngineAsync(lease, commandId, command.Payload),
                "command.set_navigation_target" => await HandleSetNavigationTargetAsync(lease, commandId, command.Payload),
                "command.clear_navigation_target" => await HandleClearNavigationTargetAsync(lease, commandId, command.Payload),
                "command.fire_weapon" => await HandleFireWeaponAsync(lease, commandId, command.Payload),
                "command.set_subsystem_mode" => await HandleSetSubsystemModeAsync(lease, commandId, command.Payload),
                _ =>
                [
                    BuildRejectedReply(commandId, "command_not_implemented", $"{command.Type} is not implemented by the live gateway runtime.")
                ]
            };
        }
        catch (Exception exception)
        {
            return
            [
                BuildRejectedReply(commandId, "connector_command_failed", exception.Message)
            ];
        }
        finally
        {
            lease.CommandGate.Release();
        }
    }

    public async ValueTask TickAsync(CancellationToken cancellationToken)
    {
        foreach (var lease in sessionStore.SnapshotLeases())
        {
            if (!lease.Galaxy.Active)
            {
                continue;
            }

            if (!runtimeStates.TryGetValue(lease.PlayerSessionId, out var playerState))
            {
                continue;
            }

            if (!await lease.CommandGate.WaitAsync(0, cancellationToken))
            {
                continue;
            }

            try
            {
                foreach (var item in playerState.SnapshotNavigationTargets())
                {
                    if (!lease.Galaxy.Controllables.TryGet(item.ControllableId, out var controllable) || controllable is null)
                    {
                        playerState.ClearNavigation(item.ControllableId);
                        continue;
                    }

                    if (!controllable.Alive)
                    {
                        continue;
                    }

                    if (timeProvider.GetUtcNow() - item.LastDispatchedUtc < NavigationDispatchInterval)
                    {
                        continue;
                    }

                    var deltaX = item.TargetX - controllable.Position.X;
                    var deltaY = item.TargetY - controllable.Position.Y;
                    var distance = Math.Sqrt(deltaX * deltaX + deltaY * deltaY);

                    if (distance <= NavigationArrivalDistance)
                    {
                        await DispatchEngineAsync(lease, controllable, 0, 0, 0).ConfigureAwait(false);
                        playerState.ClearNavigation(item.ControllableId);
                        continue;
                    }

                    await DispatchEngineAsync(lease, controllable, deltaX, deltaY, 0.55).ConfigureAwait(false);
                    playerState.MarkNavigationDispatched(item.ControllableId, timeProvider.GetUtcNow());
                }
            }
            catch
            {
            }
            finally
            {
                lease.CommandGate.Release();
            }
        }
    }

    private async Task<IReadOnlyList<GatewayMessage>> HandleChatAsync(ConnectorPlayerSessionLease lease, string commandId, JsonObject? payload)
    {
        var scope = NormalizeScope(ReadString(payload, "scope"));
        var chatMessage = ReadString(payload, "message");

        if (string.IsNullOrWhiteSpace(chatMessage))
        {
            return
            [
                BuildRejectedReply(commandId, "chat_message_empty", "Chat messages must include non-empty text.")
            ];
        }

        switch (scope)
        {
            case "galaxy":
                await lease.Galaxy.Chat(chatMessage).ConfigureAwait(false);
                break;
            case "team":
                await lease.Galaxy.Player.Team.Chat(chatMessage).ConfigureAwait(false);
                break;
            default:
                return
                [
                    BuildRejectedReply(commandId, "private_chat_not_supported", "Private chat cannot be routed through the current gateway session pool.")
                ];
        }

        return
        [
            new ChatReceivedMessage(
                new ChatEntry(
                    $"chat-{Guid.NewGuid():N}"[..18],
                    scope,
                    lease.Galaxy.Player.Name,
                    lease.PlayerSessionId,
                    chatMessage,
                    timeProvider.GetUtcNow())),
            BuildCompletedReply(
                commandId,
                new JsonObject
                {
                    ["scope"] = scope,
                    ["playerSessionId"] = lease.PlayerSessionId
                })
        ];
    }

    private async Task<IReadOnlyList<GatewayMessage>> HandleCreateShipAsync(ConnectorPlayerSessionLease lease, string commandId, JsonObject? payload)
    {
        var shipName = ReadString(payload, "name") ?? $"{lease.Galaxy.Player.Name} Wing";
        var shipClass = NormalizeShipClass(ReadString(payload, "shipClass"));
        var crystals = ReadStringArray(payload, "crystalNames").Take(3).ToArray();
        Array.Resize(ref crystals, 3);

        Controllable controllable = shipClass == "classic"
            ? await lease.Galaxy.CreateClassicShip(shipName, crystals[0] ?? string.Empty, crystals[1] ?? string.Empty, crystals[2] ?? string.Empty).ConfigureAwait(false)
            : await lease.Galaxy.CreateModernShip(shipName, crystals[0] ?? string.Empty, crystals[1] ?? string.Empty, crystals[2] ?? string.Empty).ConfigureAwait(false);

        runtimeStates.GetOrAdd(lease.PlayerSessionId, static _ => new PlayerRuntimeState()).EnsureControllable(controllable.Id);

        if (!controllable.Alive)
        {
            await controllable.Continue().ConfigureAwait(false);
        }

        return BuildRefreshSequence(
            lease,
            BuildCompletedReply(
                commandId,
                new JsonObject
                {
                    ["action"] = "created",
                    ["controllableId"] = BuildControllableId(lease.PlayerSessionId, controllable.Id)
                }));
    }

    private async Task<IReadOnlyList<GatewayMessage>> HandleDestroyShipAsync(ConnectorPlayerSessionLease lease, string commandId, JsonObject? payload)
    {
        var resolved = ResolveControllable(lease, commandId, payload);
        if (resolved.Rejection is not null)
        {
            return [resolved.Rejection];
        }

        await resolved.Controllable!.Suicide().ConfigureAwait(false);
        runtimeStates.GetOrAdd(lease.PlayerSessionId, static _ => new PlayerRuntimeState()).ClearNavigation(resolved.Controllable.Id);

        return BuildRefreshSequence(
            lease,
            BuildCompletedReply(
                commandId,
                new JsonObject
                {
                    ["action"] = "destroyed",
                    ["controllableId"] = BuildControllableId(lease.PlayerSessionId, resolved.Controllable.Id)
                }));
    }

    private async Task<IReadOnlyList<GatewayMessage>> HandleContinueShipAsync(ConnectorPlayerSessionLease lease, string commandId, JsonObject? payload)
    {
        var resolved = ResolveControllable(lease, commandId, payload);
        if (resolved.Rejection is not null)
        {
            return [resolved.Rejection];
        }

        await resolved.Controllable!.Continue().ConfigureAwait(false);

        return BuildRefreshSequence(
            lease,
            BuildCompletedReply(
                commandId,
                new JsonObject
                {
                    ["action"] = "continued",
                    ["controllableId"] = BuildControllableId(lease.PlayerSessionId, resolved.Controllable.Id)
                }));
    }

    private Task<IReadOnlyList<GatewayMessage>> HandleRemoveShipAsync(ConnectorPlayerSessionLease lease, string commandId, JsonObject? payload)
    {
        var resolved = ResolveControllable(lease, commandId, payload);
        if (resolved.Rejection is not null)
        {
            return Task.FromResult<IReadOnlyList<GatewayMessage>>([resolved.Rejection]);
        }

        runtimeStates.GetOrAdd(lease.PlayerSessionId, static _ => new PlayerRuntimeState()).ClearNavigation(resolved.Controllable!.Id);
        resolved.Controllable.RequestClose();

        return Task.FromResult(BuildRefreshSequence(
            lease,
            BuildCompletedReply(
                commandId,
                new JsonObject
                {
                    ["action"] = "remove_requested",
                    ["controllableId"] = BuildControllableId(lease.PlayerSessionId, resolved.Controllable.Id)
                })));
    }

    private async Task<IReadOnlyList<GatewayMessage>> HandleSetEngineAsync(ConnectorPlayerSessionLease lease, string commandId, JsonObject? payload)
    {
        var resolved = ResolveControllable(lease, commandId, payload);
        if (resolved.Rejection is not null)
        {
            return [resolved.Rejection];
        }

        if (!resolved.Controllable!.Alive)
        {
            return [BuildRejectedReply(commandId, "controllable_destroyed", "The selected ship is destroyed and cannot change thrust.")];
        }

        var thrust = Clamp(ReadDouble(payload, "thrust") ?? 0, 0, 1);
        var directionX = ReadDouble(payload, "x");
        var directionY = ReadDouble(payload, "y");

        if (directionX is null || directionY is null)
        {
            var forward = Vector.FromAngleLength(resolved.Controllable.Angle, 1f);
            directionX = forward.X;
            directionY = forward.Y;
        }

        var state = runtimeStates.GetOrAdd(lease.PlayerSessionId, static _ => new PlayerRuntimeState());
        state.ClearNavigation(resolved.Controllable.Id);
        await DispatchEngineAsync(lease, resolved.Controllable, directionX.Value, directionY.Value, thrust).ConfigureAwait(false);

        return BuildRefreshSequence(
            lease,
            BuildCompletedReply(
                commandId,
                new JsonObject
                {
                    ["controllableId"] = BuildControllableId(lease.PlayerSessionId, resolved.Controllable.Id),
                    ["thrust"] = thrust
                }));
    }

    private async Task<IReadOnlyList<GatewayMessage>> HandleSetNavigationTargetAsync(ConnectorPlayerSessionLease lease, string commandId, JsonObject? payload)
    {
        var resolved = ResolveControllable(lease, commandId, payload);
        if (resolved.Rejection is not null)
        {
            return [resolved.Rejection];
        }

        if (!resolved.Controllable!.Alive)
        {
            return [BuildRejectedReply(commandId, "controllable_destroyed", "The selected ship is destroyed and cannot navigate.")];
        }

        var targetX = ReadDouble(payload, "targetX");
        var targetY = ReadDouble(payload, "targetY");
        if (targetX is null || targetY is null)
        {
            return [BuildRejectedReply(commandId, "navigation_target_invalid", "Navigation targets require numeric targetX and targetY values.")];
        }

        var state = runtimeStates.GetOrAdd(lease.PlayerSessionId, static _ => new PlayerRuntimeState());
        state.SetNavigation(resolved.Controllable.Id, targetX.Value, targetY.Value, DateTimeOffset.MinValue);
        await DispatchEngineAsync(lease, resolved.Controllable, targetX.Value - resolved.Controllable.Position.X, targetY.Value - resolved.Controllable.Position.Y, 0.55).ConfigureAwait(false);
        state.MarkNavigationDispatched(resolved.Controllable.Id, timeProvider.GetUtcNow());

        return BuildRefreshSequence(
            lease,
            BuildCompletedReply(
                commandId,
                new JsonObject
                {
                    ["controllableId"] = BuildControllableId(lease.PlayerSessionId, resolved.Controllable.Id),
                    ["action"] = "navigation_updated"
                }));
    }

    private async Task<IReadOnlyList<GatewayMessage>> HandleClearNavigationTargetAsync(ConnectorPlayerSessionLease lease, string commandId, JsonObject? payload)
    {
        var resolved = ResolveControllable(lease, commandId, payload);
        if (resolved.Rejection is not null)
        {
            return [resolved.Rejection];
        }

        runtimeStates.GetOrAdd(lease.PlayerSessionId, static _ => new PlayerRuntimeState()).ClearNavigation(resolved.Controllable!.Id);
        await DispatchEngineAsync(lease, resolved.Controllable, 0, 0, 0).ConfigureAwait(false);

        return BuildRefreshSequence(
            lease,
            BuildCompletedReply(
                commandId,
                new JsonObject
                {
                    ["controllableId"] = BuildControllableId(lease.PlayerSessionId, resolved.Controllable.Id),
                    ["action"] = "navigation_cleared"
                }));
    }

    private async Task<IReadOnlyList<GatewayMessage>> HandleFireWeaponAsync(ConnectorPlayerSessionLease lease, string commandId, JsonObject? payload)
    {
        var resolved = ResolveControllable(lease, commandId, payload);
        if (resolved.Rejection is not null)
        {
            return [resolved.Rejection];
        }

        if (!resolved.Controllable!.Alive)
        {
            return [BuildRejectedReply(commandId, "controllable_destroyed", "The selected ship is destroyed and cannot fire.")];
        }

        switch (resolved.Controllable)
        {
            case ClassicShipControllable classicShip:
                if (classicShip.ShotMagazine.CurrentShots <= 0)
                {
                    return [BuildRejectedReply(commandId, "ammo_depleted", "The selected ship has no ammunition remaining.")];
                }

                await classicShip.ShotLauncher.Shoot(
                    Vector.FromAngleLength(0, Clamp(classicShip.ShotLauncher.MaximumRelativeMovement, classicShip.ShotLauncher.MinimumRelativeMovement, classicShip.ShotLauncher.MaximumRelativeMovement)),
                    classicShip.ShotLauncher.MinimumTicks,
                    classicShip.ShotLauncher.MinimumLoad,
                    classicShip.ShotLauncher.MinimumDamage).ConfigureAwait(false);
                break;
            case ModernShipControllable modernShip:
                var launcher = modernShip.ShotLauncherN;
                var magazine = modernShip.ShotMagazineN;
                if (!launcher.Exists || magazine.CurrentShots <= 0)
                {
                    return [BuildRejectedReply(commandId, "ammo_depleted", "The selected ship has no ammunition remaining on its primary launcher.")];
                }

                await launcher.Shoot(
                    Clamp(launcher.MaximumRelativeMovement, launcher.MinimumRelativeMovement, launcher.MaximumRelativeMovement),
                    launcher.MinimumTicks,
                    launcher.MinimumLoad,
                    launcher.MinimumDamage).ConfigureAwait(false);
                break;
            default:
                return [BuildRejectedReply(commandId, "weapon_not_supported", "The selected controllable does not expose a supported primary weapon.")];
        }

        return BuildRefreshSequence(
            lease,
            BuildCompletedReply(
                commandId,
                new JsonObject
                {
                    ["controllableId"] = BuildControllableId(lease.PlayerSessionId, resolved.Controllable.Id),
                    ["action"] = "weapon_fired"
                }));
    }

    private async Task<IReadOnlyList<GatewayMessage>> HandleSetSubsystemModeAsync(ConnectorPlayerSessionLease lease, string commandId, JsonObject? payload)
    {
        var resolved = ResolveControllable(lease, commandId, payload);
        if (resolved.Rejection is not null)
        {
            return [resolved.Rejection];
        }

        var subsystemId = ReadString(payload, "subsystemId");
        if (!string.Equals(subsystemId, "primary_scanner", StringComparison.OrdinalIgnoreCase))
        {
            return [BuildRejectedReply(commandId, "subsystem_not_supported", "Only primary_scanner mode changes are currently supported.")];
        }

        var mode = ReadString(payload, "mode") ?? "pinpoint";
        var controllable = resolved.Controllable!;
        await ApplyScannerModeAsync(controllable, mode).ConfigureAwait(false);
        runtimeStates.GetOrAdd(lease.PlayerSessionId, static _ => new PlayerRuntimeState()).SetScannerMode(controllable.Id, mode);

        return BuildRefreshSequence(
            lease,
            BuildCompletedReply(
                commandId,
                new JsonObject
                {
                    ["controllableId"] = BuildControllableId(lease.PlayerSessionId, controllable.Id),
                    ["mode"] = mode
                }));
    }

    private async Task DispatchEngineAsync(ConnectorPlayerSessionLease lease, Controllable controllable, double directionX, double directionY, double thrust)
    {
        var state = runtimeStates.GetOrAdd(lease.PlayerSessionId, static _ => new PlayerRuntimeState());
        var controllableState = state.EnsureControllable(controllable.Id);

        if (thrust <= 0.0001)
        {
            switch (controllable)
            {
                case ClassicShipControllable classicShip:
                    await classicShip.Engine.Off().ConfigureAwait(false);
                    break;
                case ModernShipControllable modernShip:
                    if (controllableState.LastModernEngineIndex is { } lastEngineIndex)
                    {
                        await GetModernEngine(modernShip, lastEngineIndex).Off().ConfigureAwait(false);
                    }
                    break;
            }

            controllableState.LastModernEngineIndex = null;
            controllableState.EngineCurrentX = 0;
            controllableState.EngineCurrentY = 0;
            controllableState.EngineMaximum = 0;
            return;
        }

        if (Math.Abs(directionX) < double.Epsilon && Math.Abs(directionY) < double.Epsilon)
        {
            var fallback = Vector.FromAngleLength(controllable.Angle, 1f);
            directionX = fallback.X;
            directionY = fallback.Y;
        }

        switch (controllable)
        {
            case ClassicShipControllable classicShip:
            {
                var vector = new Vector((float)directionX, (float)directionY);
                vector.Length = classicShip.Engine.Maximum * (float)thrust;
                await classicShip.Engine.Set(vector).ConfigureAwait(false);
                controllableState.LastModernEngineIndex = null;
                controllableState.EngineCurrentX = vector.X;
                controllableState.EngineCurrentY = vector.Y;
                controllableState.EngineMaximum = classicShip.Engine.Maximum;
                break;
            }
            case ModernShipControllable modernShip:
            {
                var targetAngle = NormalizeAngle((float)(Math.Atan2(directionY, directionX) * 180d / Math.PI));
                var engineIndex = SelectModernEngineIndex(modernShip.Angle, targetAngle);
                var engine = GetModernEngine(modernShip, engineIndex);

                if (controllableState.LastModernEngineIndex is { } previousEngineIndex && previousEngineIndex != engineIndex)
                {
                    await GetModernEngine(modernShip, previousEngineIndex).Off().ConfigureAwait(false);
                }

                await engine.SetThrust(engine.MaximumThrust * (float)thrust).ConfigureAwait(false);
                controllableState.LastModernEngineIndex = engineIndex;
                var vector = Vector.FromAngleLength(GetModernEngineAngle(modernShip.Angle, engineIndex), engine.MaximumThrust * (float)thrust);
                controllableState.EngineCurrentX = vector.X;
                controllableState.EngineCurrentY = vector.Y;
                controllableState.EngineMaximum = engine.MaximumThrust;
                break;
            }
        }
    }

    private async Task ApplyScannerModeAsync(Controllable controllable, string mode)
    {
        if (string.Equals(mode, "off", StringComparison.OrdinalIgnoreCase))
        {
            switch (controllable)
            {
                case ClassicShipControllable classicShip:
                    await classicShip.MainScanner.Off().ConfigureAwait(false);
                    break;
                case ModernShipControllable modernShip:
                    await modernShip.ScannerN.Off().ConfigureAwait(false);
                    break;
            }

            return;
        }

        switch (controllable)
        {
            case ClassicShipControllable classicShip:
            {
                var (width, length) = ResolveScannerProfile(mode, classicShip.MainScanner.MinimumWidth, classicShip.MainScanner.MaximumWidth, classicShip.MainScanner.MinimumLength, classicShip.MainScanner.MaximumLength);
                await classicShip.MainScanner.Set(width, length, controllable.Angle).ConfigureAwait(false);
                await classicShip.MainScanner.On().ConfigureAwait(false);
                break;
            }
            case ModernShipControllable modernShip:
            {
                var scanner = modernShip.ScannerN;
                var (width, length) = ResolveScannerProfile(mode, scanner.MinimumWidth, scanner.MaximumWidth, scanner.MinimumLength, scanner.MaximumLength);
                await scanner.Set(width, length, 0).ConfigureAwait(false);
                await scanner.On().ConfigureAwait(false);
                break;
            }
        }
    }

    private IReadOnlyList<GatewayMessage> BuildRefreshSequence(ConnectorPlayerSessionLease lease, CommandReplyMessage reply)
    {
        try
        {
            return
            [
                new SnapshotFullMessage(BuildSnapshot(lease)),
                BuildOwnerOverlay(lease),
                reply
            ];
        }
        catch
        {
            return [reply];
        }
    }

    private WorldSnapshot BuildSnapshot(ConnectorPlayerSessionLease lease)
    {
        var galaxy = lease.Galaxy;

        var teams = galaxy.Teams
            .Where(static team => team.Active)
            .OrderBy(static team => team.Id)
            .Select(static team => new TeamSnapshot(team.Id, team.Name, team.Score.Mission, ToColorHex(team.Red, team.Green, team.Blue)))
            .ToArray();

        var clusters = galaxy.Clusters
            .Where(static cluster => cluster.Active)
            .OrderBy(static cluster => cluster.Id)
            .Select(static cluster => new ClusterSnapshot(cluster.Id, cluster.Name, cluster.Start, cluster.Respawn))
            .ToArray();

        var units = new List<UnitSnapshot>();
        foreach (var cluster in galaxy.Clusters.Where(static entry => entry.Active).OrderBy(static entry => entry.Id))
        {
            foreach (var unit in cluster.Units.ToArray())
            {
                units.Add(BuildUnitSnapshot(lease, unit));
            }
        }

        var controllables = galaxy.Player.ControllableInfos
            .Where(static info => info.Active)
            .OrderBy(static info => info.Id)
            .Select(info => new PublicControllableSnapshot(
                BuildControllableId(lease.PlayerSessionId, info.Id),
                info.Name,
                galaxy.Player.Team.Name,
                info.Alive,
                info.Score.Mission))
            .ToArray();

        return new WorldSnapshot(
            galaxy.Name,
            galaxy.Description,
            NormalizeGameMode(galaxy.GameMode),
            teams,
            clusters,
            units,
            controllables);
    }

    private OwnerDeltaMessage BuildOwnerOverlay(ConnectorPlayerSessionLease lease)
    {
        var state = runtimeStates.GetOrAdd(lease.PlayerSessionId, static _ => new PlayerRuntimeState());
        var events = new List<OverlayEvent>
        {
            new("overlay.snapshot", null, null)
        };

        foreach (var info in lease.Galaxy.Player.ControllableInfos.Where(static entry => entry.Active).OrderBy(static entry => entry.Id))
        {
            lease.Galaxy.Controllables.TryGet(info.Id, out var controllable);
            var controllableState = state.EnsureControllable(info.Id);
            events.Add(new OverlayEvent(
                "overlay.updated",
                BuildControllableId(lease.PlayerSessionId, info.Id),
                BuildOverlayObject(lease, info, controllable, controllableState)));
        }

        return new OwnerDeltaMessage(lease.PlayerSessionId, events);
    }

    private JsonObject BuildOverlayObject(
        ConnectorPlayerSessionLease lease,
        ControllableInfo info,
        Controllable? controllable,
        ControllableRuntimeState state)
    {
        var clusterId = controllable?.Cluster.Id ?? 0;
        var clusterName = controllable?.Cluster.Name ?? "Offline";
        var position = controllable?.Position ?? new Vector();
        var movement = controllable?.Movement ?? new Vector();
        var hullMaximum = controllable?.Hull.Maximum ?? 0;
        var hullCurrent = controllable?.Hull.Current ?? 0;
        var shieldMaximum = controllable?.Shield.Maximum ?? 0;
        var shieldCurrent = controllable?.Shield.Current ?? 0;
        var shieldActive = controllable?.Shield.Active ?? false;
        var batteryMaximum = controllable?.EnergyBattery.Maximum ?? 0;
        var batteryCurrent = controllable?.EnergyBattery.Current ?? 0;
        var kind = NormalizeUnitKind(controllable?.Kind ?? info.Kind);
        var scannerActive = GetScannerActive(controllable);
        var scannerMode = state.ScannerMode ?? InferScannerMode(controllable);

        var overlay = new JsonObject
        {
            ["displayName"] = info.Name,
            ["teamName"] = lease.Galaxy.Player.Team.Name,
            ["kind"] = kind,
            ["clusterId"] = clusterId,
            ["clusterName"] = clusterName,
            ["active"] = info.Active,
            ["alive"] = info.Alive,
            ["ammo"] = GetAmmo(controllable),
            ["radius"] = controllable?.Size ?? 0,
            ["position"] = new JsonObject
            {
                ["x"] = position.X,
                ["y"] = position.Y,
                ["angle"] = controllable?.Angle ?? 0
            },
            ["movement"] = new JsonObject
            {
                ["x"] = movement.X,
                ["y"] = movement.Y
            },
            ["engine"] = new JsonObject
            {
                ["currentX"] = state.EngineCurrentX,
                ["currentY"] = state.EngineCurrentY,
                ["maximum"] = state.EngineMaximum
            },
            ["scanner"] = new JsonObject
            {
                ["active"] = scannerActive,
                ["mode"] = scannerMode
            },
            ["shield"] = new JsonObject
            {
                ["current"] = shieldCurrent,
                ["maximum"] = shieldMaximum,
                ["active"] = shieldActive
            },
            ["hull"] = new JsonObject
            {
                ["current"] = hullCurrent,
                ["maximum"] = hullMaximum
            },
            ["energyBattery"] = new JsonObject
            {
                ["current"] = batteryCurrent,
                ["maximum"] = batteryMaximum
            }
        };

        if (state.Navigation is not null)
        {
            overlay["navigation"] = new JsonObject
            {
                ["targetX"] = state.Navigation.TargetX,
                ["targetY"] = state.Navigation.TargetY
            };
        }

        return overlay;
    }

    private UnitSnapshot BuildUnitSnapshot(ConnectorPlayerSessionLease lease, Unit unit)
    {
        var unitId = unit is PlayerUnit playerUnit && playerUnit.Player.Id == lease.Galaxy.Player.Id
            ? BuildControllableId(lease.PlayerSessionId, playerUnit.ControllableInfo.Id)
            : $"{unit.Cluster.Id}/{unit.Name}";

        var sun = unit as Sun;
        return new UnitSnapshot(
            unitId,
            unit.Cluster.Id,
            NormalizeUnitKind(unit.Kind),
            unit.Position.X,
            unit.Position.Y,
            unit.Angle,
            unit.Radius,
            unit.Team?.Name,
            sun?.Energy,
            sun?.Ions,
            sun?.Neutrinos,
            sun?.Heat,
            sun?.Drain);
    }

    private ResolvedControllable ResolveControllable(ConnectorPlayerSessionLease lease, string commandId, JsonObject? payload)
    {
        var controllableId = ReadString(payload, "controllableId");
        if (string.IsNullOrWhiteSpace(controllableId))
        {
            return new ResolvedControllable(null, BuildRejectedReply(commandId, "missing_controllable", "The command payload must include a controllableId."));
        }

        if (!TryParseControllableId(lease.PlayerSessionId, controllableId, out var protocolId))
        {
            return new ResolvedControllable(null, BuildRejectedReply(commandId, "controllable_not_owned", "The selected controllable is not attached to the active player session."));
        }

        if (!lease.Galaxy.Controllables.TryGet(protocolId, out var controllable) || controllable is null)
        {
            return new ResolvedControllable(null, BuildRejectedReply(commandId, "controllable_not_owned", "The selected controllable is not attached to the active player session."));
        }

        return new ResolvedControllable(controllable, null);
    }

    private ConnectorPlayerSessionLease GetRequiredLease(string playerSessionId)
    {
        if (!sessionStore.TryGetLease(playerSessionId, out var lease))
        {
            throw new InvalidOperationException("The selected player session is not available in the gateway runtime.");
        }

        return lease;
    }

    private static T ReadRetry<T>(Func<T> reader)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return reader();
            }
            catch (InvalidOperationException) when (attempt < 2)
            {
                Thread.Yield();
            }
        }

        return reader();
    }

    private static string BuildControllableId(string playerSessionId, int protocolId)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{playerSessionId}/c/{protocolId:D3}");
    }

    private static bool TryParseControllableId(string playerSessionId, string controllableId, out byte protocolId)
    {
        var prefix = $"{playerSessionId}/c/";
        if (!controllableId.StartsWith(prefix, StringComparison.Ordinal)
            || !byte.TryParse(controllableId[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out protocolId))
        {
            protocolId = 0;
            return false;
        }

        return true;
    }

    private static CommandReplyMessage BuildCompletedReply(string commandId, JsonObject? result)
    {
        return new CommandReplyMessage(commandId, "completed", result, null);
    }

    private static CommandReplyMessage BuildRejectedReply(string? commandId, string code, string message)
    {
        return new CommandReplyMessage(
            string.IsNullOrWhiteSpace(commandId) ? Guid.NewGuid().ToString("N") : commandId,
            "rejected",
            null,
            new CommandError(code, message, true));
    }

    private static string NormalizeShipClass(string? shipClass)
    {
        return string.Equals(shipClass, "classic", StringComparison.OrdinalIgnoreCase) ? "classic" : "modern";
    }

    private static string NormalizeScope(string? scope)
    {
        return scope?.Trim().ToLowerInvariant() switch
        {
            "team" => "team",
            "private" => "private",
            _ => "galaxy"
        };
    }

    private static string NormalizeGameMode(GameMode gameMode)
    {
        return gameMode switch
        {
            GameMode.ShootTheFlag => "shoot-the-flag",
            GameMode.Domination => "domination",
            _ => "mission"
        };
    }

    private static string NormalizeUnitKind(UnitKind kind)
    {
        return kind switch
        {
            UnitKind.ClassicShipPlayerUnit => "classic-ship",
            UnitKind.ModernShipPlayerUnit => "modern-ship",
            UnitKind.BlackHole => "black-hole",
            UnitKind.CurrentField => "current-field",
            UnitKind.WormHole => "wormhole",
            UnitKind.MissionTarget => "mission-target",
            UnitKind.DominationPoint => "domination-point",
            UnitKind.EnergyChargePowerUp => "energy-charge",
            UnitKind.IonChargePowerUp => "ion-charge",
            UnitKind.NeutrinoChargePowerUp => "neutrino-charge",
            UnitKind.MetalCargoPowerUp => "metal-cargo",
            UnitKind.CarbonCargoPowerUp => "carbon-cargo",
            UnitKind.HydrogenCargoPowerUp => "hydrogen-cargo",
            UnitKind.SiliconCargoPowerUp => "silicon-cargo",
            UnitKind.ShieldChargePowerUp => "shield-charge",
            UnitKind.HullRepairPowerUp => "hull-repair",
            UnitKind.ShotChargePowerUp => "shot-charge",
            UnitKind.InterceptorExplosion => "interceptor-explosion",
            UnitKind.StormCommencingWhirl => "storm-commencing-whirl",
            UnitKind.StormActiveWhirl => "storm-active-whirl",
            _ => ToKebabCase(kind.ToString())
        };
    }

    private static string ToColorHex(byte red, byte green, byte blue)
    {
        return string.Create(7, (red, green, blue), static (span, value) =>
        {
            span[0] = '#';
            value.red.TryFormat(span[1..3], out _, "X2");
            value.green.TryFormat(span[3..5], out _, "X2");
            value.blue.TryFormat(span[5..7], out _, "X2");
        });
    }

    private static string ToKebabCase(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsUpper(character) && index > 0)
            {
                builder.Append('-');
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }

    private static string? ReadString(JsonObject? payload, string key)
    {
        return payload?[key]?.GetValue<string?>();
    }

    private static IReadOnlyList<string> ReadStringArray(JsonObject? payload, string key)
    {
        if (payload?[key] is not JsonArray array)
        {
            return [];
        }

        return array
            .Select(static item => item?.GetValue<string?>())
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(static item => item!.Trim())
            .ToArray();
    }

    private static double? ReadDouble(JsonObject? payload, string key)
    {
        return payload?[key]?.GetValue<double?>();
    }

    private static double Clamp(double value, double minimum, double maximum)
    {
        return Math.Min(maximum, Math.Max(minimum, value));
    }

    private static float Clamp(float value, float minimum, float maximum)
    {
        return MathF.Min(maximum, MathF.Max(minimum, value));
    }

    private static (float Width, float Length) ResolveScannerProfile(string mode, float minimumWidth, float maximumWidth, float minimumLength, float maximumLength)
    {
        return mode.Trim().ToLowerInvariant() switch
        {
            "focused" => (Clamp(maximumWidth * 0.3f, minimumWidth, maximumWidth), maximumLength),
            "sweep" => (maximumWidth, Clamp(maximumLength * 0.65f, minimumLength, maximumLength)),
            "wide" => (maximumWidth, Clamp(maximumLength * 0.5f, minimumLength, maximumLength)),
            _ => (Clamp(maximumWidth * 0.2f, minimumWidth, maximumWidth), maximumLength)
        };
    }

    private static int GetAmmo(Controllable? controllable)
    {
        return controllable switch
        {
            ClassicShipControllable classicShip => (int)Math.Round(classicShip.ShotMagazine.CurrentShots),
            ModernShipControllable modernShip => (int)Math.Round(modernShip.ShotMagazineN.CurrentShots),
            _ => 0
        };
    }

    private static bool GetScannerActive(Controllable? controllable)
    {
        return controllable switch
        {
            ClassicShipControllable classicShip => classicShip.MainScanner.Active,
            ModernShipControllable modernShip => modernShip.ScannerN.Active,
            _ => false
        };
    }

    private static string InferScannerMode(Controllable? controllable)
    {
        return GetScannerActive(controllable) ? "pinpoint" : "off";
    }

    private static ModernShipEngineSubsystem GetModernEngine(ModernShipControllable controllable, int engineIndex)
    {
        return engineIndex switch
        {
            0 => controllable.EngineN,
            1 => controllable.EngineNE,
            2 => controllable.EngineE,
            3 => controllable.EngineSE,
            4 => controllable.EngineS,
            5 => controllable.EngineSW,
            6 => controllable.EngineW,
            _ => controllable.EngineNW
        };
    }

    private static int SelectModernEngineIndex(float shipAngle, float targetAngle)
    {
        var engineAngles = new[] { 0f, 315f, 270f, 225f, 180f, 135f, 90f, 45f };
        var bestIndex = 0;
        var bestDistance = float.MaxValue;

        for (var index = 0; index < engineAngles.Length; index++)
        {
            var absoluteAngle = NormalizeAngle(shipAngle + engineAngles[index]);
            var distance = Math.Abs(NormalizeSignedAngle(targetAngle - absoluteAngle));
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestIndex = index;
            }
        }

        return bestIndex;
    }

    private static float GetModernEngineAngle(float shipAngle, int engineIndex)
    {
        var engineAngles = new[] { 0f, 315f, 270f, 225f, 180f, 135f, 90f, 45f };
        return NormalizeAngle(shipAngle + engineAngles[engineIndex]);
    }

    private static float NormalizeAngle(float angle)
    {
        var normalizedAngle = angle % 360f;
        return normalizedAngle < 0 ? normalizedAngle + 360f : normalizedAngle;
    }

    private static float NormalizeSignedAngle(float angle)
    {
        var normalizedAngle = NormalizeAngle(angle + 180f) - 180f;
        return normalizedAngle <= -180f ? normalizedAngle + 360f : normalizedAngle;
    }

    private sealed record ResolvedControllable(Controllable? Controllable, CommandReplyMessage? Rejection);

    private sealed class PlayerRuntimeState
    {
        private readonly object syncRoot = new();
        private readonly Dictionary<int, ControllableRuntimeState> controllables = [];

        public ControllableRuntimeState EnsureControllable(int controllableId)
        {
            lock (syncRoot)
            {
                if (!controllables.TryGetValue(controllableId, out var state))
                {
                    state = new ControllableRuntimeState();
                    controllables[controllableId] = state;
                }

                return state;
            }
        }

        public void SetNavigation(int controllableId, double targetX, double targetY, DateTimeOffset lastDispatchedUtc)
        {
            var state = EnsureControllable(controllableId);
            lock (state.SyncRoot)
            {
                state.Navigation = new NavigationTargetState(targetX, targetY, lastDispatchedUtc);
            }
        }

        public void MarkNavigationDispatched(int controllableId, DateTimeOffset timestamp)
        {
            var state = EnsureControllable(controllableId);
            lock (state.SyncRoot)
            {
                if (state.Navigation is not null)
                {
                    state.Navigation = state.Navigation with { LastDispatchedUtc = timestamp };
                }
            }
        }

        public void ClearNavigation(int controllableId)
        {
            var state = EnsureControllable(controllableId);
            lock (state.SyncRoot)
            {
                state.Navigation = null;
            }
        }

        public void SetScannerMode(int controllableId, string mode)
        {
            var state = EnsureControllable(controllableId);
            lock (state.SyncRoot)
            {
                state.ScannerMode = string.IsNullOrWhiteSpace(mode) ? "pinpoint" : mode.Trim();
            }
        }

        public IReadOnlyList<NavigationDispatchState> SnapshotNavigationTargets()
        {
            lock (syncRoot)
            {
                return controllables
                    .Where(static entry => entry.Value.Navigation is not null)
                    .Select(entry => new NavigationDispatchState(entry.Key, entry.Value.Navigation!.TargetX, entry.Value.Navigation.TargetY, entry.Value.Navigation.LastDispatchedUtc))
                    .ToArray();
            }
        }
    }

    private sealed class ControllableRuntimeState
    {
        public object SyncRoot { get; } = new();

        public NavigationTargetState? Navigation { get; set; }

        public string ScannerMode { get; set; } = "pinpoint";

        public int? LastModernEngineIndex { get; set; }

        public double EngineCurrentX { get; set; }

        public double EngineCurrentY { get; set; }

        public double EngineMaximum { get; set; }
    }

    private sealed record NavigationDispatchState(int ControllableId, double TargetX, double TargetY, DateTimeOffset LastDispatchedUtc);

    private sealed record NavigationTargetState(double TargetX, double TargetY, DateTimeOffset LastDispatchedUtc);
}
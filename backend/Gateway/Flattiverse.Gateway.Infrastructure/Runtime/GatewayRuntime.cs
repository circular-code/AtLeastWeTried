using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics.CodeAnalysis;
using Flattiverse.Gateway.Application.Abstractions;
using Flattiverse.Gateway.Contracts.Messages.Client;
using Flattiverse.Gateway.Contracts.Messages.Server;
using Flattiverse.Gateway.Domain.Sessions;

namespace Flattiverse.Gateway.Infrastructure.Runtime;

public sealed class GatewayRuntime : IGatewayRuntime
{
    private const int PrimaryClusterId = 1;
    private const string PrimaryClusterName = "Perimeter Ring";
    private const double WorldHalfWidth = 1800;
    private const double WorldHalfHeight = 1200;
    private const double MaxShipSpeed = 240;
    private const double ArrivalDistance = 24;
    private const double ProjectileSpeed = 620;
    private static readonly string[] TeamPalette =
    [
        "#78d7ff",
        "#ff9f6e",
        "#9bffb0",
        "#f4d35e",
        "#c8a7ff",
        "#ff7fb7"
    ];

    private static readonly ClusterSnapshot[] Clusters =
    [
        new(PrimaryClusterId, PrimaryClusterName, true, true)
    ];

    private static readonly StaticUnitState[] StaticUnits =
    [
        new("sun-helion", PrimaryClusterId, "sun", 0, 0, 0, 180, null, 920, 340, 180, 12, 5),
        new("planet-aurelia", PrimaryClusterId, "planet", -780, -360, 18, 96, null, null, null, null, null, null),
        new("moon-cinder", PrimaryClusterId, "moon", -1030, -470, 42, 44, null, null, null, null, null, null),
        new("gate-kestrel", PrimaryClusterId, "gate", 1080, 280, 0, 64, null, null, null, null, null, null),
        new("wormhole-lens", PrimaryClusterId, "wormhole", 1280, -540, 0, 58, null, null, null, null, null, null)
    ];

    private readonly object syncRoot = new();
    private readonly TimeProvider timeProvider;
    private readonly Dictionary<string, PlayerState> playersBySessionId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ShipState> shipsByControllableId = new(StringComparer.Ordinal);
    private readonly List<ProjectileState> projectiles = [];
    private DateTimeOffset lastSimulationUtc;
    private int nextTeamId = 1;
    private int nextShipOrdinal = 1;
    private int nextProjectileOrdinal = 1;

    public GatewayRuntime(TimeProvider timeProvider)
    {
        this.timeProvider = timeProvider;
        lastSimulationUtc = timeProvider.GetUtcNow();
    }

    public ValueTask EnsurePlayerSessionAsync(AttachedPlayerSession playerSession, CancellationToken cancellationToken)
    {
        lock (syncRoot)
        {
            AdvanceWorldToNowNoLock();

            if (!playersBySessionId.TryGetValue(playerSession.PlayerSessionId, out var playerState))
            {
                playerState = new PlayerState(
                    playerSession.PlayerSessionId,
                    playerSession.DisplayName,
                    NormalizeTeamName(playerSession.TeamName, nextTeamId),
                    nextTeamId++);

                playersBySessionId[playerSession.PlayerSessionId] = playerState;
            }
            else
            {
                playerState.DisplayName = playerSession.DisplayName;
                playerState.TeamName = NormalizeTeamName(playerSession.TeamName, playerState.TeamId);
            }

            foreach (var shipId in playerState.ShipIds)
            {
                if (!shipsByControllableId.TryGetValue(shipId, out var existingShip))
                {
                    continue;
                }

                existingShip.TeamName = playerState.TeamName;
                if (string.IsNullOrWhiteSpace(existingShip.DisplayName))
                {
                    existingShip.DisplayName = BuildStarterShipName(playerState.DisplayName);
                }
            }

            if (GetOwnedShipsNoLock(playerState.PlayerSessionId).Count == 0)
            {
                var ship = CreateShipNoLock(playerState, BuildStarterShipName(playerState.DisplayName), "modern");
                ActivateShipNoLock(playerState, ship.ControllableId);
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<WorldSnapshot> GetSnapshotAsync(string playerSessionId, CancellationToken cancellationToken)
    {
        lock (syncRoot)
        {
            AdvanceWorldToNowNoLock();
            return ValueTask.FromResult(BuildSnapshotNoLock());
        }
    }

    public ValueTask<OwnerDeltaMessage> GetOwnerOverlayAsync(string playerSessionId, CancellationToken cancellationToken)
    {
        lock (syncRoot)
        {
            AdvanceWorldToNowNoLock();
            return ValueTask.FromResult(BuildOwnerOverlayNoLock(playerSessionId));
        }
    }

    public ValueTask<IReadOnlyList<GatewayMessage>> ExecuteCommandAsync(
        string playerSessionId,
        CommandMessage command,
        CancellationToken cancellationToken)
    {
        lock (syncRoot)
        {
            AdvanceWorldToNowNoLock();

            if (!playersBySessionId.TryGetValue(playerSessionId, out var playerState))
            {
                return ValueTask.FromResult<IReadOnlyList<GatewayMessage>>(
                [
                    BuildRejectedReply(
                        command.CommandId,
                        "player_session_unavailable",
                        "The selected player session is not available in the gateway runtime.")
                ]);
            }

            var commandId = string.IsNullOrWhiteSpace(command.CommandId)
                ? Guid.NewGuid().ToString("N")
                : command.CommandId;

            return ValueTask.FromResult<IReadOnlyList<GatewayMessage>>(command.Type switch
            {
                "command.chat" => HandleChatNoLock(playerState, commandId, command.Payload),
                "command.create_ship" => HandleCreateShipNoLock(playerState, commandId, command.Payload),
                "command.destroy_ship" => HandleDestroyShipNoLock(playerState, commandId, command.Payload),
                "command.continue_ship" => HandleContinueShipNoLock(playerState, commandId, command.Payload),
                "command.remove_ship" => HandleRemoveShipNoLock(playerState, commandId, command.Payload),
                "command.set_engine" => HandleSetEngineNoLock(playerState, commandId, command.Payload),
                "command.set_navigation_target" => HandleSetNavigationTargetNoLock(playerState, commandId, command.Payload),
                "command.clear_navigation_target" => HandleClearNavigationTargetNoLock(playerState, commandId, command.Payload),
                "command.fire_weapon" => HandleFireWeaponNoLock(playerState, commandId, command.Payload),
                "command.set_subsystem_mode" => HandleSetSubsystemModeNoLock(playerState, commandId, command.Payload),
                _ =>
                [
                    BuildRejectedReply(
                        commandId,
                        "command_not_implemented",
                        $"{command.Type} is not implemented by the MVP gateway runtime.")
                ]
            });
        }
    }

    public ValueTask TickAsync(CancellationToken cancellationToken)
    {
        lock (syncRoot)
        {
            AdvanceWorldToNowNoLock();
        }

        return ValueTask.CompletedTask;
    }

    private IReadOnlyList<GatewayMessage> HandleChatNoLock(PlayerState playerState, string commandId, JsonObject? payload)
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

        var entry = new ChatEntry(
            $"chat-{Guid.NewGuid():N}"[..18],
            scope,
            playerState.DisplayName,
            playerState.PlayerSessionId,
            chatMessage,
            timeProvider.GetUtcNow());

        return
        [
            new ChatReceivedMessage(entry),
            BuildCompletedReply(
                commandId,
                new JsonObject
                {
                    ["scope"] = scope,
                    ["playerSessionId"] = playerState.PlayerSessionId
                })
        ];
    }

    private IReadOnlyList<GatewayMessage> HandleCreateShipNoLock(PlayerState playerState, string commandId, JsonObject? payload)
    {
        var shipName = ReadString(payload, "name") ?? $"{playerState.DisplayName} Wing {playerState.ShipIds.Count + 1}";
        var shipClass = NormalizeShipClass(ReadString(payload, "shipClass"));
        var ship = CreateShipNoLock(playerState, shipName, shipClass);

        ActivateShipNoLock(playerState, ship.ControllableId);
        AdvanceWorldStepNoLock(TimeSpan.FromMilliseconds(180));

        return BuildRefreshSequenceNoLock(
            playerState.PlayerSessionId,
            BuildCompletedReply(
                commandId,
                new JsonObject
                {
                    ["action"] = "created",
                    ["controllableId"] = ship.ControllableId
                }));
    }

    private IReadOnlyList<GatewayMessage> HandleDestroyShipNoLock(PlayerState playerState, string commandId, JsonObject? payload)
    {
        if (!TryGetOwnedShipNoLock(playerState, commandId, payload, out var ship, out var rejection))
        {
            return [rejection!];
        }

        ship.Alive = false;
        ship.Active = false;
        ship.NavigationTarget = null;
        ship.EngineThrust = 0;
        ship.EngineX = 0;
        ship.EngineY = 0;
        ship.VelocityX = 0;
        ship.VelocityY = 0;
        ship.HullCurrent = 0;
        ship.ShieldCurrent = 0;

        PromoteFirstAliveShipNoLock(playerState);

        return BuildRefreshSequenceNoLock(
            playerState.PlayerSessionId,
            BuildCompletedReply(
                commandId,
                new JsonObject
                {
                    ["action"] = "destroyed",
                    ["controllableId"] = ship.ControllableId
                }));
    }

    private IReadOnlyList<GatewayMessage> HandleContinueShipNoLock(PlayerState playerState, string commandId, JsonObject? payload)
    {
        if (!TryGetOwnedShipNoLock(playerState, commandId, payload, out var ship, out var rejection))
        {
            return [rejection!];
        }

        ResetShipNoLock(ship, playerState, revived: true);
        ActivateShipNoLock(playerState, ship.ControllableId);
        AdvanceWorldStepNoLock(TimeSpan.FromMilliseconds(120));

        return BuildRefreshSequenceNoLock(
            playerState.PlayerSessionId,
            BuildCompletedReply(
                commandId,
                new JsonObject
                {
                    ["action"] = "continued",
                    ["controllableId"] = ship.ControllableId
                }));
    }

    private IReadOnlyList<GatewayMessage> HandleRemoveShipNoLock(PlayerState playerState, string commandId, JsonObject? payload)
    {
        if (!TryGetOwnedShipNoLock(playerState, commandId, payload, out var ship, out var rejection))
        {
            return [rejection!];
        }

        shipsByControllableId.Remove(ship.ControllableId);
        playerState.ShipIds.Remove(ship.ControllableId);
        PromoteFirstAliveShipNoLock(playerState);

        if (GetOwnedShipsNoLock(playerState.PlayerSessionId).Count == 0)
        {
            var replacement = CreateShipNoLock(playerState, BuildStarterShipName(playerState.DisplayName), "modern");
            ActivateShipNoLock(playerState, replacement.ControllableId);
        }

        return BuildRefreshSequenceNoLock(
            playerState.PlayerSessionId,
            BuildCompletedReply(
                commandId,
                new JsonObject
                {
                    ["action"] = "remove_requested",
                    ["controllableId"] = ship.ControllableId
                }));
    }

    private IReadOnlyList<GatewayMessage> HandleSetEngineNoLock(PlayerState playerState, string commandId, JsonObject? payload)
    {
        if (!TryGetOwnedShipNoLock(playerState, commandId, payload, out var ship, out var rejection))
        {
            return [rejection!];
        }

        if (!ship.Alive)
        {
            return [BuildRejectedReply(commandId, "controllable_destroyed", "The selected ship is destroyed and cannot change thrust.")];
        }

        ship.EngineThrust = Clamp(ReadDouble(payload, "thrust") ?? 0, 0, 1);
        ApplySteeringNoLock(ship);
        ActivateShipNoLock(playerState, ship.ControllableId);
        AdvanceWorldStepNoLock(TimeSpan.FromMilliseconds(280));

        return BuildRefreshSequenceNoLock(
            playerState.PlayerSessionId,
            BuildCompletedReply(
                commandId,
                new JsonObject
                {
                    ["controllableId"] = ship.ControllableId,
                    ["thrust"] = ship.EngineThrust
                }));
    }

    private IReadOnlyList<GatewayMessage> HandleSetNavigationTargetNoLock(PlayerState playerState, string commandId, JsonObject? payload)
    {
        if (!TryGetOwnedShipNoLock(playerState, commandId, payload, out var ship, out var rejection))
        {
            return [rejection!];
        }

        if (!ship.Alive)
        {
            return [BuildRejectedReply(commandId, "controllable_destroyed", "The selected ship is destroyed and cannot navigate.")];
        }

        var targetX = ReadDouble(payload, "targetX");
        var targetY = ReadDouble(payload, "targetY");
        if (targetX is null || targetY is null)
        {
            return [BuildRejectedReply(commandId, "navigation_target_invalid", "Navigation targets require numeric targetX and targetY values.")];
        }

        ship.NavigationTarget = new NavigationTarget(targetX.Value, targetY.Value);
        ship.EngineThrust = Math.Max(ship.EngineThrust, 0.35);
        ApplySteeringNoLock(ship);
        ActivateShipNoLock(playerState, ship.ControllableId);
        AdvanceWorldStepNoLock(TimeSpan.FromMilliseconds(420));

        return BuildRefreshSequenceNoLock(
            playerState.PlayerSessionId,
            BuildCompletedReply(
                commandId,
                new JsonObject
                {
                    ["controllableId"] = ship.ControllableId,
                    ["action"] = "navigation_updated"
                }));
    }

    private IReadOnlyList<GatewayMessage> HandleClearNavigationTargetNoLock(PlayerState playerState, string commandId, JsonObject? payload)
    {
        if (!TryGetOwnedShipNoLock(playerState, commandId, payload, out var ship, out var rejection))
        {
            return [rejection!];
        }

        ship.NavigationTarget = null;
        ship.EngineX = 0;
        ship.EngineY = 0;
        ship.VelocityX = 0;
        ship.VelocityY = 0;
        ship.EngineThrust = 0;
        ActivateShipNoLock(playerState, ship.ControllableId);

        return BuildRefreshSequenceNoLock(
            playerState.PlayerSessionId,
            BuildCompletedReply(
                commandId,
                new JsonObject
                {
                    ["controllableId"] = ship.ControllableId,
                    ["action"] = "navigation_cleared"
                }));
    }

    private IReadOnlyList<GatewayMessage> HandleFireWeaponNoLock(PlayerState playerState, string commandId, JsonObject? payload)
    {
        if (!TryGetOwnedShipNoLock(playerState, commandId, payload, out var ship, out var rejection))
        {
            return [rejection!];
        }

        if (!ship.Alive)
        {
            return [BuildRejectedReply(commandId, "controllable_destroyed", "The selected ship is destroyed and cannot fire.")];
        }

        if (ship.Ammo <= 0)
        {
            return [BuildRejectedReply(commandId, "ammo_depleted", "The selected ship has no ammunition remaining.")];
        }

        ship.Ammo--;
        ship.BatteryCurrent = Clamp(ship.BatteryCurrent - 18, 0, ship.BatteryMaximum);
        ActivateShipNoLock(playerState, ship.ControllableId);

        var radians = DegreesToRadians(ship.Angle);
        var projectile = new ProjectileState(
            $"shot-{nextProjectileOrdinal++:D4}",
            PrimaryClusterId,
            ship.TeamName,
            ship.X + Math.Cos(radians) * (ship.Radius + 10),
            ship.Y + Math.Sin(radians) * (ship.Radius + 10),
            ship.Angle,
            Math.Cos(radians) * ProjectileSpeed,
            Math.Sin(radians) * ProjectileSpeed,
            timeProvider.GetUtcNow().AddSeconds(1.2));

        projectiles.Add(projectile);
        AdvanceWorldStepNoLock(TimeSpan.FromMilliseconds(150));

        return BuildRefreshSequenceNoLock(
            playerState.PlayerSessionId,
            BuildCompletedReply(
                commandId,
                new JsonObject
                {
                    ["controllableId"] = ship.ControllableId,
                    ["action"] = "weapon_fired"
                }));
    }

    private IReadOnlyList<GatewayMessage> HandleSetSubsystemModeNoLock(PlayerState playerState, string commandId, JsonObject? payload)
    {
        if (!TryGetOwnedShipNoLock(playerState, commandId, payload, out var ship, out var rejection))
        {
            return [rejection!];
        }

        var mode = ReadString(payload, "mode") ?? "pinpoint";
        ship.ScannerMode = mode;
        ship.ScannerActive = !string.Equals(mode, "off", StringComparison.OrdinalIgnoreCase);
        ActivateShipNoLock(playerState, ship.ControllableId);

        return BuildRefreshSequenceNoLock(
            playerState.PlayerSessionId,
            BuildCompletedReply(
                commandId,
                new JsonObject
                {
                    ["controllableId"] = ship.ControllableId,
                    ["mode"] = ship.ScannerMode
                }));
    }

    private IReadOnlyList<GatewayMessage> BuildRefreshSequenceNoLock(string playerSessionId, CommandReplyMessage reply)
    {
        return
        [
            new SnapshotFullMessage(BuildSnapshotNoLock()),
            BuildOwnerOverlayNoLock(playerSessionId),
            reply
        ];
    }

    private WorldSnapshot BuildSnapshotNoLock()
    {
        var teams = playersBySessionId.Values
            .OrderBy(player => player.TeamId)
            .Select(player => new TeamSnapshot(
                player.TeamId,
                player.TeamName,
                GetOwnedShipsNoLock(player.PlayerSessionId).Where(ship => ship.Alive).Sum(ship => ship.Score),
                GetTeamColor(player.TeamName)))
            .ToArray();

        var units = new List<UnitSnapshot>(StaticUnits.Length + shipsByControllableId.Count + projectiles.Count);
        units.AddRange(StaticUnits.Select(staticUnit => staticUnit.ToSnapshot()));
        units.AddRange(shipsByControllableId.Values
            .OrderBy(ship => ship.ControllableId, StringComparer.Ordinal)
            .Select(BuildShipUnitSnapshotNoLock));
        units.AddRange(projectiles
            .OrderBy(projectile => projectile.UnitId, StringComparer.Ordinal)
            .Select(projectile => new UnitSnapshot(
                projectile.UnitId,
                projectile.ClusterId,
                "shot",
                projectile.X,
                projectile.Y,
                projectile.Angle,
                6,
                projectile.TeamName,
                null,
                null,
                null,
                null,
                null)));

        var controllables = shipsByControllableId.Values
            .OrderBy(ship => ship.ControllableId, StringComparer.Ordinal)
            .Select(ship => new PublicControllableSnapshot(
                ship.ControllableId,
                ship.DisplayName,
                ship.TeamName,
                ship.Alive,
                ship.Score))
            .ToArray();

        return new WorldSnapshot(
            "Gateway MVP Sandbox",
            "Local in-memory runtime for the prototype web UI.",
            "classic",
            teams,
            Clusters,
            units,
            controllables);
    }

    private OwnerDeltaMessage BuildOwnerOverlayNoLock(string playerSessionId)
    {
        var events = new List<OverlayEvent>
        {
            new("overlay.snapshot", null, null)
        };

        foreach (var ship in GetOwnedShipsNoLock(playerSessionId).OrderBy(entry => entry.ControllableId, StringComparer.Ordinal))
        {
            events.Add(new OverlayEvent("overlay.updated", ship.ControllableId, BuildShipOverlayNoLock(ship)));
        }

        return new OwnerDeltaMessage(playerSessionId, events);
    }

    private JsonObject BuildShipOverlayNoLock(ShipState ship)
    {
        var overlay = new JsonObject
        {
            ["displayName"] = ship.DisplayName,
            ["teamName"] = ship.TeamName,
            ["kind"] = ship.Kind,
            ["clusterId"] = ship.ClusterId,
            ["clusterName"] = PrimaryClusterName,
            ["active"] = ship.Active,
            ["alive"] = ship.Alive,
            ["ammo"] = ship.Ammo,
            ["radius"] = ship.Radius,
            ["position"] = new JsonObject
            {
                ["x"] = ship.X,
                ["y"] = ship.Y,
                ["angle"] = ship.Angle
            },
            ["movement"] = new JsonObject
            {
                ["x"] = ship.VelocityX,
                ["y"] = ship.VelocityY
            },
            ["engine"] = new JsonObject
            {
                ["currentX"] = ship.EngineX,
                ["currentY"] = ship.EngineY,
                ["maximum"] = ship.EngineMaximum
            },
            ["scanner"] = new JsonObject
            {
                ["active"] = ship.ScannerActive,
                ["mode"] = ship.ScannerMode
            },
            ["shield"] = new JsonObject
            {
                ["current"] = ship.ShieldCurrent,
                ["maximum"] = ship.ShieldMaximum,
                ["active"] = ship.Alive && ship.ShieldCurrent > 0
            },
            ["hull"] = new JsonObject
            {
                ["current"] = ship.HullCurrent,
                ["maximum"] = ship.HullMaximum
            },
            ["energyBattery"] = new JsonObject
            {
                ["current"] = ship.BatteryCurrent,
                ["maximum"] = ship.BatteryMaximum
            }
        };

        if (ship.NavigationTarget is not null)
        {
            overlay["navigation"] = new JsonObject
            {
                ["targetX"] = ship.NavigationTarget.X,
                ["targetY"] = ship.NavigationTarget.Y
            };
        }

        return overlay;
    }

    private UnitSnapshot BuildShipUnitSnapshotNoLock(ShipState ship)
    {
        return new UnitSnapshot(
            ship.ControllableId,
            ship.ClusterId,
            ship.Kind,
            ship.X,
            ship.Y,
            ship.Angle,
            ship.Radius,
            ship.TeamName,
            null,
            null,
            null,
            null,
            null);
    }

    private List<ShipState> GetOwnedShipsNoLock(string playerSessionId)
    {
        if (!playersBySessionId.TryGetValue(playerSessionId, out var playerState))
        {
            return [];
        }

        return playerState.ShipIds
            .Select(shipId => shipsByControllableId.GetValueOrDefault(shipId))
            .Where(static ship => ship is not null)
            .Cast<ShipState>()
            .ToList();
    }

    private ShipState CreateShipNoLock(PlayerState playerState, string shipName, string shipClass)
    {
        var shipOrdinal = nextShipOrdinal++;
        var spawn = GetSpawnPoint(playerState.TeamId, shipOrdinal - 1);
        var kind = shipClass == "classic" ? "classic-ship" : "modern-ship";
        var radius = shipClass == "classic" ? 22 : 28;

        var ship = new ShipState(
            $"ship-{shipOrdinal:D4}",
            playerState.PlayerSessionId,
            playerState.TeamName,
            string.IsNullOrWhiteSpace(shipName) ? $"{playerState.DisplayName} Wing {shipOrdinal}" : shipName.Trim(),
            kind,
            PrimaryClusterId,
            spawn.X,
            spawn.Y,
            spawn.Angle,
            radius,
            100,
            100,
            80,
            100,
            24)
        {
            HullCurrent = 100,
            ShieldCurrent = 80,
            BatteryCurrent = 100,
            Ammo = 24,
            ScannerMode = "pinpoint",
            ScannerActive = true,
            Alive = true,
            Active = true,
            Score = 100
        };

        shipsByControllableId[ship.ControllableId] = ship;
        playerState.ShipIds.Add(ship.ControllableId);
        return ship;
    }

    private void ResetShipNoLock(ShipState ship, PlayerState playerState, bool revived)
    {
        var spawn = GetSpawnPoint(playerState.TeamId, ship.Score + playerState.ShipIds.Count);
        ship.TeamName = playerState.TeamName;
        ship.ClusterId = PrimaryClusterId;
        ship.X = spawn.X;
        ship.Y = spawn.Y;
        ship.Angle = spawn.Angle;
        ship.NavigationTarget = null;
        ship.VelocityX = 0;
        ship.VelocityY = 0;
        ship.EngineX = 0;
        ship.EngineY = 0;
        ship.EngineThrust = 0;
        ship.HullCurrent = ship.HullMaximum;
        ship.ShieldCurrent = ship.ShieldMaximum;
        ship.BatteryCurrent = ship.BatteryMaximum;
        ship.Ammo = ship.AmmoMaximum;
        ship.ScannerActive = true;
        ship.ScannerMode = string.IsNullOrWhiteSpace(ship.ScannerMode) ? "pinpoint" : ship.ScannerMode;
        ship.Alive = true;
        ship.Active = true;

        if (revived)
        {
            ship.Score = Math.Max(ship.Score, 100);
        }
    }

    private void ActivateShipNoLock(PlayerState playerState, string controllableId)
    {
        foreach (var ownedShip in GetOwnedShipsNoLock(playerState.PlayerSessionId))
        {
            ownedShip.Active = ownedShip.ControllableId == controllableId && ownedShip.Alive;
        }
    }

    private void PromoteFirstAliveShipNoLock(PlayerState playerState)
    {
        var candidate = GetOwnedShipsNoLock(playerState.PlayerSessionId)
            .Where(ship => ship.Alive)
            .OrderBy(ship => ship.ControllableId, StringComparer.Ordinal)
            .FirstOrDefault();

        foreach (var ship in GetOwnedShipsNoLock(playerState.PlayerSessionId))
        {
            ship.Active = candidate is not null && ship.ControllableId == candidate.ControllableId;
        }
    }

    private bool TryGetOwnedShipNoLock(
        PlayerState playerState,
        string commandId,
        JsonObject? payload,
        [NotNullWhen(true)]
        out ShipState? ship,
        [NotNullWhen(false)]
        out CommandReplyMessage? rejection)
    {
        var controllableId = ReadString(payload, "controllableId");
        if (string.IsNullOrWhiteSpace(controllableId))
        {
            ship = null;
            rejection = BuildRejectedReply(commandId, "missing_controllable", "The command payload must include a controllableId.");
            return false;
        }

        if (!shipsByControllableId.TryGetValue(controllableId, out ship) || !string.Equals(ship.PlayerSessionId, playerState.PlayerSessionId, StringComparison.Ordinal))
        {
            rejection = BuildRejectedReply(commandId, "controllable_not_owned", "The selected controllable is not attached to the active player session.");
            ship = null;
            return false;
        }

        rejection = null;
        return true;
    }

    private void ApplySteeringNoLock(ShipState ship)
    {
        if (!ship.Alive)
        {
            ship.EngineX = 0;
            ship.EngineY = 0;
            ship.VelocityX = 0;
            ship.VelocityY = 0;
            return;
        }

        var directionX = Math.Cos(DegreesToRadians(ship.Angle));
        var directionY = Math.Sin(DegreesToRadians(ship.Angle));

        if (ship.NavigationTarget is not null)
        {
            var deltaX = ship.NavigationTarget.X - ship.X;
            var deltaY = ship.NavigationTarget.Y - ship.Y;
            var distance = Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
            if (distance > 0.001)
            {
                directionX = deltaX / distance;
                directionY = deltaY / distance;
                ship.Angle = RadiansToDegrees(Math.Atan2(directionY, directionX));
            }
        }

        var speed = ship.EngineThrust * MaxShipSpeed;
        ship.EngineX = directionX * ship.EngineMaximum * ship.EngineThrust;
        ship.EngineY = directionY * ship.EngineMaximum * ship.EngineThrust;
        ship.VelocityX = directionX * speed;
        ship.VelocityY = directionY * speed;
    }

    private void AdvanceWorldToNowNoLock()
    {
        var now = timeProvider.GetUtcNow();
        var delta = now - lastSimulationUtc;
        if (delta <= TimeSpan.Zero)
        {
            return;
        }

        AdvanceWorldStepNoLock(delta > TimeSpan.FromSeconds(0.5) ? TimeSpan.FromSeconds(0.5) : delta);
        lastSimulationUtc = now;
    }

    private void AdvanceWorldStepNoLock(TimeSpan step)
    {
        var seconds = Math.Max(0, step.TotalSeconds);
        if (seconds <= 0)
        {
            return;
        }

        foreach (var ship in shipsByControllableId.Values)
        {
            if (!ship.Alive)
            {
                ship.EngineX = 0;
                ship.EngineY = 0;
                ship.VelocityX = 0;
                ship.VelocityY = 0;
                continue;
            }

            if (ship.NavigationTarget is not null)
            {
                var deltaX = ship.NavigationTarget.X - ship.X;
                var deltaY = ship.NavigationTarget.Y - ship.Y;
                var distance = Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
                if (distance <= ArrivalDistance)
                {
                    ship.NavigationTarget = null;
                    ship.EngineThrust = 0;
                    ship.EngineX = 0;
                    ship.EngineY = 0;
                    ship.VelocityX = 0;
                    ship.VelocityY = 0;
                }
                else
                {
                    ship.EngineThrust = Math.Max(ship.EngineThrust, 0.35);
                    ApplySteeringNoLock(ship);
                }
            }
            else if (ship.EngineThrust <= 0.001)
            {
                ship.VelocityX *= 0.55;
                ship.VelocityY *= 0.55;
                if (Math.Abs(ship.VelocityX) < 0.5)
                {
                    ship.VelocityX = 0;
                }

                if (Math.Abs(ship.VelocityY) < 0.5)
                {
                    ship.VelocityY = 0;
                }
            }
            else
            {
                ApplySteeringNoLock(ship);
            }

            ship.X = Clamp(ship.X + ship.VelocityX * seconds, -WorldHalfWidth, WorldHalfWidth);
            ship.Y = Clamp(ship.Y + ship.VelocityY * seconds, -WorldHalfHeight, WorldHalfHeight);
            ship.ShieldCurrent = Clamp(ship.ShieldCurrent + 8 * seconds, 0, ship.ShieldMaximum);
            ship.BatteryCurrent = Clamp(ship.BatteryCurrent + 10 * seconds - Math.Abs(ship.EngineThrust) * 6 * seconds, 0, ship.BatteryMaximum);
        }

        for (var index = projectiles.Count - 1; index >= 0; index--)
        {
            var projectile = projectiles[index];
            projectile.X += projectile.VelocityX * seconds;
            projectile.Y += projectile.VelocityY * seconds;

            if (projectile.ExpiresAtUtc <= timeProvider.GetUtcNow()
                || projectile.X < -WorldHalfWidth - 200
                || projectile.X > WorldHalfWidth + 200
                || projectile.Y < -WorldHalfHeight - 200
                || projectile.Y > WorldHalfHeight + 200)
            {
                projectiles.RemoveAt(index);
            }
        }
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

    private static string NormalizeTeamName(string? teamName, int teamId)
    {
        return string.IsNullOrWhiteSpace(teamName) ? $"Team {teamId}" : teamName.Trim();
    }

    private static string NormalizeShipClass(string? shipClass)
    {
        return string.Equals(shipClass, "classic", StringComparison.OrdinalIgnoreCase) ? "classic" : "modern";
    }

    private static string NormalizeScope(string? scope)
    {
        return scope switch
        {
            "team" => "team",
            "private" => "private",
            _ => "galaxy"
        };
    }

    private static string BuildStarterShipName(string displayName)
    {
        return string.IsNullOrWhiteSpace(displayName) ? "Prototype Wing" : $"{displayName} Vanguard";
    }

    private static string GetTeamColor(string teamName)
    {
        var hash = 17;
        foreach (var character in teamName)
        {
            hash = (hash * 31) + character;
        }

        return TeamPalette[Math.Abs(hash) % TeamPalette.Length];
    }

    private static SpawnPoint GetSpawnPoint(int teamId, int offset)
    {
        var angle = ((teamId + offset) % 6) * 56;
        var radians = DegreesToRadians(angle);
        var radius = 460 + ((offset % 3) * 110);

        return new SpawnPoint(
            Math.Cos(radians) * radius,
            Math.Sin(radians) * radius,
            angle + 180);
    }

    private static string? ReadString(JsonObject? payload, string key)
    {
        try
        {
            if (payload is null || !payload.TryGetPropertyValue(key, out var node) || node is null)
            {
                return null;
            }

            return node.Deserialize<string>();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static double? ReadDouble(JsonObject? payload, string key)
    {
        try
        {
            if (payload is null || !payload.TryGetPropertyValue(key, out var node) || node is null)
            {
                return null;
            }

            return node.Deserialize<double>();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static double Clamp(double value, double min, double max)
    {
        return Math.Max(min, Math.Min(max, value));
    }

    private static double DegreesToRadians(double degrees)
    {
        return degrees * Math.PI / 180.0;
    }

    private static double RadiansToDegrees(double radians)
    {
        return radians * 180.0 / Math.PI;
    }

    private sealed record StaticUnitState(
        string UnitId,
        int ClusterId,
        string Kind,
        double X,
        double Y,
        double Angle,
        double Radius,
        string? TeamName,
        double? SunEnergy,
        double? SunIons,
        double? SunNeutrinos,
        double? SunHeat,
        double? SunDrain)
    {
        public UnitSnapshot ToSnapshot()
        {
            return new UnitSnapshot(UnitId, ClusterId, Kind, X, Y, Angle, Radius, TeamName, SunEnergy, SunIons, SunNeutrinos, SunHeat, SunDrain);
        }
    }

    private sealed class PlayerState
    {
        public PlayerState(string playerSessionId, string displayName, string teamName, int teamId)
        {
            PlayerSessionId = playerSessionId;
            DisplayName = displayName;
            TeamName = teamName;
            TeamId = teamId;
        }

        public string PlayerSessionId { get; }

        public string DisplayName { get; set; }

        public string TeamName { get; set; }

        public int TeamId { get; }

        public HashSet<string> ShipIds { get; } = new(StringComparer.Ordinal);
    }

    private sealed class ShipState
    {
        public ShipState(
            string controllableId,
            string playerSessionId,
            string teamName,
            string displayName,
            string kind,
            int clusterId,
            double x,
            double y,
            double angle,
            double radius,
            double engineMaximum,
            double hullMaximum,
            double shieldMaximum,
            double batteryMaximum,
            int ammoMaximum)
        {
            ControllableId = controllableId;
            PlayerSessionId = playerSessionId;
            TeamName = teamName;
            DisplayName = displayName;
            Kind = kind;
            ClusterId = clusterId;
            X = x;
            Y = y;
            Angle = angle;
            Radius = radius;
            EngineMaximum = engineMaximum;
            HullMaximum = hullMaximum;
            ShieldMaximum = shieldMaximum;
            BatteryMaximum = batteryMaximum;
            AmmoMaximum = ammoMaximum;
        }

        public string ControllableId { get; }

        public string PlayerSessionId { get; }

        public string TeamName { get; set; }

        public string DisplayName { get; set; }

        public string Kind { get; }

        public int ClusterId { get; set; }

        public double X { get; set; }

        public double Y { get; set; }

        public double Angle { get; set; }

        public double Radius { get; }

        public bool Alive { get; set; }

        public bool Active { get; set; }

        public double VelocityX { get; set; }

        public double VelocityY { get; set; }

        public double EngineThrust { get; set; }

        public double EngineX { get; set; }

        public double EngineY { get; set; }

        public double EngineMaximum { get; }

        public double HullCurrent { get; set; }

        public double HullMaximum { get; }

        public double ShieldCurrent { get; set; }

        public double ShieldMaximum { get; }

        public double BatteryCurrent { get; set; }

        public double BatteryMaximum { get; }

        public int Ammo { get; set; }

        public int AmmoMaximum { get; }

        public int Score { get; set; }

        public string ScannerMode { get; set; } = "pinpoint";

        public bool ScannerActive { get; set; } = true;

        public NavigationTarget? NavigationTarget { get; set; }
    }

    private sealed class ProjectileState
    {
        public ProjectileState(
            string unitId,
            int clusterId,
            string teamName,
            double x,
            double y,
            double angle,
            double velocityX,
            double velocityY,
            DateTimeOffset expiresAtUtc)
        {
            UnitId = unitId;
            ClusterId = clusterId;
            TeamName = teamName;
            X = x;
            Y = y;
            Angle = angle;
            VelocityX = velocityX;
            VelocityY = velocityY;
            ExpiresAtUtc = expiresAtUtc;
        }

        public string UnitId { get; }

        public int ClusterId { get; }

        public string TeamName { get; }

        public double X { get; set; }

        public double Y { get; set; }

        public double Angle { get; }

        public double VelocityX { get; }

        public double VelocityY { get; }

        public DateTimeOffset ExpiresAtUtc { get; }
    }

    private sealed record NavigationTarget(double X, double Y);

    private sealed record SpawnPoint(double X, double Y, double Angle);
}
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Flattiverse.Connector.Events;
using Flattiverse.Connector.GalaxyHierarchy;
using Flattiverse.Connector.Units;
using Flattiverse.Gateway.Application.Abstractions;
using Flattiverse.Gateway.Contracts.Messages.Server;
using Flattiverse.Gateway.Domain.Sessions;
using Flattiverse.Gateway.Infrastructure.Connector;

namespace Flattiverse.Gateway.Infrastructure.Runtime;

internal sealed class ConnectorGatewayEventPipeline : IConnectorEventPipeline
{
    private readonly IBrowserSessionStore browserSessionStore;
    private readonly IGatewayConnectionMessageQueue connectionMessageQueue;

    public ConnectorGatewayEventPipeline(
        IBrowserSessionStore browserSessionStore,
        IGatewayConnectionMessageQueue connectionMessageQueue)
    {
        this.browserSessionStore = browserSessionStore;
        this.connectionMessageQueue = connectionMessageQueue;
    }

    public async ValueTask ProcessAsync(ConnectorPlayerSessionLease lease, FlattiverseEvent @event, CancellationToken cancellationToken)
    {
        if (@event is not GalaxyTickEvent)
        {
            return;
        }

        var sessions = await browserSessionStore.ListAsync(cancellationToken).ConfigureAwait(false);
        if (sessions.Count == 0)
        {
            return;
        }

        var message = BuildOwnerDelta(lease);
        if (message.Events.Count == 0)
        {
            return;
        }

        foreach (var session in sessions)
        {
            if (!ShouldReceiveTickUpdate(session, lease.PlayerSessionId))
            {
                continue;
            }

            await connectionMessageQueue.EnqueueAsync(session.ConnectionId, [message], cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool ShouldReceiveTickUpdate(BrowserSession session, string playerSessionId)
    {
        return string.Equals(session.SelectedPlayerSessionId, playerSessionId, StringComparison.Ordinal)
            && session.HasPlayerSession(playerSessionId);
    }

    private static OwnerDeltaMessage BuildOwnerDelta(ConnectorPlayerSessionLease lease)
    {
        var events = new List<OverlayEvent>();

        foreach (var info in lease.Galaxy.Player.ControllableInfos.Where(static entry => entry.Active).OrderBy(static entry => entry.Id))
        {
            lease.Galaxy.Controllables.TryGet(info.Id, out var controllable);
            events.Add(new OverlayEvent(
                "overlay.updated",
                BuildControllableId(lease.PlayerSessionId, info.Id),
                BuildControllableChanges(lease, info, controllable)));
        }

        return new OwnerDeltaMessage(lease.PlayerSessionId, events);
    }

    private static JsonObject BuildControllableChanges(ConnectorPlayerSessionLease lease, ControllableInfo info, Controllable? controllable)
    {
        var clusterId = controllable?.Cluster.Id ?? 0;
        var clusterName = controllable?.Cluster.Name ?? "Offline";
        var position = controllable?.Position ?? new Vector();
        var movement = controllable?.Movement ?? new Vector();

        return new JsonObject
        {
            ["displayName"] = info.Name,
            ["teamName"] = lease.Galaxy.Player.Team.Name,
            ["kind"] = NormalizeUnitKind(controllable?.Kind ?? info.Kind),
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
            ["scanner"] = new JsonObject
            {
                ["active"] = GetScannerActive(controllable)
            },
            ["shield"] = new JsonObject
            {
                ["current"] = controllable?.Shield.Current ?? 0,
                ["maximum"] = controllable?.Shield.Maximum ?? 0,
                ["active"] = controllable?.Shield.Active ?? false
            },
            ["hull"] = new JsonObject
            {
                ["current"] = controllable?.Hull.Current ?? 0,
                ["maximum"] = controllable?.Hull.Maximum ?? 0
            },
            ["energyBattery"] = new JsonObject
            {
                ["current"] = controllable?.EnergyBattery.Current ?? 0,
                ["maximum"] = controllable?.EnergyBattery.Maximum ?? 0
            }
        };
    }

    private static string BuildControllableId(string playerSessionId, int protocolId)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{playerSessionId}/c/{protocolId:D3}");
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
}
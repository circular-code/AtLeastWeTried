using Flattiverse.Connector.Events;
using Flattiverse.Connector.Units;
using Flattiverse.Gateway.Connector;
using Flattiverse.Gateway.Protocol.Dtos;

namespace Flattiverse.Gateway.Services;

/// <summary>
/// Per-player-session service that tracks known units and produces
/// world deltas for incremental delivery.
/// </summary>
public sealed class MappingService : IConnectorEventHandler
{
    private readonly Dictionary<string, UnitSnapshotDto> _knownUnits = new();
    private readonly List<WorldDeltaDto> _pendingDeltas = new();
    private readonly object _lock = new();

    /// <summary>
    /// Process a Connector event and update internal state.
    /// Called synchronously on the event-loop task — no locking needed.
    /// </summary>
    public void Handle(FlattiverseEvent @event)
    {
        switch (@event)
        {
            case NewUnitFlattiverseEvent newUnit:
                HandleUnitCreated(newUnit.Unit, newUnit.Unit.Cluster?.Id ?? 0);
                break;

            case UpdatedUnitFlattiverseEvent updatedUnit:
                HandleUnitUpdated(updatedUnit.Unit, updatedUnit.Unit.Cluster?.Id ?? 0);
                break;

            case RemovedUnitFlattiverseEvent removedUnit:
                HandleUnitRemoved(removedUnit.Unit);
                break;
        }
    }

    /// <summary>
    /// Build a full snapshot of all known units for snapshot.full messages.
    /// Safe to call from any thread (returns a copy).
    /// </summary>
    public List<UnitSnapshotDto> BuildUnitSnapshots()
    {
        lock (_lock)
            return _knownUnits.Values.ToList();
    }

    /// <summary>
    /// Collect and clear all pending world deltas accumulated since the last call.
    /// Called by TickService on each tick.
    /// </summary>
    public List<WorldDeltaDto> CollectPendingDeltas()
    {
        lock (_lock)
        {
            if (_pendingDeltas.Count == 0)
                return new List<WorldDeltaDto>();

            var deltas = new List<WorldDeltaDto>(_pendingDeltas);
            _pendingDeltas.Clear();
            return deltas;
        }
    }

    private void HandleUnitCreated(Unit unit, int clusterId)
    {
        var dto = MapUnit(unit, clusterId);
        if (dto is null) return;

        lock (_lock)
        {
            _knownUnits[dto.UnitId] = dto;
            _pendingDeltas.Add(new WorldDeltaDto
            {
                EventType = "unit.created",
                EntityId = dto.UnitId,
                Changes = UnitToChanges(dto)
            });
        }
    }

    private void HandleUnitUpdated(Unit unit, int clusterId)
    {
        var dto = MapUnit(unit, clusterId);
        if (dto is null) return;

        lock (_lock)
        {
            _knownUnits[dto.UnitId] = dto;
            _pendingDeltas.Add(new WorldDeltaDto
            {
                EventType = "unit.updated",
                EntityId = dto.UnitId,
                Changes = UnitToChanges(dto)
            });
        }
    }

    private void HandleUnitRemoved(Unit unit)
    {
        var unitId = unit.Name;

        lock (_lock)
        {
            _knownUnits.Remove(unitId);
            _pendingDeltas.Add(new WorldDeltaDto
            {
                EventType = "unit.removed",
                EntityId = unitId
            });
        }
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
}

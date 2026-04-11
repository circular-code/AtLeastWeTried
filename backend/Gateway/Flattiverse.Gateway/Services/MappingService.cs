using Flattiverse.Connector.Events;
using Flattiverse.Connector.Units;
using Flattiverse.Gateway.Connector;
using Flattiverse.Gateway.Protocol.Dtos;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Flattiverse.Gateway.Services;

/// <summary>
/// Per-player-session service that tracks known units and produces
/// world deltas for incremental delivery.
/// </summary>
public sealed class MappingService : IConnectorEventHandler
{
    private const int MaxStoredDeltasPerScope = 4096;
    private const int HiddenTrajectoryLookaheadTicks = 20;
    private const int HiddenTrajectoryMaximumTicks = 72;
    private const int HiddenTrajectoryDownsample = 1;
    private const double HiddenTrajectoryMinimumPointDistance = 0.2d;
    private const uint RecentDynamicTargetRetentionTicks = HiddenTrajectoryMaximumTicks;

    // Mapped units are shared across sessions by scope (Galaxy + Cluster).
    private static readonly Dictionary<MappingScopeKey, ScopeState> _scopeStates = new();
    private static readonly object _stateLock = new();
    private static readonly Dictionary<string, Dictionary<string, RecentTargetSnapshotEntry>> _recentTargetSnapshotsByGalaxy = new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static string? _worldStateBaseFilePath;
    private static string? _worldStateDirectory;
    private static string? _worldStateFileStem;
    private static string? _worldStateFileExtension;
    private static bool _persistenceConfigured;
    private static readonly HashSet<string> _loadedGalaxyIds = new();
    private static readonly Dictionary<string, uint> _latestTickByGalaxy = new();

    private readonly Func<MappingScopeContext?> _scopeResolver;
    private readonly Dictionary<MappingScopeKey, long> _lastDeliveredSequenceByScope = new();
    private readonly Dictionary<string, Dictionary<string, UnitSnapshotDto>> _lastDeliveredGalaxyUnitsByGalaxy = new(StringComparer.Ordinal);

    public MappingService(Func<MappingScopeContext?> scopeResolver)
    {
        _scopeResolver = scopeResolver;
    }

    public static void ConfigurePersistence(string? worldStateFilePath)
    {
        lock (_stateLock)
        {
            if (_persistenceConfigured)
                return;

            ConfigurePersistencePathsUnsafe(worldStateFilePath);
            _loadedGalaxyIds.Clear();
            _persistenceConfigured = true;
        }
    }

    /// <summary>
    /// Process a Connector event and update internal state.
    /// Called synchronously on the event-loop task - no locking needed.
    /// </summary>
    public void Handle(FlattiverseEvent @event)
    {
        switch (@event)
        {
            case GalaxyTickEvent tickEvent:
                HandleGalaxyTick(tickEvent);
                break;

            case AppearedUnitEvent newUnit:
                if (ShouldIgnoreLifecycleEvent(newUnit.Unit))
                    break;

                HandleUnitCreated(newUnit.Unit, newUnit.Unit.Cluster?.Id ?? 0);
                break;

            case UpdatedUnitEvent updatedUnit:
                if (ShouldIgnoreLifecycleEvent(updatedUnit.Unit))
                    break;

                HandleUnitUpdated(updatedUnit.Unit, updatedUnit.Unit.Cluster?.Id ?? 0);
                break;

            case RemovedUnitEvent removedUnit:
                if (ShouldIgnoreLifecycleEvent(removedUnit.Unit))
                    break;

                HandleUnitRemoved(removedUnit.Unit, removedUnit.Unit.Cluster?.Id ?? 0);
                break;

            case DestroyedControllableInfoEvent destroyedControllable:
                HandleControllableRemoved(destroyedControllable);
                break;

            case ClosedControllableInfoEvent closedControllable:
                HandleControllableRemoved(closedControllable);
                break;
        }
    }

    private bool ShouldIgnoreLifecycleEvent(Unit unit)
    {
        if (unit is not PlayerUnit playerUnit)
            return false;

        var scope = _scopeResolver();
        if (scope is null || !scope.Value.LocalPlayerId.HasValue)
            return false;

        return playerUnit.Player.Id == scope.Value.LocalPlayerId.Value;
    }

    /// <summary>
    /// Build a full snapshot of all known units for snapshot.full messages.
    /// Safe to call from any thread (returns a copy).
    /// </summary>
    public List<UnitSnapshotDto> BuildUnitSnapshots()
    {
        EnsurePersistenceConfigured();

        var scopeKey = ResolveScopeKey();
        if (scopeKey is null)
            return new List<UnitSnapshotDto>();

        lock (_stateLock)
        {
            EnsureGalaxyLoadedUnsafe(scopeKey.Value.GalaxyId);

            if (!_scopeStates.TryGetValue(scopeKey.Value, out var scopeState))
                return new List<UnitSnapshotDto>();

            return scopeState.Units.Values.Select(CloneUnitSnapshot).ToList();
        }
    }

    /// <summary>
    /// Build a full snapshot of all known units across every known scope in the
    /// current galaxy. This is the browser-facing collaborative view.
    /// </summary>
    public List<UnitSnapshotDto> BuildGalaxyUnitSnapshots()
    {
        EnsurePersistenceConfigured();

        var scope = _scopeResolver();
        if (scope is null || string.IsNullOrWhiteSpace(scope.Value.GalaxyId))
            return new List<UnitSnapshotDto>();

        lock (_stateLock)
        {
            EnsureGalaxyLoadedUnsafe(scope.Value.GalaxyId);
            return BuildMergedGalaxyUnitSnapshotsUnsafe(scope.Value.GalaxyId);
        }
    }

    /// <summary>
    /// Try to resolve one unit snapshot in the current mapping scope.
    /// </summary>
    public bool TryGetUnitSnapshot(string unitId, out UnitSnapshotDto? snapshot)
    {
        return TryGetUnitSnapshotCore(unitId, ResolveScopeKey(), out snapshot);
    }

    /// <summary>
    /// Try to resolve one unit snapshot in a specific cluster scope for the current galaxy.
    /// </summary>
    public bool TryGetUnitSnapshot(string unitId, int clusterId, out UnitSnapshotDto? snapshot)
    {
        snapshot = null;
        if (clusterId <= 0)
            return TryGetUnitSnapshot(unitId, out snapshot);

        var scope = _scopeResolver();
        if (scope is null || string.IsNullOrWhiteSpace(scope.Value.GalaxyId))
            return false;

        return TryGetUnitSnapshotCore(unitId, new MappingScopeKey(scope.Value.GalaxyId, clusterId), out snapshot);
    }

    /// <summary>
    /// Try to resolve one unit snapshot from the merged galaxy-wide collaborative
    /// browser view. This allows backend-shared intel from other sessions to be
    /// consumed by command paths that need a target position.
    /// </summary>
    public bool TryGetGalaxyUnitSnapshot(string unitId, out UnitSnapshotDto? snapshot)
    {
        snapshot = null;
        if (string.IsNullOrWhiteSpace(unitId))
            return false;

        EnsurePersistenceConfigured();

        var scope = _scopeResolver();
        if (scope is null || string.IsNullOrWhiteSpace(scope.Value.GalaxyId))
            return false;

        lock (_stateLock)
        {
            EnsureGalaxyLoadedUnsafe(scope.Value.GalaxyId);
            foreach (var unit in BuildMergedGalaxyUnitSnapshotsUnsafe(scope.Value.GalaxyId))
            {
                if (!string.Equals(unit.UnitId, unitId, StringComparison.Ordinal))
                    continue;

                snapshot = CloneUnitSnapshot(unit);
                return true;
            }
        }

        return false;
    }

    public bool TryGetCurrentTickForCurrentScope(out uint currentTick)
    {
        currentTick = 0;
        var scopeKey = ResolveScopeKey();
        if (scopeKey is null)
            return false;

        lock (_stateLock)
        {
            EnsureGalaxyLoadedUnsafe(scopeKey.Value.GalaxyId);
            currentTick = _latestTickByGalaxy.TryGetValue(scopeKey.Value.GalaxyId, out var tick) ? tick : 0;
            return true;
        }
    }

    /// <summary>
    /// Try to resolve a recently removed dynamic target from short-lived
    /// backend memory. This keeps target resolution aligned with the browser's
    /// lost-unit traces without reintroducing removed units into the live map.
    /// </summary>
    internal bool TryGetRecentGalaxyUnitSnapshot(string unitId, int clusterId, out UnitSnapshotDto? snapshot)
    {
        snapshot = null;
        if (string.IsNullOrWhiteSpace(unitId))
            return false;

        EnsurePersistenceConfigured();

        var scope = _scopeResolver();
        if (scope is null || string.IsNullOrWhiteSpace(scope.Value.GalaxyId))
            return false;

        lock (_stateLock)
        {
            EnsureGalaxyLoadedUnsafe(scope.Value.GalaxyId);
            var currentTick = _latestTickByGalaxy.TryGetValue(scope.Value.GalaxyId, out var tick) ? tick : 0;
            return TryGetRecentTargetSnapshotUnsafe(scope.Value.GalaxyId, unitId, clusterId, currentTick, out snapshot);
        }
    }

    internal static void RecordRecentTargetSnapshot(string galaxyId, UnitSnapshotDto snapshot, uint currentTick)
    {
        lock (_stateLock)
            RecordRecentTargetSnapshotUnsafe(galaxyId, snapshot, currentTick);
    }

    internal static bool TryGetRecentTargetSnapshot(string galaxyId, string unitId, int clusterId, uint currentTick, out UnitSnapshotDto? snapshot)
    {
        lock (_stateLock)
            return TryGetRecentTargetSnapshotUnsafe(galaxyId, unitId, clusterId, currentTick, out snapshot);
    }

    private bool TryGetUnitSnapshotCore(string unitId, MappingScopeKey? scopeKey, out UnitSnapshotDto? snapshot)
    {
        snapshot = null;
        if (string.IsNullOrWhiteSpace(unitId) || scopeKey is null)
            return false;

        EnsurePersistenceConfigured();

        lock (_stateLock)
        {
            EnsureGalaxyLoadedUnsafe(scopeKey.Value.GalaxyId);

            if (!_scopeStates.TryGetValue(scopeKey.Value, out var scopeState))
                return false;

            if (!scopeState.Units.TryGetValue(unitId, out var unit))
                return false;

            snapshot = CloneUnitSnapshot(unit);
            return true;
        }
    }

    /// <summary>
    /// Collect all pending world deltas for this session's current scope.
    /// Called by TickService on each tick.
    /// </summary>
    public List<WorldDeltaDto> CollectPendingDeltas()
    {
        EnsurePersistenceConfigured();

        var scopeKey = ResolveScopeKey();
        if (scopeKey is null)
            return new List<WorldDeltaDto>();

        lock (_stateLock)
        {
            EnsureGalaxyLoadedUnsafe(scopeKey.Value.GalaxyId);

            if (!_scopeStates.TryGetValue(scopeKey.Value, out var scopeState) || scopeState.Deltas.Count == 0)
                return new List<WorldDeltaDto>();

            if (!_lastDeliveredSequenceByScope.TryGetValue(scopeKey.Value, out var lastDelivered))
            {
                // Start from the beginning of retained deltas so the newest discovery
                // is not dropped when this scope is observed for the first time.
                lastDelivered = scopeState.FirstSequence - 1;
            }

            if (lastDelivered < scopeState.FirstSequence - 1)
                lastDelivered = scopeState.FirstSequence - 1;

            var result = new List<WorldDeltaDto>();
            var newestDelivered = lastDelivered;

            foreach (var sequenced in scopeState.Deltas)
            {
                if (sequenced.Sequence <= lastDelivered)
                    continue;

                result.Add(CloneWorldDelta(sequenced.Delta));
                newestDelivered = sequenced.Sequence;
            }

            _lastDeliveredSequenceByScope[scopeKey.Value] = newestDelivered;
            return result;
        }
    }

    /// <summary>
    /// Collect browser-facing world deltas across every known scope in the
    /// current galaxy. This turns the shared mapping cache into one unified
    /// collaborative stream for attached frontends.
    /// </summary>
    public List<WorldDeltaDto> CollectPendingGalaxyDeltas()
    {
        EnsurePersistenceConfigured();

        var scope = _scopeResolver();
        if (scope is null || string.IsNullOrWhiteSpace(scope.Value.GalaxyId))
            return new List<WorldDeltaDto>();

        lock (_stateLock)
        {
            EnsureGalaxyLoadedUnsafe(scope.Value.GalaxyId);

            var currentUnits = BuildMergedGalaxyUnitSnapshotsUnsafe(scope.Value.GalaxyId);
            var currentById = currentUnits.ToDictionary(unit => unit.UnitId, CloneUnitSnapshot, StringComparer.Ordinal);
            if (!_lastDeliveredGalaxyUnitsByGalaxy.TryGetValue(scope.Value.GalaxyId, out var previousById))
            {
                previousById = new Dictionary<string, UnitSnapshotDto>(StringComparer.Ordinal);
                _lastDeliveredGalaxyUnitsByGalaxy[scope.Value.GalaxyId] = previousById;
            }

            var deltas = new List<WorldDeltaDto>();
            foreach (var unit in currentUnits)
            {
                if (!previousById.TryGetValue(unit.UnitId, out var previous))
                {
                    deltas.Add(new WorldDeltaDto
                    {
                        EventType = "unit.created",
                        EntityId = unit.UnitId,
                        Changes = UnitToChanges(unit)
                    });
                    continue;
                }

                if (UnitSnapshotsEqual(previous, unit))
                    continue;

                deltas.Add(new WorldDeltaDto
                {
                    EventType = "unit.updated",
                    EntityId = unit.UnitId,
                    Changes = UnitToChanges(unit)
                });
            }

            foreach (var previousUnitId in previousById.Keys)
            {
                if (currentById.ContainsKey(previousUnitId))
                    continue;

                deltas.Add(new WorldDeltaDto
                {
                    EventType = "unit.removed",
                    EntityId = previousUnitId
                });
            }

            _lastDeliveredGalaxyUnitsByGalaxy[scope.Value.GalaxyId] = currentById;
            return deltas;
        }
    }

    private void HandleUnitCreated(Unit unit, int clusterId)
    {
        EnsurePersistenceConfigured();

        var scopeKey = ResolveScopeKey(clusterId);
        if (scopeKey is null)
            return;

        var dto = MapUnit(unit, clusterId);
        if (dto is null)
            return;

        var isStatic = IsStaticUnit(unit);
        dto.IsStatic = isStatic;
        dto.IsSeen = true;
        dto.LastSeenTick = GetCurrentTick(scopeKey.Value.GalaxyId);

        lock (_stateLock)
        {
            EnsureGalaxyLoadedUnsafe(scopeKey.Value.GalaxyId);

            var scopeState = GetOrCreateScopeStateUnsafe(scopeKey.Value);
            ForgetRecentTargetSnapshotUnsafe(scopeKey.Value.GalaxyId, dto.UnitId);
            RemoveLegacyPlayerUnitIdUnsafe(scopeState, dto.UnitId, unit.Name);
            var alreadyKnown = scopeState.Units.TryGetValue(dto.UnitId, out var existing);
            MergeKnownDetailState(dto, existing);

            scopeState.Units[dto.UnitId] = CloneUnitSnapshot(dto);

            if (isStatic)
                scopeState.StaticUnitIds.Add(dto.UnitId);
            else
                scopeState.StaticUnitIds.Remove(dto.UnitId);

            AppendDeltaUnsafe(scopeState, new WorldDeltaDto
            {
                EventType = alreadyKnown ? "unit.updated" : "unit.created",
                EntityId = dto.UnitId,
                Changes = UnitToChanges(dto)
            });

            if (isStatic)
                SavePersistedGalaxyUnsafe(scopeKey.Value.GalaxyId);
        }
    }

    private void HandleUnitUpdated(Unit unit, int clusterId)
    {
        EnsurePersistenceConfigured();

        var scopeKey = ResolveScopeKey(clusterId);
        if (scopeKey is null)
            return;

        var dto = MapUnit(unit, clusterId);
        if (dto is null)
            return;

        var isStatic = IsStaticUnit(unit);
        dto.IsStatic = isStatic;
        dto.IsSeen = true;
        dto.LastSeenTick = GetCurrentTick(scopeKey.Value.GalaxyId);

        lock (_stateLock)
        {
            EnsureGalaxyLoadedUnsafe(scopeKey.Value.GalaxyId);

            var scopeState = GetOrCreateScopeStateUnsafe(scopeKey.Value);
            ForgetRecentTargetSnapshotUnsafe(scopeKey.Value.GalaxyId, dto.UnitId);
            RemoveLegacyPlayerUnitIdUnsafe(scopeState, dto.UnitId, unit.Name);
            scopeState.Units.TryGetValue(dto.UnitId, out var existing);
            MergeKnownDetailState(dto, existing);
            scopeState.Units[dto.UnitId] = CloneUnitSnapshot(dto);

            if (isStatic)
                scopeState.StaticUnitIds.Add(dto.UnitId);
            else
                scopeState.StaticUnitIds.Remove(dto.UnitId);

            AppendDeltaUnsafe(scopeState, new WorldDeltaDto
            {
                EventType = "unit.updated",
                EntityId = dto.UnitId,
                Changes = UnitToChanges(dto)
            });

            if (isStatic)
                SavePersistedGalaxyUnsafe(scopeKey.Value.GalaxyId);
        }
    }

    private void HandleUnitRemoved(Unit unit, int clusterId)
    {
        EnsurePersistenceConfigured();

        if (IsStaticUnit(unit))
            return;

        var scopeKey = ResolveScopeKey(clusterId);
        if (scopeKey is null)
            return;

        var unitId = BuildUnitId(unit);
        var legacyUnitId = unit.Name;

        lock (_stateLock)
        {
            EnsureGalaxyLoadedUnsafe(scopeKey.Value.GalaxyId);

            var scopeState = GetOrCreateScopeStateUnsafe(scopeKey.Value);
            if (ShouldRemoveImmediatelyWhenUnseen(unit))
            {
                var removedAny = scopeState.Units.Remove(unitId);
                if (!string.IsNullOrWhiteSpace(legacyUnitId) &&
                    !string.Equals(legacyUnitId, unitId, StringComparison.Ordinal))
                {
                    removedAny = scopeState.Units.Remove(legacyUnitId) || removedAny;
                }

                scopeState.StaticUnitIds.Remove(unitId);
                if (!string.IsNullOrWhiteSpace(legacyUnitId))
                    scopeState.StaticUnitIds.Remove(legacyUnitId);

                if (removedAny)
                {
                    AppendDeltaUnsafe(scopeState, new WorldDeltaDto
                    {
                        EventType = "unit.removed",
                        EntityId = unitId
                    });
                }

                return;
            }

            if (!scopeState.Units.TryGetValue(unitId, out var existing))
            {
                if (RemoveLegacyPlayerUnitIdUnsafe(scopeState, unitId, legacyUnitId))
                    scopeState.Units.TryGetValue(unitId, out existing);
            }

            if (existing is null)
            {
                // Player-unit removals can follow a destruction event and should not
                // resurrect the unit as an unseen marker.
                if (unit is PlayerUnit)
                    return;

                var mapped = MapUnit(unit, clusterId);
                if (mapped is null)
                    return;

                existing = mapped;
                existing.LastSeenTick = GetCurrentTick(scopeKey.Value.GalaxyId);
                scopeState.Units[unitId] = existing;
            }

            existing.IsStatic = false;
            existing.IsSeen = false;
            existing.PredictedTrajectory = TrajectoryPredictionService.SupportsHiddenTrajectory(existing)
                ? BuildHiddenTrajectory(existing, scopeState.Units.Values, GetCurrentTick(scopeKey.Value.GalaxyId))
                : null;

            AppendDeltaUnsafe(scopeState, new WorldDeltaDto
            {
                EventType = "unit.updated",
                EntityId = unitId,
                Changes = UnitToChanges(existing)
            });
        }
    }

    private void HandleControllableRemoved(ControllableInfoEvent controllableEvent)
    {
        EnsurePersistenceConfigured();

        var scope = _scopeResolver();
        if (scope is null || string.IsNullOrWhiteSpace(scope.Value.GalaxyId))
            return;

        if (scope.Value.LocalPlayerId.HasValue && controllableEvent.Player.Id == scope.Value.LocalPlayerId.Value)
            return;

        var unitId = BuildControllableUnitId(controllableEvent.Player.Id, controllableEvent.ControllableInfo.Id);
        var galaxyId = scope.Value.GalaxyId;

        lock (_stateLock)
        {
            EnsureGalaxyLoadedUnsafe(galaxyId);
            var currentTick = _latestTickByGalaxy.TryGetValue(galaxyId, out var tick) ? tick : 0;

            var scopeKeys = _scopeStates.Keys
                .Where(key => string.Equals(key.GalaxyId, galaxyId, StringComparison.Ordinal))
                .ToList();

            foreach (var scopeKey in scopeKeys)
            {
                if (!_scopeStates.TryGetValue(scopeKey, out var scopeState))
                    continue;

                if (!scopeState.Units.Remove(unitId, out var removed))
                    continue;

                RecordRecentTargetSnapshotUnsafe(galaxyId, removed, currentTick);
                scopeState.StaticUnitIds.Remove(unitId);
                AppendDeltaUnsafe(scopeState, new WorldDeltaDto
                {
                    EventType = "unit.removed",
                    EntityId = unitId
                });
            }
        }
    }

    private MappingScopeKey? ResolveScopeKey(int? clusterIdOverride = null)
    {
        var scope = _scopeResolver();
        if (scope is null || string.IsNullOrWhiteSpace(scope.Value.GalaxyId))
            return null;

        var clusterId = clusterIdOverride ?? scope.Value.ClusterId;
        return new MappingScopeKey(scope.Value.GalaxyId, clusterId);
    }

    private static ScopeState GetOrCreateScopeStateUnsafe(MappingScopeKey scopeKey)
    {
        if (!_scopeStates.TryGetValue(scopeKey, out var state))
        {
            state = new ScopeState();
            _scopeStates[scopeKey] = state;
        }

        return state;
    }

    private static void AppendDeltaUnsafe(ScopeState scopeState, WorldDeltaDto delta)
    {
        scopeState.Deltas.Add(new SequencedDelta(scopeState.NextSequence++, CloneWorldDelta(delta)));

        if (scopeState.Deltas.Count > MaxStoredDeltasPerScope)
        {
            var removeCount = scopeState.Deltas.Count - MaxStoredDeltasPerScope;
            scopeState.Deltas.RemoveRange(0, removeCount);
            scopeState.FirstSequence = scopeState.Deltas.Count == 0
                ? scopeState.NextSequence
                : scopeState.Deltas[0].Sequence;
        }
    }

    internal static UnitSnapshotDto? MapUnit(Unit unit, int clusterId)
    {
        var dto = new UnitSnapshotDto
        {
            UnitId = BuildUnitId(unit),
            ClusterId = clusterId,
            Kind = MapUnitKind(unit.Kind),
            FullStateKnown = unit.FullStateKnown,
            IsSolid = unit.IsSolid,
            X = unit.Position.X,
            Y = unit.Position.Y,
            MovementX = unit.Movement.X,
            MovementY = unit.Movement.Y,
            Angle = unit.Angle,
            Radius = unit.Radius,
            Gravity = unit.Gravity,
            SpeedLimit = unit.SpeedLimit > 0f ? unit.SpeedLimit : null,
            TeamName = unit.Team?.Name
        };

        PopulatePropulsionMetadata(dto, unit);
        dto.ScannedSubsystems = BuildScannedSubsystems(unit);

        if (unit.FullStateKnown && unit is Sun sun)
        {
            dto.SunEnergy = sun.Energy;
            dto.SunIons = sun.Ions;
            dto.SunNeutrinos = sun.Neutrinos;
            dto.SunHeat = sun.Heat;
            dto.SunDrain = sun.Drain;
        }

        if (unit.FullStateKnown && unit is Planet planet)
        {
            dto.PlanetMetal = planet.Metal;
            dto.PlanetCarbon = planet.Carbon;
            dto.PlanetHydrogen = planet.Hydrogen;
            dto.PlanetSilicon = planet.Silicon;
        }

        if (unit.FullStateKnown)
        {
            switch (unit)
            {
                case MissionTarget missionTarget:
                    dto.MissionTargetSequenceNumber = missionTarget.SequenceNumber;
                    dto.MissionTargetVectorCount = missionTarget.VectorCount;
                    dto.MissionTargetVectors = missionTarget.Vectors
                        .Select(vector => new TrajectoryPointDto { X = vector.X, Y = vector.Y })
                        .ToList();
                    break;

                case Flag flag:
                    dto.FlagActive = flag.Active;
                    dto.FlagGraceTicks = flag.GraceTicks;
                    break;

                case DominationPoint dominationPoint:
                    dto.DominationRadius = dominationPoint.DominationRadius;
                    dto.Domination = dominationPoint.Domination;
                    dto.DominationScoreCountdown = dominationPoint.ScoreCountdown;
                    break;

                case WormHole wormHole:
                    dto.WormHoleTargetClusterName = wormHole.TargetCluster?.Name;
                    dto.WormHoleTargetLeft = wormHole.TargetLeft;
                    dto.WormHoleTargetTop = wormHole.TargetTop;
                    dto.WormHoleTargetRight = wormHole.TargetRight;
                    dto.WormHoleTargetBottom = wormHole.TargetBottom;
                    break;

                case CurrentField currentField:
                    dto.CurrentFieldMode = currentField.Mode switch
                    {
                        CurrentFieldMode.Relative => "relative",
                        _ => "directional"
                    };
                    dto.CurrentFieldFlowX = currentField.Flow.X;
                    dto.CurrentFieldFlowY = currentField.Flow.Y;
                    dto.CurrentFieldRadialForce = currentField.RadialForce;
                    dto.CurrentFieldTangentialForce = currentField.TangentialForce;
                    break;

                case Nebula nebula:
                    dto.NebulaHue = nebula.Hue;
                    break;

                case Storm storm:
                    dto.StormSpawnChancePerTick = storm.SpawnChancePerTick;
                    dto.StormMinAnnouncementTicks = storm.MinAnnouncementTicks;
                    dto.StormMaxAnnouncementTicks = storm.MaxAnnouncementTicks;
                    dto.StormMinActiveTicks = storm.MinActiveTicks;
                    dto.StormMaxActiveTicks = storm.MaxActiveTicks;
                    dto.StormMinWhirlRadius = storm.MinWhirlRadius;
                    dto.StormMaxWhirlRadius = storm.MaxWhirlRadius;
                    dto.StormMinWhirlSpeed = storm.MinWhirlSpeed;
                    dto.StormMaxWhirlSpeed = storm.MaxWhirlSpeed;
                    dto.StormMinWhirlGravity = storm.MinWhirlGravity;
                    dto.StormMaxWhirlGravity = storm.MaxWhirlGravity;
                    dto.StormDamage = storm.Damage;
                    break;

                case StormCommencingWhirl commencingWhirl:
                    dto.StormWhirlRemainingTicks = commencingWhirl.RemainingTicks;
                    break;

                case StormActiveWhirl activeWhirl:
                    dto.StormWhirlRemainingTicks = activeWhirl.RemainingTicks;
                    dto.StormDamage = activeWhirl.Damage;
                    break;

                case PowerUp powerUp:
                    dto.PowerUpAmount = powerUp.Amount;
                    break;
            }
        }

        return dto;
    }

    private static void PopulatePropulsionMetadata(UnitSnapshotDto dto, Unit unit)
    {
        switch (unit)
        {
            case ClassicShipPlayerUnit classicShip when classicShip.Engine.Exists:
                dto.CurrentThrust = classicShip.Engine.Current.Length;
                dto.MaximumThrust = classicShip.Engine.Maximum > 0f ? classicShip.Engine.Maximum : null;
                dto.PropulsionPrediction = new PropulsionPredictionSnapshotDto
                {
                    Mode = PropulsionPredictionMode.DirectVector,
                    CurrentX = classicShip.Engine.Current.X,
                    CurrentY = classicShip.Engine.Current.Y,
                    TargetX = classicShip.Engine.Target.X,
                    TargetY = classicShip.Engine.Target.Y,
                    MaximumMagnitude = Math.Max(0f, classicShip.Engine.Maximum),
                    MaximumChangePerTick = 0f
                };
                break;

            case ModernShipPlayerUnit modernShip:
            {
                var hasEngine = false;
                var currentThrust = 0f;
                var maximumThrust = 0f;
                var maximumChange = 0f;
                var currentVectorX = 0f;
                var currentVectorY = 0f;
                var targetVectorX = 0f;
                var targetVectorY = 0f;
                var thrusters = new List<PropulsionThrusterSnapshotDto>(modernShip.Engines.Count);

                for (var engineIndex = 0; engineIndex < modernShip.Engines.Count; engineIndex++)
                {
                    var engine = modernShip.Engines[engineIndex];
                    if (!engine.Exists)
                        continue;

                    hasEngine = true;
                    currentThrust += MathF.Abs(engine.CurrentThrust);
                    maximumThrust += MathF.Max(0f, engine.MaximumThrust);
                    maximumChange += MathF.Max(0f, engine.MaximumThrustChangePerTick);

                    var worldAngleDegrees = ResolveModernThrusterWorldAngleDegrees(modernShip.Angle, engineIndex);
                    var radians = worldAngleDegrees * (MathF.PI / 180f);
                    var unitX = MathF.Cos(radians);
                    var unitY = MathF.Sin(radians);
                    currentVectorX += unitX * engine.CurrentThrust;
                    currentVectorY += unitY * engine.CurrentThrust;
                    targetVectorX += unitX * engine.TargetThrust;
                    targetVectorY += unitY * engine.TargetThrust;

                    thrusters.Add(new PropulsionThrusterSnapshotDto
                    {
                        WorldAngleDegrees = worldAngleDegrees,
                        CurrentThrust = engine.CurrentThrust,
                        TargetThrust = engine.TargetThrust,
                        MaximumThrust = MathF.Max(0f, engine.MaximumThrust),
                        MaximumThrustChangePerTick = MathF.Max(0f, engine.MaximumThrustChangePerTick)
                    });
                }

                if (!hasEngine)
                    break;

                dto.CurrentThrust = currentThrust;
                dto.MaximumThrust = maximumThrust > 0f ? maximumThrust : null;
                dto.PropulsionPrediction = new PropulsionPredictionSnapshotDto
                {
                    Mode = PropulsionPredictionMode.DirectionalThrusters,
                    CurrentX = currentVectorX,
                    CurrentY = currentVectorY,
                    TargetX = targetVectorX,
                    TargetY = targetVectorY,
                    MaximumMagnitude = Math.Max(0f, maximumThrust),
                    MaximumChangePerTick = Math.Max(0f, maximumChange),
                    Thrusters = thrusters
                };
                break;
            }
        }
    }

    private static List<UnitSnapshotDto> BuildMergedGalaxyUnitSnapshotsUnsafe(string galaxyId)
    {
        var mergedById = new Dictionary<string, UnitSnapshotDto>(StringComparer.Ordinal);
        foreach (var (scopeKey, scopeState) in _scopeStates)
        {
            if (!string.Equals(scopeKey.GalaxyId, galaxyId, StringComparison.Ordinal))
                continue;

            foreach (var unit in scopeState.Units.Values)
            {
                if (!mergedById.TryGetValue(unit.UnitId, out var existing))
                {
                    mergedById[unit.UnitId] = CloneUnitSnapshot(unit);
                    continue;
                }

                if (PreferCandidateSnapshot(existing, unit))
                    mergedById[unit.UnitId] = CloneUnitSnapshot(unit);
            }
        }

        return mergedById.Values.ToList();
    }

    private static bool PreferCandidateSnapshot(UnitSnapshotDto current, UnitSnapshotDto candidate)
    {
        if (candidate.LastSeenTick != current.LastSeenTick)
            return candidate.LastSeenTick > current.LastSeenTick;

        if (candidate.IsSeen != current.IsSeen)
            return candidate.IsSeen;

        if (candidate.FullStateKnown != current.FullStateKnown)
            return candidate.FullStateKnown;

        var candidateScore = ComputeSnapshotDetailScore(candidate);
        var currentScore = ComputeSnapshotDetailScore(current);
        if (candidateScore != currentScore)
            return candidateScore > currentScore;

        return false;
    }

    private static int ComputeSnapshotDetailScore(UnitSnapshotDto unit)
    {
        var score = 0;
        if (unit.PredictedTrajectory is { Count: > 0 })
            score += unit.PredictedTrajectory.Count;
        if (unit.ScannedSubsystems is { Count: > 0 })
            score += unit.ScannedSubsystems.Count * 2;
        if (unit.CurrentThrust.HasValue)
            score += 2;
        if (unit.MaximumThrust.HasValue)
            score += 2;
        if (unit.SunEnergy.HasValue || unit.SunIons.HasValue || unit.SunNeutrinos.HasValue)
            score += 2;
        if (unit.PlanetMetal.HasValue || unit.PlanetCarbon.HasValue || unit.PlanetHydrogen.HasValue || unit.PlanetSilicon.HasValue)
            score += 2;

        return score;
    }

    private static float ResolveModernThrusterWorldAngleDegrees(float shipAngle, int engineIndex)
    {
        var localAngle = engineIndex switch
        {
            0 => 0f,
            1 => 315f,
            2 => 270f,
            3 => 225f,
            4 => 180f,
            5 => 135f,
            6 => 90f,
            7 => 45f,
            _ => 0f
        };

        return NormalizeAngle(shipAngle + localAngle);
    }

    private static float NormalizeAngle(float angle)
    {
        angle %= 360f;
        if (angle < 0f)
            angle += 360f;

        return angle;
    }

    private static List<ScannedSubsystemDto>? BuildScannedSubsystems(Unit unit)
    {
        if (!unit.FullStateKnown || unit is not PlayerUnit playerUnit)
            return null;

        var subsystems = new List<ScannedSubsystemDto>();
        AddScannedSubsystem(subsystems, "Energy Battery", playerUnit.EnergyBattery.Exists, playerUnit.EnergyBattery.Status.ToString(), BuildBatteryStats(playerUnit.EnergyBattery));
        AddScannedSubsystem(subsystems, "Ion Battery", playerUnit.IonBattery.Exists, playerUnit.IonBattery.Status.ToString(), BuildBatteryStats(playerUnit.IonBattery));
        AddScannedSubsystem(subsystems, "Neutrino Battery", playerUnit.NeutrinoBattery.Exists, playerUnit.NeutrinoBattery.Status.ToString(), BuildBatteryStats(playerUnit.NeutrinoBattery));
        AddScannedSubsystem(subsystems, "Energy Cell", playerUnit.EnergyCell.Exists, playerUnit.EnergyCell.Status.ToString(), BuildEnergyCellStats(playerUnit.EnergyCell));
        AddScannedSubsystem(subsystems, "Ion Cell", playerUnit.IonCell.Exists, playerUnit.IonCell.Status.ToString(), BuildEnergyCellStats(playerUnit.IonCell));
        AddScannedSubsystem(subsystems, "Neutrino Cell", playerUnit.NeutrinoCell.Exists, playerUnit.NeutrinoCell.Status.ToString(), BuildEnergyCellStats(playerUnit.NeutrinoCell));
        AddScannedSubsystem(subsystems, "Hull", playerUnit.Hull.Exists, playerUnit.Hull.Status.ToString(), BuildHullStats(playerUnit.Hull));
        AddScannedSubsystem(subsystems, "Shield", playerUnit.Shield.Exists, playerUnit.Shield.Status.ToString(), BuildShieldStats(playerUnit.Shield));
        AddScannedSubsystem(subsystems, "Armor", playerUnit.Armor.Exists, playerUnit.Armor.Status.ToString(), BuildArmorStats(playerUnit.Armor));
        AddScannedSubsystem(subsystems, "Repair", playerUnit.Repair.Exists, playerUnit.Repair.Status.ToString(), BuildRepairStats(playerUnit.Repair));
        AddScannedSubsystem(subsystems, "Cargo", playerUnit.Cargo.Exists, playerUnit.Cargo.Status.ToString(), BuildCargoStats(playerUnit.Cargo));
        AddScannedSubsystem(subsystems, "Resource Miner", playerUnit.ResourceMiner.Exists, playerUnit.ResourceMiner.Status.ToString(), BuildResourceMinerStats(playerUnit.ResourceMiner));

        switch (playerUnit)
        {
            case ClassicShipPlayerUnit classic:
                AddScannedSubsystem(subsystems, "Nebula Collector", classic.NebulaCollector.Exists, classic.NebulaCollector.Status.ToString(), BuildNebulaCollectorStats(classic.NebulaCollector));
                AddScannedSubsystem(subsystems, "Engine", classic.Engine.Exists, classic.Engine.Status.ToString(), BuildClassicEngineStats(classic.Engine));
                AddScannedSubsystem(subsystems, "Main Scanner", classic.MainScanner.Exists, classic.MainScanner.Status.ToString(), BuildDynamicScannerStats(classic.MainScanner));
                AddScannedSubsystem(subsystems, "Secondary Scanner", classic.SecondaryScanner.Exists, classic.SecondaryScanner.Status.ToString(), BuildDynamicScannerStats(classic.SecondaryScanner));
                AddScannedSubsystem(subsystems, "Shot Magazine", classic.ShotMagazine.Exists, classic.ShotMagazine.Status.ToString(), BuildShotMagazineStats(classic.ShotMagazine));
                AddScannedSubsystem(subsystems, "Interceptor Magazine", classic.InterceptorMagazine.Exists, classic.InterceptorMagazine.Status.ToString(), BuildShotMagazineStats(classic.InterceptorMagazine));
                AddScannedSubsystem(subsystems, "Jump Drive", classic.JumpDrive.Exists, classic.JumpDrive.Exists ? "Available" : "Off", BuildJumpDriveStats(classic.JumpDrive));
                break;

            case ModernShipPlayerUnit modern:
                AddScannedSubsystem(subsystems, "Nebula Collector", modern.NebulaCollector.Exists, modern.NebulaCollector.Status.ToString(), BuildNebulaCollectorStats(modern.NebulaCollector));

                for (var index = 0; index < modern.Engines.Count; index++)
                {
                    var engine = modern.Engines[index];
                    AddScannedSubsystem(subsystems, $"Engine {GetModernSlotLabel(index)}", engine.Exists, engine.Status.ToString(), BuildModernEngineStats(engine));
                }

                for (var index = 0; index < modern.Scanners.Count; index++)
                {
                    var scanner = modern.Scanners[index];
                    AddScannedSubsystem(subsystems, $"Scanner {GetModernSlotLabel(index)}", scanner.Exists, scanner.Status.ToString(), BuildDynamicScannerStats(scanner));
                }

                for (var index = 0; index < modern.ShotMagazines.Count; index++)
                {
                    var magazine = modern.ShotMagazines[index];
                    AddScannedSubsystem(subsystems, $"Shot Magazine {GetModernSlotLabel(index)}", magazine.Exists, magazine.Status.ToString(), BuildShotMagazineStats(magazine));
                }

                AddScannedSubsystem(subsystems, "Jump Drive", modern.JumpDrive.Exists, modern.JumpDrive.Exists ? "Available" : "Off", BuildJumpDriveStats(modern.JumpDrive));
                break;
        }

        return subsystems.Count > 0 ? subsystems : null;
    }

    private static string GetModernSlotLabel(int index)
    {
        return index switch
        {
            0 => "N",
            1 => "NE",
            2 => "E",
            3 => "SE",
            4 => "S",
            5 => "SW",
            6 => "W",
            7 => "NW",
            _ => (index + 1).ToString(CultureInfo.InvariantCulture)
        };
    }

    private static void AddScannedSubsystem(
        List<ScannedSubsystemDto> subsystems,
        string name,
        bool exists,
        string status,
        List<ScannedSubsystemStatDto> stats)
    {
        if (!exists)
            return;

        subsystems.Add(new ScannedSubsystemDto
        {
            Id = name,
            Name = name,
            Exists = true,
            Status = status,
            Stats = stats
        });
    }

    private static List<ScannedSubsystemStatDto> BuildBatteryStats(BatterySubsystemInfo battery)
    {
        var stats = new List<ScannedSubsystemStatDto>();
        AddRatioStat(stats, "Charge", battery.Current, battery.Maximum);
        AddRateStat(stats, "Drain", battery.ConsumedThisTick);
        return stats;
    }

    private static List<ScannedSubsystemStatDto> BuildEnergyCellStats(EnergyCellSubsystemInfo cell)
    {
        var stats = new List<ScannedSubsystemStatDto>();
        AddStat(stats, "Efficiency", FormatFloat(cell.Efficiency));
        AddRateStat(stats, "Collected", cell.CollectedThisTick);
        return stats;
    }

    private static List<ScannedSubsystemStatDto> BuildHullStats(HullSubsystemInfo hull)
    {
        var stats = new List<ScannedSubsystemStatDto>();
        AddRatioStat(stats, "Integrity", hull.Current, hull.Maximum);
        return stats;
    }

    private static List<ScannedSubsystemStatDto> BuildShieldStats(ShieldSubsystemInfo shield)
    {
        var stats = new List<ScannedSubsystemStatDto>();
        AddRatioStat(stats, "Integrity", shield.Current, shield.Maximum);
        AddStat(stats, "Rate", $"{FormatFloat(shield.Rate)} per tick");
        AddStat(stats, "Mode", shield.Active ? "Active" : "Idle");
        return stats;
    }

    private static List<ScannedSubsystemStatDto> BuildArmorStats(ArmorSubsystemInfo armor)
    {
        var stats = new List<ScannedSubsystemStatDto>();
        AddStat(stats, "Reduction", FormatFloat(armor.Reduction));
        AddRateStat(stats, "Blocked", armor.BlockedDirectDamageThisTick + armor.BlockedRadiationDamageThisTick);
        return stats;
    }

    private static List<ScannedSubsystemStatDto> BuildRepairStats(RepairSubsystemInfo repair)
    {
        var stats = new List<ScannedSubsystemStatDto>();
        AddPerTickRatioStat(stats, "Rate", repair.Rate, repair.MaximumRate);
        AddRateStat(stats, "Repair", repair.RepairedHullThisTick);
        return stats;
    }

    private static List<ScannedSubsystemStatDto> BuildCargoStats(CargoSubsystemInfo cargo)
    {
        var stats = new List<ScannedSubsystemStatDto>();
        AddRatioStat(stats, "Metal", cargo.CurrentMetal, cargo.MaximumMetal);
        AddRatioStat(stats, "Carbon", cargo.CurrentCarbon, cargo.MaximumCarbon);
        AddRatioStat(stats, "Hydrogen", cargo.CurrentHydrogen, cargo.MaximumHydrogen);
        AddRatioStat(stats, "Silicon", cargo.CurrentSilicon, cargo.MaximumSilicon);
        AddRatioStat(stats, "Nebula", cargo.CurrentNebula, cargo.MaximumNebula);
        return stats;
    }

    private static List<ScannedSubsystemStatDto> BuildResourceMinerStats(ResourceMinerSubsystemInfo miner)
    {
        var stats = new List<ScannedSubsystemStatDto>();
        AddPerTickRatioStat(stats, "Rate", miner.Rate, miner.MaximumRate);
        AddRateStat(stats, "Yield", miner.MinedMetalThisTick + miner.MinedCarbonThisTick + miner.MinedHydrogenThisTick + miner.MinedSiliconThisTick);
        AddRateStat(stats, "Metal", miner.MinedMetalThisTick);
        AddRateStat(stats, "Carbon", miner.MinedCarbonThisTick);
        AddRateStat(stats, "Hydrogen", miner.MinedHydrogenThisTick);
        AddRateStat(stats, "Silicon", miner.MinedSiliconThisTick);
        return stats;
    }

    private static List<ScannedSubsystemStatDto> BuildNebulaCollectorStats(NebulaCollectorSubsystemInfo collector)
    {
        var stats = new List<ScannedSubsystemStatDto>();
        AddPerTickRatioStat(stats, "Rate", collector.Rate, collector.MaximumRate);
        AddRateStat(stats, "Yield", collector.CollectedThisTick);
        AddStat(stats, "Hue", FormatFloat(collector.CollectedHueThisTick));
        return stats;
    }

    private static List<ScannedSubsystemStatDto> BuildClassicEngineStats(ClassicShipEngineSubsystemInfo engine)
    {
        var stats = new List<ScannedSubsystemStatDto>();
        AddStat(stats, "Max impulse", FormatFloat(engine.Maximum));
        AddStat(stats, "Current", FormatFloat(engine.Current.Length));
        AddStat(stats, "Target", FormatFloat(engine.Target.Length));
        return stats;
    }

    private static List<ScannedSubsystemStatDto> BuildModernEngineStats(ModernShipEngineSubsystemInfo engine)
    {
        var stats = new List<ScannedSubsystemStatDto>();
        AddRatioStat(stats, "Thrust", engine.CurrentThrust, engine.MaximumThrust);
        AddStat(stats, "Target", FormatFloat(engine.TargetThrust));
        AddRateStat(stats, "Response", engine.MaximumThrustChangePerTick);
        return stats;
    }

    private static List<ScannedSubsystemStatDto> BuildDynamicScannerStats(DynamicScannerSubsystemInfo scanner)
    {
        var stats = new List<ScannedSubsystemStatDto>();
        AddRatioStat(stats, "Width", scanner.CurrentWidth, scanner.MaximumWidth, "deg");
        AddRatioStat(stats, "Length", scanner.CurrentLength, scanner.MaximumLength);
        AddStat(stats, "Angle", FormatFloat(scanner.CurrentAngle, "deg"));
        return stats;
    }

    private static List<ScannedSubsystemStatDto> BuildShotMagazineStats(DynamicShotMagazineSubsystemInfo magazine)
    {
        var stats = new List<ScannedSubsystemStatDto>();
        AddRatioStat(stats, "Shots", magazine.CurrentShots, magazine.MaximumShots);
        return stats;
    }

    private static List<ScannedSubsystemStatDto> BuildJumpDriveStats(JumpDriveSubsystemInfo jumpDrive)
    {
        var stats = new List<ScannedSubsystemStatDto>();
        AddStat(stats, "Jump cost", FormatFloat(jumpDrive.EnergyCost));
        return stats;
    }

    private static void AddRatioStat(List<ScannedSubsystemStatDto> stats, string label, float current, float maximum, string unit = "")
    {
        AddStat(stats, label, $"{FormatFloat(current, unit)} / {FormatFloat(maximum, unit)}");
    }

    private static void AddPerTickRatioStat(List<ScannedSubsystemStatDto> stats, string label, float current, float maximum)
    {
        AddStat(stats, label, $"{FormatFloat(current)} / {FormatFloat(maximum)} per tick");
    }

    private static void AddRateStat(List<ScannedSubsystemStatDto> stats, string label, float value)
    {
        AddStat(stats, label, $"{FormatFloat(value)}/tick");
    }

    private static void AddStat(List<ScannedSubsystemStatDto> stats, string label, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        stats.Add(new ScannedSubsystemStatDto
        {
            Label = label,
            Value = value
        });
    }

    private static string FormatFloat(float value, string unit = "")
    {
        var formatted = value.ToString("0.##", CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(unit))
            return formatted;

        return unit == "%" ? $"{formatted}{unit}" : $"{formatted} {unit}";
    }

    private static string BuildUnitId(Unit unit)
    {
        if (unit is PlayerUnit playerUnit)
            return BuildControllableUnitId(playerUnit.Player.Id, playerUnit.ControllableInfo.Id);

        return unit.Name;
    }

    private static string BuildControllableUnitId(int playerId, int controllableId)
    {
        return $"p{playerId}-c{controllableId}";
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
            { "fullStateKnown", unit.FullStateKnown },
            { "isStatic", unit.IsStatic },
            { "isSolid", unit.IsSolid ?? true },
            { "isSeen", unit.IsSeen },
            { "lastSeenTick", unit.LastSeenTick },
            { "x", unit.X },
            { "y", unit.Y },
            { "movementX", unit.MovementX },
            { "movementY", unit.MovementY },
            { "angle", unit.Angle },
            { "radius", unit.Radius },
            { "gravity", unit.Gravity },
            { "speedLimit", unit.SpeedLimit },
            { "currentThrust", unit.CurrentThrust },
            { "maximumThrust", unit.MaximumThrust },
            { "predictedTrajectory", unit.PredictedTrajectory?.Select(CloneTrajectoryPoint).ToArray() }
        };

        if (unit.TeamName is not null)
            changes["teamName"] = unit.TeamName;

        if (unit.ScannedSubsystems is not null)
            changes["scannedSubsystems"] = CloneScannedSubsystems(unit.ScannedSubsystems);

        if (unit.SunEnergy.HasValue)
            changes["sunEnergy"] = unit.SunEnergy;

        if (unit.SunIons.HasValue)
            changes["sunIons"] = unit.SunIons;

        if (unit.SunNeutrinos.HasValue)
            changes["sunNeutrinos"] = unit.SunNeutrinos;

        if (unit.SunHeat.HasValue)
            changes["sunHeat"] = unit.SunHeat;

        if (unit.SunDrain.HasValue)
            changes["sunDrain"] = unit.SunDrain;

        if (unit.PlanetMetal.HasValue)
            changes["planetMetal"] = unit.PlanetMetal;

        if (unit.PlanetCarbon.HasValue)
            changes["planetCarbon"] = unit.PlanetCarbon;

        if (unit.PlanetHydrogen.HasValue)
            changes["planetHydrogen"] = unit.PlanetHydrogen;

        if (unit.PlanetSilicon.HasValue)
            changes["planetSilicon"] = unit.PlanetSilicon;

        if (unit.MissionTargetSequenceNumber.HasValue)
            changes["missionTargetSequenceNumber"] = unit.MissionTargetSequenceNumber;

        if (unit.MissionTargetVectorCount.HasValue)
            changes["missionTargetVectorCount"] = unit.MissionTargetVectorCount;

        if (unit.MissionTargetVectors is not null)
            changes["missionTargetVectors"] = unit.MissionTargetVectors.Select(CloneTrajectoryPoint).ToArray();

        if (unit.FlagActive.HasValue)
            changes["flagActive"] = unit.FlagActive;

        if (unit.FlagGraceTicks.HasValue)
            changes["flagGraceTicks"] = unit.FlagGraceTicks;

        if (unit.DominationRadius.HasValue)
            changes["dominationRadius"] = unit.DominationRadius;

        if (unit.Domination.HasValue)
            changes["domination"] = unit.Domination;

        if (unit.DominationScoreCountdown.HasValue)
            changes["dominationScoreCountdown"] = unit.DominationScoreCountdown;

        if (unit.WormHoleTargetClusterName is not null)
            changes["wormHoleTargetClusterName"] = unit.WormHoleTargetClusterName;

        if (unit.WormHoleTargetLeft.HasValue)
            changes["wormHoleTargetLeft"] = unit.WormHoleTargetLeft;

        if (unit.WormHoleTargetTop.HasValue)
            changes["wormHoleTargetTop"] = unit.WormHoleTargetTop;

        if (unit.WormHoleTargetRight.HasValue)
            changes["wormHoleTargetRight"] = unit.WormHoleTargetRight;

        if (unit.WormHoleTargetBottom.HasValue)
            changes["wormHoleTargetBottom"] = unit.WormHoleTargetBottom;

        if (unit.CurrentFieldMode is not null)
            changes["currentFieldMode"] = unit.CurrentFieldMode;

        if (unit.CurrentFieldFlowX.HasValue)
            changes["currentFieldFlowX"] = unit.CurrentFieldFlowX;

        if (unit.CurrentFieldFlowY.HasValue)
            changes["currentFieldFlowY"] = unit.CurrentFieldFlowY;

        if (unit.CurrentFieldRadialForce.HasValue)
            changes["currentFieldRadialForce"] = unit.CurrentFieldRadialForce;

        if (unit.CurrentFieldTangentialForce.HasValue)
            changes["currentFieldTangentialForce"] = unit.CurrentFieldTangentialForce;

        if (unit.NebulaHue.HasValue)
            changes["nebulaHue"] = unit.NebulaHue;

        if (unit.StormSpawnChancePerTick.HasValue)
            changes["stormSpawnChancePerTick"] = unit.StormSpawnChancePerTick;

        if (unit.StormMinAnnouncementTicks.HasValue)
            changes["stormMinAnnouncementTicks"] = unit.StormMinAnnouncementTicks;

        if (unit.StormMaxAnnouncementTicks.HasValue)
            changes["stormMaxAnnouncementTicks"] = unit.StormMaxAnnouncementTicks;

        if (unit.StormMinActiveTicks.HasValue)
            changes["stormMinActiveTicks"] = unit.StormMinActiveTicks;

        if (unit.StormMaxActiveTicks.HasValue)
            changes["stormMaxActiveTicks"] = unit.StormMaxActiveTicks;

        if (unit.StormMinWhirlRadius.HasValue)
            changes["stormMinWhirlRadius"] = unit.StormMinWhirlRadius;

        if (unit.StormMaxWhirlRadius.HasValue)
            changes["stormMaxWhirlRadius"] = unit.StormMaxWhirlRadius;

        if (unit.StormMinWhirlSpeed.HasValue)
            changes["stormMinWhirlSpeed"] = unit.StormMinWhirlSpeed;

        if (unit.StormMaxWhirlSpeed.HasValue)
            changes["stormMaxWhirlSpeed"] = unit.StormMaxWhirlSpeed;

        if (unit.StormMinWhirlGravity.HasValue)
            changes["stormMinWhirlGravity"] = unit.StormMinWhirlGravity;

        if (unit.StormMaxWhirlGravity.HasValue)
            changes["stormMaxWhirlGravity"] = unit.StormMaxWhirlGravity;

        if (unit.StormDamage.HasValue)
            changes["stormDamage"] = unit.StormDamage;

        if (unit.StormWhirlRemainingTicks.HasValue)
            changes["stormWhirlRemainingTicks"] = unit.StormWhirlRemainingTicks;

        if (unit.PowerUpAmount.HasValue)
            changes["powerUpAmount"] = unit.PowerUpAmount;

        return changes;
    }

    private static void MergeKnownDetailState(UnitSnapshotDto current, UnitSnapshotDto? previous)
    {
        current.SunEnergy = MergeKnownIntelMetric(current.SunEnergy, previous?.SunEnergy);
        current.SunIons = MergeKnownIntelMetric(current.SunIons, previous?.SunIons);
        current.SunNeutrinos = MergeKnownIntelMetric(current.SunNeutrinos, previous?.SunNeutrinos);
        current.SunHeat = MergeKnownIntelMetric(current.SunHeat, previous?.SunHeat);
        current.SunDrain = MergeKnownIntelMetric(current.SunDrain, previous?.SunDrain);
        current.PlanetMetal = MergeKnownIntelMetric(current.PlanetMetal, previous?.PlanetMetal);
        current.PlanetCarbon = MergeKnownIntelMetric(current.PlanetCarbon, previous?.PlanetCarbon);
        current.PlanetHydrogen = MergeKnownIntelMetric(current.PlanetHydrogen, previous?.PlanetHydrogen);
        current.PlanetSilicon = MergeKnownIntelMetric(current.PlanetSilicon, previous?.PlanetSilicon);
        current.CurrentThrust = MergeKnownIntelMetric(current.CurrentThrust, previous?.CurrentThrust);
        current.MaximumThrust = MergeKnownIntelMetric(current.MaximumThrust, previous?.MaximumThrust);
        current.ScannedSubsystems = current.ScannedSubsystems is { Count: > 0 }
            ? CloneScannedSubsystems(current.ScannedSubsystems)
            : CloneScannedSubsystems(previous?.ScannedSubsystems);
        current.PropulsionPrediction = ClonePropulsionPrediction(current.PropulsionPrediction ?? previous?.PropulsionPrediction);
    }

    private static float? MergeKnownIntelMetric(float? currentValue, float? previousValue)
    {
        if (IsKnownIntelMetric(currentValue))
            return currentValue;

        return IsKnownIntelMetric(previousValue) ? previousValue : null;
    }

    private static bool IsKnownIntelMetric(float? value)
    {
        return value.HasValue && float.IsFinite(value.Value) && value.Value > 0f;
    }

    private static void RecordRecentTargetSnapshotUnsafe(string galaxyId, UnitSnapshotDto snapshot, uint currentTick)
    {
        if (string.IsNullOrWhiteSpace(galaxyId) ||
            string.IsNullOrWhiteSpace(snapshot.UnitId))
            return;

        var recentSnapshot = CloneUnitSnapshot(snapshot);
        recentSnapshot.IsStatic = false;
        recentSnapshot.IsSeen = false;

        var storedAtTick = currentTick > 0
            ? currentTick
            : recentSnapshot.LastSeenTick;

        if (!_recentTargetSnapshotsByGalaxy.TryGetValue(galaxyId, out var snapshotsById))
        {
            snapshotsById = new Dictionary<string, RecentTargetSnapshotEntry>(StringComparer.Ordinal);
            _recentTargetSnapshotsByGalaxy[galaxyId] = snapshotsById;
        }

        if (snapshotsById.TryGetValue(recentSnapshot.UnitId, out var existing) &&
            !PreferCandidateSnapshot(existing.Snapshot, recentSnapshot))
        {
            recentSnapshot = existing.Snapshot;
        }

        snapshotsById[recentSnapshot.UnitId] = new RecentTargetSnapshotEntry(CloneUnitSnapshot(recentSnapshot), storedAtTick);
    }

    private static bool TryGetRecentTargetSnapshotUnsafe(
        string galaxyId,
        string unitId,
        int clusterId,
        uint currentTick,
        out UnitSnapshotDto? snapshot)
    {
        snapshot = null;
        if (string.IsNullOrWhiteSpace(galaxyId) || string.IsNullOrWhiteSpace(unitId))
            return false;

        PruneRecentTargetSnapshotsUnsafe(galaxyId, currentTick);
        if (!_recentTargetSnapshotsByGalaxy.TryGetValue(galaxyId, out var snapshotsById))
            return false;

        if (!snapshotsById.TryGetValue(unitId, out var entry))
            return false;

        if (clusterId > 0 &&
            entry.Snapshot.ClusterId > 0 &&
            entry.Snapshot.ClusterId != clusterId)
        {
            return false;
        }

        snapshot = CloneUnitSnapshot(entry.Snapshot);
        return true;
    }

    private static void ForgetRecentTargetSnapshotUnsafe(string galaxyId, string unitId)
    {
        if (string.IsNullOrWhiteSpace(galaxyId) ||
            string.IsNullOrWhiteSpace(unitId) ||
            !_recentTargetSnapshotsByGalaxy.TryGetValue(galaxyId, out var snapshotsById))
            return;

        snapshotsById.Remove(unitId);
        if (snapshotsById.Count == 0)
            _recentTargetSnapshotsByGalaxy.Remove(galaxyId);
    }

    private static void PruneRecentTargetSnapshotsUnsafe(string galaxyId, uint currentTick)
    {
        if (string.IsNullOrWhiteSpace(galaxyId) ||
            !_recentTargetSnapshotsByGalaxy.TryGetValue(galaxyId, out var snapshotsById) ||
            currentTick == 0)
            return;

        var expiredUnitIds = snapshotsById
            .Where(pair => IsRecentTargetSnapshotExpired(currentTick, pair.Value.StoredAtTick))
            .Select(pair => pair.Key)
            .ToList();

        for (var index = 0; index < expiredUnitIds.Count; index++)
            snapshotsById.Remove(expiredUnitIds[index]);

        if (snapshotsById.Count == 0)
            _recentTargetSnapshotsByGalaxy.Remove(galaxyId);
    }

    private static bool IsRecentTargetSnapshotExpired(uint currentTick, uint storedAtTick)
    {
        return currentTick > storedAtTick &&
               currentTick - storedAtTick > RecentDynamicTargetRetentionTicks;
    }

    private static bool IsStaticUnit(Unit unit)
    {
        return unit is SteadyUnit;
    }

    private void HandleGalaxyTick(GalaxyTickEvent tickEvent)
    {
        var scopeKey = ResolveScopeKey();
        if (scopeKey is null)
            return;

        lock (_stateLock)
        {
            _latestTickByGalaxy[scopeKey.Value.GalaxyId] = tickEvent.Tick;
            EnsureGalaxyLoadedUnsafe(scopeKey.Value.GalaxyId);
            if (_scopeStates.TryGetValue(scopeKey.Value, out var scopeState))
                UpdateHiddenTrajectoryPredictionsUnsafe(scopeKey.Value, scopeState, tickEvent.Tick);

            PruneRecentTargetSnapshotsUnsafe(scopeKey.Value.GalaxyId, tickEvent.Tick);
        }
    }

    private static void UpdateHiddenTrajectoryPredictionsUnsafe(MappingScopeKey scopeKey, ScopeState scopeState, uint currentTick)
    {
        foreach (var unit in scopeState.Units.Values)
        {
            if (unit.ClusterId != scopeKey.ClusterId)
                continue;

            if (unit.IsSeen || unit.IsStatic || !TrajectoryPredictionService.SupportsHiddenTrajectory(unit))
            {
                if (unit.PredictedTrajectory is null)
                    continue;

                unit.PredictedTrajectory = null;
                AppendDeltaUnsafe(scopeState, new WorldDeltaDto
                {
                    EventType = "unit.updated",
                    EntityId = unit.UnitId,
                    Changes = UnitToChanges(unit)
                });
                continue;
            }

            var nextPrediction = BuildHiddenTrajectory(unit, scopeState.Units.Values, currentTick);
            if (TrajectoryMatches(unit.PredictedTrajectory, nextPrediction))
                continue;

            unit.PredictedTrajectory = nextPrediction;
            AppendDeltaUnsafe(scopeState, new WorldDeltaDto
            {
                EventType = "unit.updated",
                EntityId = unit.UnitId,
                Changes = UnitToChanges(unit)
            });
        }
    }

    internal static List<TrajectoryPointDto>? BuildHiddenTrajectory(
        UnitSnapshotDto unit,
        IEnumerable<UnitSnapshotDto> scopeUnits,
        uint currentTick)
    {
        return TrajectoryPredictionService.BuildHiddenTrajectory(
            unit,
            scopeUnits,
            currentTick,
            new TrajectoryPredictionService.PredictionOptions(
                HiddenTrajectoryLookaheadTicks,
                HiddenTrajectoryMaximumTicks,
                HiddenTrajectoryDownsample,
                HiddenTrajectoryMinimumPointDistance));
    }

    private static bool ShouldRemoveImmediatelyWhenUnseen(Unit unit)
    {
        return ShouldRemoveImmediatelyWhenUnseen(unit.Kind);
    }

    internal static bool ShouldRemoveImmediatelyWhenUnseen(UnitKind kind)
    {
        return kind is UnitKind.Explosion or UnitKind.InterceptorExplosion;
    }

    private static bool TrajectoryMatches(
        IReadOnlyList<TrajectoryPointDto>? left,
        IReadOnlyList<TrajectoryPointDto>? right)
    {
        if (ReferenceEquals(left, right))
            return true;

        if (left is null || right is null)
            return left is null && right is null;

        if (left.Count != right.Count)
            return false;

        for (var index = 0; index < left.Count; index++)
        {
            if (Math.Abs(left[index].X - right[index].X) > 0.01f ||
                Math.Abs(left[index].Y - right[index].Y) > 0.01f)
            {
                return false;
            }
        }

        return true;
    }

    private static uint GetCurrentTick(string galaxyId)
    {
        lock (_stateLock)
            return _latestTickByGalaxy.TryGetValue(galaxyId, out var tick) ? tick : 0;
    }

    private static void EnsurePersistenceConfigured()
    {
        if (_persistenceConfigured)
            return;

        lock (_stateLock)
        {
            if (_persistenceConfigured)
                return;

            ConfigurePersistencePathsUnsafe(null);
            _loadedGalaxyIds.Clear();
            _persistenceConfigured = true;
        }
    }

    private static bool RemoveLegacyPlayerUnitIdUnsafe(ScopeState scopeState, string canonicalUnitId, string legacyUnitId)
    {
        if (string.IsNullOrWhiteSpace(canonicalUnitId) ||
            string.IsNullOrWhiteSpace(legacyUnitId) ||
            string.Equals(canonicalUnitId, legacyUnitId, StringComparison.Ordinal))
            return false;

        if (!scopeState.Units.TryGetValue(legacyUnitId, out var legacy))
            return false;

        scopeState.Units.Remove(legacyUnitId);
        scopeState.Units[canonicalUnitId] = legacy;
        scopeState.StaticUnitIds.Remove(legacyUnitId);
        scopeState.StaticUnitIds.Remove(canonicalUnitId);

        AppendDeltaUnsafe(scopeState, new WorldDeltaDto
        {
            EventType = "unit.removed",
            EntityId = legacyUnitId
        });

        return true;
    }

    private static void ConfigurePersistencePathsUnsafe(string? configuredPath)
    {
        var baseFile = ResolveWorldStateFilePath(configuredPath);
        var directory = Path.GetDirectoryName(baseFile);

        _worldStateBaseFilePath = baseFile;
        _worldStateDirectory = string.IsNullOrWhiteSpace(directory) ? AppContext.BaseDirectory : directory;
        _worldStateFileStem = Path.GetFileNameWithoutExtension(baseFile);
        if (string.IsNullOrWhiteSpace(_worldStateFileStem))
            _worldStateFileStem = "world-state";

        _worldStateFileExtension = Path.GetExtension(baseFile);
        if (string.IsNullOrWhiteSpace(_worldStateFileExtension))
            _worldStateFileExtension = ".json";
    }

    private static string ResolveWorldStateFilePath(string? configuredPath)
    {
        var candidate = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(AppContext.BaseDirectory, "world-state.json")
            : configuredPath!;

        return Path.GetFullPath(candidate);
    }

    private static void EnsureGalaxyLoadedUnsafe(string galaxyId)
    {
        if (_loadedGalaxyIds.Contains(galaxyId))
            return;

        LoadPersistedGalaxyUnsafe(galaxyId);
        _loadedGalaxyIds.Add(galaxyId);
    }

    private static void LoadPersistedGalaxyUnsafe(string galaxyId)
    {
        var specificPath = GetGalaxyStateFilePathUnsafe(galaxyId);

        if (File.Exists(specificPath))
        {
            TryLoadFileIntoScopesUnsafe(specificPath, galaxyId, allowFallbackStaticUnits: false);
            return;
        }

        // Backward compatibility: if an old/shared file exists, import matching data for this galaxy.
        if (!string.IsNullOrWhiteSpace(_worldStateBaseFilePath) && File.Exists(_worldStateBaseFilePath))
            TryLoadFileIntoScopesUnsafe(_worldStateBaseFilePath, galaxyId, allowFallbackStaticUnits: true);
    }

    private static void TryLoadFileIntoScopesUnsafe(string path, string galaxyId, bool allowFallbackStaticUnits)
    {
        try
        {
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
                return;

            var stateFile = JsonSerializer.Deserialize<WorldStateFile>(json, _jsonOptions);
            if (stateFile is null)
                return;

            if (stateFile.Scopes is { Count: > 0 })
            {
                foreach (var scope in stateFile.Scopes)
                    LoadScopeFromPersistenceUnsafe(galaxyId, scope);
            }
            else if (allowFallbackStaticUnits && stateFile.StaticUnits is { Count: > 0 })
            {
                var fallbackScope = new PersistedScopeDto
                {
                    ClusterId = 0,
                    StaticUnits = stateFile.StaticUnits
                };
                LoadScopeFromPersistenceUnsafe(galaxyId, fallbackScope);
            }
        }
        catch
        {
            // Keep running with in-memory state if persisted state is malformed.
        }
    }

    private static void LoadScopeFromPersistenceUnsafe(string targetGalaxyId, PersistedScopeDto scope)
    {
        if (!string.IsNullOrWhiteSpace(scope.GalaxyId) && !string.Equals(scope.GalaxyId, targetGalaxyId, StringComparison.Ordinal))
            return;

        if (scope.StaticUnits is null || scope.StaticUnits.Count == 0)
            return;

        var key = new MappingScopeKey(targetGalaxyId, scope.ClusterId);
        var scopeState = GetOrCreateScopeStateUnsafe(key);

        foreach (var unit in scope.StaticUnits)
        {
            if (string.IsNullOrWhiteSpace(unit.UnitId))
                continue;

            unit.IsStatic = true;
            unit.IsSeen = true;
            scopeState.Units[unit.UnitId] = CloneUnitSnapshot(unit);
            scopeState.StaticUnitIds.Add(unit.UnitId);
        }
    }

    private static void SavePersistedGalaxyUnsafe(string galaxyId)
    {
        if (string.IsNullOrWhiteSpace(_worldStateDirectory) || string.IsNullOrWhiteSpace(_worldStateFileStem) || string.IsNullOrWhiteSpace(_worldStateFileExtension))
            return;

        try
        {
            Directory.CreateDirectory(_worldStateDirectory);

            var persistedScopes = new List<PersistedScopeDto>();
            foreach (var (scopeKey, scopeState) in _scopeStates)
            {
                if (!string.Equals(scopeKey.GalaxyId, galaxyId, StringComparison.Ordinal))
                    continue;

                if (scopeState.StaticUnitIds.Count == 0)
                    continue;

                var staticUnits = new List<UnitSnapshotDto>();
                foreach (var staticUnitId in scopeState.StaticUnitIds)
                {
                    if (scopeState.Units.TryGetValue(staticUnitId, out var unit))
                        staticUnits.Add(CloneUnitSnapshot(unit));
                }

                if (staticUnits.Count == 0)
                    continue;

                persistedScopes.Add(new PersistedScopeDto
                {
                    GalaxyId = galaxyId,
                    ClusterId = scopeKey.ClusterId,
                    StaticUnits = staticUnits
                });
            }

            var stateFile = new WorldStateFile { Scopes = persistedScopes };
            var json = JsonSerializer.Serialize(stateFile, _jsonOptions);
            var path = GetGalaxyStateFilePathUnsafe(galaxyId);
            var temporaryPath = $"{path}.tmp";

            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch
        {
            // Non-fatal by design; state stays available in memory.
        }
    }

    private static string GetGalaxyStateFilePathUnsafe(string galaxyId)
    {
        var slug = BuildSafeGalaxySlug(galaxyId);
        var hash = BuildGalaxyHash(galaxyId);
        var fileName = $"{_worldStateFileStem}.{slug}.{hash}{_worldStateFileExtension}";
        return Path.Combine(_worldStateDirectory!, fileName);
    }

    private static string BuildSafeGalaxySlug(string galaxyId)
    {
        var chars = new List<char>(galaxyId.Length);
        var lastWasDash = false;

        foreach (var c in galaxyId)
        {
            var normalized = char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-';
            if (normalized == '-')
            {
                if (lastWasDash)
                    continue;

                lastWasDash = true;
                chars.Add('-');
                continue;
            }

            lastWasDash = false;
            chars.Add(normalized);
        }

        var slug = new string(chars.ToArray()).Trim('-');
        if (string.IsNullOrWhiteSpace(slug))
            slug = "galaxy";

        const int maxLength = 40;
        if (slug.Length > maxLength)
            slug = slug[..maxLength].Trim('-');

        return string.IsNullOrWhiteSpace(slug) ? "galaxy" : slug;
    }

    private static string BuildGalaxyHash(string galaxyId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(galaxyId));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..16];
    }

    private static UnitSnapshotDto CloneUnitSnapshot(UnitSnapshotDto source)
    {
        return new UnitSnapshotDto
        {
            UnitId = source.UnitId,
            ClusterId = source.ClusterId,
            Kind = source.Kind,
            FullStateKnown = source.FullStateKnown,
            IsStatic = source.IsStatic,
            IsSolid = source.IsSolid,
            IsSeen = source.IsSeen,
            LastSeenTick = source.LastSeenTick,
            X = source.X,
            Y = source.Y,
            MovementX = source.MovementX,
            MovementY = source.MovementY,
            Angle = source.Angle,
            Radius = source.Radius,
            Gravity = source.Gravity,
            SpeedLimit = source.SpeedLimit,
            CurrentThrust = source.CurrentThrust,
            MaximumThrust = source.MaximumThrust,
            PredictedTrajectory = source.PredictedTrajectory?.Select(CloneTrajectoryPoint).ToList(),
            PropulsionPrediction = ClonePropulsionPrediction(source.PropulsionPrediction),
            TeamName = source.TeamName,
            ScannedSubsystems = CloneScannedSubsystems(source.ScannedSubsystems),
            SunEnergy = source.SunEnergy,
            SunIons = source.SunIons,
            SunNeutrinos = source.SunNeutrinos,
            SunHeat = source.SunHeat,
            SunDrain = source.SunDrain,
            PlanetMetal = source.PlanetMetal,
            PlanetCarbon = source.PlanetCarbon,
            PlanetHydrogen = source.PlanetHydrogen,
            PlanetSilicon = source.PlanetSilicon,
            MissionTargetSequenceNumber = source.MissionTargetSequenceNumber,
            MissionTargetVectorCount = source.MissionTargetVectorCount,
            MissionTargetVectors = source.MissionTargetVectors?.Select(CloneTrajectoryPoint).ToList(),
            FlagActive = source.FlagActive,
            FlagGraceTicks = source.FlagGraceTicks,
            DominationRadius = source.DominationRadius,
            Domination = source.Domination,
            DominationScoreCountdown = source.DominationScoreCountdown,
            WormHoleTargetClusterName = source.WormHoleTargetClusterName,
            WormHoleTargetLeft = source.WormHoleTargetLeft,
            WormHoleTargetTop = source.WormHoleTargetTop,
            WormHoleTargetRight = source.WormHoleTargetRight,
            WormHoleTargetBottom = source.WormHoleTargetBottom,
            CurrentFieldMode = source.CurrentFieldMode,
            CurrentFieldFlowX = source.CurrentFieldFlowX,
            CurrentFieldFlowY = source.CurrentFieldFlowY,
            CurrentFieldRadialForce = source.CurrentFieldRadialForce,
            CurrentFieldTangentialForce = source.CurrentFieldTangentialForce,
            NebulaHue = source.NebulaHue,
            StormSpawnChancePerTick = source.StormSpawnChancePerTick,
            StormMinAnnouncementTicks = source.StormMinAnnouncementTicks,
            StormMaxAnnouncementTicks = source.StormMaxAnnouncementTicks,
            StormMinActiveTicks = source.StormMinActiveTicks,
            StormMaxActiveTicks = source.StormMaxActiveTicks,
            StormMinWhirlRadius = source.StormMinWhirlRadius,
            StormMaxWhirlRadius = source.StormMaxWhirlRadius,
            StormMinWhirlSpeed = source.StormMinWhirlSpeed,
            StormMaxWhirlSpeed = source.StormMaxWhirlSpeed,
            StormMinWhirlGravity = source.StormMinWhirlGravity,
            StormMaxWhirlGravity = source.StormMaxWhirlGravity,
            StormDamage = source.StormDamage,
            StormWhirlRemainingTicks = source.StormWhirlRemainingTicks,
            PowerUpAmount = source.PowerUpAmount
        };
    }

    private static bool UnitSnapshotsEqual(UnitSnapshotDto left, UnitSnapshotDto right)
    {
        return string.Equals(CreateUnitSnapshotSignature(left), CreateUnitSnapshotSignature(right), StringComparison.Ordinal);
    }

    private static string CreateUnitSnapshotSignature(UnitSnapshotDto unit)
    {
        return JsonSerializer.Serialize(UnitToChanges(unit), _jsonOptions);
    }

    private static List<ScannedSubsystemDto>? CloneScannedSubsystems(IReadOnlyList<ScannedSubsystemDto>? source)
    {
        if (source is null)
            return null;

        return source
            .Select(subsystem => new ScannedSubsystemDto
            {
                Id = subsystem.Id,
                Name = subsystem.Name,
                Exists = subsystem.Exists,
                Status = subsystem.Status,
                Stats = subsystem.Stats
                    .Select(stat => new ScannedSubsystemStatDto
                    {
                        Label = stat.Label,
                        Value = stat.Value
                    })
                    .ToList()
            })
            .ToList();
    }

    private static PropulsionPredictionSnapshotDto? ClonePropulsionPrediction(PropulsionPredictionSnapshotDto? source)
    {
        if (source is null)
            return null;

        return new PropulsionPredictionSnapshotDto
        {
            Mode = source.Mode,
            CurrentX = source.CurrentX,
            CurrentY = source.CurrentY,
            TargetX = source.TargetX,
            TargetY = source.TargetY,
            MaximumMagnitude = source.MaximumMagnitude,
            MaximumChangePerTick = source.MaximumChangePerTick,
            Thrusters = source.Thrusters?
                .Select(thruster => new PropulsionThrusterSnapshotDto
                {
                    WorldAngleDegrees = thruster.WorldAngleDegrees,
                    CurrentThrust = thruster.CurrentThrust,
                    TargetThrust = thruster.TargetThrust,
                    MaximumThrust = thruster.MaximumThrust,
                    MaximumThrustChangePerTick = thruster.MaximumThrustChangePerTick
                })
                .ToList()
        };
    }

    private static TrajectoryPointDto CloneTrajectoryPoint(TrajectoryPointDto source)
    {
        return new TrajectoryPointDto
        {
            X = source.X,
            Y = source.Y
        };
    }

    private static WorldDeltaDto CloneWorldDelta(WorldDeltaDto source)
    {
        var changes = source.Changes is null
            ? null
            : new Dictionary<string, object?>(source.Changes);

        return new WorldDeltaDto
        {
            EventType = source.EventType,
            EntityId = source.EntityId,
            Changes = changes
        };
    }

    public readonly record struct MappingScopeContext(string GalaxyId, int ClusterId, int? LocalPlayerId);

    private readonly record struct MappingScopeKey(string GalaxyId, int ClusterId);

    private sealed class SequencedDelta
    {
        public SequencedDelta(long sequence, WorldDeltaDto delta)
        {
            Sequence = sequence;
            Delta = delta;
        }

        public long Sequence { get; }
        public WorldDeltaDto Delta { get; }
    }

    private sealed class ScopeState
    {
        public Dictionary<string, UnitSnapshotDto> Units { get; } = new();
        public HashSet<string> StaticUnitIds { get; } = new();
        public List<SequencedDelta> Deltas { get; } = new();
        public long NextSequence { get; set; } = 1;
        public long FirstSequence { get; set; } = 1;
    }

    private sealed class RecentTargetSnapshotEntry
    {
        public RecentTargetSnapshotEntry(UnitSnapshotDto snapshot, uint storedAtTick)
        {
            Snapshot = snapshot;
            StoredAtTick = storedAtTick;
        }

        public UnitSnapshotDto Snapshot { get; }
        public uint StoredAtTick { get; }
    }

    private readonly record struct PredictionObstacle(float X, float Y, float Radius);

    private sealed class WorldStateFile
    {
        public List<PersistedScopeDto> Scopes { get; set; } = new();

        // Backward compatibility with very old single-scope persistence format.
        public List<UnitSnapshotDto>? StaticUnits { get; set; }
    }

    private sealed class PersistedScopeDto
    {
        public string? GalaxyId { get; set; }
        public int ClusterId { get; set; }
        public List<UnitSnapshotDto> StaticUnits { get; set; } = new();
    }
}

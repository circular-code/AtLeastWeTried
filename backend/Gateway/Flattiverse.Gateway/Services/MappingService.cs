using Flattiverse.Connector.Events;
using Flattiverse.Connector.Units;
using Flattiverse.Gateway.Connector;
using Flattiverse.Gateway.Protocol.Dtos;
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

    // Mapped units are shared across sessions by scope (Galaxy + Cluster).
    private static readonly Dictionary<MappingScopeKey, ScopeState> _scopeStates = new();
    private static readonly object _stateLock = new();

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
        }
    }

    private bool ShouldIgnoreLifecycleEvent(Unit unit)
    {
        if (unit.Kind is not (UnitKind.ClassicShipPlayerUnit or UnitKind.ModernShipPlayerUnit))
            return false;

        var scope = _scopeResolver();
        if (scope is null || string.IsNullOrWhiteSpace(scope.Value.FriendlyTeamName))
            return false;

        return string.Equals(unit.Team?.Name, scope.Value.FriendlyTeamName, StringComparison.Ordinal);
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
    /// Try to resolve one unit snapshot in the current mapping scope.
    /// </summary>
    public bool TryGetUnitSnapshot(string unitId, out UnitSnapshotDto? snapshot)
    {
        snapshot = null;
        if (string.IsNullOrWhiteSpace(unitId))
            return false;

        EnsurePersistenceConfigured();
        var scopeKey = ResolveScopeKey();
        if (scopeKey is null)
            return false;

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

        var unitId = unit.Name;

        lock (_stateLock)
        {
            EnsureGalaxyLoadedUnsafe(scopeKey.Value.GalaxyId);

            var scopeState = GetOrCreateScopeStateUnsafe(scopeKey.Value);
            if (!scopeState.Units.TryGetValue(unitId, out var existing))
            {
                var mapped = MapUnit(unit, clusterId);
                if (mapped is null)
                    return;

                existing = mapped;
                existing.LastSeenTick = GetCurrentTick(scopeKey.Value.GalaxyId);
                scopeState.Units[unitId] = existing;
            }

            existing.IsStatic = false;
            existing.IsSeen = false;

            AppendDeltaUnsafe(scopeState, new WorldDeltaDto
            {
                EventType = "unit.updated",
                EntityId = unitId,
                Changes = UnitToChanges(existing)
            });
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
            UnitId = unit.Name,
            ClusterId = clusterId,
            Kind = MapUnitKind(unit.Kind),
            FullStateKnown = unit.FullStateKnown,
            IsSolid = unit.IsSolid,
            X = unit.Position.X,
            Y = unit.Position.Y,
            Angle = unit.Angle,
            Radius = unit.Radius,
            Gravity = unit.Gravity,
            TeamName = unit.Team?.Name
        };

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
            { "fullStateKnown", unit.FullStateKnown },
            { "isStatic", unit.IsStatic },
            { "isSolid", unit.IsSolid ?? true },
            { "isSeen", unit.IsSeen },
            { "lastSeenTick", unit.LastSeenTick },
            { "x", unit.X },
            { "y", unit.Y },
            { "angle", unit.Angle },
            { "radius", unit.Radius },
            { "gravity", unit.Gravity }
        };

        if (unit.TeamName is not null)
            changes["teamName"] = unit.TeamName;

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

        return changes;
    }

    private static void MergeKnownDetailState(UnitSnapshotDto current, UnitSnapshotDto? previous)
    {
        if (previous is null)
            return;

        if (current.FullStateKnown)
            return;

        current.SunEnergy = previous.SunEnergy;
        current.SunIons = previous.SunIons;
        current.SunNeutrinos = previous.SunNeutrinos;
        current.SunHeat = previous.SunHeat;
        current.SunDrain = previous.SunDrain;
        current.PlanetMetal = previous.PlanetMetal;
        current.PlanetCarbon = previous.PlanetCarbon;
        current.PlanetHydrogen = previous.PlanetHydrogen;
        current.PlanetSilicon = previous.PlanetSilicon;
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
            _latestTickByGalaxy[scopeKey.Value.GalaxyId] = tickEvent.Tick;
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
            Angle = source.Angle,
            Radius = source.Radius,
            Gravity = source.Gravity,
            TeamName = source.TeamName,
            SunEnergy = source.SunEnergy,
            SunIons = source.SunIons,
            SunNeutrinos = source.SunNeutrinos,
            SunHeat = source.SunHeat,
            SunDrain = source.SunDrain,
            PlanetMetal = source.PlanetMetal,
            PlanetCarbon = source.PlanetCarbon,
            PlanetHydrogen = source.PlanetHydrogen,
            PlanetSilicon = source.PlanetSilicon
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

    public readonly record struct MappingScopeContext(string GalaxyId, int ClusterId, string? FriendlyTeamName);

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

using System.Globalization;
using System.Text;
using Flattiverse.Connector;
using Flattiverse.Connector.Events;
using Flattiverse.Connector.GalaxyHierarchy;
using Flattiverse.Connector.Units;
using Flattiverse.Gateway.Connector;
using static Flattiverse.Gateway.Services.GravitySimulator;

namespace Flattiverse.Gateway.Services;

/// <summary>
/// Per-player-session service that stores tactical intent (mode + target) for each controllable,
/// evaluates auto-fire opportunities on every galaxy tick, and exposes an API for burst fire,
/// manual point shots, and UI overlay data.
/// </summary>
public sealed class TacticalService : IConnectorEventHandler
{
    private const uint AutoFireCooldownTicks = 2;
    private const float DefaultShotLoad = 12f;
    private const float DefaultShotDamage = 8f;
    private const float SpawnPaddingDistance = 2f;
    private const double DefaultSpeedLimit = 6.0d;
    private const float ShotRadius = 1f;
    private const int BallisticMaxIterations = 120;
    private const int GravityCompensationIterations = 8;
    private const int LinearFallbackRefinementIterations = 3;
    private const float UncertainPredictionThresholdScale = 0.65f;
    private const float UncertainPredictionThresholdFloor = 0.85f;
    private const float FallbackSelectionEpsilon = 0.05f;

    private const string TraceEnvVar = "FV_TACTICAL_TRACE";
    private static readonly bool TraceEnabled = string.Equals(Environment.GetEnvironmentVariable(TraceEnvVar), "1", StringComparison.Ordinal);
    private static readonly string TraceDirectory = Path.Combine(AppContext.BaseDirectory, "data", "tactical-traces");

    public enum TacticalMode
    {
        Off,
        Enemy,
        Target,
        Scan
    }

    public readonly record struct AutoFireRequest(
        string ControllableId,
        ClassicShipControllable Ship,
        string TargetReference,
        Vector RelativeMovement,
        ushort Ticks,
        float Load,
        float Damage,
        float PredictedMissDistance,
        uint Tick);

    private sealed class TacticalState
    {
        public TacticalMode Mode { get; set; } = TacticalMode.Off;
        public string? TargetId { get; set; }
        public ClassicShipControllable? Ship { get; set; }
        public uint LastFireTick { get; set; }
        public bool HasLastFireTick { get; set; }
    }

    private readonly object _sync = new();
    private readonly Dictionary<string, TacticalState> _states = new();
    private readonly List<AutoFireRequest> _pendingAutoFire = new();
    private readonly Func<ClassicShipControllable, IReadOnlyList<GravitySource>> _gravitySourceResolver;

    public TacticalService(Func<ClassicShipControllable, IReadOnlyList<GravitySource>>? gravitySourceResolver = null)
    {
        _gravitySourceResolver = gravitySourceResolver ?? BuildGravitySourcesFromCluster;
    }

    public void Handle(FlattiverseEvent @event)
    {
        lock (_sync)
        {
            switch (@event)
            {
                case DestroyedControllableInfoEvent destroyed:
                {
                    var id = UnitIdentity.BuildControllableId(destroyed.Player.Id, destroyed.ControllableInfo.Id);
                    if (_states.TryGetValue(id, out var state))
                        state.Ship = null;
                    _pendingAutoFire.RemoveAll(r => r.ControllableId == id);
                    break;
                }

                case ClosedControllableInfoEvent closed:
                {
                    var id = UnitIdentity.BuildControllableId(closed.Player.Id, closed.ControllableInfo.Id);
                    _states.Remove(id);
                    _pendingAutoFire.RemoveAll(r => r.ControllableId == id);
                    break;
                }

                case GalaxyTickEvent tick:
                    EvaluateAutoFire(tick.Tick);
                    break;
            }
        }
    }

    public void AttachControllable(string id, ClassicShipControllable ship)
    {
        lock (_sync)
        {
            if (!_states.TryGetValue(id, out var state))
            {
                state = new TacticalState();
                _states[id] = state;
            }

            state.Ship = ship;
        }
    }

    public List<AutoFireRequest> DequeuePendingAutoFireRequests()
    {
        lock (_sync)
        {
            if (_pendingAutoFire.Count == 0)
                return new List<AutoFireRequest>();

            var result = new List<AutoFireRequest>(_pendingAutoFire);
            _pendingAutoFire.Clear();
            return result;
        }
    }

    public void SetMode(string id, TacticalMode mode)
    {
        lock (_sync)
        {
            if (!_states.TryGetValue(id, out var state))
            {
                state = new TacticalState();
                _states[id] = state;
            }

            var previousMode = state.Mode;
            state.Mode = mode;

            if ((mode == TacticalMode.Target && previousMode != TacticalMode.Target) ||
                (mode == TacticalMode.Scan && previousMode != TacticalMode.Scan) ||
                (mode != TacticalMode.Target && mode != TacticalMode.Scan))
            {
                state.TargetId = null;
            }
        }
    }

    public void SetTarget(string id, string targetId)
    {
        lock (_sync)
        {
            if (!_states.TryGetValue(id, out var state))
            {
                state = new TacticalState();
                _states[id] = state;
            }

            state.TargetId = state.Ship is null
                ? targetId
                : UnitIdentity.NormalizeUnitId(targetId, state.Ship.Cluster?.Id ?? 0);
        }
    }

    public void ClearTarget(string id)
    {
        lock (_sync)
        {
            if (_states.TryGetValue(id, out var state))
                state.TargetId = null;
        }
    }

    public bool IsTargetAllowedForTargetMode(ClassicShipControllable ship, string targetId)
    {
        if (!UnitIdentity.TryParseControllableId(targetId, out var targetPlayerId, out _))
            return true; // Non-player units are always allowed.

        var galaxy = ship.Cluster?.Galaxy;
        if (galaxy is null)
            return true;

        var myTeam = galaxy.Player?.Team;
        if (myTeam is null)
            return true; // No team → no friendly fire restriction.

        try
        {
            var targetPlayer = galaxy.Players[targetPlayerId];
            return targetPlayer.Team is null || targetPlayer.Team.Id != myTeam.Id;
        }
        catch
        {
            return true; // Player not found — allow.
        }
    }

    public bool ShouldAutoFire(string id, uint tick, out AutoFireRequest request)
    {
        lock (_sync)
        {
            return ShouldAutoFireCore(id, tick, enforceCooldown: true, requireTargetMode: false, out request);
        }
    }

    public bool TryBuildTargetBurstRequest(string id, uint tick, out AutoFireRequest request)
    {
        lock (_sync)
        {
            return ShouldAutoFireCore(id, tick, enforceCooldown: false, requireTargetMode: true, out request);
        }
    }

    public bool TryBuildPointShotRequest(string id, ClassicShipControllable ship, float targetX, float targetY, uint tick, out AutoFireRequest request)
    {
        request = default;

        if (!ship.Active || !ship.Alive)
            return false;

        if (ControllableRebuildState.IsRebuilding(ship))
            return false;

        if (!ship.ShotLauncher.Exists || !ship.ShotMagazine.Exists)
            return false;

        if (ship.ShotLauncher.Status == SubsystemStatus.Upgrading || ship.ShotMagazine.Status == SubsystemStatus.Upgrading)
            return false;

        if (ship.ShotMagazine.CurrentShots < 1f)
            return false;

        var gravitySources = BuildGravitySources(ship);

        if (!TryBuildBestShotSolution(
                ship, targetX, targetY, 0f, 0f, 0f, gravitySources,
                out var relMovement, out var shotTicks, out var shotLoad, out var shotDamage, out var missDistance))
            return false;

        request = new AutoFireRequest(id, ship, $"point({targetX},{targetY})", relMovement, shotTicks, shotLoad, shotDamage, missDistance, tick);
        return true;
    }

    public void RegisterSuccessfulFire(string id, uint tick)
    {
        lock (_sync)
        {
            if (_states.TryGetValue(id, out var state))
            {
                state.LastFireTick = tick;
                state.HasLastFireTick = true;
            }
        }
    }

    public Dictionary<string, object?> BuildOverlay(string id)
    {
        lock (_sync)
        {
            if (!_states.TryGetValue(id, out var state))
            {
                return new Dictionary<string, object?>
                {
                    { "mode", "off" },
                    { "targetId", null },
                    { "hasTarget", false }
                };
            }

            return new Dictionary<string, object?>
            {
                { "mode", state.Mode switch { TacticalMode.Enemy => "enemy", TacticalMode.Target => "target", TacticalMode.Scan => "scan", _ => "off" } },
                { "targetId", state.TargetId },
                { "hasTarget", state.TargetId is not null }
            };
        }
    }

    public bool TryGetScanLock(string id, out string? targetId)
    {
        lock (_sync)
        {
            if (_states.TryGetValue(id, out var state) && state.Mode == TacticalMode.Scan)
            {
                targetId = state.TargetId;
                return true;
            }

            targetId = null;
            return false;
        }
    }

    public void Remove(string id)
    {
        lock (_sync)
        {
            _states.Remove(id);
            _pendingAutoFire.RemoveAll(r => r.ControllableId == id);
        }
    }

    // --- Private implementation ---

    private void EvaluateAutoFire(uint tick)
    {
        // Already inside _sync lock (called from Handle).
        foreach (var (id, state) in _states)
        {
            if (ShouldAutoFireCore(id, tick, enforceCooldown: true, requireTargetMode: false, out var request))
                _pendingAutoFire.Add(request);
        }
    }

    private bool ShouldAutoFireCore(string id, uint tick, bool enforceCooldown, bool requireTargetMode, out AutoFireRequest request)
    {
        request = default;

        if (!_states.TryGetValue(id, out var state))
            return false;

        if (state.Mode == TacticalMode.Off)
            return false;

        if (requireTargetMode && state.Mode != TacticalMode.Target)
            return false;

        var ship = state.Ship;
        if (ship is null || !ship.Active || !ship.Alive)
            return false;

        if (ControllableRebuildState.IsRebuilding(ship))
            return false;

        if (!ship.ShotLauncher.Exists || !ship.ShotMagazine.Exists)
            return false;

        if (ship.ShotLauncher.Status == SubsystemStatus.Upgrading || ship.ShotMagazine.Status == SubsystemStatus.Upgrading)
            return false;

        if (ship.ShotMagazine.CurrentShots < 1f)
            return false;

        if (enforceCooldown && state.HasLastFireTick && (tick - state.LastFireTick) < AutoFireCooldownTicks)
            return false;

        // Resolve target
        if (!ResolveTarget(ship, state, out var targetUnit, out var targetReference))
            return false;

        var targetX = targetUnit.Position.X;
        var targetY = targetUnit.Position.Y;
        var targetVx = targetUnit.Movement.X;
        var targetVy = targetUnit.Movement.Y;

        var gravitySources = BuildGravitySources(ship);

        if (!TryBuildBestShotSolution(
                ship, targetX, targetY, targetVx, targetVy, targetUnit.Radius, gravitySources,
                out var relMovement, out var shotTicks, out var shotLoad, out var shotDamage, out var missDistance))
            return false;

        request = new AutoFireRequest(id, ship, targetReference, relMovement, shotTicks, shotLoad, shotDamage, missDistance, tick);
        return true;
    }

    private bool ResolveTarget(ClassicShipControllable ship, TacticalState state, out Unit targetUnit, out string targetReference)
    {
        targetUnit = null!;
        targetReference = "";

        switch (state.Mode)
        {
            case TacticalMode.Enemy:
                return FindBestEnemyTarget(ship, out targetUnit, out targetReference);

            case TacticalMode.Target:
                if (string.IsNullOrEmpty(state.TargetId))
                    return false;
                return ResolvePinnedTarget(ship, state.TargetId, out targetUnit, out targetReference);

            case TacticalMode.Scan:
                return ResolveOrAcquireScanTarget(ship, state, out targetUnit, out targetReference);

            default:
                return false;
        }
    }

    private static bool ResolveOrAcquireScanTarget(ClassicShipControllable ship, TacticalState state, out Unit targetUnit, out string targetReference)
    {
        targetUnit = null!;
        targetReference = "";

        var myTeam = ship.Cluster?.Galaxy.Player?.Team;
        if (!string.IsNullOrWhiteSpace(state.TargetId) &&
            ResolvePinnedTarget(ship, state.TargetId, out targetUnit, out targetReference) &&
            IsScanModeTargetCandidate(targetUnit, myTeam))
        {
            return true;
        }

        state.TargetId = null;
        if (!FindBestScanTarget(ship, out targetUnit, out targetReference))
            return false;

        state.TargetId = targetReference;
        return true;
    }

    private static bool FindBestEnemyTarget(ClassicShipControllable ship, out Unit bestTarget, out string targetReference)
    {
        return TryFindBestTarget(ship, IsEnemyModeTargetCandidate, out bestTarget, out targetReference);
    }

    private static bool FindBestScanTarget(ClassicShipControllable ship, out Unit bestTarget, out string targetReference)
    {
        return TryFindBestTarget(ship, IsScanModeTargetCandidate, out bestTarget, out targetReference);
    }

    private static bool ResolvePinnedTarget(ClassicShipControllable ship, string targetId, out Unit targetUnit, out string targetReference)
    {
        targetUnit = null!;
        targetReference = "";

        var cluster = ship.Cluster;
        if (cluster is null)
            return false;

        var normalizedTargetId = UnitIdentity.NormalizeUnitId(targetId, cluster.Id);
        var myTeam = cluster.Galaxy.Player?.Team;
        foreach (var unit in cluster.Units)
        {
            var unitId = UnitIdentity.BuildUnitId(unit, cluster.Id);
            if (!string.Equals(unitId, normalizedTargetId, StringComparison.Ordinal))
                continue;

            if (unit is PlayerUnit playerUnit
                && myTeam is not null
                && playerUnit.Team is not null
                && playerUnit.Team.Id == myTeam.Id)
            {
                return false;
            }

            targetUnit = unit;
            targetReference = unitId;
            return true;
        }

        var legacyName = TryExtractLegacyUnitName(targetId);
        if (string.IsNullOrWhiteSpace(legacyName))
            return false;

        foreach (var unit in cluster.Units)
        {
            if (!string.Equals(unit.Name, legacyName, StringComparison.Ordinal))
                continue;

            if (unit is PlayerUnit playerUnit
                && myTeam is not null
                && playerUnit.Team is not null
                && playerUnit.Team.Id == myTeam.Id)
            {
                return false;
            }

            targetUnit = unit;
            targetReference = UnitIdentity.BuildUnitId(unit, cluster.Id);
            return true;
        }

        return false;
    }

    private static bool TryFindBestTarget(
        ClassicShipControllable ship,
        Func<Unit, Team?, bool> targetPredicate,
        out Unit bestTarget,
        out string targetReference)
    {
        bestTarget = null!;
        targetReference = "";
        var bestScore = double.MaxValue;

        var cluster = ship.Cluster;
        if (cluster is null)
            return false;

        var myTeam = cluster.Galaxy.Player?.Team;
        foreach (var unit in cluster.Units)
        {
            if (!targetPredicate(unit, myTeam))
                continue;

            var score = ComputeTargetSelectionScore(ship, unit);
            if (score >= bestScore)
                continue;

            bestScore = score;
            bestTarget = unit;
            targetReference = UnitIdentity.BuildUnitId(unit, cluster.Id);
        }

        return bestTarget is not null;
    }

    private static string? TryExtractLegacyUnitName(string targetId)
    {
        if (string.IsNullOrWhiteSpace(targetId))
            return null;

        if (UnitIdentity.TryParseControllableId(targetId, out _, out _))
            return null;

        const string unitSeparator = "/unit/";
        var separatorIndex = targetId.IndexOf(unitSeparator, StringComparison.Ordinal);
        if (separatorIndex < 0)
            return targetId;

        var nameStart = separatorIndex + unitSeparator.Length;
        return nameStart >= targetId.Length ? null : targetId[nameStart..];
    }

    private static double ComputeTargetSelectionScore(ClassicShipControllable ship, Unit unit)
    {
        var shipX = (double)ship.Position.X;
        var shipY = (double)ship.Position.Y;
        var shipVx = (double)ship.Movement.X;
        var shipVy = (double)ship.Movement.Y;

        var dx = unit.Position.X - shipX;
        var dy = unit.Position.Y - shipY;
        var distance = Math.Sqrt(dx * dx + dy * dy);

        if (distance < 0.001d)
            return double.MaxValue;

        var toTargetX = dx / distance;
        var toTargetY = dy / distance;

        var relVx = unit.Movement.X - shipVx;
        var relVy = unit.Movement.Y - shipVy;
        var closingSpeed = relVx * toTargetX + relVy * toTargetY;

        var lateralVx = relVx - closingSpeed * toTargetX;
        var lateralVy = relVy - closingSpeed * toTargetY;
        var lateralSpeed = Math.Sqrt(lateralVx * lateralVx + lateralVy * lateralVy);

        return distance
               + lateralSpeed * 22d
               + Math.Max(0d, closingSpeed) * 48d
               - Math.Max(0d, -closingSpeed) * 18d;
    }

    private static bool IsEnemyModeTargetCandidate(Unit unit, Team? myTeam)
    {
        return (unit is PlayerUnit || unit is MobileNpcUnit) && !IsFriendlyUnit(unit, myTeam);
    }

    internal static bool IsScanModeTargetCandidate(Unit unit, Team? myTeam)
    {
        return unit is MobileUnit
               && unit is not Projectile
               && unit is not Explosion
               && !IsFriendlyUnit(unit, myTeam);
    }

    private static bool IsFriendlyUnit(Unit unit, Team? myTeam)
    {
        return myTeam is not null && unit.Team is not null && unit.Team.Id == myTeam.Id;
    }

    private List<GravitySource> BuildGravitySources(ClassicShipControllable ship)
    {
        try
        {
            var resolved = _gravitySourceResolver(ship);
            if (resolved is { Count: > 0 })
                return resolved.ToList();
        }
        catch
        {
            // Ignore resolver failures and fall back to live cluster units.
        }

        return BuildGravitySourcesFromCluster(ship);
    }

    private static List<GravitySource> BuildGravitySourcesFromCluster(ClassicShipControllable ship)
    {
        var sources = new List<GravitySource>();
        var cluster = ship.Cluster;
        if (cluster is null)
            return sources;

        foreach (var unit in cluster.Units)
        {
            if (unit.Gravity > 0f)
                sources.Add(new GravitySource(unit.Position.X, unit.Position.Y, unit.Gravity));
        }

        return sources;
    }

    private static bool TryBuildBestShotSolution(
        ClassicShipControllable ship,
        float targetX,
        float targetY,
        float targetVx,
        float targetVy,
        float targetRadius,
        List<GravitySource> gravitySources,
        out Vector relativeMovement,
        out ushort shotTicks,
        out float shotLoad,
        out float shotDamage,
        out float missDistance)
    {
        relativeMovement = new Vector();
        shotTicks = 0;
        shotLoad = 0f;
        shotDamage = 0f;
        missDistance = float.MaxValue;

        var hasBallistic = ComputeBallisticSolution(
            ship,
            targetX,
            targetY,
            targetVx,
            targetVy,
            targetRadius,
            gravitySources,
            out var ballisticMovement,
            out var ballisticTicks,
            out var ballisticLoad,
            out var ballisticDamage,
            out var ballisticMissDistance);

        var hasFallback = false;
        var fallbackMovement = new Vector();
        var fallbackTicks = (ushort)0;
        var fallbackMissDistance = float.MaxValue;
        var fallbackLoad = Math.Clamp(DefaultShotLoad, ship.ShotLauncher.MinimumLoad, ship.ShotLauncher.MaximumLoad);
        var fallbackDamage = Math.Clamp(DefaultShotDamage, ship.ShotLauncher.MinimumDamage, ship.ShotLauncher.MaximumDamage);

        var uncertainMissDistance = ComputeUncertainMissDistance(targetRadius);
        if (!hasBallistic || ballisticMissDistance > uncertainMissDistance)
        {
            hasFallback = TryComputeSpeedOnlyFallbackSolution(
                ship,
                targetX,
                targetY,
                targetVx,
                targetVy,
                targetRadius,
                out fallbackMovement,
                out fallbackTicks,
                out fallbackMissDistance);
        }

        if (hasFallback && (!hasBallistic || fallbackMissDistance + FallbackSelectionEpsilon < ballisticMissDistance))
        {
            relativeMovement = fallbackMovement;
            shotTicks = fallbackTicks;
            shotLoad = fallbackLoad;
            shotDamage = fallbackDamage;
            missDistance = fallbackMissDistance;
            return true;
        }

        if (!hasBallistic)
            return false;

        relativeMovement = ballisticMovement;
        shotTicks = ballisticTicks;
        shotLoad = ballisticLoad;
        shotDamage = ballisticDamage;
        missDistance = ballisticMissDistance;
        return true;
    }

    private static float ComputeAcceptableMissDistance(float targetRadius)
    {
        return Math.Max(ShotRadius, targetRadius + ShotRadius);
    }

    private static float ComputeUncertainMissDistance(float targetRadius)
    {
        var acceptableMissDistance = ComputeAcceptableMissDistance(targetRadius);
        return Math.Max(UncertainPredictionThresholdFloor, acceptableMissDistance * UncertainPredictionThresholdScale);
    }

    private static bool TryComputeSpeedOnlyFallbackSolution(
        ClassicShipControllable ship,
        float targetX,
        float targetY,
        float targetVx,
        float targetVy,
        float targetRadius,
        out Vector relativeMovement,
        out ushort shotTicks,
        out float missDistance)
    {
        var launcher = ship.ShotLauncher;
        return TryComputeSpeedOnlyIntercept(
            ship.Position.X,
            ship.Position.Y,
            ship.Movement.X,
            ship.Movement.Y,
            launcher.MaximumRelativeMovement,
            ship.Size + SpawnPaddingDistance,
            launcher.MinimumTicks,
            launcher.MaximumTicks,
            targetX,
            targetY,
            targetVx,
            targetVy,
            targetRadius,
            out relativeMovement,
            out shotTicks,
            out missDistance);
    }

    internal static bool TryComputeSpeedOnlyIntercept(
        float shipX,
        float shipY,
        float shipVx,
        float shipVy,
        float shotSpeed,
        float spawnOffset,
        ushort minTicks,
        ushort maxTicks,
        float targetX,
        float targetY,
        float targetVx,
        float targetVy,
        float targetRadius,
        out Vector relativeMovement,
        out ushort shotTicks,
        out float missDistance)
    {
        relativeMovement = new Vector();
        shotTicks = 0;
        missDistance = float.MaxValue;

        if (shotSpeed <= 0.0001f)
            return false;

        if (minTicks > maxTicks)
            return false;

        var bestMiss = double.MaxValue;
        var bestTick = -1;
        var bestRelX = 0f;
        var bestRelY = 0f;

        for (var ticks = minTicks; ticks <= maxTicks; ticks++)
        {
            if (ticks <= 0)
                continue;

            var interceptTargetX = (double)targetX + (double)targetVx * ticks;
            var interceptTargetY = (double)targetY + (double)targetVy * ticks;

            var candidateRelX = (float)((interceptTargetX - shipX) / ticks - shipVx);
            var candidateRelY = (float)((interceptTargetY - shipY) / ticks - shipVy);
            NormalizeToSpeed(ref candidateRelX, ref candidateRelY, shotSpeed);

            for (var iter = 0; iter < LinearFallbackRefinementIterations; iter++)
            {
                var relLength = Math.Sqrt(candidateRelX * candidateRelX + candidateRelY * candidateRelY);
                if (relLength < 0.0001d)
                    break;

                var spawnX = shipX + candidateRelX / relLength * spawnOffset;
                var spawnY = shipY + candidateRelY / relLength * spawnOffset;

                var neededRelX = (float)((interceptTargetX - spawnX) / ticks - shipVx);
                var neededRelY = (float)((interceptTargetY - spawnY) / ticks - shipVy);
                NormalizeToSpeed(ref neededRelX, ref neededRelY, shotSpeed);

                candidateRelX = neededRelX;
                candidateRelY = neededRelY;
            }

            var finalRelLength = Math.Sqrt(candidateRelX * candidateRelX + candidateRelY * candidateRelY);
            if (finalRelLength < 0.0001d)
                continue;

            var finalSpawnX = shipX + candidateRelX / finalRelLength * spawnOffset;
            var finalSpawnY = shipY + candidateRelY / finalRelLength * spawnOffset;
            var shotEndX = finalSpawnX + (shipVx + candidateRelX) * ticks;
            var shotEndY = finalSpawnY + (shipVy + candidateRelY) * ticks;

            var missX = shotEndX - interceptTargetX;
            var missY = shotEndY - interceptTargetY;
            var miss = Math.Sqrt(missX * missX + missY * missY);

            if (miss >= bestMiss)
                continue;

            bestMiss = miss;
            bestTick = ticks;
            bestRelX = candidateRelX;
            bestRelY = candidateRelY;
        }

        if (bestTick <= 0)
            return false;

        if (bestMiss > ComputeAcceptableMissDistance(targetRadius))
            return false;

        relativeMovement = new Vector(bestRelX, bestRelY);
        shotTicks = (ushort)bestTick;
        missDistance = (float)bestMiss;
        return true;
    }

    private static void NormalizeToSpeed(ref float x, ref float y, float speed)
    {
        var length = Math.Sqrt(x * x + y * y);
        if (length <= 0.0001d)
        {
            x = speed;
            y = 0f;
            return;
        }

        var scale = speed / length;
        x = (float)(x * scale);
        y = (float)(y * scale);
    }

    /// <summary>
    /// Computes a ballistic firing solution by forward-simulating the target's path with
    /// gravity and finding the intercept tick where a shot can meet the target.
    /// </summary>
    private static bool ComputeBallisticSolution(
        ClassicShipControllable ship,
        float targetX, float targetY,
        float targetVx, float targetVy,
        float targetRadius,
        List<GravitySource> gravitySources,
        out Vector relativeMovement,
        out ushort shotTicks,
        out float shotLoad,
        out float shotDamage,
        out float missDistance)
    {
        relativeMovement = new Vector();
        shotTicks = 0;
        shotLoad = 0f;
        shotDamage = 0f;
        missDistance = float.MaxValue;

        var launcher = ship.ShotLauncher;
        var shotSpeed = launcher.MaximumRelativeMovement;
        if (shotSpeed <= 0.0001f)
            return false;

        var minTicks = launcher.MinimumTicks;
        var maxTicks = launcher.MaximumTicks;
        if (minTicks > maxTicks)
            return false;

        var load = Math.Clamp(DefaultShotLoad, launcher.MinimumLoad, launcher.MaximumLoad);
        var damage = Math.Clamp(DefaultShotDamage, launcher.MinimumDamage, launcher.MaximumDamage);

        var shipX = (double)ship.Position.X;
        var shipY = (double)ship.Position.Y;
        var shipVx = (double)ship.Movement.X;
        var shipVy = (double)ship.Movement.Y;
        var spawnOffset = ship.Size + SpawnPaddingDistance;

        // Forward-simulate target position with gravity
        var tX = (double)targetX;
        var tY = (double)targetY;
        var tVx = (double)targetVx;
        var tVy = (double)targetVy;
        var targetSpeedLimit = DefaultSpeedLimit;

        var bestMiss = double.MaxValue;
        var bestTick = -1;
        var bestTargetX = tX;
        var bestTargetY = tY;
        var bestRelX = 0f;
        var bestRelY = 0f;

        // Trace: collect target trajectory for logging
        var trace = TraceEnabled ? new TraceCollector(shipX, shipY, shipVx, shipVy, targetX, targetY, targetVx, targetVy, shotSpeed, spawnOffset) : null;

        var iterationLimit = Math.Min((int)maxTicks, BallisticMaxIterations);

        for (var t = 1; t <= iterationLimit; t++)
        {
            var (gx, gy) = ComputeGravityAcceleration(tX, tY, gravitySources);
            tVx += gx;
            tVy += gy;
            (tVx, tVy) = ApplySoftCap(tVx, tVy, targetSpeedLimit);
            tX += tVx;
            tY += tVy;

            trace?.RecordTargetTick(t, tX, tY, tVx, tVy, gx, gy);

            if (t < minTicks)
                continue;

            // Quick straight-line distance check: can the shot even plausibly reach?
            var dx = tX - shipX;
            var dy = tY - shipY;
            var dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist < 0.001d)
                continue;

            // Initial aim: straight at predicted target (no gravity compensation yet)
            var aimX = tX;
            var aimY = tY;
            var candidateMiss = double.MaxValue;
            var candidateRelX = 0f;
            var candidateRelY = 0f;

            // Iteratively refine aim direction to compensate for gravity deflection
            for (var iter = 0; iter < GravityCompensationIterations; iter++)
            {
                var adx = aimX - shipX;
                var ady = aimY - shipY;
                var adist = Math.Sqrt(adx * adx + ady * ady);
                if (adist < 0.001d)
                    break;

                // Desired absolute shot velocity direction
                var dirX = adx / adist;
                var dirY = ady / adist;

                // Compute relativeMovement so that shipVel + rel gives desired absolute direction.
                // desiredAbsoluteVel = dir * shotAbsSpeed, but we control only relativeMovement
                // and the constraint is |relativeMovement| = shotSpeed.
                // rel = desiredDir * shotSpeed, compensated for ship velocity:
                var desRelX = dirX * shotSpeed - shipVx;
                var desRelY = dirY * shotSpeed - shipVy;
                // Re-normalize to exactly shotSpeed (the server enforces the length)
                var desRelLen = Math.Sqrt(desRelX * desRelX + desRelY * desRelY);
                if (desRelLen < 0.0001d)
                    break;
                var relX = (float)(desRelX / desRelLen * shotSpeed);
                var relY = (float)(desRelY / desRelLen * shotSpeed);

                // Spawn offset is in the direction of the relative movement (ship-frame launch direction)
                var spawnDirX = relX / shotSpeed;
                var spawnDirY = relY / shotSpeed;

                // Simulate shot trajectory for t ticks
                var spawnX = shipX + spawnDirX * spawnOffset;
                var spawnY = shipY + spawnDirY * spawnOffset;
                var sX = spawnX;
                var sY = spawnY;
                var sVx = shipVx + relX;
                var sVy = shipVy + relY;

                if (iter == 0)
                    trace?.BeginShotCandidate(t, spawnX, spawnY, sVx, sVy, relX, relY);

                for (var st = 0; st < t; st++)
                {
                    var (sgx, sgy) = ComputeGravityAcceleration(sX, sY, gravitySources);
                    sVx += sgx;
                    sVy += sgy;
                    sX += sVx;
                    sY += sVy;

                    if (iter == 0)
                        trace?.RecordShotTick(st + 1, sX, sY, sVx, sVy, sgx, sgy);
                }

                var mx = sX - tX;
                var my = sY - tY;
                var miss = Math.Sqrt(mx * mx + my * my);

                if (iter == 0)
                    trace?.RecordCandidateResult(t, miss, sX, sY, tX, tY);

                if (miss < candidateMiss)
                {
                    candidateMiss = miss;
                    candidateRelX = relX;
                    candidateRelY = relY;
                }

                // If miss is already tiny, stop iterating
                if (miss < 0.5d)
                    break;

                // Adjust aim point: shift it by the error so the next shot lands closer
                aimX += (tX - sX);
                aimY += (tY - sY);
            }

            trace?.RecordRefinedResult(t, candidateMiss, candidateRelX, candidateRelY);

            if (candidateMiss < bestMiss)
            {
                bestMiss = candidateMiss;
                bestTick = t;
                bestTargetX = tX;
                bestTargetY = tY;
                bestRelX = candidateRelX;
                bestRelY = candidateRelY;
            }
        }

        if (bestTick < 0 || bestMiss > targetRadius + ShotRadius)
        {
            trace?.Flush(bestTick < 0 ? "no_solution" : "miss_too_large", null);
            return false;
        }

        relativeMovement = new Vector(bestRelX, bestRelY);
        shotTicks = (ushort)bestTick;
        shotLoad = load;
        shotDamage = damage;
        missDistance = (float)bestMiss;

        trace?.RecordShootCommand(relativeMovement, shotTicks, shotLoad, shotDamage, missDistance, bestTargetX, bestTargetY);
        trace?.Flush("fire", null);

        return true;
    }

    /// <summary>
    /// Collects ballistic trace data and writes it to a timestamped file.
    /// Only instantiated when FV_TACTICAL_TRACE=1.
    /// </summary>
    private sealed class TraceCollector
    {
        private readonly StringBuilder _sb = new();
        private int _candidateCount;

        public TraceCollector(
            double shipX, double shipY, double shipVx, double shipVy,
            float targetX, float targetY, float targetVx, float targetVy,
            double shotSpeed, double spawnOffset)
        {
            _sb.AppendLine(CultureInfo.InvariantCulture, $"# Tactical Ballistic Trace  {DateTime.UtcNow:O}");
            _sb.AppendLine(CultureInfo.InvariantCulture, $"ship_pos:     ({shipX:F4}, {shipY:F4})");
            _sb.AppendLine(CultureInfo.InvariantCulture, $"ship_vel:     ({shipVx:F4}, {shipVy:F4})");
            _sb.AppendLine(CultureInfo.InvariantCulture, $"target_pos:   ({targetX:F4}, {targetY:F4})");
            _sb.AppendLine(CultureInfo.InvariantCulture, $"target_vel:   ({targetVx:F4}, {targetVy:F4})");
            _sb.AppendLine(CultureInfo.InvariantCulture, $"shot_speed:   {shotSpeed:F4}");
            _sb.AppendLine(CultureInfo.InvariantCulture, $"spawn_offset: {spawnOffset:F4}");
            _sb.AppendLine();
        }

        public void RecordTargetTick(int tick, double x, double y, double vx, double vy, double gx, double gy)
        {
            _sb.AppendLine(CultureInfo.InvariantCulture,
                $"target  t={tick,4}  pos=({x,10:F4}, {y,10:F4})  vel=({vx,8:F4}, {vy,8:F4})  grav=({gx,8:F6}, {gy,8:F6})");
        }

        public void BeginShotCandidate(int candidateTicks, double spawnX, double spawnY, double vx, double vy, float relX, float relY)
        {
            _candidateCount++;
            _sb.AppendLine();
            _sb.AppendLine(CultureInfo.InvariantCulture,
                $"--- shot candidate #{_candidateCount}  ticks={candidateTicks}  spawn=({spawnX:F4}, {spawnY:F4})  vel=({vx:F4}, {vy:F4})  rel=({relX:F4}, {relY:F4})");
        }

        public void RecordShotTick(int tick, double x, double y, double vx, double vy, double gx, double gy)
        {
            _sb.AppendLine(CultureInfo.InvariantCulture,
                $"  shot  t={tick,4}  pos=({x,10:F4}, {y,10:F4})  vel=({vx,8:F4}, {vy,8:F4})  grav=({gx,8:F6}, {gy,8:F6})");
        }

        public void RecordCandidateResult(int candidateTicks, double miss, double shotEndX, double shotEndY, double targetEndX, double targetEndY)
        {
            _sb.AppendLine(CultureInfo.InvariantCulture,
                $"  result  miss={miss:F4}  shot_end=({shotEndX:F4}, {shotEndY:F4})  target_end=({targetEndX:F4}, {targetEndY:F4})");
        }

        public void RecordRefinedResult(int candidateTicks, double miss, float relX, float relY)
        {
            _sb.AppendLine(CultureInfo.InvariantCulture,
                $"  refined ticks={candidateTicks}  miss={miss:F4}  rel=({relX:F6}, {relY:F6})");
        }

        public void RecordShootCommand(Vector relativeMovement, ushort ticks, float load, float damage, float missDistance, double aimX, double aimY)
        {
            _sb.AppendLine();
            _sb.AppendLine("=== SHOOT COMMAND ===");
            _sb.AppendLine(CultureInfo.InvariantCulture, $"relativeMovement: ({relativeMovement.X:F6}, {relativeMovement.Y:F6})  len={relativeMovement.Length:F6}");
            _sb.AppendLine(CultureInfo.InvariantCulture, $"ticks:            {ticks}");
            _sb.AppendLine(CultureInfo.InvariantCulture, $"load:             {load:F4}");
            _sb.AppendLine(CultureInfo.InvariantCulture, $"damage:           {damage:F4}");
            _sb.AppendLine(CultureInfo.InvariantCulture, $"missDistance:      {missDistance:F4}");
            _sb.AppendLine(CultureInfo.InvariantCulture, $"aimPoint:         ({aimX:F4}, {aimY:F4})");
        }

        public void Flush(string outcome, string? extra)
        {
            _sb.AppendLine();
            _sb.AppendLine(CultureInfo.InvariantCulture, $"outcome: {outcome}");
            if (extra is not null)
                _sb.AppendLine(extra);

            try
            {
                Directory.CreateDirectory(TraceDirectory);
                var fileName = $"trace_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{_candidateCount}c_{outcome}.txt";
                var filePath = Path.Combine(TraceDirectory, fileName);
                File.WriteAllText(filePath, _sb.ToString());
            }
            catch
            {
                // Tracing is best-effort; never fail the shot because of I/O.
            }
        }
    }
}

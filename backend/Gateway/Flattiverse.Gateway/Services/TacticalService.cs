using Flattiverse.Connector;
using Flattiverse.Connector.Events;
using Flattiverse.Connector.GalaxyHierarchy;
using Flattiverse.Connector.Units;
using Flattiverse.Gateway.Connector;

namespace Flattiverse.Gateway.Services;

/// <summary>
/// Per-player-session service that stores tactical intent for each controllable and
/// evaluates auto-fire decisions on every GalaxyTick event.
/// Event handlers run on the connector event loop, but state can also be touched from command/overlay paths,
/// so accesses are synchronized.
/// </summary>
public sealed class TacticalService : IConnectorEventHandler
{
    private const float DefaultShotRelativeSpeed = 2f;
    private const float DefaultShotLoad = 12f;
    private const float DefaultShotDamage = 8f;
    private const uint AutoFireCooldownTicks = 2;
    private const float MaxTargetDistance = 1400f;
    private const float MinimumTargetDistance = 8f;
    private const float NumericEpsilon = 0.0001f;
    private const int PredictionIterations = 6;
    private const int PredictionTickSearchRadius = 10;
    private const float PredictionHitTolerance = 2.5f;
    private const float PredictionQualityGate = 6.5f;
    private const float PredictionMaximumMissDistance = 18f;
    private const float PredictionMissScoreWeight = 3.25f;
    private const float PredictionTickPenalty = 0.12f;
    private const int TargetPredictionIterations = 14;
    private const int TargetPredictionTickSearchRadius = 18;
    private const float TargetPredictionMaximumMissDistance = 12f;
    private const float TargetPredictionQualityGate = 4.5f;
    private const int PointPredictionIterations = 18;
    private const float PointPredictionMaximumMissDistance = 10f;
    private const float PointPredictionQualityGate = 3.5f;
    private const float GravityMinimumDistance = 6f;
    private const float GravityInfluenceRange = 3200f;
    private const float GravityInfluenceRangeSquared = GravityInfluenceRange * GravityInfluenceRange;
    private const float ProjectileSpawnPaddingDistance = 2f;

    public enum TacticalMode
    {
        Off,
        Enemy,
        Target
    }

    public readonly record struct AutoFireRequest(
        string ControllableId,
        ClassicShipControllable Ship,
        Vector RelativeMovement,
        ushort Ticks,
        float Load,
        float Damage,
        uint Tick,
        string TargetUnitName,
        float PredictedMissDistance
    );

    private sealed class TacticalState
    {
        public TacticalMode Mode { get; set; } = TacticalMode.Off;
        public string? TargetId { get; set; }
        public ClassicShipControllable? Ship { get; set; }
        public uint LastFireTick { get; set; }
        public bool HasLastFireTick { get; set; }
    }

    private readonly record struct GravitySource(
        Unit Unit,
        float PositionX,
        float PositionY,
        float MovementX,
        float MovementY,
        float Gravity,
        float Radius,
        float GravityWellRadius,
        float GravityWellForce
    );

    private readonly record struct ShotProfile(
        float Load,
        float Damage,
        float PreferencePenalty
    );

    private readonly Dictionary<string, TacticalState> _states = new();
    private readonly Queue<AutoFireRequest> _pendingAutoFireRequests = new();
    private readonly object _sync = new();

    public void Handle(FlattiverseEvent @event)
    {
        lock (_sync)
        {
            if (@event is ControllableInfoEvent infoEvent &&
                (@event is DestroyedControllableInfoEvent || @event is ClosedControllableInfoEvent))
            {
                RemoveCore(BuildControllableId(infoEvent.Player.Id, infoEvent.ControllableInfo.Id));
                return;
            }

            if (@event is not GalaxyTickEvent tickEvent)
                return;

            EvaluateAutoFire(tickEvent.Tick);
        }
    }

    public void AttachControllable(string controllableId, ClassicShipControllable ship)
    {
        lock (_sync)
        {
            var state = GetOrCreateState(controllableId);
            state.Ship = ship;
        }
    }

    public List<AutoFireRequest> DequeuePendingAutoFireRequests()
    {
        lock (_sync)
        {
            if (_pendingAutoFireRequests.Count == 0)
                return new List<AutoFireRequest>();

            var requests = new List<AutoFireRequest>(_pendingAutoFireRequests.Count);

            while (_pendingAutoFireRequests.Count > 0)
                requests.Add(_pendingAutoFireRequests.Dequeue());

            return requests;
        }
    }

    private void EvaluateAutoFire(uint tick)
    {
        if (_states.Count == 0)
            return;

        Dictionary<Cluster, List<GravitySource>> gravitySourcesByCluster = new();

        foreach (var entry in _states)
        {
            if (ShouldAutoFireCore(entry.Key, tick, gravitySourcesByCluster, enforceCooldown: true, requireTargetMode: false, out var request))
                _pendingAutoFireRequests.Enqueue(request);
        }
    }

    public void SetMode(string controllableId, TacticalMode mode)
    {
        lock (_sync)
        {
            var state = GetOrCreateState(controllableId);
            state.Mode = mode;

            if (mode == TacticalMode.Off)
                state.TargetId = null;
        }
    }

    public void SetTarget(string controllableId, string targetId)
    {
        lock (_sync)
        {
            var state = GetOrCreateState(controllableId);
            state.TargetId = targetId;
        }
    }

    public bool IsTargetAllowedForTargetMode(ClassicShipControllable ship, string targetId)
    {
        if (string.IsNullOrWhiteSpace(targetId))
            return false;

        lock (_sync)
            return IsTargetAllowedForTargetModeCore(ship, targetId);
    }

    public void ClearTarget(string controllableId)
    {
        lock (_sync)
        {
            if (_states.TryGetValue(controllableId, out var state))
                state.TargetId = null;
        }
    }

    public bool ShouldAutoFire(string controllableId, uint tick, out AutoFireRequest request)
    {
        lock (_sync)
            return ShouldAutoFireCore(controllableId, tick, null, enforceCooldown: true, requireTargetMode: false, out request);
    }

    public bool TryBuildTargetBurstRequest(string controllableId, uint tick, out AutoFireRequest request)
    {
        lock (_sync)
            return ShouldAutoFireCore(controllableId, tick, null, enforceCooldown: false, requireTargetMode: true, out request);
    }

    public bool TryBuildPointShotRequest(string controllableId, ClassicShipControllable ship, float targetX, float targetY, uint tick,
        out AutoFireRequest request)
    {
        lock (_sync)
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

            if (!PassesTargetBasicChecks(ship, targetX, targetY))
                return false;

            List<GravitySource> gravitySources = GetOrBuildGravitySources(ship, null);
            return TryBuildPredictedShotRequestToPoint(controllableId, ship, targetX, targetY, tick, gravitySources, out request);
        }
    }

    public void RegisterSuccessfulFire(string controllableId, uint tick)
    {
        lock (_sync)
        {
            if (!_states.TryGetValue(controllableId, out var state))
                return;

            state.LastFireTick = tick;
            state.HasLastFireTick = true;
        }
    }

    private bool ShouldAutoFireCore(string controllableId, uint tick, Dictionary<Cluster, List<GravitySource>>? gravitySourcesByCluster,
        bool enforceCooldown, bool requireTargetMode, out AutoFireRequest request)
    {
        request = default;

        if (!_states.TryGetValue(controllableId, out var state) || state.Mode == TacticalMode.Off || state.Ship is null)
            return false;

        if (requireTargetMode && state.Mode != TacticalMode.Target)
            return false;

        var ship = state.Ship;

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

        if (enforceCooldown && state.HasLastFireTick && tick - state.LastFireTick < AutoFireCooldownTicks)
            return false;

        Unit? target = ResolveTarget(ship, state);
        if (target is null || !PassesTeamFilter(ship, target) || !PassesTargetBasicChecks(ship, target))
            return false;

        List<GravitySource> gravitySources = GetOrBuildGravitySources(ship, gravitySourcesByCluster);
        bool highPrecisionTargeting = state.Mode == TacticalMode.Target;

        if (!TryBuildPredictedShotRequest(controllableId, ship, target, tick, gravitySources, highPrecisionTargeting, out request))
            return false;

        return true;
    }

    public Dictionary<string, object?> BuildOverlay(string controllableId)
    {
        lock (_sync)
        {
            if (!_states.TryGetValue(controllableId, out var state))
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
                {
                    "mode",
                    state.Mode switch
                    {
                        TacticalMode.Enemy => "enemy",
                        TacticalMode.Target => "target",
                        _ => "off"
                    }
                },
                { "targetId", state.TargetId },
                { "hasTarget", !string.IsNullOrWhiteSpace(state.TargetId) }
            };
        }
    }

    public void Remove(string controllableId)
    {
        lock (_sync)
            RemoveCore(controllableId);
    }

    private void RemoveCore(string controllableId)
    {
        _states.Remove(controllableId);

        if (_pendingAutoFireRequests.Count == 0)
            return;

        var kept = _pendingAutoFireRequests.Where(request => request.ControllableId != controllableId).ToList();
        _pendingAutoFireRequests.Clear();

        foreach (var request in kept)
            _pendingAutoFireRequests.Enqueue(request);
    }

    private TacticalState GetOrCreateState(string controllableId)
    {
        if (_states.TryGetValue(controllableId, out var state))
            return state;

        state = new TacticalState();
        _states[controllableId] = state;
        return state;
    }

    private static string BuildControllableId(int playerId, int controllableId)
    {
        return $"p{playerId}-c{controllableId}";
    }

    private static bool TryParseControllableId(string value, out byte playerId, out byte controllableId)
    {
        playerId = 0;
        controllableId = 0;

        if (string.IsNullOrWhiteSpace(value))
            return false;

        if (!value.StartsWith('p'))
            return false;

        int separatorIndex = value.IndexOf("-c", StringComparison.Ordinal);
        if (separatorIndex <= 1 || separatorIndex + 2 >= value.Length)
            return false;

        if (!byte.TryParse(value.AsSpan(1, separatorIndex - 1), out playerId))
            return false;

        return byte.TryParse(value.AsSpan(separatorIndex + 2), out controllableId);
    }

    private static float Dot(Vector left, Vector right)
    {
        return (left.X * right.X) + (left.Y * right.Y);
    }

    private static Unit? ResolveTarget(ClassicShipControllable ship, TacticalState state)
    {
        return state.Mode switch
        {
            TacticalMode.Target => ResolvePinnedTarget(ship, state.TargetId),
            TacticalMode.Enemy => FindNearestEnemyTarget(ship),
            _ => null
        };
    }

    private static Unit? ResolvePinnedTarget(ClassicShipControllable ship, string? targetId)
    {
        if (string.IsNullOrWhiteSpace(targetId))
            return null;

        if (TryParseControllableId(targetId, out byte targetPlayerId, out byte targetControllableId))
        {
            foreach (Unit unit in ship.Cluster.Units)
            {
                if (unit is PlayerUnit playerUnit &&
                    playerUnit.Player.Id == targetPlayerId &&
                    playerUnit.ControllableInfo.Id == targetControllableId)
                {
                    return playerUnit;
                }
            }

            return null;
        }

        foreach (Unit unit in ship.Cluster.Units)
        {
            if (unit.Name == targetId)
                return unit;
        }

        return null;
    }

    private static bool IsTargetAllowedForTargetModeCore(ClassicShipControllable ship, string targetId)
    {
        if (TryParseControllableId(targetId, out byte targetPlayerId, out _))
        {
            if (ship.Cluster.Galaxy.Players.TryGet(targetPlayerId, out Player? targetPlayer) &&
                targetPlayer is not null &&
                targetPlayer.Team.Id == ship.Cluster.Galaxy.Player.Team.Id)
            {
                return false;
            }
        }

        Unit? resolved = ResolvePinnedTarget(ship, targetId);
        if (resolved is null)
            return true;

        return PassesTeamFilter(ship, resolved);
    }

    private static Unit? FindNearestEnemyTarget(ClassicShipControllable ship)
    {
        Unit? bestTarget = null;
        float bestDistanceSquared = float.MaxValue;
        byte ownPlayerId = ship.Cluster.Galaxy.Player.Id;
        byte ownTeamId = ship.Cluster.Galaxy.Player.Team.Id;
        Vector ownPosition = ship.Position;

        foreach (Unit unit in ship.Cluster.Units)
        {
            if (unit is not PlayerUnit candidate)
                continue;

            if (candidate.Player.Id == ownPlayerId)
                continue;

            if (candidate.Player.Team.Id == ownTeamId)
                continue;

            if (!candidate.ControllableInfo.Alive)
                continue;

            Vector delta = candidate.Position - ownPosition;
            float distanceSquared = delta.LengthSquared;

            if (distanceSquared < bestDistanceSquared)
            {
                bestDistanceSquared = distanceSquared;
                bestTarget = unit;
            }
        }

        return bestTarget;
    }

    private static bool PassesTeamFilter(ClassicShipControllable ship, Unit target)
    {
        if (target is not PlayerUnit targetPlayerUnit)
            return true;

        return targetPlayerUnit.Player.Team.Id != ship.Cluster.Galaxy.Player.Team.Id;
    }

    private static bool PassesTargetBasicChecks(ClassicShipControllable ship, Unit target)
    {
        if (!target.Cluster.Active || target.Cluster != ship.Cluster)
            return false;

        if (target is PlayerUnit targetPlayerUnit)
        {
            if (!targetPlayerUnit.ControllableInfo.Alive)
                return false;
        }

        Vector toTarget = target.Position - ship.Position;
        float distance = toTarget.Length;

        if (distance < MinimumTargetDistance)
            return false;

        if (distance > MaxTargetDistance)
            return false;

        return true;
    }

    private static bool PassesTargetBasicChecks(ClassicShipControllable ship, float targetX, float targetY)
    {
        if (!ship.Cluster.Active)
            return false;

        float deltaX = targetX - ship.Position.X;
        float deltaY = targetY - ship.Position.Y;
        float distance = MathF.Sqrt(deltaX * deltaX + deltaY * deltaY);

        if (distance < MinimumTargetDistance)
            return false;

        if (distance > MaxTargetDistance)
            return false;

        return true;
    }

    private static bool TryBuildPredictedShotRequest(string controllableId, ClassicShipControllable ship, Unit target, uint tick,
        IReadOnlyList<GravitySource> gravitySources, bool highPrecisionTargeting, out AutoFireRequest request)
    {
        request = default;

        float shotSpeed = Math.Clamp(DefaultShotRelativeSpeed, ship.ShotLauncher.MinimumRelativeMovement, ship.ShotLauncher.MaximumRelativeMovement);
        List<ShotProfile> shotProfiles = BuildShotProfiles(ship, highPrecisionTargeting);
        ushort minimumTicks = ship.ShotLauncher.MinimumTicks;
        ushort maximumTicks = ship.ShotLauncher.MaximumTicks;
        if (minimumTicks > maximumTicks)
            return false;

        int tickSearchRadius = highPrecisionTargeting ? TargetPredictionTickSearchRadius : PredictionTickSearchRadius;
        int baselineTicksRaw = EstimateBaselineTicks(ship.Position, ship.Movement, target.Position, target.Movement, shotSpeed);
        int baselineTicks = Math.Clamp(baselineTicksRaw, minimumTicks, maximumTicks);
        int startTicks = Math.Max(minimumTicks, baselineTicks - tickSearchRadius);
        int endTicks = Math.Min(maximumTicks, baselineTicks + tickSearchRadius);

        float bestMissDistance = float.MaxValue;
        float bestScore = float.MaxValue;
        Vector bestRelativeMovement = new();
        ushort bestTicks = 0;
        float bestLoad = 0f;
        float bestDamage = 0f;
        float bestEnergyCost = 0f;
        float bestIonCost = 0f;
        float bestNeutrinoCost = 0f;
        bool bestFound = false;

        for (int ticks = startTicks; ticks <= endTicks; ticks++)
        {
            if (!TryPredictRelativeMovementWithGravity(ship, target, gravitySources, ticks, highPrecisionTargeting, out Vector relativeMovement,
                    out float missDistance))
            {
                continue;
            }

            foreach (ShotProfile profile in shotProfiles)
            {
                if (!ship.ShotLauncher.CalculateCost(relativeMovement, (ushort)ticks, profile.Load, profile.Damage, out float energyCost,
                        out float ionCost, out float neutrinoCost))
                {
                    continue;
                }

                if (energyCost > ship.EnergyBattery.Current + NumericEpsilon)
                    continue;

                if (ionCost > ship.IonBattery.Current + NumericEpsilon)
                    continue;

                if (neutrinoCost > ship.NeutrinoBattery.Current + NumericEpsilon)
                    continue;

                float score = missDistance * PredictionMissScoreWeight + ticks * PredictionTickPenalty + profile.PreferencePenalty;
                bool scoreTie = MathF.Abs(score - bestScore) <= NumericEpsilon;
                if (score > bestScore + NumericEpsilon)
                    continue;

                if (scoreTie)
                {
                    bool worseMissDistance = missDistance > bestMissDistance + NumericEpsilon;
                    bool sameMissDistance = MathF.Abs(missDistance - bestMissDistance) <= NumericEpsilon;
                    bool worseOrEqualTicks = ticks >= bestTicks;
                    bool lowerOrEqualDamage = profile.Damage <= bestDamage + NumericEpsilon;

                    if (worseMissDistance || (sameMissDistance && worseOrEqualTicks && lowerOrEqualDamage))
                        continue;
                }

                bestMissDistance = missDistance;
                bestScore = score;
                bestRelativeMovement = relativeMovement;
                bestTicks = (ushort)ticks;
                bestLoad = profile.Load;
                bestDamage = profile.Damage;
                bestEnergyCost = energyCost;
                bestIonCost = ionCost;
                bestNeutrinoCost = neutrinoCost;
                bestFound = true;
            }
        }

        float maximumMissDistance = highPrecisionTargeting ? TargetPredictionMaximumMissDistance : PredictionMaximumMissDistance;
        float qualityGate = highPrecisionTargeting ? TargetPredictionQualityGate : PredictionQualityGate;

        if (!bestFound || bestMissDistance > maximumMissDistance)
            return false;

        if (bestMissDistance > qualityGate)
            return false;

        if (bestEnergyCost > ship.EnergyBattery.Current + NumericEpsilon)
            return false;

        if (bestIonCost > ship.IonBattery.Current + NumericEpsilon)
            return false;

        if (bestNeutrinoCost > ship.NeutrinoBattery.Current + NumericEpsilon)
            return false;

        request = new AutoFireRequest(controllableId, ship, bestRelativeMovement, bestTicks, bestLoad, bestDamage, tick, target.Name,
            bestMissDistance);
        return true;
    }

    private static bool TryBuildPredictedShotRequestToPoint(string controllableId, ClassicShipControllable ship, float targetX, float targetY, uint tick,
        IReadOnlyList<GravitySource> gravitySources, out AutoFireRequest request)
    {
        request = default;

        float shotSpeed = Math.Clamp(DefaultShotRelativeSpeed, ship.ShotLauncher.MinimumRelativeMovement, ship.ShotLauncher.MaximumRelativeMovement);
        List<ShotProfile> shotProfiles = BuildShotProfiles(ship, adaptive: true);
        ushort minimumTicks = ship.ShotLauncher.MinimumTicks;
        ushort maximumTicks = ship.ShotLauncher.MaximumTicks;
        if (minimumTicks > maximumTicks)
            return false;

        int startTicks = minimumTicks;
        int endTicks = maximumTicks;

        float bestMissDistance = float.MaxValue;
        float bestScore = float.MaxValue;
        Vector bestRelativeMovement = new();
        ushort bestTicks = 0;
        float bestLoad = 0f;
        float bestDamage = 0f;
        float bestEnergyCost = 0f;
        float bestIonCost = 0f;
        float bestNeutrinoCost = 0f;
        bool bestFound = false;

        for (int ticks = startTicks; ticks <= endTicks; ticks++)
        {
            if (!TryPredictRelativeMovementToPointWithGravity(ship, targetX, targetY, gravitySources, ticks, out Vector relativeMovement,
                    out float missDistance))
            {
                continue;
            }

            foreach (ShotProfile profile in shotProfiles)
            {
                if (!ship.ShotLauncher.CalculateCost(relativeMovement, (ushort)ticks, profile.Load, profile.Damage, out float energyCost,
                        out float ionCost, out float neutrinoCost))
                {
                    continue;
                }

                if (energyCost > ship.EnergyBattery.Current + NumericEpsilon)
                    continue;

                if (ionCost > ship.IonBattery.Current + NumericEpsilon)
                    continue;

                if (neutrinoCost > ship.NeutrinoBattery.Current + NumericEpsilon)
                    continue;

                float score = missDistance * PredictionMissScoreWeight + ticks * PredictionTickPenalty + profile.PreferencePenalty;
                bool scoreTie = MathF.Abs(score - bestScore) <= NumericEpsilon;
                if (score > bestScore + NumericEpsilon)
                    continue;

                if (scoreTie)
                {
                    bool worseMissDistance = missDistance > bestMissDistance + NumericEpsilon;
                    bool sameMissDistance = MathF.Abs(missDistance - bestMissDistance) <= NumericEpsilon;
                    bool worseOrEqualTicks = ticks >= bestTicks;
                    bool lowerOrEqualDamage = profile.Damage <= bestDamage + NumericEpsilon;

                    if (worseMissDistance || (sameMissDistance && worseOrEqualTicks && lowerOrEqualDamage))
                        continue;
                }

                bestMissDistance = missDistance;
                bestScore = score;
                bestRelativeMovement = relativeMovement;
                bestTicks = (ushort)ticks;
                bestLoad = profile.Load;
                bestDamage = profile.Damage;
                bestEnergyCost = energyCost;
                bestIonCost = ionCost;
                bestNeutrinoCost = neutrinoCost;
                bestFound = true;
            }
        }

        if (!bestFound || bestMissDistance > PointPredictionMaximumMissDistance)
            return false;

        if (bestMissDistance > PointPredictionQualityGate)
            return false;

        if (bestEnergyCost > ship.EnergyBattery.Current + NumericEpsilon)
            return false;

        if (bestIonCost > ship.IonBattery.Current + NumericEpsilon)
            return false;

        if (bestNeutrinoCost > ship.NeutrinoBattery.Current + NumericEpsilon)
            return false;

        string targetLabel = $"point:{targetX:0.###},{targetY:0.###}";
        request = new AutoFireRequest(controllableId, ship, bestRelativeMovement, bestTicks, bestLoad, bestDamage, tick, targetLabel,
            bestMissDistance);
        return true;
    }

    private static int EstimateBaselineTicks(Vector sourcePosition, Vector sourceMovement, Vector targetPosition, Vector targetMovement,
        float projectileSpeed)
    {
        float straightFlightTicks = projectileSpeed > NumericEpsilon
            ? Vector.Distance(sourcePosition, targetPosition) / projectileSpeed
            : 0f;

        float predictedFlightTicks = straightFlightTicks;

        if (TryPredictRelativeMovement(sourcePosition, sourceMovement, targetPosition, targetMovement, projectileSpeed, out _, out float interceptTicks))
            predictedFlightTicks = interceptTicks;

        return (int)MathF.Ceiling(predictedFlightTicks + 2f);
    }

    private static bool TryPredictRelativeMovementWithGravity(ClassicShipControllable ship, Unit target, IReadOnlyList<GravitySource> gravitySources,
        int ticks, bool highPrecisionTargeting, out Vector relativeMovement, out float missDistance)
    {
        relativeMovement = new Vector();
        missDistance = float.MaxValue;

        if (ticks <= 0)
            return false;

        SimulateBodyWithGravity(target.Position.X, target.Position.Y, target.Movement.X, target.Movement.Y, ticks, gravitySources, target, out float targetX,
            out float targetY);

        ComputeProjectedLaunchOrigin(ship, targetX - ship.Position.X, targetY - ship.Position.Y, out float projectedStartX, out float projectedStartY);
        float baseRelativeX = (targetX - projectedStartX) / ticks - ship.Movement.X;
        float baseRelativeY = (targetY - projectedStartY) / ticks - ship.Movement.Y;

        if (!highPrecisionTargeting)
        {
            float relativeX = baseRelativeX;
            float relativeY = baseRelativeY;

            for (int iteration = 0; iteration < PredictionIterations; iteration++)
            {
                SimulateShotWithGravity(ship, relativeX, relativeY, ticks, gravitySources, out float projectileX, out float projectileY);

                float errorX = targetX - projectileX;
                float errorY = targetY - projectileY;
                missDistance = MathF.Sqrt(errorX * errorX + errorY * errorY);

                if (missDistance <= PredictionHitTolerance)
                    break;

                float correctionScale = 1f / ticks;
                relativeX += errorX * correctionScale;
                relativeY += errorY * correctionScale;

                if (float.IsNaN(relativeX) || float.IsInfinity(relativeX) || float.IsNaN(relativeY) || float.IsInfinity(relativeY))
                    return false;
            }

            relativeMovement = new Vector(relativeX, relativeY);
            return true;
        }

        var candidateSeeds = new (float X, float Y)[]
        {
            (baseRelativeX, baseRelativeY),
            (baseRelativeX * 0.85f, baseRelativeY * 0.85f),
            (baseRelativeX * 1.15f, baseRelativeY * 1.15f),
            Rotate(baseRelativeX, baseRelativeY, 10f),
            Rotate(baseRelativeX, baseRelativeY, -10f),
        };

        Vector bestMovement = new();
        float bestMiss = float.MaxValue;
        bool converged = false;

        foreach (var seed in candidateSeeds)
        {
            float relativeX = seed.X;
            float relativeY = seed.Y;

            for (int iteration = 0; iteration < TargetPredictionIterations; iteration++)
            {
                if (!TryComputeMissDistance(ship, relativeX, relativeY, ticks, gravitySources, targetX, targetY, out float errorX, out float errorY,
                        out float currentMiss))
                {
                    break;
                }

                if (currentMiss < bestMiss)
                {
                    bestMiss = currentMiss;
                    bestMovement = new Vector(relativeX, relativeY);
                    converged = true;
                }

                if (currentMiss <= PredictionHitTolerance)
                    break;

                if (!TryApplyCorrectionStep(ship, gravitySources, ticks, targetX, targetY, relativeX, relativeY, errorX, errorY, currentMiss,
                        out float nextRelativeX, out float nextRelativeY, out float nextMiss))
                {
                    break;
                }

                relativeX = nextRelativeX;
                relativeY = nextRelativeY;

                if (nextMiss < bestMiss)
                {
                    bestMiss = nextMiss;
                    bestMovement = new Vector(relativeX, relativeY);
                    converged = true;
                }
            }
        }

        if (!converged)
            return false;

        relativeMovement = bestMovement;
        missDistance = bestMiss;
        return true;
    }

    private static bool TryPredictRelativeMovementToPointWithGravity(ClassicShipControllable ship, float targetX, float targetY,
        IReadOnlyList<GravitySource> gravitySources, int ticks, out Vector relativeMovement, out float missDistance)
    {
        relativeMovement = new Vector();
        missDistance = float.MaxValue;

        if (ticks <= 0)
            return false;

        ComputeProjectedLaunchOrigin(ship, targetX - ship.Position.X, targetY - ship.Position.Y, out float projectedStartX, out float projectedStartY);
        float baseRelativeX = (targetX - projectedStartX) / ticks - ship.Movement.X;
        float baseRelativeY = (targetY - projectedStartY) / ticks - ship.Movement.Y;

        var candidateSeeds = new (float X, float Y)[]
        {
            (baseRelativeX, baseRelativeY),
            (baseRelativeX * 0.85f, baseRelativeY * 0.85f),
            (baseRelativeX * 1.15f, baseRelativeY * 1.15f),
            Rotate(baseRelativeX, baseRelativeY, 10f),
            Rotate(baseRelativeX, baseRelativeY, -10f),
        };

        Vector bestMovement = new();
        float bestMiss = float.MaxValue;
        bool converged = false;

        foreach (var seed in candidateSeeds)
        {
            float relativeX = seed.X;
            float relativeY = seed.Y;

            for (int iteration = 0; iteration < PointPredictionIterations; iteration++)
            {
                if (!TryComputeMissDistance(ship, relativeX, relativeY, ticks, gravitySources, targetX, targetY, out float errorX, out float errorY,
                        out float currentMiss))
                {
                    break;
                }

                if (currentMiss < bestMiss)
                {
                    bestMiss = currentMiss;
                    bestMovement = new Vector(relativeX, relativeY);
                    converged = true;
                }

                if (currentMiss <= PredictionHitTolerance)
                    break;

                if (!TryApplyCorrectionStep(ship, gravitySources, ticks, targetX, targetY, relativeX, relativeY, errorX, errorY, currentMiss,
                        out float nextRelativeX, out float nextRelativeY, out float nextMiss))
                {
                    break;
                }

                relativeX = nextRelativeX;
                relativeY = nextRelativeY;

                if (nextMiss < bestMiss)
                {
                    bestMiss = nextMiss;
                    bestMovement = new Vector(relativeX, relativeY);
                    converged = true;
                }
            }
        }

        if (!converged)
            return false;

        relativeMovement = bestMovement;
        missDistance = bestMiss;
        return true;
    }

    private static (float X, float Y) Rotate(float x, float y, float degrees)
    {
        float radians = MathF.PI * degrees / 180f;
        float cos = MathF.Cos(radians);
        float sin = MathF.Sin(radians);
        return (x * cos - y * sin, x * sin + y * cos);
    }

    private static List<ShotProfile> BuildShotProfiles(ClassicShipControllable ship, bool adaptive)
    {
        float baseLoad = Math.Clamp(DefaultShotLoad, ship.ShotLauncher.MinimumLoad, ship.ShotLauncher.MaximumLoad);
        float baseDamage = Math.Clamp(DefaultShotDamage, ship.ShotLauncher.MinimumDamage, ship.ShotLauncher.MaximumDamage);
        var profiles = new List<ShotProfile>();
        AddOrUpdateShotProfile(profiles, baseLoad, baseDamage, 0f);

        if (!adaptive)
            return profiles;

        float[] scales = { 0.95f, 0.9f, 0.82f, 0.74f, 0.66f, 0.58f };
        foreach (float scale in scales)
        {
            float load = Math.Clamp(baseLoad * scale, ship.ShotLauncher.MinimumLoad, ship.ShotLauncher.MaximumLoad);
            float damage = Math.Clamp(baseDamage * scale, ship.ShotLauncher.MinimumDamage, ship.ShotLauncher.MaximumDamage);
            float preferencePenalty = (1f - scale) * 1.35f;
            AddOrUpdateShotProfile(profiles, load, damage, preferencePenalty);
        }

        return profiles;
    }

    private static void AddOrUpdateShotProfile(List<ShotProfile> profiles, float load, float damage, float preferencePenalty)
    {
        const float mergeTolerance = 0.01f;
        for (int index = 0; index < profiles.Count; index++)
        {
            ShotProfile existing = profiles[index];
            if (MathF.Abs(existing.Load - load) > mergeTolerance || MathF.Abs(existing.Damage - damage) > mergeTolerance)
                continue;

            if (preferencePenalty < existing.PreferencePenalty)
                profiles[index] = new ShotProfile(load, damage, preferencePenalty);

            return;
        }

        profiles.Add(new ShotProfile(load, damage, preferencePenalty));
    }

    private static bool TryComputeMissDistance(ClassicShipControllable ship, float relativeX, float relativeY, int ticks,
        IReadOnlyList<GravitySource> gravitySources, float targetX, float targetY, out float errorX, out float errorY, out float missDistance)
    {
        errorX = 0f;
        errorY = 0f;
        missDistance = float.MaxValue;

        SimulateShotWithGravity(ship, relativeX, relativeY, ticks, gravitySources, out float projectileX, out float projectileY);

        errorX = targetX - projectileX;
        errorY = targetY - projectileY;
        missDistance = MathF.Sqrt(errorX * errorX + errorY * errorY);
        if (float.IsNaN(missDistance) || float.IsInfinity(missDistance))
            return false;

        return true;
    }

    private static bool TryApplyCorrectionStep(ClassicShipControllable ship, IReadOnlyList<GravitySource> gravitySources, int ticks, float targetX,
        float targetY, float currentRelativeX, float currentRelativeY, float errorX, float errorY, float currentMissDistance,
        out float nextRelativeX, out float nextRelativeY, out float nextMissDistance)
    {
        nextRelativeX = currentRelativeX;
        nextRelativeY = currentRelativeY;
        nextMissDistance = currentMissDistance;

        var correctionCandidates = new List<(float DeltaX, float DeltaY)>(capacity: 2);

        if (TryBuildJacobianCorrection(ship, gravitySources, ticks, targetX, targetY, currentRelativeX, currentRelativeY, errorX, errorY, currentMissDistance,
                out float jacobianDeltaX, out float jacobianDeltaY))
        {
            correctionCandidates.Add((jacobianDeltaX, jacobianDeltaY));
        }

        float legacyCorrectionScale = 1f / ticks;
        correctionCandidates.Add((errorX * legacyCorrectionScale, errorY * legacyCorrectionScale));

        float[] dampingFactors = { 1f, 0.65f, 0.35f, 0.18f };
        foreach ((float correctionX, float correctionY) in correctionCandidates)
        {
            foreach (float damping in dampingFactors)
            {
                float candidateRelativeX = currentRelativeX + correctionX * damping;
                float candidateRelativeY = currentRelativeY + correctionY * damping;
                if (float.IsNaN(candidateRelativeX) || float.IsInfinity(candidateRelativeX) || float.IsNaN(candidateRelativeY) ||
                    float.IsInfinity(candidateRelativeY))
                {
                    continue;
                }

                if (!TryComputeMissDistance(ship, candidateRelativeX, candidateRelativeY, ticks, gravitySources, targetX, targetY, out _, out _,
                        out float candidateMissDistance))
                {
                    continue;
                }

                if (candidateMissDistance + NumericEpsilon >= nextMissDistance)
                    continue;

                nextRelativeX = candidateRelativeX;
                nextRelativeY = candidateRelativeY;
                nextMissDistance = candidateMissDistance;
            }
        }

        return nextMissDistance + NumericEpsilon < currentMissDistance;
    }

    private static bool TryBuildJacobianCorrection(ClassicShipControllable ship, IReadOnlyList<GravitySource> gravitySources, int ticks, float targetX,
        float targetY, float currentRelativeX, float currentRelativeY, float errorX, float errorY, float currentMissDistance, out float deltaX,
        out float deltaY)
    {
        deltaX = 0f;
        deltaY = 0f;

        float probeStep = MathF.Max(0.02f, MathF.Min(0.2f, (currentMissDistance / MathF.Max(1, ticks)) * 1.8f));

        if (!TryComputeMissDistance(ship, currentRelativeX + probeStep, currentRelativeY, ticks, gravitySources, targetX, targetY, out float errorXdx,
                out float errorYdx, out _))
        {
            return false;
        }

        if (!TryComputeMissDistance(ship, currentRelativeX, currentRelativeY + probeStep, ticks, gravitySources, targetX, targetY, out float errorXdy,
                out float errorYdy, out _))
        {
            return false;
        }

        float a11 = (errorXdx - errorX) / probeStep;
        float a21 = (errorYdx - errorY) / probeStep;
        float a12 = (errorXdy - errorX) / probeStep;
        float a22 = (errorYdy - errorY) / probeStep;
        float determinant = a11 * a22 - a12 * a21;

        if (MathF.Abs(determinant) <= 0.0001f)
            return false;

        float rightSideX = -errorX;
        float rightSideY = -errorY;
        deltaX = (rightSideX * a22 - a12 * rightSideY) / determinant;
        deltaY = (a11 * rightSideY - rightSideX * a21) / determinant;

        if (float.IsNaN(deltaX) || float.IsInfinity(deltaX) || float.IsNaN(deltaY) || float.IsInfinity(deltaY))
            return false;

        float maxCorrectionMagnitude = MathF.Max(0.2f, (currentMissDistance / MathF.Max(1, ticks)) * 2.25f);
        float correctionMagnitude = MathF.Sqrt(deltaX * deltaX + deltaY * deltaY);
        if (correctionMagnitude <= NumericEpsilon)
            return false;

        if (correctionMagnitude > maxCorrectionMagnitude)
        {
            float clampScale = maxCorrectionMagnitude / correctionMagnitude;
            deltaX *= clampScale;
            deltaY *= clampScale;
        }

        return true;
    }

    private static void SimulateShotWithGravity(ClassicShipControllable ship, float relativeMovementX, float relativeMovementY, int ticks,
        IReadOnlyList<GravitySource> gravitySources, out float positionX, out float positionY)
    {
        ComputeShotLaunchState(ship, relativeMovementX, relativeMovementY, out float startX, out float startY, out float movementX, out float movementY);
        SimulateBodyWithGravity(startX, startY, movementX, movementY, ticks, gravitySources, null, out positionX, out positionY);
    }

    private static void ComputeShotLaunchState(ClassicShipControllable ship, float relativeMovementX, float relativeMovementY, out float startX,
        out float startY, out float movementX, out float movementY)
    {
        movementX = ship.Movement.X + relativeMovementX;
        movementY = ship.Movement.Y + relativeMovementY;
        startX = ship.Position.X;
        startY = ship.Position.Y;
        ComputeProjectedLaunchOrigin(ship, movementX, movementY, out startX, out startY);
    }

    private static void ComputeProjectedLaunchOrigin(ClassicShipControllable ship, float directionX, float directionY, out float startX, out float startY)
    {
        startX = ship.Position.X;
        startY = ship.Position.Y;

        float directionSquared = directionX * directionX + directionY * directionY;
        if (directionSquared <= NumericEpsilon)
            return;

        float directionLength = MathF.Sqrt(directionSquared);
        float launchDistance = MathF.Max(0f, ship.Size + ProjectileSpawnPaddingDistance);
        float inverseDirection = 1f / directionLength;
        startX += directionX * inverseDirection * launchDistance;
        startY += directionY * inverseDirection * launchDistance;
    }

    private static void SimulateBodyWithGravity(float startX, float startY, float startMovementX, float startMovementY, int ticks,
        IReadOnlyList<GravitySource> gravitySources, Unit? excludedSourceUnit, out float positionX, out float positionY)
    {
        float currentX = startX;
        float currentY = startY;
        float currentMovementX = startMovementX;
        float currentMovementY = startMovementY;

        for (int tick = 0; tick < ticks; tick++)
        {
            ComputeGravityAcceleration(currentX, currentY, tick, gravitySources, excludedSourceUnit, out float accelerationX, out float accelerationY);
            currentMovementX += accelerationX;
            currentMovementY += accelerationY;
            currentX += currentMovementX;
            currentY += currentMovementY;
        }

        positionX = currentX;
        positionY = currentY;
    }

    private static void ComputeGravityAcceleration(float positionX, float positionY, int tick, IReadOnlyList<GravitySource> gravitySources,
        Unit? excludedSourceUnit, out float accelerationX, out float accelerationY)
    {
        accelerationX = 0f;
        accelerationY = 0f;

        foreach (GravitySource source in gravitySources)
        {
            if (excludedSourceUnit is not null && ReferenceEquals(source.Unit, excludedSourceUnit))
                continue;

            float sourceX = source.PositionX + source.MovementX * tick;
            float sourceY = source.PositionY + source.MovementY * tick;
            float deltaX = sourceX - positionX;
            float deltaY = sourceY - positionY;
            float distanceSquared = deltaX * deltaX + deltaY * deltaY;

            if (distanceSquared <= NumericEpsilon)
                continue;

            if (distanceSquared > GravityInfluenceRangeSquared &&
                (source.GravityWellRadius <= NumericEpsilon || distanceSquared > source.GravityWellRadius * source.GravityWellRadius))
            {
                continue;
            }

            float distance = MathF.Sqrt(distanceSquared);
            float minimumDistance = MathF.Max(source.Radius, GravityMinimumDistance);

            if (distance < minimumDistance)
            {
                distance = minimumDistance;
                distanceSquared = minimumDistance * minimumDistance;
            }

            float effectiveGravity = source.Gravity;
            if (source.GravityWellForce > NumericEpsilon && source.GravityWellRadius > NumericEpsilon && distance < source.GravityWellRadius)
            {
                float normalizedDepth = 1f - (distance / source.GravityWellRadius);
                float softenedWellFactor = normalizedDepth * normalizedDepth;
                effectiveGravity += source.GravityWellForce * softenedWellFactor;
            }

            if (effectiveGravity <= NumericEpsilon)
                continue;

            float inverseDistance = 1f / distance;
            float attraction = effectiveGravity / distanceSquared;
            accelerationX += deltaX * inverseDistance * attraction;
            accelerationY += deltaY * inverseDistance * attraction;
        }
    }

    private static List<GravitySource> GetOrBuildGravitySources(ClassicShipControllable ship, Dictionary<Cluster, List<GravitySource>>? cache)
    {
        if (cache is not null && cache.TryGetValue(ship.Cluster, out List<GravitySource>? cached))
            return cached;

        List<GravitySource> gravitySources = BuildGravitySources(ship.Cluster);

        if (cache is not null)
            cache[ship.Cluster] = gravitySources;

        return gravitySources;
    }

    private static List<GravitySource> BuildGravitySources(Cluster cluster)
    {
        List<GravitySource> gravitySources = new();

        foreach (Unit unit in cluster.Units)
        {
            float gravity = unit.Gravity;
            float gravityWellRadius = 0f;
            float gravityWellForce = 0f;

            if (unit is BlackHole blackHole)
            {
                gravityWellRadius = blackHole.GravityWellRadius;
                gravityWellForce = blackHole.GravityWellForce;
            }

            if (gravity <= NumericEpsilon && gravityWellForce <= NumericEpsilon)
                continue;

            gravitySources.Add(new GravitySource(
                unit,
                unit.Position.X,
                unit.Position.Y,
                unit.Movement.X,
                unit.Movement.Y,
                gravity,
                MathF.Max(0f, unit.Radius),
                gravityWellRadius,
                gravityWellForce
            ));
        }

        return gravitySources;
    }

    private static bool TryPredictRelativeMovement(Vector sourcePosition, Vector sourceMovement, Vector targetPosition, Vector targetMovement,
        float projectileSpeed, out Vector relativeMovement, out float predictedFlightTicks)
    {
        relativeMovement = new Vector();
        predictedFlightTicks = 0f;

        if (projectileSpeed <= NumericEpsilon)
            return false;

        Vector relativePosition = targetPosition - sourcePosition;
        Vector relativeVelocity = targetMovement - sourceMovement;

        float a = Dot(relativeVelocity, relativeVelocity) - (projectileSpeed * projectileSpeed);
        float b = 2f * Dot(relativePosition, relativeVelocity);
        float c = Dot(relativePosition, relativePosition);

        float time;
        if (MathF.Abs(a) <= NumericEpsilon)
        {
            if (MathF.Abs(b) <= NumericEpsilon)
                return false;

            time = -c / b;
            if (time <= NumericEpsilon)
                return false;
        }
        else
        {
            float discriminant = b * b - (4f * a * c);
            if (discriminant < 0f)
                return false;

            float sqrtDiscriminant = MathF.Sqrt(discriminant);
            float timeA = (-b - sqrtDiscriminant) / (2f * a);
            float timeB = (-b + sqrtDiscriminant) / (2f * a);

            time = float.MaxValue;
            if (timeA > NumericEpsilon)
                time = timeA;
            if (timeB > NumericEpsilon && timeB < time)
                time = timeB;

            if (time == float.MaxValue)
                return false;
        }

        Vector interceptDelta = relativePosition + (relativeVelocity * time);
        if (interceptDelta.Length <= NumericEpsilon)
            return false;

        relativeMovement = interceptDelta / time;
        predictedFlightTicks = time;
        return true;
    }
}

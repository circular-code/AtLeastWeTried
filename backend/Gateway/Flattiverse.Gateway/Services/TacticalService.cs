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
    private const float GravityMinimumDistance = 6f;
    private const float GravityInfluenceRange = 3200f;
    private const float GravityInfluenceRangeSquared = GravityInfluenceRange * GravityInfluenceRange;

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
        string TargetUnitName
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
            if (ShouldAutoFireCore(entry.Key, tick, gravitySourcesByCluster, out var request))
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
            return ShouldAutoFireCore(controllableId, tick, null, out request);
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
        out AutoFireRequest request)
    {
        request = default;

        if (!_states.TryGetValue(controllableId, out var state) || state.Mode == TacticalMode.Off || state.Ship is null)
            return false;

        var ship = state.Ship;

        if (!ship.Active || !ship.Alive)
            return false;

        if (!ship.ShotLauncher.Exists || !ship.ShotMagazine.Exists)
            return false;

        if (ship.ShotLauncher.Status == SubsystemStatus.Upgrading || ship.ShotMagazine.Status == SubsystemStatus.Upgrading)
            return false;

        if (ship.ShotMagazine.CurrentShots < 1f)
            return false;

        if (state.HasLastFireTick && tick - state.LastFireTick < AutoFireCooldownTicks)
            return false;

        Unit? target = ResolveTarget(ship, state);
        if (target is null || !PassesTargetBasicChecks(ship, target))
            return false;

        List<GravitySource> gravitySources = GetOrBuildGravitySources(ship, gravitySourcesByCluster);

        if (!TryBuildPredictedShotRequest(controllableId, ship, target, tick, gravitySources, out request))
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

    private static Unit? FindNearestEnemyTarget(ClassicShipControllable ship)
    {
        Unit? bestTarget = null;
        float bestDistanceSquared = float.MaxValue;
        byte ownPlayerId = ship.Cluster.Galaxy.Player.Id;
        Vector ownPosition = ship.Position;

        foreach (Unit unit in ship.Cluster.Units)
        {
            if (unit is not PlayerUnit candidate)
                continue;

            if (candidate.Player.Id == ownPlayerId)
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

    private static bool TryBuildPredictedShotRequest(string controllableId, ClassicShipControllable ship, Unit target, uint tick,
        IReadOnlyList<GravitySource> gravitySources, out AutoFireRequest request)
    {
        request = default;

        float requestedLoad = Math.Clamp(DefaultShotLoad, ship.ShotLauncher.MinimumLoad, ship.ShotLauncher.MaximumLoad);
        float requestedDamage = Math.Clamp(DefaultShotDamage, ship.ShotLauncher.MinimumDamage, ship.ShotLauncher.MaximumDamage);
        float shotSpeed = Math.Clamp(DefaultShotRelativeSpeed, ship.ShotLauncher.MinimumRelativeMovement, ship.ShotLauncher.MaximumRelativeMovement);
        ushort minimumTicks = ship.ShotLauncher.MinimumTicks;
        ushort maximumTicks = ship.ShotLauncher.MaximumTicks;
        if (minimumTicks > maximumTicks)
            return false;

        int baselineTicksRaw = EstimateBaselineTicks(ship.Position, ship.Movement, target.Position, target.Movement, shotSpeed);
        int baselineTicks = Math.Clamp(baselineTicksRaw, minimumTicks, maximumTicks);
        int startTicks = Math.Max(minimumTicks, baselineTicks - PredictionTickSearchRadius);
        int endTicks = Math.Min(maximumTicks, baselineTicks + PredictionTickSearchRadius);

        float bestMissDistance = float.MaxValue;
        float bestScore = float.MaxValue;
        Vector bestRelativeMovement = new();
        ushort bestTicks = 0;
        float bestEnergyCost = 0f;
        float bestIonCost = 0f;
        float bestNeutrinoCost = 0f;
        bool bestFound = false;

        for (int ticks = startTicks; ticks <= endTicks; ticks++)
        {
            if (!TryPredictRelativeMovementWithGravity(ship, target, gravitySources, ticks, out Vector relativeMovement, out float missDistance))
                continue;

            if (!ship.ShotLauncher.CalculateCost(relativeMovement, (ushort)ticks, requestedLoad, requestedDamage, out float energyCost,
                    out float ionCost, out float neutrinoCost))
                continue;

            if (energyCost > ship.EnergyBattery.Current + NumericEpsilon)
                continue;

            if (ionCost > ship.IonBattery.Current + NumericEpsilon)
                continue;

            if (neutrinoCost > ship.NeutrinoBattery.Current + NumericEpsilon)
                continue;

            float score = missDistance * PredictionMissScoreWeight + ticks * PredictionTickPenalty;
            bool scoreTie = MathF.Abs(score - bestScore) <= NumericEpsilon;
            if (score > bestScore + NumericEpsilon)
                continue;

            if (scoreTie)
            {
                bool worseMissDistance = missDistance > bestMissDistance + NumericEpsilon;
                bool sameMissDistance = MathF.Abs(missDistance - bestMissDistance) <= NumericEpsilon;
                bool worseOrEqualTicks = ticks >= bestTicks;

                if (worseMissDistance || (sameMissDistance && worseOrEqualTicks))
                    continue;
            }

            bestMissDistance = missDistance;
            bestScore = score;
            bestRelativeMovement = relativeMovement;
            bestTicks = (ushort)ticks;
            bestEnergyCost = energyCost;
            bestIonCost = ionCost;
            bestNeutrinoCost = neutrinoCost;
            bestFound = true;
        }

        if (!bestFound || bestMissDistance > PredictionMaximumMissDistance)
            return false;

        if (bestMissDistance > PredictionQualityGate)
            return false;

        if (bestEnergyCost > ship.EnergyBattery.Current + NumericEpsilon)
            return false;

        if (bestIonCost > ship.IonBattery.Current + NumericEpsilon)
            return false;

        if (bestNeutrinoCost > ship.NeutrinoBattery.Current + NumericEpsilon)
            return false;

        request = new AutoFireRequest(controllableId, ship, bestRelativeMovement, bestTicks, requestedLoad, requestedDamage, tick, target.Name);
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
        int ticks, out Vector relativeMovement, out float missDistance)
    {
        relativeMovement = new Vector();
        missDistance = float.MaxValue;

        if (ticks <= 0)
            return false;

        SimulateBodyWithGravity(target.Position.X, target.Position.Y, target.Movement.X, target.Movement.Y, ticks, gravitySources, target, out float targetX,
            out float targetY);

        float relativeX = (targetX - ship.Position.X) / ticks - ship.Movement.X;
        float relativeY = (targetY - ship.Position.Y) / ticks - ship.Movement.Y;

        for (int iteration = 0; iteration < PredictionIterations; iteration++)
        {
            SimulateBodyWithGravity(ship.Position.X, ship.Position.Y, ship.Movement.X + relativeX, ship.Movement.Y + relativeY, ticks, gravitySources, null,
                out float projectileX, out float projectileY);

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
                effectiveGravity += source.GravityWellForce;

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

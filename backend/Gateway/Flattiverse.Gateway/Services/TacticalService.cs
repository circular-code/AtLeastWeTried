using Flattiverse.Connector;
using Flattiverse.Connector.Events;
using Flattiverse.Connector.GalaxyHierarchy;
using Flattiverse.Connector.Units;
using Flattiverse.Gateway.Connector;

namespace Flattiverse.Gateway.Services;

/// <summary>
/// Per-player-session service that stores tactical intent for each controllable and
/// evaluates auto-fire decisions on every GalaxyTick event.
/// Event handlers run on the connector event loop, but sparkleNekoState can also be touched from command/overlay paths,
/// so accesses are synchronized.
/// </summary>
public sealed class TacticalService : IConnectorEventHandler
{
    private const float UltraMegaKawaiiShotRelativeSpeed = 2f;
    private const float UltraMegaKawaiiShotLoad = 12f;
    private const float UltraMegaKawaiiShotDamage = 8f;
    private const uint UltraMegaNyanAutoFireCooldownTicks = 2;
    private const float UltraMegaWaifuMaxTargetDistance = 1400f;
    private const float UltraMegaWaifuMinimumTargetDistance = 8f;
    private const float UltraMegaKawaiiNumericEpsilon = 0.0001f;
    private const int UltraMegaNyanPredictionIterations = 6;
    private const int UltraMegaNyanPredictionTickSearchRadius = 10;
    private const float UltraMegaNyanPredictionHitTolerance = 2.5f;
    private const float UltraMegaNyanPredictionQualityGate = 6.5f;
    private const float UltraMegaNyanPredictionMaximumMissDistance = 18f;
    private const float UltraMegaNyanPredictionMissScoreWeight = 3.25f;
    private const float UltraMegaNyanPredictionTickPenalty = 0.12f;
    private const int UltraMegaNyanTargetPredictionIterations = 14;
    private const int UltraMegaNyanTargetPredictionTickSearchRadius = 18;
    private const float UltraMegaNyanTargetPredictionMaximumMissDistance = 12f;
    private const float UltraMegaNyanTargetPredictionQualityGate = 4.5f;
    private const int UltraMegaNyanPointPredictionIterations = 24;
    private const float UltraMegaNyanPointPredictionMaximumMissDistance = 10f;
    private const float UltraMegaNyanPointPredictionQualityGate = 3.5f;
    private const float UltraMegaNyanPointPredictionHitTolerance = 1.1f;
    private const float UltraMegaNyanProjectileSpawnPaddingDistance = 2f;

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
        float Gravity
    );

    private readonly record struct ShotProfile(
        float Load,
        float Damage,
        float PreferencePenalty
    );

    private readonly Dictionary<string, TacticalState> _sparkleNekoStates = new();
    private readonly Queue<AutoFireRequest> _sparkleNekoPendingAutoFireRequests = new();
    private readonly object _sparkleNekoSync = new();

    public void Handle(FlattiverseEvent @event)
    {
        lock (_sparkleNekoSync)
        {
            if (@event is ControllableInfoEvent infoEvent && @event is DestroyedControllableInfoEvent)
            {
                MarkShipUnavailableCore(BuildControllableId(infoEvent.Player.Id, infoEvent.ControllableInfo.Id));
                return;
            }

            if (@event is ControllableInfoEvent closedInfoEvent && @event is ClosedControllableInfoEvent)
            {
                RemoveCore(BuildControllableId(closedInfoEvent.Player.Id, closedInfoEvent.ControllableInfo.Id));
                return;
            }

            if (@event is not GalaxyTickEvent tickEvent)
                return;

            EvaluateAutoFire(tickEvent.Tick);
        }
    }

    public void AttachControllable(string sparkleNekoControllableId, ClassicShipControllable sparkleNekoShip)
    {
        lock (_sparkleNekoSync)
        {
            var sparkleNekoState = GetOrCreateState(sparkleNekoControllableId);
            sparkleNekoState.Ship = sparkleNekoShip;
        }
    }

    public List<AutoFireRequest> DequeuePendingAutoFireRequests()
    {
        lock (_sparkleNekoSync)
        {
            if (_sparkleNekoPendingAutoFireRequests.Count == 0)
                return new List<AutoFireRequest>();

            var requests = new List<AutoFireRequest>(_sparkleNekoPendingAutoFireRequests.Count);

            while (_sparkleNekoPendingAutoFireRequests.Count > 0)
                requests.Add(_sparkleNekoPendingAutoFireRequests.Dequeue());

            return requests;
        }
    }

    private void EvaluateAutoFire(uint sparkleNekoTick)
    {
        if (_sparkleNekoStates.Count == 0)
            return;

        Dictionary<Cluster, List<GravitySource>> sparkleNekoGravitySourcesByCluster = new();

        foreach (var sparkleNekoEntry in _sparkleNekoStates)
        {
            if (ShouldAutoFireCore(sparkleNekoEntry.Key, sparkleNekoTick, sparkleNekoGravitySourcesByCluster, enforceCooldown: true, requireTargetMode: false, out var sparkleNekoRequest))
                _sparkleNekoPendingAutoFireRequests.Enqueue(sparkleNekoRequest);
        }
    }

    public void SetMode(string sparkleNekoControllableId, TacticalMode mode)
    {
        lock (_sparkleNekoSync)
        {
            var sparkleNekoState = GetOrCreateState(sparkleNekoControllableId);
            sparkleNekoState.Mode = mode;

            if (mode == TacticalMode.Off)
                sparkleNekoState.TargetId = null;
        }
    }

    public void SetTarget(string sparkleNekoControllableId, string targetId)
    {
        lock (_sparkleNekoSync)
        {
            var sparkleNekoState = GetOrCreateState(sparkleNekoControllableId);
            sparkleNekoState.TargetId = targetId;
        }
    }

    public bool IsTargetAllowedForTargetMode(ClassicShipControllable sparkleNekoShip, string targetId)
    {
        if (string.IsNullOrWhiteSpace(targetId))
            return false;

        lock (_sparkleNekoSync)
            return IsTargetAllowedForTargetModeCore(sparkleNekoShip, targetId);
    }

    public void ClearTarget(string sparkleNekoControllableId)
    {
        lock (_sparkleNekoSync)
        {
            if (_sparkleNekoStates.TryGetValue(sparkleNekoControllableId, out var sparkleNekoState))
                sparkleNekoState.TargetId = null;
        }
    }

    public bool ShouldAutoFire(string sparkleNekoControllableId, uint sparkleNekoTick, out AutoFireRequest sparkleNekoRequest)
    {
        lock (_sparkleNekoSync)
            return ShouldAutoFireCore(sparkleNekoControllableId, sparkleNekoTick, null, enforceCooldown: true, requireTargetMode: false, out sparkleNekoRequest);
    }

    public bool TryBuildTargetBurstRequest(string sparkleNekoControllableId, uint sparkleNekoTick, out AutoFireRequest sparkleNekoRequest)
    {
        lock (_sparkleNekoSync)
            return ShouldAutoFireCore(sparkleNekoControllableId, sparkleNekoTick, null, enforceCooldown: false, requireTargetMode: true, out sparkleNekoRequest);
    }

    public bool TryBuildPointShotRequest(string sparkleNekoControllableId, ClassicShipControllable sparkleNekoShip, float targetX, float targetY, uint sparkleNekoTick,
        out AutoFireRequest sparkleNekoRequest)
    {
        lock (_sparkleNekoSync)
        {
            sparkleNekoRequest = default;

            if (!sparkleNekoShip.Active || !sparkleNekoShip.Alive)
                return false;
            if (ControllableRebuildState.IsRebuilding(sparkleNekoShip))
                return false;

            if (!sparkleNekoShip.ShotLauncher.Exists || !sparkleNekoShip.ShotMagazine.Exists)
                return false;

            if (sparkleNekoShip.ShotLauncher.Status == SubsystemStatus.Upgrading || sparkleNekoShip.ShotMagazine.Status == SubsystemStatus.Upgrading)
                return false;

            if (sparkleNekoShip.ShotMagazine.CurrentShots < 1f)
                return false;

            if (!PassesTargetBasicChecks(sparkleNekoShip, targetX, targetY))
                return false;

            List<GravitySource> sparkleNekoGravitySources = GetOrBuildGravitySources(sparkleNekoShip, null);
            return TryBuildPredictedShotRequestToPoint(sparkleNekoControllableId, sparkleNekoShip, targetX, targetY, sparkleNekoTick, sparkleNekoGravitySources, out sparkleNekoRequest);
        }
    }

    public void RegisterSuccessfulFire(string sparkleNekoControllableId, uint sparkleNekoTick)
    {
        lock (_sparkleNekoSync)
        {
            if (!_sparkleNekoStates.TryGetValue(sparkleNekoControllableId, out var sparkleNekoState))
                return;

            sparkleNekoState.LastFireTick = sparkleNekoTick;
            sparkleNekoState.HasLastFireTick = true;
        }
    }

    private bool ShouldAutoFireCore(string sparkleNekoControllableId, uint sparkleNekoTick, Dictionary<Cluster, List<GravitySource>>? sparkleNekoGravitySourcesByCluster,
        bool enforceCooldown, bool requireTargetMode, out AutoFireRequest sparkleNekoRequest)
    {
        sparkleNekoRequest = default;

        if (!_sparkleNekoStates.TryGetValue(sparkleNekoControllableId, out var sparkleNekoState) || sparkleNekoState.Mode == TacticalMode.Off || sparkleNekoState.Ship is null)
            return false;

        if (requireTargetMode && sparkleNekoState.Mode != TacticalMode.Target)
            return false;

        var sparkleNekoShip = sparkleNekoState.Ship;

        if (!sparkleNekoShip.Active || !sparkleNekoShip.Alive)
            return false;
        if (ControllableRebuildState.IsRebuilding(sparkleNekoShip))
            return false;

        if (!sparkleNekoShip.ShotLauncher.Exists || !sparkleNekoShip.ShotMagazine.Exists)
            return false;

        if (sparkleNekoShip.ShotLauncher.Status == SubsystemStatus.Upgrading || sparkleNekoShip.ShotMagazine.Status == SubsystemStatus.Upgrading)
            return false;

        if (sparkleNekoShip.ShotMagazine.CurrentShots < 1f)
            return false;

        if (enforceCooldown && sparkleNekoState.HasLastFireTick && sparkleNekoTick - sparkleNekoState.LastFireTick < UltraMegaNyanAutoFireCooldownTicks)
            return false;

        Unit? sparkleNekoTarget = ResolveTarget(sparkleNekoShip, sparkleNekoState);
        if (sparkleNekoTarget is null || !PassesTeamFilter(sparkleNekoShip, sparkleNekoTarget) || !PassesTargetBasicChecks(sparkleNekoShip, sparkleNekoTarget))
            return false;

        List<GravitySource> sparkleNekoGravitySources = GetOrBuildGravitySources(sparkleNekoShip, sparkleNekoGravitySourcesByCluster);
        bool sparkleNekoHighPrecisionTargeting = sparkleNekoState.Mode == TacticalMode.Target;

        if (!TryBuildPredictedShotRequest(sparkleNekoControllableId, sparkleNekoShip, sparkleNekoTarget, sparkleNekoTick, sparkleNekoGravitySources, sparkleNekoHighPrecisionTargeting, out sparkleNekoRequest))
            return false;

        return true;
    }

    public Dictionary<string, object?> BuildOverlay(string sparkleNekoControllableId)
    {
        lock (_sparkleNekoSync)
        {
            if (!_sparkleNekoStates.TryGetValue(sparkleNekoControllableId, out var sparkleNekoState))
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
                    sparkleNekoState.Mode switch
                    {
                        TacticalMode.Enemy => "enemy",
                        TacticalMode.Target => "target",
                        _ => "off"
                    }
                },
                { "targetId", sparkleNekoState.TargetId },
                { "hasTarget", !string.IsNullOrWhiteSpace(sparkleNekoState.TargetId) }
            };
        }
    }

    public void Remove(string sparkleNekoControllableId)
    {
        lock (_sparkleNekoSync)
            RemoveCore(sparkleNekoControllableId);
    }

    private void RemoveCore(string sparkleNekoControllableId)
    {
        _sparkleNekoStates.Remove(sparkleNekoControllableId);

        if (_sparkleNekoPendingAutoFireRequests.Count == 0)
            return;

        var kept = _sparkleNekoPendingAutoFireRequests.Where(sparkleNekoRequest => sparkleNekoRequest.ControllableId != sparkleNekoControllableId).ToList();
        _sparkleNekoPendingAutoFireRequests.Clear();

        foreach (var sparkleNekoRequest in kept)
            _sparkleNekoPendingAutoFireRequests.Enqueue(sparkleNekoRequest);
    }

    private void MarkShipUnavailableCore(string sparkleNekoControllableId)
    {
        if (_sparkleNekoStates.TryGetValue(sparkleNekoControllableId, out var sparkleNekoState))
            sparkleNekoState.Ship = null;

        if (_sparkleNekoPendingAutoFireRequests.Count == 0)
            return;

        var kept = _sparkleNekoPendingAutoFireRequests.Where(sparkleNekoRequest => sparkleNekoRequest.ControllableId != sparkleNekoControllableId).ToList();
        _sparkleNekoPendingAutoFireRequests.Clear();

        foreach (var sparkleNekoRequest in kept)
            _sparkleNekoPendingAutoFireRequests.Enqueue(sparkleNekoRequest);
    }

    private TacticalState GetOrCreateState(string sparkleNekoControllableId)
    {
        if (_sparkleNekoStates.TryGetValue(sparkleNekoControllableId, out var sparkleNekoState))
            return sparkleNekoState;

        sparkleNekoState = new TacticalState();
        _sparkleNekoStates[sparkleNekoControllableId] = sparkleNekoState;
        return sparkleNekoState;
    }

    private static string BuildControllableId(int playerId, int sparkleNekoControllableId)
    {
        return $"p{playerId}-c{sparkleNekoControllableId}";
    }

    private static bool TryParseControllableId(string value, out byte playerId, out byte sparkleNekoControllableId)
    {
        playerId = 0;
        sparkleNekoControllableId = 0;

        if (string.IsNullOrWhiteSpace(value))
            return false;

        if (!value.StartsWith('p'))
            return false;

        int separatorIndex = value.IndexOf("-c", StringComparison.Ordinal);
        if (separatorIndex <= 1 || separatorIndex + 2 >= value.Length)
            return false;

        if (!byte.TryParse(value.AsSpan(1, separatorIndex - 1), out playerId))
            return false;

        return byte.TryParse(value.AsSpan(separatorIndex + 2), out sparkleNekoControllableId);
    }

    private static float Dot(Vector left, Vector right)
    {
        return (left.X * right.X) + (left.Y * right.Y);
    }

    private static Unit? ResolveTarget(ClassicShipControllable sparkleNekoShip, TacticalState sparkleNekoState)
    {
        return sparkleNekoState.Mode switch
        {
            TacticalMode.Target => ResolvePinnedTarget(sparkleNekoShip, sparkleNekoState.TargetId),
            TacticalMode.Enemy => FindNearestEnemyTarget(sparkleNekoShip),
            _ => null
        };
    }

    private static Unit? ResolvePinnedTarget(ClassicShipControllable sparkleNekoShip, string? targetId)
    {
        if (string.IsNullOrWhiteSpace(targetId))
            return null;

        if (TryParseControllableId(targetId, out byte targetPlayerId, out byte targetControllableId))
        {
            foreach (Unit unit in sparkleNekoShip.Cluster.Units)
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

        foreach (Unit unit in sparkleNekoShip.Cluster.Units)
        {
            if (unit.Name == targetId)
                return unit;
        }

        return null;
    }

    private static bool IsTargetAllowedForTargetModeCore(ClassicShipControllable sparkleNekoShip, string targetId)
    {
        if (TryParseControllableId(targetId, out byte targetPlayerId, out _))
        {
            if (sparkleNekoShip.Cluster.Galaxy.Players.TryGet(targetPlayerId, out Player? targetPlayer) &&
                targetPlayer is not null &&
                targetPlayer.Team.Id == sparkleNekoShip.Cluster.Galaxy.Player.Team.Id)
            {
                return false;
            }
        }

        Unit? resolved = ResolvePinnedTarget(sparkleNekoShip, targetId);
        if (resolved is null)
            return true;

        return PassesTeamFilter(sparkleNekoShip, resolved);
    }

    private static Unit? FindNearestEnemyTarget(ClassicShipControllable sparkleNekoShip)
    {
        Unit? bestTarget = null;
        float bestDistanceSquared = float.MaxValue;
        byte ownPlayerId = sparkleNekoShip.Cluster.Galaxy.Player.Id;
        byte ownTeamId = sparkleNekoShip.Cluster.Galaxy.Player.Team.Id;
        Vector ownPosition = sparkleNekoShip.Position;

        foreach (Unit unit in sparkleNekoShip.Cluster.Units)
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

    private static bool PassesTeamFilter(ClassicShipControllable sparkleNekoShip, Unit sparkleNekoTarget)
    {
        if (sparkleNekoTarget is not PlayerUnit targetPlayerUnit)
            return true;

        return targetPlayerUnit.Player.Team.Id != sparkleNekoShip.Cluster.Galaxy.Player.Team.Id;
    }

    private static bool PassesTargetBasicChecks(ClassicShipControllable sparkleNekoShip, Unit sparkleNekoTarget)
    {
        if (!sparkleNekoTarget.Cluster.Active || sparkleNekoTarget.Cluster != sparkleNekoShip.Cluster)
            return false;

        if (sparkleNekoTarget is PlayerUnit targetPlayerUnit)
        {
            if (!targetPlayerUnit.ControllableInfo.Alive)
                return false;
        }

        Vector toTarget = sparkleNekoTarget.Position - sparkleNekoShip.Position;
        float distance = toTarget.Length;

        if (distance < UltraMegaWaifuMinimumTargetDistance)
            return false;

        if (distance > UltraMegaWaifuMaxTargetDistance)
            return false;

        return true;
    }

    private static bool PassesTargetBasicChecks(ClassicShipControllable sparkleNekoShip, float targetX, float targetY)
    {
        if (!sparkleNekoShip.Cluster.Active)
            return false;

        float deltaX = targetX - sparkleNekoShip.Position.X;
        float deltaY = targetY - sparkleNekoShip.Position.Y;
        float distance = MathF.Sqrt(deltaX * deltaX + deltaY * deltaY);

        if (distance < UltraMegaWaifuMinimumTargetDistance)
            return false;

        if (distance > UltraMegaWaifuMaxTargetDistance)
            return false;

        return true;
    }

    private static bool TryBuildPredictedShotRequest(string sparkleNekoControllableId, ClassicShipControllable sparkleNekoShip, Unit sparkleNekoTarget, uint sparkleNekoTick,
        IReadOnlyList<GravitySource> sparkleNekoGravitySources, bool sparkleNekoHighPrecisionTargeting, out AutoFireRequest sparkleNekoRequest)
    {
        sparkleNekoRequest = default;

        float shotSpeed = Math.Clamp(UltraMegaKawaiiShotRelativeSpeed, sparkleNekoShip.ShotLauncher.MinimumRelativeMovement, sparkleNekoShip.ShotLauncher.MaximumRelativeMovement);
        List<ShotProfile> shotProfiles = BuildShotProfiles(sparkleNekoShip, sparkleNekoHighPrecisionTargeting);
        ushort minimumTicks = sparkleNekoShip.ShotLauncher.MinimumTicks;
        ushort maximumTicks = sparkleNekoShip.ShotLauncher.MaximumTicks;
        if (minimumTicks > maximumTicks)
            return false;

        int tickSearchRadius = sparkleNekoHighPrecisionTargeting ? UltraMegaNyanTargetPredictionTickSearchRadius : UltraMegaNyanPredictionTickSearchRadius;
        int baselineTicksRaw = EstimateBaselineTicks(sparkleNekoShip.Position, sparkleNekoShip.Movement, sparkleNekoTarget.Position, sparkleNekoTarget.Movement, shotSpeed);
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
            if (!TryPredictRelativeMovementWithGravity(sparkleNekoShip, sparkleNekoTarget, sparkleNekoGravitySources, ticks, sparkleNekoHighPrecisionTargeting, out Vector sparkleNekoRelativeMovement,
                    out float sparkleNekoMissDistance))
            {
                continue;
            }

            foreach (ShotProfile sparkleNekoProfile in shotProfiles)
            {
                if (!sparkleNekoShip.ShotLauncher.CalculateCost(sparkleNekoRelativeMovement, (ushort)ticks, sparkleNekoProfile.Load, sparkleNekoProfile.Damage, out float energyCost,
                        out float ionCost, out float neutrinoCost))
                {
                    continue;
                }

                if (energyCost > sparkleNekoShip.EnergyBattery.Current + UltraMegaKawaiiNumericEpsilon)
                    continue;

                if (ionCost > sparkleNekoShip.IonBattery.Current + UltraMegaKawaiiNumericEpsilon)
                    continue;

                if (neutrinoCost > sparkleNekoShip.NeutrinoBattery.Current + UltraMegaKawaiiNumericEpsilon)
                    continue;

                float score = sparkleNekoMissDistance * UltraMegaNyanPredictionMissScoreWeight + ticks * UltraMegaNyanPredictionTickPenalty + sparkleNekoProfile.PreferencePenalty;
                bool scoreTie = MathF.Abs(score - bestScore) <= UltraMegaKawaiiNumericEpsilon;
                if (score > bestScore + UltraMegaKawaiiNumericEpsilon)
                    continue;

                if (scoreTie)
                {
                    bool worseMissDistance = sparkleNekoMissDistance > bestMissDistance + UltraMegaKawaiiNumericEpsilon;
                    bool sameMissDistance = MathF.Abs(sparkleNekoMissDistance - bestMissDistance) <= UltraMegaKawaiiNumericEpsilon;
                    bool worseOrEqualTicks = ticks >= bestTicks;
                    bool lowerOrEqualDamage = sparkleNekoProfile.Damage <= bestDamage + UltraMegaKawaiiNumericEpsilon;

                    if (worseMissDistance || (sameMissDistance && worseOrEqualTicks && lowerOrEqualDamage))
                        continue;
                }

                bestMissDistance = sparkleNekoMissDistance;
                bestScore = score;
                bestRelativeMovement = sparkleNekoRelativeMovement;
                bestTicks = (ushort)ticks;
                bestLoad = sparkleNekoProfile.Load;
                bestDamage = sparkleNekoProfile.Damage;
                bestEnergyCost = energyCost;
                bestIonCost = ionCost;
                bestNeutrinoCost = neutrinoCost;
                bestFound = true;
            }
        }

        float maximumMissDistance = sparkleNekoHighPrecisionTargeting ? UltraMegaNyanTargetPredictionMaximumMissDistance : UltraMegaNyanPredictionMaximumMissDistance;
        float qualityGate = sparkleNekoHighPrecisionTargeting ? UltraMegaNyanTargetPredictionQualityGate : UltraMegaNyanPredictionQualityGate;

        if (!bestFound || bestMissDistance > maximumMissDistance)
            return false;

        if (bestMissDistance > qualityGate)
            return false;

        if (bestEnergyCost > sparkleNekoShip.EnergyBattery.Current + UltraMegaKawaiiNumericEpsilon)
            return false;

        if (bestIonCost > sparkleNekoShip.IonBattery.Current + UltraMegaKawaiiNumericEpsilon)
            return false;

        if (bestNeutrinoCost > sparkleNekoShip.NeutrinoBattery.Current + UltraMegaKawaiiNumericEpsilon)
            return false;

        sparkleNekoRequest = new AutoFireRequest(sparkleNekoControllableId, sparkleNekoShip, bestRelativeMovement, bestTicks, bestLoad, bestDamage, sparkleNekoTick, sparkleNekoTarget.Name,
            bestMissDistance);
        return true;
    }

    private static bool TryBuildPredictedShotRequestToPoint(string sparkleNekoControllableId, ClassicShipControllable sparkleNekoShip, float targetX, float targetY, uint sparkleNekoTick,
        IReadOnlyList<GravitySource> sparkleNekoGravitySources, out AutoFireRequest sparkleNekoRequest)
    {
        sparkleNekoRequest = default;

        float shotSpeed = Math.Clamp(UltraMegaKawaiiShotRelativeSpeed, sparkleNekoShip.ShotLauncher.MinimumRelativeMovement, sparkleNekoShip.ShotLauncher.MaximumRelativeMovement);
        List<ShotProfile> shotProfiles = BuildShotProfiles(sparkleNekoShip, adaptive: true);
        ushort minimumTicks = sparkleNekoShip.ShotLauncher.MinimumTicks;
        ushort maximumTicks = sparkleNekoShip.ShotLauncher.MaximumTicks;
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
            if (!TryPredictRelativeMovementToPointWithGravity(sparkleNekoShip, targetX, targetY, sparkleNekoGravitySources, ticks, out Vector sparkleNekoRelativeMovement,
                    out float sparkleNekoMissDistance))
            {
                continue;
            }

            foreach (ShotProfile sparkleNekoProfile in shotProfiles)
            {
                if (!sparkleNekoShip.ShotLauncher.CalculateCost(sparkleNekoRelativeMovement, (ushort)ticks, sparkleNekoProfile.Load, sparkleNekoProfile.Damage, out float energyCost,
                        out float ionCost, out float neutrinoCost))
                {
                    continue;
                }

                if (energyCost > sparkleNekoShip.EnergyBattery.Current + UltraMegaKawaiiNumericEpsilon)
                    continue;

                if (ionCost > sparkleNekoShip.IonBattery.Current + UltraMegaKawaiiNumericEpsilon)
                    continue;

                if (neutrinoCost > sparkleNekoShip.NeutrinoBattery.Current + UltraMegaKawaiiNumericEpsilon)
                    continue;

                float score = sparkleNekoMissDistance * UltraMegaNyanPredictionMissScoreWeight + ticks * UltraMegaNyanPredictionTickPenalty + sparkleNekoProfile.PreferencePenalty;
                bool scoreTie = MathF.Abs(score - bestScore) <= UltraMegaKawaiiNumericEpsilon;
                if (score > bestScore + UltraMegaKawaiiNumericEpsilon)
                    continue;

                if (scoreTie)
                {
                    bool worseMissDistance = sparkleNekoMissDistance > bestMissDistance + UltraMegaKawaiiNumericEpsilon;
                    bool sameMissDistance = MathF.Abs(sparkleNekoMissDistance - bestMissDistance) <= UltraMegaKawaiiNumericEpsilon;
                    bool worseOrEqualTicks = ticks >= bestTicks;
                    bool lowerOrEqualDamage = sparkleNekoProfile.Damage <= bestDamage + UltraMegaKawaiiNumericEpsilon;

                    if (worseMissDistance || (sameMissDistance && worseOrEqualTicks && lowerOrEqualDamage))
                        continue;
                }

                bestMissDistance = sparkleNekoMissDistance;
                bestScore = score;
                bestRelativeMovement = sparkleNekoRelativeMovement;
                bestTicks = (ushort)ticks;
                bestLoad = sparkleNekoProfile.Load;
                bestDamage = sparkleNekoProfile.Damage;
                bestEnergyCost = energyCost;
                bestIonCost = ionCost;
                bestNeutrinoCost = neutrinoCost;
                bestFound = true;
            }
        }

        if (!bestFound || bestMissDistance > UltraMegaNyanPointPredictionMaximumMissDistance)
            return false;

        if (bestMissDistance > UltraMegaNyanPointPredictionQualityGate)
            return false;

        if (bestEnergyCost > sparkleNekoShip.EnergyBattery.Current + UltraMegaKawaiiNumericEpsilon)
            return false;

        if (bestIonCost > sparkleNekoShip.IonBattery.Current + UltraMegaKawaiiNumericEpsilon)
            return false;

        if (bestNeutrinoCost > sparkleNekoShip.NeutrinoBattery.Current + UltraMegaKawaiiNumericEpsilon)
            return false;

        string targetLabel = $"point:{targetX:0.###},{targetY:0.###}";
        sparkleNekoRequest = new AutoFireRequest(sparkleNekoControllableId, sparkleNekoShip, bestRelativeMovement, bestTicks, bestLoad, bestDamage, sparkleNekoTick, targetLabel,
            bestMissDistance);
        return true;
    }

    private static int EstimateBaselineTicks(Vector sourcePosition, Vector sourceMovement, Vector targetPosition, Vector targetMovement,
        float projectileSpeed)
    {
        float straightFlightTicks = projectileSpeed > UltraMegaKawaiiNumericEpsilon
            ? Vector.Distance(sourcePosition, targetPosition) / projectileSpeed
            : 0f;

        float predictedFlightTicks = straightFlightTicks;

        if (TryPredictRelativeMovement(sourcePosition, sourceMovement, targetPosition, targetMovement, projectileSpeed, out _, out float interceptTicks))
            predictedFlightTicks = interceptTicks;

        return (int)MathF.Ceiling(predictedFlightTicks + 2f);
    }

    private static bool TryPredictRelativeMovementWithGravity(ClassicShipControllable sparkleNekoShip, Unit sparkleNekoTarget, IReadOnlyList<GravitySource> sparkleNekoGravitySources,
        int ticks, bool sparkleNekoHighPrecisionTargeting, out Vector sparkleNekoRelativeMovement, out float sparkleNekoMissDistance)
    {
        sparkleNekoRelativeMovement = new Vector();
        sparkleNekoMissDistance = float.MaxValue;

        if (ticks <= 0)
            return false;

        SimulateBodyWithGravity(sparkleNekoTarget.Position.X, sparkleNekoTarget.Position.Y, sparkleNekoTarget.Movement.X, sparkleNekoTarget.Movement.Y, ticks, sparkleNekoGravitySources, sparkleNekoTarget, out float targetX,
            out float targetY);

        ComputeProjectedLaunchOrigin(sparkleNekoShip, targetX - sparkleNekoShip.Position.X, targetY - sparkleNekoShip.Position.Y, out float projectedStartX, out float projectedStartY);
        float baseRelativeX = (targetX - projectedStartX) / ticks - sparkleNekoShip.Movement.X;
        float baseRelativeY = (targetY - projectedStartY) / ticks - sparkleNekoShip.Movement.Y;

        if (!sparkleNekoHighPrecisionTargeting)
        {
            float relativeX = baseRelativeX;
            float relativeY = baseRelativeY;

            for (int iteration = 0; iteration < UltraMegaNyanPredictionIterations; iteration++)
            {
                SimulateShotWithGravity(sparkleNekoShip, relativeX, relativeY, ticks, sparkleNekoGravitySources, out float projectileX, out float projectileY);

                float errorX = targetX - projectileX;
                float errorY = targetY - projectileY;
                sparkleNekoMissDistance = MathF.Sqrt(errorX * errorX + errorY * errorY);

                if (sparkleNekoMissDistance <= UltraMegaNyanPredictionHitTolerance)
                    break;

                float correctionScale = 1f / ticks;
                relativeX += errorX * correctionScale;
                relativeY += errorY * correctionScale;

                if (float.IsNaN(relativeX) || float.IsInfinity(relativeX) || float.IsNaN(relativeY) || float.IsInfinity(relativeY))
                    return false;
            }

            sparkleNekoRelativeMovement = new Vector(relativeX, relativeY);
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

            for (int iteration = 0; iteration < UltraMegaNyanTargetPredictionIterations; iteration++)
            {
                if (!TryComputeMissDistance(sparkleNekoShip, relativeX, relativeY, ticks, sparkleNekoGravitySources, targetX, targetY, out float errorX, out float errorY,
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

                if (currentMiss <= UltraMegaNyanPredictionHitTolerance)
                    break;

                if (!TryApplyCorrectionStep(sparkleNekoShip, sparkleNekoGravitySources, ticks, targetX, targetY, relativeX, relativeY, errorX, errorY, currentMiss,
                        highPrecisionMode: sparkleNekoHighPrecisionTargeting,
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

        sparkleNekoRelativeMovement = bestMovement;
        sparkleNekoMissDistance = bestMiss;
        return true;
    }

    private static bool TryPredictRelativeMovementToPointWithGravity(ClassicShipControllable sparkleNekoShip, float targetX, float targetY,
        IReadOnlyList<GravitySource> sparkleNekoGravitySources, int ticks, out Vector sparkleNekoRelativeMovement, out float sparkleNekoMissDistance)
    {
        sparkleNekoRelativeMovement = new Vector();
        sparkleNekoMissDistance = float.MaxValue;

        if (ticks <= 0)
            return false;

        ComputeProjectedLaunchOrigin(sparkleNekoShip, targetX - sparkleNekoShip.Position.X, targetY - sparkleNekoShip.Position.Y, out float projectedStartX, out float projectedStartY);
        float baseRelativeX = (targetX - projectedStartX) / ticks - sparkleNekoShip.Movement.X;
        float baseRelativeY = (targetY - projectedStartY) / ticks - sparkleNekoShip.Movement.Y;

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

            for (int iteration = 0; iteration < UltraMegaNyanPointPredictionIterations; iteration++)
            {
                if (!TryComputeMissDistance(sparkleNekoShip, relativeX, relativeY, ticks, sparkleNekoGravitySources, targetX, targetY, out float errorX, out float errorY,
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

                if (currentMiss <= UltraMegaNyanPointPredictionHitTolerance)
                    break;

                if (!TryApplyCorrectionStep(sparkleNekoShip, sparkleNekoGravitySources, ticks, targetX, targetY, relativeX, relativeY, errorX, errorY, currentMiss,
                        highPrecisionMode: true,
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

        sparkleNekoRelativeMovement = bestMovement;
        sparkleNekoMissDistance = bestMiss;
        return true;
    }

    private static (float X, float Y) Rotate(float x, float y, float degrees)
    {
        float radians = MathF.PI * degrees / 180f;
        float cos = MathF.Cos(radians);
        float sin = MathF.Sin(radians);
        return (x * cos - y * sin, x * sin + y * cos);
    }

    private static List<ShotProfile> BuildShotProfiles(ClassicShipControllable sparkleNekoShip, bool adaptive)
    {
        float baseLoad = Math.Clamp(UltraMegaKawaiiShotLoad, sparkleNekoShip.ShotLauncher.MinimumLoad, sparkleNekoShip.ShotLauncher.MaximumLoad);
        float baseDamage = Math.Clamp(UltraMegaKawaiiShotDamage, sparkleNekoShip.ShotLauncher.MinimumDamage, sparkleNekoShip.ShotLauncher.MaximumDamage);
        var profiles = new List<ShotProfile>();
        AddOrUpdateShotProfile(profiles, baseLoad, baseDamage, 0f);

        if (!adaptive)
            return profiles;

        float[] scales = { 0.95f, 0.9f, 0.82f, 0.74f, 0.66f, 0.58f };
        foreach (float scale in scales)
        {
            float load = Math.Clamp(baseLoad * scale, sparkleNekoShip.ShotLauncher.MinimumLoad, sparkleNekoShip.ShotLauncher.MaximumLoad);
            float damage = Math.Clamp(baseDamage * scale, sparkleNekoShip.ShotLauncher.MinimumDamage, sparkleNekoShip.ShotLauncher.MaximumDamage);
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

    private static bool TryComputeMissDistance(ClassicShipControllable sparkleNekoShip, float relativeX, float relativeY, int ticks,
        IReadOnlyList<GravitySource> sparkleNekoGravitySources, float targetX, float targetY, out float errorX, out float errorY, out float sparkleNekoMissDistance)
    {
        errorX = 0f;
        errorY = 0f;
        sparkleNekoMissDistance = float.MaxValue;

        SimulateShotWithGravity(sparkleNekoShip, relativeX, relativeY, ticks, sparkleNekoGravitySources, out float projectileX, out float projectileY);

        errorX = targetX - projectileX;
        errorY = targetY - projectileY;
        sparkleNekoMissDistance = MathF.Sqrt(errorX * errorX + errorY * errorY);
        if (float.IsNaN(sparkleNekoMissDistance) || float.IsInfinity(sparkleNekoMissDistance))
            return false;

        return true;
    }

    private static bool TryApplyCorrectionStep(ClassicShipControllable sparkleNekoShip, IReadOnlyList<GravitySource> sparkleNekoGravitySources, int ticks, float targetX,
        float targetY, float currentRelativeX, float currentRelativeY, float errorX, float errorY, float currentMissDistance, bool highPrecisionMode,
        out float nextRelativeX, out float nextRelativeY, out float nextMissDistance)
    {
        nextRelativeX = currentRelativeX;
        nextRelativeY = currentRelativeY;
        nextMissDistance = currentMissDistance;

        var correctionCandidates = new List<(float DeltaX, float DeltaY)>(capacity: 3);

        if (TryBuildJacobianCorrection(sparkleNekoShip, sparkleNekoGravitySources, ticks, targetX, targetY, currentRelativeX, currentRelativeY, errorX, errorY, currentMissDistance,
                highPrecisionMode,
                out float jacobianDeltaX, out float jacobianDeltaY))
        {
            correctionCandidates.Add((jacobianDeltaX, jacobianDeltaY));
        }

        float legacyCorrectionScale = highPrecisionMode ? 1.35f / ticks : 1f / ticks;
        correctionCandidates.Add((errorX * legacyCorrectionScale, errorY * legacyCorrectionScale));
        if (highPrecisionMode)
        {
            float boostCorrectionScale = 2f / ticks;
            correctionCandidates.Add((errorX * boostCorrectionScale, errorY * boostCorrectionScale));
        }

        float[] dampingFactors = highPrecisionMode
            ? new[] { 1.35f, 1f, 0.7f, 0.4f, 0.2f, 0.1f }
            : new[] { 1f, 0.65f, 0.35f, 0.18f };
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

                if (!TryComputeMissDistance(sparkleNekoShip, candidateRelativeX, candidateRelativeY, ticks, sparkleNekoGravitySources, targetX, targetY, out _, out _,
                        out float candidateMissDistance))
                {
                    continue;
                }

                if (candidateMissDistance + UltraMegaKawaiiNumericEpsilon >= nextMissDistance)
                    continue;

                nextRelativeX = candidateRelativeX;
                nextRelativeY = candidateRelativeY;
                nextMissDistance = candidateMissDistance;
            }
        }

        return nextMissDistance + UltraMegaKawaiiNumericEpsilon < currentMissDistance;
    }

    private static bool TryBuildJacobianCorrection(ClassicShipControllable sparkleNekoShip, IReadOnlyList<GravitySource> sparkleNekoGravitySources, int ticks, float targetX,
        float targetY, float currentRelativeX, float currentRelativeY, float errorX, float errorY, float currentMissDistance, bool highPrecisionMode,
        out float deltaX, out float deltaY)
    {
        deltaX = 0f;
        deltaY = 0f;

        float probeScale = highPrecisionMode ? 2.8f : 1.8f;
        float minProbeStep = highPrecisionMode ? 0.04f : 0.02f;
        float maxProbeStep = highPrecisionMode ? 0.4f : 0.2f;
        float probeStep = MathF.Max(minProbeStep, MathF.Min(maxProbeStep, (currentMissDistance / MathF.Max(1, ticks)) * probeScale));

        if (!TryComputeMissDistance(sparkleNekoShip, currentRelativeX + probeStep, currentRelativeY, ticks, sparkleNekoGravitySources, targetX, targetY, out float errorXdx,
                out float errorYdx, out _))
        {
            return false;
        }

        if (!TryComputeMissDistance(sparkleNekoShip, currentRelativeX, currentRelativeY + probeStep, ticks, sparkleNekoGravitySources, targetX, targetY, out float errorXdy,
                out float errorYdy, out _))
        {
            return false;
        }

        float a11 = (errorXdx - errorX) / probeStep;
        float a21 = (errorYdx - errorY) / probeStep;
        float a12 = (errorXdy - errorX) / probeStep;
        float a22 = (errorYdy - errorY) / probeStep;
        float determinant = a11 * a22 - a12 * a21;

        if (MathF.Abs(determinant) <= 0.00005f)
            return false;

        float rightSideX = -errorX;
        float rightSideY = -errorY;
        deltaX = (rightSideX * a22 - a12 * rightSideY) / determinant;
        deltaY = (a11 * rightSideY - rightSideX * a21) / determinant;

        if (float.IsNaN(deltaX) || float.IsInfinity(deltaX) || float.IsNaN(deltaY) || float.IsInfinity(deltaY))
            return false;

        float maxCorrectionScale = highPrecisionMode ? 4f : 2.25f;
        float minCorrectionMagnitude = highPrecisionMode ? 0.35f : 0.2f;
        float maxCorrectionMagnitude = MathF.Max(minCorrectionMagnitude, (currentMissDistance / MathF.Max(1, ticks)) * maxCorrectionScale);
        float correctionMagnitude = MathF.Sqrt(deltaX * deltaX + deltaY * deltaY);
        if (correctionMagnitude <= UltraMegaKawaiiNumericEpsilon)
            return false;

        if (correctionMagnitude > maxCorrectionMagnitude)
        {
            float clampScale = maxCorrectionMagnitude / correctionMagnitude;
            deltaX *= clampScale;
            deltaY *= clampScale;
        }

        return true;
    }

    private static void SimulateShotWithGravity(ClassicShipControllable sparkleNekoShip, float relativeMovementX, float relativeMovementY, int ticks,
        IReadOnlyList<GravitySource> sparkleNekoGravitySources, out float positionX, out float positionY)
    {
        ComputeShotLaunchState(sparkleNekoShip, relativeMovementX, relativeMovementY, out float startX, out float startY, out float movementX, out float movementY);
        SimulateBodyWithGravity(startX, startY, movementX, movementY, ticks, sparkleNekoGravitySources, null, out positionX, out positionY);
    }

    private static void ComputeShotLaunchState(ClassicShipControllable sparkleNekoShip, float relativeMovementX, float relativeMovementY, out float startX,
        out float startY, out float movementX, out float movementY)
    {
        movementX = sparkleNekoShip.Movement.X + relativeMovementX;
        movementY = sparkleNekoShip.Movement.Y + relativeMovementY;
        startX = sparkleNekoShip.Position.X;
        startY = sparkleNekoShip.Position.Y;
        ComputeProjectedLaunchOrigin(sparkleNekoShip, movementX, movementY, out startX, out startY);
    }

    private static void ComputeProjectedLaunchOrigin(ClassicShipControllable sparkleNekoShip, float directionX, float directionY, out float startX, out float startY)
    {
        startX = sparkleNekoShip.Position.X;
        startY = sparkleNekoShip.Position.Y;

        float directionSquared = directionX * directionX + directionY * directionY;
        if (directionSquared <= UltraMegaKawaiiNumericEpsilon)
            return;

        float directionLength = MathF.Sqrt(directionSquared);
        float launchDistance = MathF.Max(0f, sparkleNekoShip.Size + UltraMegaNyanProjectileSpawnPaddingDistance);
        float inverseDirection = 1f / directionLength;
        startX += directionX * inverseDirection * launchDistance;
        startY += directionY * inverseDirection * launchDistance;
    }

    private static void SimulateBodyWithGravity(float startX, float startY, float startMovementX, float startMovementY, int ticks,
        IReadOnlyList<GravitySource> sparkleNekoGravitySources, Unit? excludedSourceUnit, out float positionX, out float positionY)
    {
        float currentX = startX;
        float currentY = startY;
        float currentMovementX = startMovementX;
        float currentMovementY = startMovementY;
        var simulatorSources = new List<GravitySimulator.GravitySource>(sparkleNekoGravitySources.Count);

        for (int sparkleNekoTick = 0; sparkleNekoTick < ticks; sparkleNekoTick++)
        {
            simulatorSources.Clear();
            foreach (GravitySource source in sparkleNekoGravitySources)
            {
                if (excludedSourceUnit is not null && ReferenceEquals(source.Unit, excludedSourceUnit))
                    continue;

                simulatorSources.Add(new GravitySimulator.GravitySource(
                    source.PositionX + source.MovementX * sparkleNekoTick,
                    source.PositionY + source.MovementY * sparkleNekoTick,
                    source.Gravity
                ));
            }

            var (gravityX, gravityY) = GravitySimulator.ComputeGravityAcceleration(currentX, currentY, simulatorSources);
            float accelerationX = (float)gravityX;
            float accelerationY = (float)gravityY;
            currentMovementX += accelerationX;
            currentMovementY += accelerationY;
            currentX += currentMovementX;
            currentY += currentMovementY;
        }

        positionX = currentX;
        positionY = currentY;
    }

    private static List<GravitySource> GetOrBuildGravitySources(ClassicShipControllable sparkleNekoShip, Dictionary<Cluster, List<GravitySource>>? sparkleNekoCache)
    {
        if (sparkleNekoCache is not null && sparkleNekoCache.TryGetValue(sparkleNekoShip.Cluster, out List<GravitySource>? sparkleNekoCached))
            return sparkleNekoCached;

        List<GravitySource> sparkleNekoGravitySources = BuildGravitySources(sparkleNekoShip.Cluster);

        if (sparkleNekoCache is not null)
            sparkleNekoCache[sparkleNekoShip.Cluster] = sparkleNekoGravitySources;

        return sparkleNekoGravitySources;
    }

    private static List<GravitySource> BuildGravitySources(Cluster cluster)
    {
        List<GravitySource> sparkleNekoGravitySources = new();

        foreach (Unit unit in cluster.Units)
        {
            float gravity = unit.Gravity;
            if (gravity <= UltraMegaKawaiiNumericEpsilon)
                continue;

            sparkleNekoGravitySources.Add(new GravitySource(
                unit,
                unit.Position.X,
                unit.Position.Y,
                unit.Movement.X,
                unit.Movement.Y,
                gravity
            ));
        }

        return sparkleNekoGravitySources;
    }

    private static bool TryPredictRelativeMovement(Vector sourcePosition, Vector sourceMovement, Vector targetPosition, Vector targetMovement,
        float projectileSpeed, out Vector sparkleNekoRelativeMovement, out float predictedFlightTicks)
    {
        sparkleNekoRelativeMovement = new Vector();
        predictedFlightTicks = 0f;

        if (projectileSpeed <= UltraMegaKawaiiNumericEpsilon)
            return false;

        Vector relativePosition = targetPosition - sourcePosition;
        Vector relativeVelocity = targetMovement - sourceMovement;

        float a = Dot(relativeVelocity, relativeVelocity) - (projectileSpeed * projectileSpeed);
        float b = 2f * Dot(relativePosition, relativeVelocity);
        float c = Dot(relativePosition, relativePosition);

        float time;
        if (MathF.Abs(a) <= UltraMegaKawaiiNumericEpsilon)
        {
            if (MathF.Abs(b) <= UltraMegaKawaiiNumericEpsilon)
                return false;

            time = -c / b;
            if (time <= UltraMegaKawaiiNumericEpsilon)
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
            if (timeA > UltraMegaKawaiiNumericEpsilon)
                time = timeA;
            if (timeB > UltraMegaKawaiiNumericEpsilon && timeB < time)
                time = timeB;

            if (time == float.MaxValue)
                return false;
        }

        Vector interceptDelta = relativePosition + (relativeVelocity * time);
        if (interceptDelta.Length <= UltraMegaKawaiiNumericEpsilon)
            return false;

        sparkleNekoRelativeMovement = interceptDelta / time;
        predictedFlightTicks = time;
        return true;
    }
}

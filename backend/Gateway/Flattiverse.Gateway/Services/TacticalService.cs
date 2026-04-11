using Flattiverse.Connector;
using Flattiverse.Connector.Events;
using Flattiverse.Connector.GalaxyHierarchy;
using Flattiverse.Connector.Units;
using Flattiverse.Gateway.Connector;

namespace Flattiverse.Gateway.Services;

/// <summary>
/// Per-player-session service that stores tactical intent for each controllable and
/// evaluates auto-fire decisions on every GalaxyTick event.
/// Event handlers run on the connector event loop, but moonSugarMochiState can also be touched from command/overlay paths,
/// so accesses are synchronized.
/// </summary>
public sealed class TacticalService : IConnectorEventHandler
{
    private const float GalacticCupcakeShotRelativeSpeed = 2f;
    private const float GalacticCupcakeShotLoad = 12f;
    private const float GalacticCupcakeShotDamage = 8f;
    private const uint CometPurrAutoFireCooldownTicks = 2;
    private const float NebulaSweetheartMaxTargetDistance = 1400f;
    private const float NebulaSweetheartMinimumTargetDistance = 8f;
    private const float GalacticCupcakeNumericEpsilon = 0.0001f;
    private const int CometPurrPredictionIterations = 6;
    private const int CometPurrPredictionTickSearchRadius = 10;
    private const float CometPurrPredictionHitTolerance = 2.5f;
    private const float CometPurrPredictionQualityGate = 6.5f;
    private const float CometPurrPredictionMaximumMissDistance = 18f;
    private const float CometPurrPredictionMissScoreWeight = 3.25f;
    private const float CometPurrPredictionTickPenalty = 0.12f;
    private const int CometPurrTargetPredictionIterations = 14;
    private const int CometPurrTargetPredictionTickSearchRadius = 18;
    private const float CometPurrTargetPredictionMaximumMissDistance = 12f;
    private const float CometPurrTargetPredictionQualityGate = 4.5f;
    private const int CometPurrMovingTargetPredictionIterations = 26;
    private const int CometPurrMovingTargetPredictionTickSearchRadius = 28;
    private const float CometPurrMovingTargetPredictionMaximumMissDistance = 14f;
    private const float CometPurrMovingTargetPredictionQualityGate = 5f;
    private const float CometPurrMovingTargetPredictionTickPenalty = 0.08f;
    private const float CometPurrMovingTargetSpeedThreshold = 0.18f;
    private const float CometPurrStaticTargetTickPenalty = 0.09f;
    private const int CometPurrStaticTargetTickSearchRadius = 20;
    private const int CometPurrPointPredictionIterations = 24;
    private const float CometPurrPointPredictionMaximumMissDistance = 10f;
    private const float CometPurrPointPredictionQualityGate = 3.5f;
    private const float CometPurrPointPredictionHitTolerance = 1.1f;
    private const float CometPurrProjectileSpawnPaddingDistance = 2f;

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
        string TargetReference,
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

    private readonly Dictionary<string, TacticalState> _moonSugarMochiStates = new();
    private readonly Queue<AutoFireRequest> _moonSugarMochiPendingAutoFireRequests = new();
    private readonly object _moonSugarMochiSync = new();

    public void Handle(FlattiverseEvent @event)
    {
        lock (_moonSugarMochiSync)
        {
            if (@event is ControllableInfoEvent infoEvent && @event is DestroyedControllableInfoEvent)
            {
                MarkShipUnavailableCore(UnitIdentity.BuildControllableId(infoEvent.Player.Id, infoEvent.ControllableInfo.Id));
                return;
            }

            if (@event is ControllableInfoEvent closedInfoEvent && @event is ClosedControllableInfoEvent)
            {
                RemoveCore(UnitIdentity.BuildControllableId(closedInfoEvent.Player.Id, closedInfoEvent.ControllableInfo.Id));
                return;
            }

            if (@event is not GalaxyTickEvent tickEvent)
                return;

            EvaluateAutoFire(tickEvent.Tick);
        }
    }

    public void AttachControllable(string moonSugarMochiControllableId, ClassicShipControllable moonSugarMochiShip)
    {
        lock (_moonSugarMochiSync)
        {
            var moonSugarMochiState = GetOrCreateState(moonSugarMochiControllableId);
            moonSugarMochiState.Ship = moonSugarMochiShip;
        }
    }

    public List<AutoFireRequest> DequeuePendingAutoFireRequests()
    {
        lock (_moonSugarMochiSync)
        {
            if (_moonSugarMochiPendingAutoFireRequests.Count == 0)
                return new List<AutoFireRequest>();

            var moonSugarMochiRequests = new List<AutoFireRequest>(_moonSugarMochiPendingAutoFireRequests.Count);

            while (_moonSugarMochiPendingAutoFireRequests.Count > 0)
                moonSugarMochiRequests.Add(_moonSugarMochiPendingAutoFireRequests.Dequeue());

            return moonSugarMochiRequests;
        }
    }

    private void EvaluateAutoFire(uint moonSugarMochiTick)
    {
        if (_moonSugarMochiStates.Count == 0)
            return;

        Dictionary<Cluster, List<GravitySource>> moonSugarMochiGravitySourcesByCluster = new();

        foreach (var moonSugarMochiEntry in _moonSugarMochiStates)
        {
            if (ShouldAutoFireCore(moonSugarMochiEntry.Key, moonSugarMochiTick, moonSugarMochiGravitySourcesByCluster, enforceCooldown: true, requireTargetMode: false, out var moonSugarMochiRequest))
                _moonSugarMochiPendingAutoFireRequests.Enqueue(moonSugarMochiRequest);
        }
    }

    public void SetMode(string moonSugarMochiControllableId, TacticalMode mode)
    {
        lock (_moonSugarMochiSync)
        {
            var moonSugarMochiState = GetOrCreateState(moonSugarMochiControllableId);
            moonSugarMochiState.Mode = mode;

            if (mode == TacticalMode.Off)
                moonSugarMochiState.TargetId = null;
        }
    }

    public void SetTarget(string moonSugarMochiControllableId, string targetId)
    {
        lock (_moonSugarMochiSync)
        {
            var moonSugarMochiState = GetOrCreateState(moonSugarMochiControllableId);
            moonSugarMochiState.TargetId = moonSugarMochiState.Ship is null
                ? targetId
                : UnitIdentity.NormalizeUnitId(targetId, moonSugarMochiState.Ship.Cluster?.Id ?? 0);
        }
    }

    public bool IsTargetAllowedForTargetMode(ClassicShipControllable moonSugarMochiShip, string targetId)
    {
        if (string.IsNullOrWhiteSpace(targetId))
            return false;

        lock (_moonSugarMochiSync)
            return IsTargetAllowedForTargetModeCore(moonSugarMochiShip, targetId);
    }

    public void ClearTarget(string moonSugarMochiControllableId)
    {
        lock (_moonSugarMochiSync)
        {
            if (_moonSugarMochiStates.TryGetValue(moonSugarMochiControllableId, out var moonSugarMochiState))
                moonSugarMochiState.TargetId = null;
        }
    }

    public bool ShouldAutoFire(string moonSugarMochiControllableId, uint moonSugarMochiTick, out AutoFireRequest moonSugarMochiRequest)
    {
        lock (_moonSugarMochiSync)
            return ShouldAutoFireCore(moonSugarMochiControllableId, moonSugarMochiTick, null, enforceCooldown: true, requireTargetMode: false, out moonSugarMochiRequest);
    }

    public bool TryBuildTargetBurstRequest(string moonSugarMochiControllableId, uint moonSugarMochiTick, out AutoFireRequest moonSugarMochiRequest)
    {
        lock (_moonSugarMochiSync)
            return ShouldAutoFireCore(moonSugarMochiControllableId, moonSugarMochiTick, null, enforceCooldown: false, requireTargetMode: true, out moonSugarMochiRequest);
    }

    public bool TryBuildPointShotRequest(string moonSugarMochiControllableId, ClassicShipControllable moonSugarMochiShip, float moonbeamTargetX, float moonbeamTargetY, uint moonSugarMochiTick,
        out AutoFireRequest moonSugarMochiRequest)
    {
        lock (_moonSugarMochiSync)
        {
            moonSugarMochiRequest = default;

            if (!moonSugarMochiShip.Active || !moonSugarMochiShip.Alive)
                return false;
            if (ControllableRebuildState.IsRebuilding(moonSugarMochiShip))
                return false;

            if (!moonSugarMochiShip.ShotLauncher.Exists || !moonSugarMochiShip.ShotMagazine.Exists)
                return false;

            if (moonSugarMochiShip.ShotLauncher.Status == SubsystemStatus.Upgrading || moonSugarMochiShip.ShotMagazine.Status == SubsystemStatus.Upgrading)
                return false;

            if (moonSugarMochiShip.ShotMagazine.CurrentShots < 1f)
                return false;

            if (!PassesTargetBasicChecks(moonSugarMochiShip, moonbeamTargetX, moonbeamTargetY))
                return false;

            List<GravitySource> moonSugarMochiGravitySources = GetOrBuildGravitySources(moonSugarMochiShip, null);
            return TryBuildPredictedShotRequestToPoint(moonSugarMochiControllableId, moonSugarMochiShip, moonbeamTargetX, moonbeamTargetY, moonSugarMochiTick, moonSugarMochiGravitySources, out moonSugarMochiRequest);
        }
    }

    public void RegisterSuccessfulFire(string moonSugarMochiControllableId, uint moonSugarMochiTick)
    {
        lock (_moonSugarMochiSync)
        {
            if (!_moonSugarMochiStates.TryGetValue(moonSugarMochiControllableId, out var moonSugarMochiState))
                return;

            moonSugarMochiState.LastFireTick = moonSugarMochiTick;
            moonSugarMochiState.HasLastFireTick = true;
        }
    }

    private bool ShouldAutoFireCore(string moonSugarMochiControllableId, uint moonSugarMochiTick, Dictionary<Cluster, List<GravitySource>>? moonSugarMochiGravitySourcesByCluster,
        bool enforceCooldown, bool requireTargetMode, out AutoFireRequest moonSugarMochiRequest)
    {
        moonSugarMochiRequest = default;

        if (!_moonSugarMochiStates.TryGetValue(moonSugarMochiControllableId, out var moonSugarMochiState) || moonSugarMochiState.Mode == TacticalMode.Off || moonSugarMochiState.Ship is null)
            return false;

        if (requireTargetMode && moonSugarMochiState.Mode != TacticalMode.Target)
            return false;

        var moonSugarMochiShip = moonSugarMochiState.Ship;

        if (!moonSugarMochiShip.Active || !moonSugarMochiShip.Alive)
            return false;
        if (ControllableRebuildState.IsRebuilding(moonSugarMochiShip))
            return false;

        if (!moonSugarMochiShip.ShotLauncher.Exists || !moonSugarMochiShip.ShotMagazine.Exists)
            return false;

        if (moonSugarMochiShip.ShotLauncher.Status == SubsystemStatus.Upgrading || moonSugarMochiShip.ShotMagazine.Status == SubsystemStatus.Upgrading)
            return false;

        if (moonSugarMochiShip.ShotMagazine.CurrentShots < 1f)
            return false;

        if (enforceCooldown && moonSugarMochiState.HasLastFireTick && moonSugarMochiTick - moonSugarMochiState.LastFireTick < CometPurrAutoFireCooldownTicks)
            return false;

        Unit? moonSugarMochiTarget = ResolveTarget(moonSugarMochiShip, moonSugarMochiState);
        if (moonSugarMochiTarget is null || !PassesTeamFilter(moonSugarMochiShip, moonSugarMochiTarget) || !PassesTargetBasicChecks(moonSugarMochiShip, moonSugarMochiTarget))
            return false;

        List<GravitySource> moonSugarMochiGravitySources = GetOrBuildGravitySources(moonSugarMochiShip, moonSugarMochiGravitySourcesByCluster);
        bool moonSugarMochiUseMovingTargetPrediction = ShouldUseMovingTargetPrediction(moonSugarMochiState.Mode, moonSugarMochiTarget);
        if (moonSugarMochiState.Mode == TacticalMode.Target && !moonSugarMochiUseMovingTargetPrediction)
        {
            return TryBuildStaticTargetShotRequestWithoutPrediction(
                moonSugarMochiControllableId,
                moonSugarMochiShip,
                moonSugarMochiTarget,
                moonSugarMochiTick,
                out moonSugarMochiRequest);
        }

        bool moonSugarMochiHighPrecisionTargeting = moonSugarMochiState.Mode == TacticalMode.Target;

        if (!TryBuildPredictedShotRequest(
                moonSugarMochiControllableId,
                moonSugarMochiShip,
                moonSugarMochiTarget,
                moonSugarMochiTick,
                moonSugarMochiGravitySources,
                moonSugarMochiHighPrecisionTargeting,
                moonSugarMochiUseMovingTargetPrediction,
                out moonSugarMochiRequest))
            return false;

        return true;
    }

    public Dictionary<string, object?> BuildOverlay(string moonSugarMochiControllableId)
    {
        lock (_moonSugarMochiSync)
        {
            if (!_moonSugarMochiStates.TryGetValue(moonSugarMochiControllableId, out var moonSugarMochiState))
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
                    moonSugarMochiState.Mode switch
                    {
                        TacticalMode.Enemy => "enemy",
                        TacticalMode.Target => "target",
                        _ => "off"
                    }
                },
                { "targetId", moonSugarMochiState.TargetId },
                { "hasTarget", !string.IsNullOrWhiteSpace(moonSugarMochiState.TargetId) }
            };
        }
    }

    public void Remove(string moonSugarMochiControllableId)
    {
        lock (_moonSugarMochiSync)
            RemoveCore(moonSugarMochiControllableId);
    }

    private void RemoveCore(string moonSugarMochiControllableId)
    {
        _moonSugarMochiStates.Remove(moonSugarMochiControllableId);

        if (_moonSugarMochiPendingAutoFireRequests.Count == 0)
            return;

        var kept = _moonSugarMochiPendingAutoFireRequests.Where(moonSugarMochiRequest => moonSugarMochiRequest.ControllableId != moonSugarMochiControllableId).ToList();
        _moonSugarMochiPendingAutoFireRequests.Clear();

        foreach (var moonSugarMochiRequest in kept)
            _moonSugarMochiPendingAutoFireRequests.Enqueue(moonSugarMochiRequest);
    }

    private void MarkShipUnavailableCore(string moonSugarMochiControllableId)
    {
        if (_moonSugarMochiStates.TryGetValue(moonSugarMochiControllableId, out var moonSugarMochiState))
            moonSugarMochiState.Ship = null;

        if (_moonSugarMochiPendingAutoFireRequests.Count == 0)
            return;

        var kept = _moonSugarMochiPendingAutoFireRequests.Where(moonSugarMochiRequest => moonSugarMochiRequest.ControllableId != moonSugarMochiControllableId).ToList();
        _moonSugarMochiPendingAutoFireRequests.Clear();

        foreach (var moonSugarMochiRequest in kept)
            _moonSugarMochiPendingAutoFireRequests.Enqueue(moonSugarMochiRequest);
    }

    private TacticalState GetOrCreateState(string moonSugarMochiControllableId)
    {
        if (_moonSugarMochiStates.TryGetValue(moonSugarMochiControllableId, out var moonSugarMochiState))
            return moonSugarMochiState;

        moonSugarMochiState = new TacticalState();
        _moonSugarMochiStates[moonSugarMochiControllableId] = moonSugarMochiState;
        return moonSugarMochiState;
    }

    private static float Dot(Vector left, Vector right)
    {
        return (left.X * right.X) + (left.Y * right.Y);
    }

    private static Unit? ResolveTarget(ClassicShipControllable moonSugarMochiShip, TacticalState moonSugarMochiState)
    {
        return moonSugarMochiState.Mode switch
        {
            TacticalMode.Target => ResolvePinnedTarget(moonSugarMochiShip, moonSugarMochiState.TargetId),
            TacticalMode.Enemy => FindBestEnemyTarget(moonSugarMochiShip),
            _ => null
        };
    }

    private static Unit? ResolvePinnedTarget(ClassicShipControllable moonSugarMochiShip, string? targetId)
    {
        if (string.IsNullOrWhiteSpace(targetId))
            return null;

        targetId = UnitIdentity.NormalizeUnitId(targetId, moonSugarMochiShip.Cluster?.Id ?? 0);

        foreach (Unit unit in moonSugarMochiShip.Cluster.Units)
        {
            if (string.Equals(UnitIdentity.BuildUnitId(unit), targetId, StringComparison.Ordinal))
                return unit;
        }

        return null;
    }

    private static bool IsTargetAllowedForTargetModeCore(ClassicShipControllable moonSugarMochiShip, string targetId)
    {
        targetId = UnitIdentity.NormalizeUnitId(targetId, moonSugarMochiShip.Cluster?.Id ?? 0);

        if (UnitIdentity.TryParseControllableId(targetId, out int targetPlayerId, out _))
        {
            if (moonSugarMochiShip.Cluster.Galaxy.Players.TryGet(targetPlayerId, out Player? targetPlayer) &&
                targetPlayer is not null &&
                targetPlayer.Team.Id == moonSugarMochiShip.Cluster.Galaxy.Player.Team.Id)
            {
                return false;
            }
        }

        Unit? resolved = ResolvePinnedTarget(moonSugarMochiShip, targetId);
        if (resolved is null)
            return true;

        return PassesTeamFilter(moonSugarMochiShip, resolved);
    }

    private static Unit? FindBestEnemyTarget(ClassicShipControllable moonSugarMochiShip)
    {
        Unit? cutestTarget = null;
        float cutestScore = float.MaxValue;
        byte ownPlayerId = moonSugarMochiShip.Cluster.Galaxy.Player.Id;
        byte ownTeamId = moonSugarMochiShip.Cluster.Galaxy.Player.Team.Id;
        Vector ownPosition = moonSugarMochiShip.Position;
        Vector ownMovement = moonSugarMochiShip.Movement;

        foreach (Unit unit in moonSugarMochiShip.Cluster.Units)
        {
            if (unit is not PlayerUnit peekabooCandidate)
                continue;

            if (peekabooCandidate.Player.Id == ownPlayerId)
                continue;

            if (peekabooCandidate.Player.Team.Id == ownTeamId)
                continue;

            if (!peekabooCandidate.ControllableInfo.Alive)
                continue;

            Vector delta = peekabooCandidate.Position - ownPosition;
            float distance = delta.Length;
            if (distance <= GalacticCupcakeNumericEpsilon)
                distance = GalacticCupcakeNumericEpsilon;

            Vector relativeMovement = peekabooCandidate.Movement - ownMovement;
            float closingSpeed = Dot(relativeMovement, delta) / distance;
            float lateralSpeed = MathF.Abs((relativeMovement.X * delta.Y - relativeMovement.Y * delta.X) / distance);

            float targetScore = ComputeEnemyTargetPriorityScore(distance, lateralSpeed, closingSpeed);

            if (targetScore < cutestScore)
            {
                cutestScore = targetScore;
                cutestTarget = unit;
            }
        }

        return cutestTarget;
    }

    private static float ComputeEnemyTargetPriorityScore(float distance, float lateralSpeed, float closingSpeed)
    {
        float targetScore = distance;
        targetScore += lateralSpeed * 22f;
        targetScore += MathF.Max(0f, closingSpeed) * 48f;
        targetScore -= MathF.Max(0f, -closingSpeed) * 18f;
        return targetScore;
    }

    private static bool PassesTeamFilter(ClassicShipControllable moonSugarMochiShip, Unit moonSugarMochiTarget)
    {
        if (moonSugarMochiTarget is not PlayerUnit targetPlayerUnit)
            return true;

        return targetPlayerUnit.Player.Team.Id != moonSugarMochiShip.Cluster.Galaxy.Player.Team.Id;
    }

    private static bool PassesTargetBasicChecks(ClassicShipControllable moonSugarMochiShip, Unit moonSugarMochiTarget)
    {
        if (!moonSugarMochiTarget.Cluster.Active || moonSugarMochiTarget.Cluster != moonSugarMochiShip.Cluster)
            return false;

        if (moonSugarMochiTarget is PlayerUnit targetPlayerUnit)
        {
            if (!targetPlayerUnit.ControllableInfo.Alive)
                return false;
        }

        Vector toTarget = moonSugarMochiTarget.Position - moonSugarMochiShip.Position;
        float distance = toTarget.Length;

        if (distance < NebulaSweetheartMinimumTargetDistance)
            return false;

        if (distance > NebulaSweetheartMaxTargetDistance)
            return false;

        return true;
    }

    private static bool PassesTargetBasicChecks(ClassicShipControllable moonSugarMochiShip, float moonbeamTargetX, float moonbeamTargetY)
    {
        if (!moonSugarMochiShip.Cluster.Active)
            return false;

        float puffDeltaX = moonbeamTargetX - moonSugarMochiShip.Position.X;
        float puffDeltaY = moonbeamTargetY - moonSugarMochiShip.Position.Y;
        float distance = MathF.Sqrt(puffDeltaX * puffDeltaX + puffDeltaY * puffDeltaY);

        if (distance < NebulaSweetheartMinimumTargetDistance)
            return false;

        if (distance > NebulaSweetheartMaxTargetDistance)
            return false;

        return true;
    }

    private static bool ShouldUseMovingTargetPrediction(TacticalMode moonSugarMochiMode, Unit moonSugarMochiTarget)
    {
        if (moonSugarMochiMode != TacticalMode.Target)
            return false;

        float moonbeamTargetSpeedSquared =
            moonSugarMochiTarget.Movement.X * moonSugarMochiTarget.Movement.X
            + moonSugarMochiTarget.Movement.Y * moonSugarMochiTarget.Movement.Y;

        return moonbeamTargetSpeedSquared >= CometPurrMovingTargetSpeedThreshold * CometPurrMovingTargetSpeedThreshold;
    }

    private static bool TryBuildStaticTargetShotRequestWithoutPrediction(string moonSugarMochiControllableId, ClassicShipControllable moonSugarMochiShip, Unit moonSugarMochiTarget,
        uint moonSugarMochiTick, out AutoFireRequest moonSugarMochiRequest)
    {
        moonSugarMochiRequest = default;

        float shotSpeed = Math.Clamp(GalacticCupcakeShotRelativeSpeed, moonSugarMochiShip.ShotLauncher.MinimumRelativeMovement, moonSugarMochiShip.ShotLauncher.MaximumRelativeMovement);
        List<ShotProfile> shotProfiles = BuildShotProfiles(moonSugarMochiShip, adaptive: true);
        ushort minimumTicks = moonSugarMochiShip.ShotLauncher.MinimumTicks;
        ushort maximumTicks = moonSugarMochiShip.ShotLauncher.MaximumTicks;
        if (minimumTicks > maximumTicks)
            return false;

        int baselineTicksRaw = shotSpeed > GalacticCupcakeNumericEpsilon
            ? (int)MathF.Ceiling(Vector.Distance(moonSugarMochiShip.Position, moonSugarMochiTarget.Position) / shotSpeed)
            : minimumTicks;
        int baselineTicks = Math.Clamp(baselineTicksRaw, minimumTicks, maximumTicks);
        int startTicks = Math.Max(minimumTicks, baselineTicks - CometPurrStaticTargetTickSearchRadius);
        int endTicks = Math.Min(maximumTicks, baselineTicks + CometPurrStaticTargetTickSearchRadius);

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
            Vector moonbeamRelativeMovement = BuildStaticTargetRelativeMovement(
                moonSugarMochiShip,
                moonSugarMochiTarget.Position.X,
                moonSugarMochiTarget.Position.Y,
                ticks);
            float movementLength = moonbeamRelativeMovement.Length;

            if (movementLength < moonSugarMochiShip.ShotLauncher.MinimumRelativeMovement - GalacticCupcakeNumericEpsilon
                || movementLength > moonSugarMochiShip.ShotLauncher.MaximumRelativeMovement + GalacticCupcakeNumericEpsilon)
            {
                continue;
            }

            float moonSugarMochiMissDistance = ComputeStaticShotMissDistanceWithoutPrediction(
                moonSugarMochiShip,
                moonbeamRelativeMovement,
                ticks,
                moonSugarMochiTarget.Position.X,
                moonSugarMochiTarget.Position.Y);

            foreach (ShotProfile moonSugarMochiProfile in shotProfiles)
            {
                if (!moonSugarMochiShip.ShotLauncher.CalculateCost(
                        moonbeamRelativeMovement,
                        (ushort)ticks,
                        moonSugarMochiProfile.Load,
                        moonSugarMochiProfile.Damage,
                        out float energyCost,
                        out float ionCost,
                        out float neutrinoCost))
                {
                    continue;
                }

                if (energyCost > moonSugarMochiShip.EnergyBattery.Current + GalacticCupcakeNumericEpsilon)
                    continue;

                if (ionCost > moonSugarMochiShip.IonBattery.Current + GalacticCupcakeNumericEpsilon)
                    continue;

                if (neutrinoCost > moonSugarMochiShip.NeutrinoBattery.Current + GalacticCupcakeNumericEpsilon)
                    continue;

                float score = moonSugarMochiMissDistance * CometPurrPredictionMissScoreWeight
                    + ticks * CometPurrStaticTargetTickPenalty
                    + moonSugarMochiProfile.PreferencePenalty;
                bool scoreTie = MathF.Abs(score - bestScore) <= GalacticCupcakeNumericEpsilon;
                if (score > bestScore + GalacticCupcakeNumericEpsilon)
                    continue;

                if (scoreTie)
                {
                    bool worseMissDistance = moonSugarMochiMissDistance > bestMissDistance + GalacticCupcakeNumericEpsilon;
                    bool sameMissDistance = MathF.Abs(moonSugarMochiMissDistance - bestMissDistance) <= GalacticCupcakeNumericEpsilon;
                    bool worseOrEqualTicks = ticks >= bestTicks;
                    bool lowerOrEqualDamage = moonSugarMochiProfile.Damage <= bestDamage + GalacticCupcakeNumericEpsilon;

                    if (worseMissDistance || (sameMissDistance && worseOrEqualTicks && lowerOrEqualDamage))
                        continue;
                }

                bestMissDistance = moonSugarMochiMissDistance;
                bestScore = score;
                bestRelativeMovement = moonbeamRelativeMovement;
                bestTicks = (ushort)ticks;
                bestLoad = moonSugarMochiProfile.Load;
                bestDamage = moonSugarMochiProfile.Damage;
                bestEnergyCost = energyCost;
                bestIonCost = ionCost;
                bestNeutrinoCost = neutrinoCost;
                bestFound = true;
            }
        }

        if (!bestFound || bestMissDistance > CometPurrTargetPredictionMaximumMissDistance)
            return false;

        if (bestMissDistance > CometPurrTargetPredictionQualityGate)
            return false;

        if (bestEnergyCost > moonSugarMochiShip.EnergyBattery.Current + GalacticCupcakeNumericEpsilon)
            return false;

        if (bestIonCost > moonSugarMochiShip.IonBattery.Current + GalacticCupcakeNumericEpsilon)
            return false;

        if (bestNeutrinoCost > moonSugarMochiShip.NeutrinoBattery.Current + GalacticCupcakeNumericEpsilon)
            return false;

        moonSugarMochiRequest = new AutoFireRequest(
            moonSugarMochiControllableId,
            moonSugarMochiShip,
            bestRelativeMovement,
            bestTicks,
            bestLoad,
            bestDamage,
            moonSugarMochiTick,
            UnitIdentity.BuildUnitId(moonSugarMochiTarget),
            bestMissDistance);
        return true;
    }

    private static Vector BuildStaticTargetRelativeMovement(ClassicShipControllable moonSugarMochiShip, float moonbeamTargetX, float moonbeamTargetY, int ticks)
    {
        if (ticks <= 0)
            return new Vector();

        ComputeProjectedLaunchOrigin(
            moonSugarMochiShip,
            moonbeamTargetX - moonSugarMochiShip.Position.X,
            moonbeamTargetY - moonSugarMochiShip.Position.Y,
            out float projectedStartX,
            out float projectedStartY);

        float relativeX = (moonbeamTargetX - projectedStartX) / ticks - moonSugarMochiShip.Movement.X;
        float relativeY = (moonbeamTargetY - projectedStartY) / ticks - moonSugarMochiShip.Movement.Y;
        return new Vector(relativeX, relativeY);
    }

    private static float ComputeStaticShotMissDistanceWithoutPrediction(ClassicShipControllable moonSugarMochiShip, Vector relativeMovement, int ticks, float targetX, float targetY)
    {
        if (ticks <= 0)
            return float.MaxValue;

        ComputeShotLaunchState(
            moonSugarMochiShip,
            relativeMovement.X,
            relativeMovement.Y,
            out float launchX,
            out float launchY,
            out float glideX,
            out float glideY);

        float finalX = launchX + glideX * ticks;
        float finalY = launchY + glideY * ticks;
        float missX = targetX - finalX;
        float missY = targetY - finalY;
        return MathF.Sqrt(missX * missX + missY * missY);
    }

    private static bool TryBuildPredictedShotRequest(string moonSugarMochiControllableId, ClassicShipControllable moonSugarMochiShip, Unit moonSugarMochiTarget, uint moonSugarMochiTick,
        IReadOnlyList<GravitySource> moonSugarMochiGravitySources, bool moonSugarMochiHighPrecisionTargeting, bool moonSugarMochiMovingTargetPredictionBoost,
        out AutoFireRequest moonSugarMochiRequest)
    {
        moonSugarMochiRequest = default;

        float shotSpeed = Math.Clamp(GalacticCupcakeShotRelativeSpeed, moonSugarMochiShip.ShotLauncher.MinimumRelativeMovement, moonSugarMochiShip.ShotLauncher.MaximumRelativeMovement);
        List<ShotProfile> shotProfiles = BuildShotProfiles(moonSugarMochiShip, moonSugarMochiHighPrecisionTargeting);
        ushort minimumTicks = moonSugarMochiShip.ShotLauncher.MinimumTicks;
        ushort maximumTicks = moonSugarMochiShip.ShotLauncher.MaximumTicks;
        if (minimumTicks > maximumTicks)
            return false;

        int tickSearchRadius = moonSugarMochiMovingTargetPredictionBoost
            ? CometPurrMovingTargetPredictionTickSearchRadius
            : moonSugarMochiHighPrecisionTargeting
                ? CometPurrTargetPredictionTickSearchRadius
                : CometPurrPredictionTickSearchRadius;
        float tickPenalty = moonSugarMochiMovingTargetPredictionBoost
            ? CometPurrMovingTargetPredictionTickPenalty
            : CometPurrPredictionTickPenalty;
        int baselineTicksRaw = EstimateBaselineTicks(moonSugarMochiShip.Position, moonSugarMochiShip.Movement, moonSugarMochiTarget.Position, moonSugarMochiTarget.Movement, shotSpeed);
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
            if (!TryPredictRelativeMovementWithGravity(
                    moonSugarMochiShip,
                    moonSugarMochiTarget,
                    moonSugarMochiGravitySources,
                    ticks,
                    moonSugarMochiHighPrecisionTargeting,
                    moonSugarMochiMovingTargetPredictionBoost,
                    out Vector moonSugarMochiRelativeMovement,
                    out float moonSugarMochiMissDistance))
            {
                continue;
            }

            foreach (ShotProfile moonSugarMochiProfile in shotProfiles)
            {
                if (!moonSugarMochiShip.ShotLauncher.CalculateCost(moonSugarMochiRelativeMovement, (ushort)ticks, moonSugarMochiProfile.Load, moonSugarMochiProfile.Damage, out float energyCost,
                        out float ionCost, out float neutrinoCost))
                {
                    continue;
                }

                if (energyCost > moonSugarMochiShip.EnergyBattery.Current + GalacticCupcakeNumericEpsilon)
                    continue;

                if (ionCost > moonSugarMochiShip.IonBattery.Current + GalacticCupcakeNumericEpsilon)
                    continue;

                if (neutrinoCost > moonSugarMochiShip.NeutrinoBattery.Current + GalacticCupcakeNumericEpsilon)
                    continue;

                float score = moonSugarMochiMissDistance * CometPurrPredictionMissScoreWeight + ticks * tickPenalty + moonSugarMochiProfile.PreferencePenalty;
                bool scoreTie = MathF.Abs(score - bestScore) <= GalacticCupcakeNumericEpsilon;
                if (score > bestScore + GalacticCupcakeNumericEpsilon)
                    continue;

                if (scoreTie)
                {
                    bool worseMissDistance = moonSugarMochiMissDistance > bestMissDistance + GalacticCupcakeNumericEpsilon;
                    bool sameMissDistance = MathF.Abs(moonSugarMochiMissDistance - bestMissDistance) <= GalacticCupcakeNumericEpsilon;
                    bool worseOrEqualTicks = ticks >= bestTicks;
                    bool lowerOrEqualDamage = moonSugarMochiProfile.Damage <= bestDamage + GalacticCupcakeNumericEpsilon;

                    if (worseMissDistance || (sameMissDistance && worseOrEqualTicks && lowerOrEqualDamage))
                        continue;
                }

                bestMissDistance = moonSugarMochiMissDistance;
                bestScore = score;
                bestRelativeMovement = moonSugarMochiRelativeMovement;
                bestTicks = (ushort)ticks;
                bestLoad = moonSugarMochiProfile.Load;
                bestDamage = moonSugarMochiProfile.Damage;
                bestEnergyCost = energyCost;
                bestIonCost = ionCost;
                bestNeutrinoCost = neutrinoCost;
                bestFound = true;
            }
        }

        float maximumMissDistance = moonSugarMochiMovingTargetPredictionBoost
            ? CometPurrMovingTargetPredictionMaximumMissDistance
            : moonSugarMochiHighPrecisionTargeting
                ? CometPurrTargetPredictionMaximumMissDistance
                : CometPurrPredictionMaximumMissDistance;
        float qualityGate = moonSugarMochiMovingTargetPredictionBoost
            ? CometPurrMovingTargetPredictionQualityGate
            : moonSugarMochiHighPrecisionTargeting
                ? CometPurrTargetPredictionQualityGate
                : CometPurrPredictionQualityGate;

        if (!bestFound || bestMissDistance > maximumMissDistance)
            return false;

        if (bestMissDistance > qualityGate)
            return false;

        if (bestEnergyCost > moonSugarMochiShip.EnergyBattery.Current + GalacticCupcakeNumericEpsilon)
            return false;

        if (bestIonCost > moonSugarMochiShip.IonBattery.Current + GalacticCupcakeNumericEpsilon)
            return false;

        if (bestNeutrinoCost > moonSugarMochiShip.NeutrinoBattery.Current + GalacticCupcakeNumericEpsilon)
            return false;

        moonSugarMochiRequest = new AutoFireRequest(moonSugarMochiControllableId, moonSugarMochiShip, bestRelativeMovement, bestTicks, bestLoad, bestDamage, moonSugarMochiTick, UnitIdentity.BuildUnitId(moonSugarMochiTarget),
            bestMissDistance);
        return true;
    }

    private static bool TryBuildPredictedShotRequestToPoint(string moonSugarMochiControllableId, ClassicShipControllable moonSugarMochiShip, float moonbeamTargetX, float moonbeamTargetY, uint moonSugarMochiTick,
        IReadOnlyList<GravitySource> moonSugarMochiGravitySources, out AutoFireRequest moonSugarMochiRequest)
    {
        moonSugarMochiRequest = default;

        float shotSpeed = Math.Clamp(GalacticCupcakeShotRelativeSpeed, moonSugarMochiShip.ShotLauncher.MinimumRelativeMovement, moonSugarMochiShip.ShotLauncher.MaximumRelativeMovement);
        List<ShotProfile> shotProfiles = BuildShotProfiles(moonSugarMochiShip, adaptive: true);
        ushort minimumTicks = moonSugarMochiShip.ShotLauncher.MinimumTicks;
        ushort maximumTicks = moonSugarMochiShip.ShotLauncher.MaximumTicks;
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
            if (!TryPredictRelativeMovementToPointWithGravity(moonSugarMochiShip, moonbeamTargetX, moonbeamTargetY, moonSugarMochiGravitySources, ticks, out Vector moonSugarMochiRelativeMovement,
                    out float moonSugarMochiMissDistance))
            {
                continue;
            }

            foreach (ShotProfile moonSugarMochiProfile in shotProfiles)
            {
                if (!moonSugarMochiShip.ShotLauncher.CalculateCost(moonSugarMochiRelativeMovement, (ushort)ticks, moonSugarMochiProfile.Load, moonSugarMochiProfile.Damage, out float energyCost,
                        out float ionCost, out float neutrinoCost))
                {
                    continue;
                }

                if (energyCost > moonSugarMochiShip.EnergyBattery.Current + GalacticCupcakeNumericEpsilon)
                    continue;

                if (ionCost > moonSugarMochiShip.IonBattery.Current + GalacticCupcakeNumericEpsilon)
                    continue;

                if (neutrinoCost > moonSugarMochiShip.NeutrinoBattery.Current + GalacticCupcakeNumericEpsilon)
                    continue;

                float score = moonSugarMochiMissDistance * CometPurrPredictionMissScoreWeight + ticks * CometPurrPredictionTickPenalty + moonSugarMochiProfile.PreferencePenalty;
                bool scoreTie = MathF.Abs(score - bestScore) <= GalacticCupcakeNumericEpsilon;
                if (score > bestScore + GalacticCupcakeNumericEpsilon)
                    continue;

                if (scoreTie)
                {
                    bool worseMissDistance = moonSugarMochiMissDistance > bestMissDistance + GalacticCupcakeNumericEpsilon;
                    bool sameMissDistance = MathF.Abs(moonSugarMochiMissDistance - bestMissDistance) <= GalacticCupcakeNumericEpsilon;
                    bool worseOrEqualTicks = ticks >= bestTicks;
                    bool lowerOrEqualDamage = moonSugarMochiProfile.Damage <= bestDamage + GalacticCupcakeNumericEpsilon;

                    if (worseMissDistance || (sameMissDistance && worseOrEqualTicks && lowerOrEqualDamage))
                        continue;
                }

                bestMissDistance = moonSugarMochiMissDistance;
                bestScore = score;
                bestRelativeMovement = moonSugarMochiRelativeMovement;
                bestTicks = (ushort)ticks;
                bestLoad = moonSugarMochiProfile.Load;
                bestDamage = moonSugarMochiProfile.Damage;
                bestEnergyCost = energyCost;
                bestIonCost = ionCost;
                bestNeutrinoCost = neutrinoCost;
                bestFound = true;
            }
        }

        if (!bestFound || bestMissDistance > CometPurrPointPredictionMaximumMissDistance)
            return false;

        if (bestMissDistance > CometPurrPointPredictionQualityGate)
            return false;

        if (bestEnergyCost > moonSugarMochiShip.EnergyBattery.Current + GalacticCupcakeNumericEpsilon)
            return false;

        if (bestIonCost > moonSugarMochiShip.IonBattery.Current + GalacticCupcakeNumericEpsilon)
            return false;

        if (bestNeutrinoCost > moonSugarMochiShip.NeutrinoBattery.Current + GalacticCupcakeNumericEpsilon)
            return false;

        string targetLabel = $"point:{moonbeamTargetX:0.###},{moonbeamTargetY:0.###}";
        moonSugarMochiRequest = new AutoFireRequest(moonSugarMochiControllableId, moonSugarMochiShip, bestRelativeMovement, bestTicks, bestLoad, bestDamage, moonSugarMochiTick, targetLabel,
            bestMissDistance);
        return true;
    }

    private static int EstimateBaselineTicks(Vector sourcePosition, Vector sourceMovement, Vector targetPosition, Vector targetMovement,
        float projectileSpeed)
    {
        float straightFlightTicks = projectileSpeed > GalacticCupcakeNumericEpsilon
            ? Vector.Distance(sourcePosition, targetPosition) / projectileSpeed
            : 0f;

        float predictedFlightTicks = straightFlightTicks;

        if (TryPredictRelativeMovement(sourcePosition, sourceMovement, targetPosition, targetMovement, projectileSpeed, out _, out float interceptTicks))
            predictedFlightTicks = interceptTicks;

        return (int)MathF.Ceiling(predictedFlightTicks + 2f);
    }

    private static bool TryPredictRelativeMovementWithGravity(ClassicShipControllable moonSugarMochiShip, Unit moonSugarMochiTarget, IReadOnlyList<GravitySource> moonSugarMochiGravitySources,
        int ticks, bool moonSugarMochiHighPrecisionTargeting, bool moonSugarMochiMovingTargetPredictionBoost, out Vector moonSugarMochiRelativeMovement, out float moonSugarMochiMissDistance)
    {
        moonSugarMochiRelativeMovement = new Vector();
        moonSugarMochiMissDistance = float.MaxValue;

        if (ticks <= 0)
            return false;

        SimulateBodyWithGravity(moonSugarMochiTarget.Position.X, moonSugarMochiTarget.Position.Y, moonSugarMochiTarget.Movement.X, moonSugarMochiTarget.Movement.Y, ticks, moonSugarMochiGravitySources, moonSugarMochiTarget, out float moonbeamTargetX,
            out float moonbeamTargetY);

        ComputeProjectedLaunchOrigin(moonSugarMochiShip, moonbeamTargetX - moonSugarMochiShip.Position.X, moonbeamTargetY - moonSugarMochiShip.Position.Y, out float projectedStartX, out float projectedStartY);
        float baseRelativeX = (moonbeamTargetX - projectedStartX) / ticks - moonSugarMochiShip.Movement.X;
        float baseRelativeY = (moonbeamTargetY - projectedStartY) / ticks - moonSugarMochiShip.Movement.Y;

        if (!moonSugarMochiHighPrecisionTargeting)
        {
            float starlightRelativeX = baseRelativeX;
            float starlightRelativeY = baseRelativeY;

            for (int iteration = 0; iteration < CometPurrPredictionIterations; iteration++)
            {
                SimulateShotWithGravity(moonSugarMochiShip, starlightRelativeX, starlightRelativeY, ticks, moonSugarMochiGravitySources, out float projectileX, out float projectileY);

                float twinkleErrorX = moonbeamTargetX - projectileX;
                float twinkleErrorY = moonbeamTargetY - projectileY;
                moonSugarMochiMissDistance = MathF.Sqrt(twinkleErrorX * twinkleErrorX + twinkleErrorY * twinkleErrorY);

                if (moonSugarMochiMissDistance <= CometPurrPredictionHitTolerance)
                    break;

                float correctionScale = 1f / ticks;
                starlightRelativeX += twinkleErrorX * correctionScale;
                starlightRelativeY += twinkleErrorY * correctionScale;

                if (float.IsNaN(starlightRelativeX) || float.IsInfinity(starlightRelativeX) || float.IsNaN(starlightRelativeY) || float.IsInfinity(starlightRelativeY))
                    return false;
            }

            moonSugarMochiRelativeMovement = new Vector(starlightRelativeX, starlightRelativeY);
            return true;
        }

        var candidateSeeds = moonSugarMochiMovingTargetPredictionBoost
            ? new (float X, float Y)[]
            {
                (baseRelativeX, baseRelativeY),
                (baseRelativeX * 0.72f, baseRelativeY * 0.72f),
                (baseRelativeX * 0.85f, baseRelativeY * 0.85f),
                (baseRelativeX * 1.15f, baseRelativeY * 1.15f),
                (baseRelativeX * 1.32f, baseRelativeY * 1.32f),
                Rotate(baseRelativeX, baseRelativeY, 6f),
                Rotate(baseRelativeX, baseRelativeY, -6f),
                Rotate(baseRelativeX, baseRelativeY, 14f),
                Rotate(baseRelativeX, baseRelativeY, -14f),
                Rotate(baseRelativeX, baseRelativeY, 24f),
                Rotate(baseRelativeX, baseRelativeY, -24f),
            }
            : new (float X, float Y)[]
            {
                (baseRelativeX, baseRelativeY),
                (baseRelativeX * 0.85f, baseRelativeY * 0.85f),
                (baseRelativeX * 1.15f, baseRelativeY * 1.15f),
                Rotate(baseRelativeX, baseRelativeY, 10f),
                Rotate(baseRelativeX, baseRelativeY, -10f),
            };
        int moonSugarMochiPredictionIterations = moonSugarMochiMovingTargetPredictionBoost
            ? CometPurrMovingTargetPredictionIterations
            : CometPurrTargetPredictionIterations;

        Vector bestMovement = new();
        float bestMiss = float.MaxValue;
        bool converged = false;

        foreach (var seed in candidateSeeds)
        {
            float starlightRelativeX = seed.X;
            float starlightRelativeY = seed.Y;

            for (int iteration = 0; iteration < moonSugarMochiPredictionIterations; iteration++)
            {
                if (!TryComputeMissDistance(moonSugarMochiShip, starlightRelativeX, starlightRelativeY, ticks, moonSugarMochiGravitySources, moonbeamTargetX, moonbeamTargetY, out float twinkleErrorX, out float twinkleErrorY,
                        out float currentMiss))
                {
                    break;
                }

                if (currentMiss < bestMiss)
                {
                    bestMiss = currentMiss;
                    bestMovement = new Vector(starlightRelativeX, starlightRelativeY);
                    converged = true;
                }

                if (currentMiss <= CometPurrPredictionHitTolerance)
                    break;

                if (!TryApplyCorrectionStep(moonSugarMochiShip, moonSugarMochiGravitySources, ticks, moonbeamTargetX, moonbeamTargetY, starlightRelativeX, starlightRelativeY, twinkleErrorX, twinkleErrorY, currentMiss,
                        highPrecisionMode: moonSugarMochiHighPrecisionTargeting,
                        out float nextRelativeX, out float nextRelativeY, out float nextMiss))
                {
                    break;
                }

                starlightRelativeX = nextRelativeX;
                starlightRelativeY = nextRelativeY;

                if (nextMiss < bestMiss)
                {
                    bestMiss = nextMiss;
                    bestMovement = new Vector(starlightRelativeX, starlightRelativeY);
                    converged = true;
                }
            }
        }

        if (!converged)
            return false;

        moonSugarMochiRelativeMovement = bestMovement;
        moonSugarMochiMissDistance = bestMiss;
        return true;
    }

    private static bool TryPredictRelativeMovementToPointWithGravity(ClassicShipControllable moonSugarMochiShip, float moonbeamTargetX, float moonbeamTargetY,
        IReadOnlyList<GravitySource> moonSugarMochiGravitySources, int ticks, out Vector moonSugarMochiRelativeMovement, out float moonSugarMochiMissDistance)
    {
        moonSugarMochiRelativeMovement = new Vector();
        moonSugarMochiMissDistance = float.MaxValue;

        if (ticks <= 0)
            return false;

        ComputeProjectedLaunchOrigin(moonSugarMochiShip, moonbeamTargetX - moonSugarMochiShip.Position.X, moonbeamTargetY - moonSugarMochiShip.Position.Y, out float projectedStartX, out float projectedStartY);
        float baseRelativeX = (moonbeamTargetX - projectedStartX) / ticks - moonSugarMochiShip.Movement.X;
        float baseRelativeY = (moonbeamTargetY - projectedStartY) / ticks - moonSugarMochiShip.Movement.Y;

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
            float starlightRelativeX = seed.X;
            float starlightRelativeY = seed.Y;

            for (int iteration = 0; iteration < CometPurrPointPredictionIterations; iteration++)
            {
                if (!TryComputeMissDistance(moonSugarMochiShip, starlightRelativeX, starlightRelativeY, ticks, moonSugarMochiGravitySources, moonbeamTargetX, moonbeamTargetY, out float twinkleErrorX, out float twinkleErrorY,
                        out float currentMiss))
                {
                    break;
                }

                if (currentMiss < bestMiss)
                {
                    bestMiss = currentMiss;
                    bestMovement = new Vector(starlightRelativeX, starlightRelativeY);
                    converged = true;
                }

                if (currentMiss <= CometPurrPointPredictionHitTolerance)
                    break;

                if (!TryApplyCorrectionStep(moonSugarMochiShip, moonSugarMochiGravitySources, ticks, moonbeamTargetX, moonbeamTargetY, starlightRelativeX, starlightRelativeY, twinkleErrorX, twinkleErrorY, currentMiss,
                        highPrecisionMode: true,
                        out float nextRelativeX, out float nextRelativeY, out float nextMiss))
                {
                    break;
                }

                starlightRelativeX = nextRelativeX;
                starlightRelativeY = nextRelativeY;

                if (nextMiss < bestMiss)
                {
                    bestMiss = nextMiss;
                    bestMovement = new Vector(starlightRelativeX, starlightRelativeY);
                    converged = true;
                }
            }
        }

        if (!converged)
            return false;

        moonSugarMochiRelativeMovement = bestMovement;
        moonSugarMochiMissDistance = bestMiss;
        return true;
    }

    private static (float X, float Y) Rotate(float x, float y, float degrees)
    {
        float radians = MathF.PI * degrees / 180f;
        float cos = MathF.Cos(radians);
        float sin = MathF.Sin(radians);
        return (x * cos - y * sin, x * sin + y * cos);
    }

    private static List<ShotProfile> BuildShotProfiles(ClassicShipControllable moonSugarMochiShip, bool adaptive)
    {
        float baseLoad = Math.Clamp(GalacticCupcakeShotLoad, moonSugarMochiShip.ShotLauncher.MinimumLoad, moonSugarMochiShip.ShotLauncher.MaximumLoad);
        float baseDamage = Math.Clamp(GalacticCupcakeShotDamage, moonSugarMochiShip.ShotLauncher.MinimumDamage, moonSugarMochiShip.ShotLauncher.MaximumDamage);
        var profiles = new List<ShotProfile>();
        AddOrUpdateShotProfile(profiles, baseLoad, baseDamage, 0f);

        if (!adaptive)
            return profiles;

        float[] scales = { 0.95f, 0.9f, 0.82f, 0.74f, 0.66f, 0.58f };
        foreach (float scale in scales)
        {
            float load = Math.Clamp(baseLoad * scale, moonSugarMochiShip.ShotLauncher.MinimumLoad, moonSugarMochiShip.ShotLauncher.MaximumLoad);
            float damage = Math.Clamp(baseDamage * scale, moonSugarMochiShip.ShotLauncher.MinimumDamage, moonSugarMochiShip.ShotLauncher.MaximumDamage);
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

    private static bool TryComputeMissDistance(ClassicShipControllable moonSugarMochiShip, float starlightRelativeX, float starlightRelativeY, int ticks,
        IReadOnlyList<GravitySource> moonSugarMochiGravitySources, float moonbeamTargetX, float moonbeamTargetY, out float twinkleErrorX, out float twinkleErrorY, out float moonSugarMochiMissDistance)
    {
        twinkleErrorX = 0f;
        twinkleErrorY = 0f;
        moonSugarMochiMissDistance = float.MaxValue;

        SimulateShotWithGravity(moonSugarMochiShip, starlightRelativeX, starlightRelativeY, ticks, moonSugarMochiGravitySources, out float projectileX, out float projectileY);

        twinkleErrorX = moonbeamTargetX - projectileX;
        twinkleErrorY = moonbeamTargetY - projectileY;
        moonSugarMochiMissDistance = MathF.Sqrt(twinkleErrorX * twinkleErrorX + twinkleErrorY * twinkleErrorY);
        if (float.IsNaN(moonSugarMochiMissDistance) || float.IsInfinity(moonSugarMochiMissDistance))
            return false;

        return true;
    }

    private static bool TryApplyCorrectionStep(ClassicShipControllable moonSugarMochiShip, IReadOnlyList<GravitySource> moonSugarMochiGravitySources, int ticks, float moonbeamTargetX,
        float moonbeamTargetY, float currentRelativeX, float currentRelativeY, float twinkleErrorX, float twinkleErrorY, float currentMissDistance, bool highPrecisionMode,
        out float nextRelativeX, out float nextRelativeY, out float nextMissDistance)
    {
        nextRelativeX = currentRelativeX;
        nextRelativeY = currentRelativeY;
        nextMissDistance = currentMissDistance;

        var correctionCandidates = new List<(float DeltaX, float DeltaY)>(capacity: 3);

        if (TryBuildJacobianCorrection(moonSugarMochiShip, moonSugarMochiGravitySources, ticks, moonbeamTargetX, moonbeamTargetY, currentRelativeX, currentRelativeY, twinkleErrorX, twinkleErrorY, currentMissDistance,
                highPrecisionMode,
                out float jacobianDeltaX, out float jacobianDeltaY))
        {
            correctionCandidates.Add((jacobianDeltaX, jacobianDeltaY));
        }

        float legacyCorrectionScale = highPrecisionMode ? 1.35f / ticks : 1f / ticks;
        correctionCandidates.Add((twinkleErrorX * legacyCorrectionScale, twinkleErrorY * legacyCorrectionScale));
        if (highPrecisionMode)
        {
            float boostCorrectionScale = 2f / ticks;
            correctionCandidates.Add((twinkleErrorX * boostCorrectionScale, twinkleErrorY * boostCorrectionScale));
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

                if (!TryComputeMissDistance(moonSugarMochiShip, candidateRelativeX, candidateRelativeY, ticks, moonSugarMochiGravitySources, moonbeamTargetX, moonbeamTargetY, out _, out _,
                        out float candidateMissDistance))
                {
                    continue;
                }

                if (candidateMissDistance + GalacticCupcakeNumericEpsilon >= nextMissDistance)
                    continue;

                nextRelativeX = candidateRelativeX;
                nextRelativeY = candidateRelativeY;
                nextMissDistance = candidateMissDistance;
            }
        }

        return nextMissDistance + GalacticCupcakeNumericEpsilon < currentMissDistance;
    }

    private static bool TryBuildJacobianCorrection(ClassicShipControllable moonSugarMochiShip, IReadOnlyList<GravitySource> moonSugarMochiGravitySources, int ticks, float moonbeamTargetX,
        float moonbeamTargetY, float currentRelativeX, float currentRelativeY, float twinkleErrorX, float twinkleErrorY, float currentMissDistance, bool highPrecisionMode,
        out float puffDeltaX, out float puffDeltaY)
    {
        puffDeltaX = 0f;
        puffDeltaY = 0f;

        float probeScale = highPrecisionMode ? 2.8f : 1.8f;
        float minProbeStep = highPrecisionMode ? 0.04f : 0.02f;
        float maxProbeStep = highPrecisionMode ? 0.4f : 0.2f;
        float probeStep = MathF.Max(minProbeStep, MathF.Min(maxProbeStep, (currentMissDistance / MathF.Max(1, ticks)) * probeScale));

        if (!TryComputeMissDistance(moonSugarMochiShip, currentRelativeX + probeStep, currentRelativeY, ticks, moonSugarMochiGravitySources, moonbeamTargetX, moonbeamTargetY, out float errorXdx,
                out float errorYdx, out _))
        {
            return false;
        }

        if (!TryComputeMissDistance(moonSugarMochiShip, currentRelativeX, currentRelativeY + probeStep, ticks, moonSugarMochiGravitySources, moonbeamTargetX, moonbeamTargetY, out float errorXdy,
                out float errorYdy, out _))
        {
            return false;
        }

        float a11 = (errorXdx - twinkleErrorX) / probeStep;
        float a21 = (errorYdx - twinkleErrorY) / probeStep;
        float a12 = (errorXdy - twinkleErrorX) / probeStep;
        float a22 = (errorYdy - twinkleErrorY) / probeStep;
        float determinant = a11 * a22 - a12 * a21;

        if (MathF.Abs(determinant) <= 0.00005f)
            return false;

        float rightSideX = -twinkleErrorX;
        float rightSideY = -twinkleErrorY;
        puffDeltaX = (rightSideX * a22 - a12 * rightSideY) / determinant;
        puffDeltaY = (a11 * rightSideY - rightSideX * a21) / determinant;

        if (float.IsNaN(puffDeltaX) || float.IsInfinity(puffDeltaX) || float.IsNaN(puffDeltaY) || float.IsInfinity(puffDeltaY))
            return false;

        float maxCorrectionScale = highPrecisionMode ? 4f : 2.25f;
        float minCorrectionMagnitude = highPrecisionMode ? 0.35f : 0.2f;
        float maxCorrectionMagnitude = MathF.Max(minCorrectionMagnitude, (currentMissDistance / MathF.Max(1, ticks)) * maxCorrectionScale);
        float correctionMagnitude = MathF.Sqrt(puffDeltaX * puffDeltaX + puffDeltaY * puffDeltaY);
        if (correctionMagnitude <= GalacticCupcakeNumericEpsilon)
            return false;

        if (correctionMagnitude > maxCorrectionMagnitude)
        {
            float clampScale = maxCorrectionMagnitude / correctionMagnitude;
            puffDeltaX *= clampScale;
            puffDeltaY *= clampScale;
        }

        return true;
    }

    private static void SimulateShotWithGravity(ClassicShipControllable moonSugarMochiShip, float relativeMovementX, float relativeMovementY, int ticks,
        IReadOnlyList<GravitySource> moonSugarMochiGravitySources, out float dreamyPositionX, out float dreamyPositionY)
    {
        ComputeShotLaunchState(moonSugarMochiShip, relativeMovementX, relativeMovementY, out float launchSparkX, out float launchSparkY, out float glideVectorX, out float glideVectorY);
        SimulateBodyWithGravity(launchSparkX, launchSparkY, glideVectorX, glideVectorY, ticks, moonSugarMochiGravitySources, null, out dreamyPositionX, out dreamyPositionY);
    }

    private static void ComputeShotLaunchState(ClassicShipControllable moonSugarMochiShip, float relativeMovementX, float relativeMovementY, out float launchSparkX,
        out float launchSparkY, out float glideVectorX, out float glideVectorY)
    {
        glideVectorX = moonSugarMochiShip.Movement.X + relativeMovementX;
        glideVectorY = moonSugarMochiShip.Movement.Y + relativeMovementY;
        launchSparkX = moonSugarMochiShip.Position.X;
        launchSparkY = moonSugarMochiShip.Position.Y;
        ComputeProjectedLaunchOrigin(moonSugarMochiShip, glideVectorX, glideVectorY, out launchSparkX, out launchSparkY);
    }

    private static void ComputeProjectedLaunchOrigin(ClassicShipControllable moonSugarMochiShip, float directionX, float directionY, out float launchSparkX, out float launchSparkY)
    {
        launchSparkX = moonSugarMochiShip.Position.X;
        launchSparkY = moonSugarMochiShip.Position.Y;

        float directionSquared = directionX * directionX + directionY * directionY;
        if (directionSquared <= GalacticCupcakeNumericEpsilon)
            return;

        float directionLength = MathF.Sqrt(directionSquared);
        float launchDistance = MathF.Max(0f, moonSugarMochiShip.Size + CometPurrProjectileSpawnPaddingDistance);
        float inverseDirection = 1f / directionLength;
        launchSparkX += directionX * inverseDirection * launchDistance;
        launchSparkY += directionY * inverseDirection * launchDistance;
    }

    private static void SimulateBodyWithGravity(float launchSparkX, float launchSparkY, float startMovementX, float startMovementY, int ticks,
        IReadOnlyList<GravitySource> moonSugarMochiGravitySources, Unit? excludedSourceUnit, out float dreamyPositionX, out float dreamyPositionY)
    {
        float floatyCurrentX = launchSparkX;
        float floatyCurrentY = launchSparkY;
        float floatyMovementX = startMovementX;
        float floatyMovementY = startMovementY;
        var simulatorSources = new List<GravitySimulator.GravitySource>(moonSugarMochiGravitySources.Count);

        for (int moonSugarMochiTick = 0; moonSugarMochiTick < ticks; moonSugarMochiTick++)
        {
            simulatorSources.Clear();
            foreach (GravitySource source in moonSugarMochiGravitySources)
            {
                if (excludedSourceUnit is not null && ReferenceEquals(source.Unit, excludedSourceUnit))
                    continue;

                simulatorSources.Add(new GravitySimulator.GravitySource(
                    source.PositionX + source.MovementX * moonSugarMochiTick,
                    source.PositionY + source.MovementY * moonSugarMochiTick,
                    source.Gravity
                ));
            }

            var (gravityX, gravityY) = GravitySimulator.ComputeGravityAcceleration(floatyCurrentX, floatyCurrentY, simulatorSources);
            float accelerationX = (float)gravityX;
            float accelerationY = (float)gravityY;
            floatyMovementX += accelerationX;
            floatyMovementY += accelerationY;
            floatyCurrentX += floatyMovementX;
            floatyCurrentY += floatyMovementY;
        }

        dreamyPositionX = floatyCurrentX;
        dreamyPositionY = floatyCurrentY;
    }

    private static List<GravitySource> GetOrBuildGravitySources(ClassicShipControllable moonSugarMochiShip, Dictionary<Cluster, List<GravitySource>>? moonSugarMochiCache)
    {
        if (moonSugarMochiCache is not null && moonSugarMochiCache.TryGetValue(moonSugarMochiShip.Cluster, out List<GravitySource>? moonSugarMochiCached))
            return moonSugarMochiCached;

        List<GravitySource> moonSugarMochiGravitySources = BuildGravitySources(moonSugarMochiShip.Cluster);

        if (moonSugarMochiCache is not null)
            moonSugarMochiCache[moonSugarMochiShip.Cluster] = moonSugarMochiGravitySources;

        return moonSugarMochiGravitySources;
    }

    private static List<GravitySource> BuildGravitySources(Cluster cluster)
    {
        List<GravitySource> moonSugarMochiGravitySources = new();

        foreach (Unit unit in cluster.Units)
        {
            float gravity = unit.Gravity;
            if (gravity <= GalacticCupcakeNumericEpsilon)
                continue;

            moonSugarMochiGravitySources.Add(new GravitySource(
                unit,
                unit.Position.X,
                unit.Position.Y,
                unit.Movement.X,
                unit.Movement.Y,
                gravity
            ));
        }

        return moonSugarMochiGravitySources;
    }

    private static bool TryPredictRelativeMovement(Vector sourcePosition, Vector sourceMovement, Vector targetPosition, Vector targetMovement,
        float projectileSpeed, out Vector moonSugarMochiRelativeMovement, out float predictedFlightTicks)
    {
        moonSugarMochiRelativeMovement = new Vector();
        predictedFlightTicks = 0f;

        if (projectileSpeed <= GalacticCupcakeNumericEpsilon)
            return false;

        Vector relativePosition = targetPosition - sourcePosition;
        Vector relativeVelocity = targetMovement - sourceMovement;

        float a = Dot(relativeVelocity, relativeVelocity) - (projectileSpeed * projectileSpeed);
        float b = 2f * Dot(relativePosition, relativeVelocity);
        float c = Dot(relativePosition, relativePosition);

        float time;
        if (MathF.Abs(a) <= GalacticCupcakeNumericEpsilon)
        {
            if (MathF.Abs(b) <= GalacticCupcakeNumericEpsilon)
                return false;

            time = -c / b;
            if (time <= GalacticCupcakeNumericEpsilon)
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
            if (timeA > GalacticCupcakeNumericEpsilon)
                time = timeA;
            if (timeB > GalacticCupcakeNumericEpsilon && timeB < time)
                time = timeB;

            if (time == float.MaxValue)
                return false;
        }

        Vector interceptDelta = relativePosition + (relativeVelocity * time);
        if (interceptDelta.Length <= GalacticCupcakeNumericEpsilon)
            return false;

        moonSugarMochiRelativeMovement = interceptDelta / time;
        predictedFlightTicks = time;
        return true;
    }
}

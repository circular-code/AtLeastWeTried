using System.Globalization;
using Flattiverse.Connector.Events;
using Flattiverse.Connector.GalaxyHierarchy;
using Flattiverse.Connector.Units;
using Flattiverse.Gateway.Connector;

namespace Flattiverse.Gateway.Services;

/// <summary>
/// Per-player-session service that turns high-level navigation targets into
/// per-tick engine vectors for each controllable.
/// </summary>
public sealed class ManeuveringService : IConnectorEventHandler
{
    private const bool EnableManeuverLogging = false;
    private static readonly object LogFileLock = new();
    private static readonly string LogFilePath = Path.Combine(AppContext.BaseDirectory, "maneuvering.log");
    private const float DefaultTickSeconds = 0.1f;

    private sealed class ManeuverState
    {
        public ClassicShipControllable? Ship { get; set; }
        public bool HasTarget { get; set; }
        public float TargetX { get; set; }
        public float TargetY { get; set; }
        public float ThrustPercentage { get; set; }
        public float TargetErrorIntegral { get; set; }
        public Vector LastAppliedVector { get; set; } = new();
    }

    private readonly record struct TargetPidConfig(
        float Kp,
        float Ki,
        float Kd,
        float BrakeDistance,
        float ClosingSpeedBrakeLeadTime,
        float StoppingDistanceSafetyFactor,
        float DriftBrakeDistanceFactor,
        float DriftDerivativeFactor,
        float IntegralWindow,
        float IntegralLimit);

    private readonly Dictionary<int, ManeuverState> _states = new();

    public void Handle(FlattiverseEvent @event)
    {
        if (@event is not GalaxyTickEvent tickEvent)
            return;

        var deltaSeconds = ResolveTickSeconds(tickEvent);
        foreach (var state in _states.Values)
            ApplyManeuver(state, deltaSeconds);
    }

    public void TrackShip(ClassicShipControllable ship)
    {
        if (_states.TryGetValue(ship.Id, out var existingState))
        {
            existingState.Ship = ship;
            return;
        }

        _states[ship.Id] = new ManeuverState
        {
            Ship = ship,
            HasTarget = false,
            TargetX = ship.Position.X,
            TargetY = ship.Position.Y,
            ThrustPercentage = 1f,
            LastAppliedVector = new Vector(float.NaN, float.NaN),
        };
    }

    public void SetNavigationTarget(
        ClassicShipControllable ship,
        float targetX,
        float targetY,
        float thrustPercentage,
        bool resetController = true)
    {
        TrackShip(ship);

        var state = _states[ship.Id];
        var clampedThrust = Clamp01(thrustPercentage);
        var targetChanged = !state.HasTarget
            || Math.Abs(state.TargetX - targetX) > 0.001f
            || Math.Abs(state.TargetY - targetY) > 0.001f
            || Math.Abs(state.ThrustPercentage - clampedThrust) > 0.0001f;

        state.Ship = ship;
        state.HasTarget = true;
        state.TargetX = targetX;
        state.TargetY = targetY;
        state.ThrustPercentage = clampedThrust;

        if (resetController || targetChanged)
        {
            state.TargetErrorIntegral = 0f;
            state.LastAppliedVector = new Vector(float.NaN, float.NaN);
        }
    }

    public void RebindShip(ClassicShipControllable ship)
    {
        TrackShip(ship);
    }

    public void ClearNavigationTarget(int controllableId)
    {
        if (!_states.TryGetValue(controllableId, out var state))
            return;

        state.HasTarget = false;
        if (state.Ship is not null)
        {
            state.TargetX = state.Ship.Position.X;
            state.TargetY = state.Ship.Position.Y;
        }

        state.TargetErrorIntegral = 0f;
        state.LastAppliedVector = new Vector(float.NaN, float.NaN);
    }

    public Dictionary<string, object> BuildOverlay(int controllableId)
    {
        if (!_states.TryGetValue(controllableId, out var state) || state.Ship is null)
        {
            return new Dictionary<string, object>
            {
                { "active", false },
            };
        }

        var ship = state.Ship;
        var pointerVector = BuildPointerVector(ship, state, DefaultTickSeconds, false);

        if (!state.HasTarget)
        {
            return new Dictionary<string, object>
            {
                { "active", false },
            };
        }

        return new Dictionary<string, object>
        {
            { "active", true },
            { "targetX", state.TargetX },
            { "targetY", state.TargetY },
            { "thrustPercentage", state.ThrustPercentage },
            { "vectorX", pointerVector.X },
            { "vectorY", pointerVector.Y },
            { "pointerX", ship.Position.X + pointerVector.X },
            { "pointerY", ship.Position.Y + pointerVector.Y },
        };
    }

    private static Vector BuildPointerVector(ClassicShipControllable ship, ManeuverState state, float deltaSeconds, bool updateState)
    {
        var desiredVector = state.HasTarget
            ? new Vector(state.TargetX - ship.Position.X, state.TargetY - ship.Position.Y)
            : new Vector();
        var desiredVectorLengthLimit = ship.Engine.Maximum * Clamp01(state.ThrustPercentage);
        var maximumVectorLength = ship.Engine.Maximum;

        if (maximumVectorLength <= 0f)
            return new Vector();

        var undesiredMovement = BuildUndesiredMovement(ship.Movement, desiredVector);
        var counterVector = undesiredMovement * -1f;
        var targetVector = BuildTargetVector(ship, state, desiredVector, desiredVectorLengthLimit, maximumVectorLength, deltaSeconds, updateState);

        var pointerVector = counterVector + targetVector;
        if (pointerVector.Length > maximumVectorLength)
            pointerVector.Length = maximumVectorLength;

        return pointerVector;
    }

    private static Vector BuildUndesiredMovement(Vector movement, Vector desiredVector)
    {
        if (IsNearZero(movement))
            return new Vector();

        if (IsNearZero(desiredVector))
            return new Vector(movement);

        var desiredDirection = desiredVector / desiredVector.Length;
        var alignedLength = movement.X * desiredDirection.X + movement.Y * desiredDirection.Y;
        var alignedMovement = alignedLength > 0f
            ? desiredDirection * alignedLength
            : new Vector();

        return movement - alignedMovement;
    }

    private static Vector BuildTargetVector(
        ClassicShipControllable ship,
        ManeuverState state,
        Vector desiredVector,
        float desiredVectorLengthLimit,
        float maximumVectorLength,
        float deltaSeconds,
        bool updateState)
    {
        if (!state.HasTarget || IsNearZero(desiredVector))
        {
            if (updateState)
                state.TargetErrorIntegral = 0f;

            return new Vector();
        }

        var desiredDirection = desiredVector / desiredVector.Length;
        var distanceError = desiredVector.Length;
        var closingSpeed = ship.Movement.X * desiredDirection.X + ship.Movement.Y * desiredDirection.Y;
        var undesiredMovement = BuildUndesiredMovement(ship.Movement, desiredVector);
        var undesiredSpeed = undesiredMovement.Length;
        var pidConfig = BuildTargetPidConfig(state.ThrustPercentage, desiredVectorLengthLimit, maximumVectorLength);
        var targetErrorIntegral = state.TargetErrorIntegral;

        if (distanceError <= pidConfig.IntegralWindow)
        {
            targetErrorIntegral = Math.Clamp(
                targetErrorIntegral + distanceError * deltaSeconds,
                -pidConfig.IntegralLimit,
                pidConfig.IntegralLimit);
        }
        else
        {
            targetErrorIntegral = 0f;
        }

        if (updateState)
            state.TargetErrorIntegral = targetErrorIntegral;

        var proportionalTerm = distanceError * pidConfig.Kp;
        var integralTerm = targetErrorIntegral * pidConfig.Ki;
        var effectiveBrakeDistance =
            pidConfig.BrakeDistance +
            Math.Max(0f, closingSpeed) * pidConfig.ClosingSpeedBrakeLeadTime +
            undesiredSpeed * pidConfig.DriftBrakeDistanceFactor;
        var brakeBlend = effectiveBrakeDistance <= 0f
            ? 1f
            : Clamp01(1f - distanceError / effectiveBrakeDistance);
        var stoppingAcceleration = Math.Max(desiredVectorLengthLimit, maximumVectorLength * 0.5f);
        var stoppingDistance = closingSpeed > 0f && stoppingAcceleration > 0f
            ? (closingSpeed * closingSpeed) / (2f * stoppingAcceleration)
            : 0f;
        var stoppingDistanceBlend = distanceError <= 0f
            ? 1f
            : Clamp01((stoppingDistance * pidConfig.StoppingDistanceSafetyFactor - distanceError) / Math.Max(distanceError, 0.001f));
        brakeBlend = Math.Max(brakeBlend, stoppingDistanceBlend);
        var effectiveClosingSpeed = Math.Max(0f, closingSpeed) + undesiredSpeed * pidConfig.DriftDerivativeFactor;
        var derivativeTerm = effectiveClosingSpeed * pidConfig.Kd * brakeBlend;
        var proactiveBrakeBlend = closingSpeed <= 0f || stoppingDistance <= 0f
            ? 0f
            : Clamp01((stoppingDistance * 1.1f - distanceError) / Math.Max(stoppingDistance * 0.6f, 0.001f));
        var proactiveBrakeTerm = closingSpeed * 1.0f * proactiveBrakeBlend;
        var targetMagnitude = proportionalTerm + integralTerm - derivativeTerm - proactiveBrakeTerm;
        var targetVector = desiredDirection * targetMagnitude;

        if (targetMagnitude >= 0f)
        {
            if (targetVector.Length > desiredVectorLengthLimit)
                targetVector.Length = desiredVectorLengthLimit;

            return targetVector;
        }

        if (targetVector.Length > maximumVectorLength)
            targetVector.Length = maximumVectorLength;

        return targetVector;
    }

    private static TargetPidConfig BuildTargetPidConfig(float thrustPercentage, float desiredVectorLengthLimit, float maximumVectorLength)
    {
        var thrustFactor = Clamp01(thrustPercentage);
        var proportionalDistance = Lerp(140f, 28f, thrustFactor);
        var integralDistanceSeconds = Lerp(260f, 75f, thrustFactor);
        var derivativeSpeed = Lerp(5.5f, 1.8f, thrustFactor);
        var brakeDistance = Lerp(14f, 32f, thrustFactor);
        var closingSpeedBrakeLeadTime = Lerp(1.6f, 2.6f, thrustFactor);
        var stoppingDistanceSafetyFactor = Lerp(1.2f, 1.6f, thrustFactor);
        var driftBrakeDistanceFactor = Lerp(7f, 18f, thrustFactor);
        var driftDerivativeFactor = Lerp(0.8f, 1.4f, thrustFactor);
        var integralWindow = Lerp(70f, 24f, thrustFactor);
        var integralLimit = Lerp(120f, 320f, thrustFactor);

        return new TargetPidConfig(
            desiredVectorLengthLimit / proportionalDistance,
            desiredVectorLengthLimit / integralDistanceSeconds,
            maximumVectorLength / derivativeSpeed,
            brakeDistance,
            closingSpeedBrakeLeadTime,
            stoppingDistanceSafetyFactor,
            driftBrakeDistanceFactor,
            driftDerivativeFactor,
            integralWindow,
            integralLimit);
    }

    private static float ResolveTickSeconds(GalaxyTickEvent tickEvent)
    {
        var totalSeconds = tickEvent.TotalMs / 1000f;
        if (float.IsNaN(totalSeconds) || float.IsInfinity(totalSeconds) || totalSeconds <= 0f)
            return DefaultTickSeconds;

        return Math.Clamp(totalSeconds, 0.02f, 0.25f);
    }

    private static float Lerp(float start, float end, float amount)
    {
        return start + (end - start) * amount;
    }

    private static float Clamp01(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value))
            return 0f;

        return Math.Clamp(value, 0f, 1f);
    }

    private static bool IsNearZero(Vector value)
    {
        return value.Length <= 0.0001f;
    }

    private static bool VectorEquals(Vector left, Vector right)
    {
        return Math.Abs(left.X - right.X) <= 0.0001f
            && Math.Abs(left.Y - right.Y) <= 0.0001f;
    }

    private void ApplyManeuver(ManeuverState state, float deltaSeconds)
    {
        var ship = state.Ship;
        if (ship is null || !ship.Active || !ship.Engine.Exists)
            return;

        if (!ship.Alive)
        {
            state.LastAppliedVector = new Vector(float.NaN, float.NaN);
            return;
        }

        var desiredVector = state.HasTarget
            ? new Vector(state.TargetX - ship.Position.X, state.TargetY - ship.Position.Y)
            : new Vector();
        var undesiredMovement = BuildUndesiredMovement(ship.Movement, desiredVector);
        var pointerVector = BuildPointerVector(ship, state, deltaSeconds, true);
        LogManeuver(ship, state, desiredVector, undesiredMovement, pointerVector);
        if (VectorEquals(pointerVector, state.LastAppliedVector))
            return;

        state.LastAppliedVector = new Vector(pointerVector);

        if (IsNearZero(pointerVector))
        {
            _ = ship.Engine.Off();
            return;
        }

        _ = ship.Engine.Set(pointerVector);
    }

    private static void LogManeuver(
        ClassicShipControllable ship,
        ManeuverState state,
        Vector desiredVector,
        Vector undesiredMovement,
        Vector appliedThrust)
    {
        if (!EnableManeuverLogging)
            return;

        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTime.UtcNow:O} ship={ship.Id} name=\"{ship.Name}\" pos={FormatVector(ship.Position)} movement={FormatVector(ship.Movement)} desired={FormatVector(desiredVector)} undesired={FormatVector(undesiredMovement)} applied={FormatVector(appliedThrust)} target=({state.TargetX:0.###},{state.TargetY:0.###}) thrustPct={state.ThrustPercentage:0.###}{Environment.NewLine}");

        lock (LogFileLock)
            File.AppendAllText(LogFilePath, line);
    }

    private static string FormatVector(Vector vector)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"({vector.X:0.###},{vector.Y:0.###})|len={vector.Length:0.###}");
    }
}

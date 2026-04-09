using Flattiverse.Connector.Events;
using Flattiverse.Connector.GalaxyHierarchy;
using Flattiverse.Connector.Units;
using Flattiverse.Gateway.Connector;
using Microsoft.Extensions.Logging;

namespace Flattiverse.Gateway.Services;

/// <summary>
/// Per-player-session service that manages engine/navigation state for each controllable.
/// Stores point-navigation targets, exposes owner-overlay data, and runs the
/// fly-to-point regulator on each gateway tick.
/// </summary>
public sealed class ManeuveringService : IConnectorEventHandler
{
    private const float NavigationGain = 0.02f;
    private const float NavigationDamping = 2.0f;
    private const float NavigationIntegralGain = 0.01f;
    private const float NavigationIntegralLimit = 25f;
    private const float NavigationDistanceTolerance = 1.5f;
    private const float NavigationVelocityTolerance = 0.01f;
    private const float NavigationTickSeconds = 0.02f;

    private sealed class NavigationState
    {
        public float TargetX { get; set; }
        public float TargetY { get; set; }
        public Vector Integral { get; set; } = new();
        public Vector LastCommanded { get; set; } = new(float.NaN, float.NaN);
    }

    private readonly ILogger _logger;
    private readonly string _sessionId;
    private readonly object _lock = new();
    private readonly Dictionary<string, NavigationState> _navigationTargets = new();
    private int _navigationTickRunning;

    public ManeuveringService(string sessionId, ILogger logger)
    {
        _sessionId = sessionId;
        _logger = logger;
    }

    public void Handle(FlattiverseEvent @event)
    {
        switch (@event)
        {
            case ClosedControllableInfoEvent closed:
                Remove(BuildControllableId(closed.Player.Id, closed.ControllableInfo.Id));
                break;

            case DestroyedControllableInfoEvent destroyed:
                Remove(BuildControllableId(destroyed.Player.Id, destroyed.ControllableInfo.Id));
                break;
        }
    }

    public void SetNavigationTarget(string controllableId, float targetX, float targetY)
    {
        lock (_lock)
        {
            if (!_navigationTargets.TryGetValue(controllableId, out var state))
            {
                state = new NavigationState();
                _navigationTargets[controllableId] = state;
            }

            state.TargetX = targetX;
            state.TargetY = targetY;
            state.Integral = new Vector();
            state.LastCommanded = new Vector(float.NaN, float.NaN);
        }
    }

    public void ClearNavigationTarget(string controllableId)
    {
        Remove(controllableId);
    }

    public (float x, float y)? GetNavigationTarget(string controllableId)
    {
        lock (_lock)
        {
            return _navigationTargets.TryGetValue(controllableId, out var target)
                ? (target.TargetX, target.TargetY)
                : null;
        }
    }

    public Dictionary<string, object> BuildOverlay(string controllableId)
    {
        var navTarget = GetNavigationTarget(controllableId);
        return new Dictionary<string, object>
        {
            { "active", navTarget.HasValue },
            { "targetX", navTarget?.x ?? 0f },
            { "targetY", navTarget?.y ?? 0f }
        };
    }

    public void Tick(Galaxy? galaxy)
    {
        if (galaxy is null)
            return;

        if (Interlocked.CompareExchange(ref _navigationTickRunning, 1, 0) == 0)
            _ = ApplyNavigationTargetsAsync(galaxy);
    }

    public void Remove(string controllableId)
    {
        lock (_lock)
            _navigationTargets.Remove(controllableId);
    }

    private async Task ApplyNavigationTargetsAsync(Galaxy galaxy)
    {
        try
        {
            List<(string ControllableId, float TargetX, float TargetY)> targets;
            lock (_lock)
            {
                targets = _navigationTargets
                    .Select(entry => (entry.Key, entry.Value.TargetX, entry.Value.TargetY))
                    .ToList();
            }

            foreach (var target in targets)
            {
                var controllable = FindControllable(galaxy, target.ControllableId);
                if (controllable is not ClassicShipControllable classic)
                    continue;

                if (!classic.Active)
                    continue;

                if (!classic.Alive)
                {
                    ResetNavigationState(target.ControllableId);
                    continue;
                }

                Vector commandedTarget = CalculateNavigationTarget(classic, target.ControllableId, target.TargetX, target.TargetY, out var nextIntegral);

                bool skipSend;
                lock (_lock)
                {
                    if (!_navigationTargets.TryGetValue(target.ControllableId, out var state))
                        continue;

                    state.Integral = nextIntegral;
                    skipSend = TargetsMatch(commandedTarget, state.LastCommanded);
                }

                if (skipSend)
                    continue;

                if (commandedTarget.Length <= 0.001f)
                    await classic.Engine.Off();
                else
                    await classic.Engine.Set(commandedTarget);

                lock (_lock)
                {
                    if (_navigationTargets.TryGetValue(target.ControllableId, out var state))
                        state.LastCommanded = new Vector(commandedTarget);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error applying navigation targets for session {SessionId}", _sessionId);
        }
        finally
        {
            Interlocked.Exchange(ref _navigationTickRunning, 0);
        }
    }

    private Vector CalculateNavigationTarget(ClassicShipControllable classic, string controllableId, float targetX, float targetY, out Vector nextIntegral)
    {
        Vector targetPosition = new(targetX, targetY);
        Vector positionError = targetPosition - classic.Position;

        lock (_lock)
        {
            nextIntegral = _navigationTargets.TryGetValue(controllableId, out var state)
                ? new Vector(state.Integral)
                : new Vector();
        }

        if (positionError.Length <= NavigationDistanceTolerance &&
            classic.Movement.Length <= NavigationVelocityTolerance)
        {
            nextIntegral = new Vector();
            return new Vector();
        }

        nextIntegral += positionError * NavigationTickSeconds;

        if (nextIntegral.Length > NavigationIntegralLimit)
            nextIntegral.Length = NavigationIntegralLimit;

        Vector target =
            positionError * NavigationGain +
            nextIntegral * NavigationIntegralGain -
            classic.Movement * NavigationDamping;

        if (target.Length > classic.Engine.Maximum)
            target.Length = classic.Engine.Maximum;

        return target;
    }

    private void ResetNavigationState(string controllableId)
    {
        lock (_lock)
        {
            if (_navigationTargets.TryGetValue(controllableId, out var state))
            {
                state.Integral = new Vector();
                state.LastCommanded = new Vector(float.NaN, float.NaN);
            }
        }
    }

    private static Controllable? FindControllable(Galaxy galaxy, string controllableId)
    {
        foreach (var controllable in galaxy.Controllables)
        {
            if (controllable is null)
                continue;

            if ($"p{galaxy.Player.Id}-c{controllable.Id}" == controllableId)
                return controllable;
        }

        return null;
    }

    private static string BuildControllableId(byte playerId, byte controllableId)
    {
        return $"p{playerId}-c{controllableId}";
    }

    private static bool TargetsMatch(Vector left, Vector right)
    {
        if (float.IsNaN(right.X) || float.IsNaN(right.Y))
            return false;

        return MathF.Abs(left.X - right.X) < 0.0005f && MathF.Abs(left.Y - right.Y) < 0.0005f;
    }
}

using Flattiverse.Connector.Events;
using Flattiverse.Connector.GalaxyHierarchy;
using Flattiverse.Gateway.Connector;

namespace Flattiverse.Gateway.Services;

/// <summary>
/// Per-player-session service that stores navigation targets for controllables
/// and exposes the state needed for a future tick-based movement controller.
/// Commands only set or clear targets; the actual fly-to-point regulator will
/// later run from <see cref="Tick(Galaxy?)"/>.
/// </summary>
public sealed class MovementService : IConnectorEventHandler
{
    private sealed class MovementState
    {
        public bool Active { get; set; }
        public float TargetX { get; set; }
        public float TargetY { get; set; }
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, MovementState> _states = new();

    /// <summary>
    /// Process Connector events that affect movement state lifecycle.
    /// Called synchronously on the event-loop task.
    /// </summary>
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

    /// <summary>
    /// Store or update the desired navigation target for a controllable.
    /// </summary>
    public void SetNavigationTarget(string controllableId, float targetX, float targetY)
    {
        lock (_lock)
        {
            if (!_states.TryGetValue(controllableId, out var state))
            {
                state = new MovementState();
                _states[controllableId] = state;
            }

            state.Active = true;
            state.TargetX = targetX;
            state.TargetY = targetY;
        }
    }

    /// <summary>
    /// Clear the stored navigation target for a controllable.
    /// </summary>
    public void ClearNavigationTarget(string controllableId)
    {
        Remove(controllableId);
    }

    /// <summary>
    /// Get the current navigation target, if any.
    /// </summary>
    public (float x, float y)? GetNavigationTarget(string controllableId)
    {
        lock (_lock)
        {
            return _states.TryGetValue(controllableId, out var state) && state.Active
                ? (state.TargetX, state.TargetY)
                : null;
        }
    }

    /// <summary>
    /// Build owner-overlay navigation data for a controllable.
    /// </summary>
    public Dictionary<string, object> BuildOverlay(string controllableId)
    {
        var target = GetNavigationTarget(controllableId);

        return new Dictionary<string, object>
        {
            { "active", target.HasValue },
            { "targetX", target?.x ?? 0f },
            { "targetY", target?.y ?? 0f }
        };
    }

    /// <summary>
    /// Tick hook for the future fly-to-point controller.
    /// This currently only acts as the integration point; actual movement logic
    /// should be implemented here once the regulator is ported.
    /// </summary>
    public void Tick(Galaxy? galaxy)
    {
        if (galaxy is null)
            return;

        // Intentionally left as boilerplate integration point.
        // Future implementation: iterate own classic ships with active targets
        // and translate the target position into Engine.Set(...) calls.
    }

    /// <summary>
    /// Remove state for a controllable.
    /// </summary>
    public void Remove(string controllableId)
    {
        lock (_lock)
            _states.Remove(controllableId);
    }

    private static string BuildControllableId(byte playerId, byte controllableId)
    {
        return $"p{playerId}-c{controllableId}";
    }
}

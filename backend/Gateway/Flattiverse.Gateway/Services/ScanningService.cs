using Flattiverse.Connector.Events;
using Flattiverse.Connector.GalaxyHierarchy;
using Flattiverse.Gateway.Connector;
using Microsoft.Extensions.Logging;

namespace Flattiverse.Gateway.Services;

/// <summary>
/// Per-player-session service that manages scanner state for each controllable.
/// Subscribes to <see cref="DynamicScannerSubsystemEvent"/> to track real-time
/// scanner geometry (the scanner turns/resizes gradually, not instantly).
/// In Forward mode, re-targets the scanner angle to the ship heading each tick.
/// Called synchronously on the event-loop task — no locking needed.
/// </summary>
public sealed class ScanningService : IConnectorEventHandler
{
    public enum ScannerMode
    {
        Off,
        Full,     // 360° scan (max width, shorter length)
        Forward   // narrow forward cone (90° width, max length)
    }

    private sealed class ScannerState
    {
        public ScannerMode Mode { get; set; } = ScannerMode.Off;
        public bool Active { get; set; }
        public float CurrentWidth { get; set; }
        public float CurrentLength { get; set; }
        public float CurrentAngle { get; set; }
        public float TargetWidth { get; set; }
        public float TargetLength { get; set; }
        public float TargetAngle { get; set; }
        /// <summary>Reference to the ship so Forward mode can track heading.</summary>
        public ClassicShipControllable? Ship { get; set; }
    }

    private readonly Dictionary<int, ScannerState> _states = new();

    /// <summary>
    /// Process a Connector event. Updates scanner geometry from
    /// <see cref="DynamicScannerSubsystemEvent"/> and re-targets Forward mode
    /// to keep the scanner aligned with the ship's heading.
    /// </summary>
    public void Handle(FlattiverseEvent @event)
    {
        if (@event is not DynamicScannerSubsystemEvent scanEvt)
            return;

        var id = scanEvt.Controllable.Id;
        if (!_states.TryGetValue(id, out var state))
        {
            state = new ScannerState();
            _states[id] = state;
        }

        state.Active = scanEvt.Active;
        state.CurrentWidth = scanEvt.CurrentWidth;
        state.CurrentLength = scanEvt.CurrentLength;
        state.CurrentAngle = scanEvt.CurrentAngle;
        state.TargetWidth = scanEvt.TargetWidth;
        state.TargetLength = scanEvt.TargetLength;
        state.TargetAngle = scanEvt.TargetAngle;

        // In Forward mode, continuously re-target the scanner to the ship heading.
        // The scanner turns gradually (AngleSpeed degrees/tick), so we issue a
        // Set() every tick to track the ship's current angle.
        if (state is { Mode: ScannerMode.Forward, Active: true, Ship: { } ship })
        {
            var scanner = ship.MainScanner;
            var width = Math.Min(90f, scanner.MaximumWidth);
            _ = scanner.Set(width, scanner.MaximumLength, ship.Angle);
        }

        // In Full mode, keep the scanner rotating by always setting the target
        // angle 180° ahead of the current position. The scanner moves at
        // AngleSpeed degrees/tick, so it will never catch up and keeps spinning.
        if (state is { Mode: ScannerMode.Full, Active: true, Ship: { } fullShip })
        {
            var scanner = fullShip.MainScanner;
            _ = scanner.Set(scanner.MaximumWidth / 2, scanner.MaximumLength, state.CurrentAngle + 20f);
        }
    }

    /// <summary>
    /// Apply a scanner mode to a controllable. Translates the high-level mode
    /// into concrete Set/On/Off calls on the Connector scanner subsystem.
    /// </summary>
    public async Task ApplyModeAsync(ClassicShipControllable ship, ScannerMode mode)
    {
        var id = ship.Id;
        if (!_states.TryGetValue(id, out var state))
        {
            state = new ScannerState();
            _states[id] = state;
        }
        state.Mode = mode;
        state.Ship = ship;

        // If the ship is dead, just remember the mode for when it respawns.
        if (!ship.Alive)
            return;

        var scanner = ship.MainScanner;

        switch (mode)
        {
            case ScannerMode.Off:
                await scanner.Off();
                break;

            case ScannerMode.Full:
                // 360° scan: max width, shorter length, keep current angle
                await scanner.Set(scanner.MaximumWidth / 2, scanner.MaximumLength, scanner.CurrentAngle);
                await scanner.On();
                break;

            case ScannerMode.Forward:
                // Forward cone: 90° width (or max if less), max length, ship heading
                var width = Math.Min(90f, scanner.MaximumWidth);
                await scanner.Set(width, scanner.MaximumLength, ship.Angle);
                await scanner.On();
                break;
        }
    }

    /// <summary>
    /// Re-apply the stored scanner mode after a respawn. No-op if mode is Off or unknown.
    /// </summary>
    public async Task ReapplyModeAsync(ClassicShipControllable ship)
    {
        var id = ship.Id;
        if (!_states.TryGetValue(id, out var state) || state.Mode == ScannerMode.Off)
            return;

        state.Ship = ship;
        await ApplyModeAsync(ship, state.Mode);
    }

    /// <summary>
    /// Get the current scanner mode for a controllable.
    /// </summary>
    public ScannerMode GetMode(int controllableId)
    {
        return _states.TryGetValue(controllableId, out var state) ? state.Mode : ScannerMode.Off;
    }

    /// <summary>
    /// Build scanner overlay data for a controllable.
    /// </summary>
    public Dictionary<string, object> BuildOverlay(int controllableId)
    {
        if (!_states.TryGetValue(controllableId, out var state))
        {
            return new Dictionary<string, object>
            {
                { "active", false },
                { "mode", "off" }
            };
        }

        return new Dictionary<string, object>
        {
            { "active", state.Active },
            { "mode", state.Mode switch
                {
                    ScannerMode.Full => "360",
                    ScannerMode.Forward => "forward",
                    _ => "off"
                }
            },
            { "currentWidth", state.CurrentWidth },
            { "currentLength", state.CurrentLength },
            { "currentAngle", state.CurrentAngle },
            { "targetWidth", state.TargetWidth },
            { "targetLength", state.TargetLength },
            { "targetAngle", state.TargetAngle }
        };
    }

    /// <summary>
    /// Remove state for a controllable (on close/destroy).
    /// </summary>
    public void Remove(int controllableId)
    {
        _states.Remove(controllableId);
    }
}

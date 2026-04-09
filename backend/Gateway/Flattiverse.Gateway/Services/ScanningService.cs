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
/// In Sweep mode, oscillates the scanner ±60° around the ship heading each tick.
/// Called synchronously on the event-loop task — no locking needed.
/// </summary>
public sealed class ScanningService : IConnectorEventHandler
{
    private const float SweepHalfArc = 60f;

    public enum ScannerMode
    {
        Off,
        Full,     // 360° scan (max width, shorter length)
        Forward,  // narrow forward cone, tracks ship heading
        Sweep     // oscillates ±60° around ship heading
    }

    private sealed class ScannerState
    {
        public ScannerMode Mode { get; set; } = ScannerMode.Off;
        public bool Active { get; set; }
        public float DesiredWidth { get; set; } = 90f;
        public float CurrentWidth { get; set; }
        public float CurrentLength { get; set; }
        public float CurrentAngle { get; set; }
        public float TargetWidth { get; set; }
        public float TargetLength { get; set; }
        public float TargetAngle { get; set; }
        /// <summary>true = sweeping toward heading+SweepHalfArc, false = toward heading-SweepHalfArc.</summary>
        public bool SweepForward { get; set; } = true;
        /// <summary>Reference to the ship so Forward/Sweep mode can track heading.</summary>
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
            var width = ResolveWidth(scanner, state);
            _ = scanner.Set(width, scanner.MaximumLength, ship.Angle);
        }

        // In Full mode, keep the scanner rotating by always setting the target
        // angle 180° ahead of the current position. The scanner moves at
        // AngleSpeed degrees/tick, so it will never catch up and keeps spinning.
        if (state is { Mode: ScannerMode.Full, Active: true, Ship: { } fullShip })
        {
            var scanner = fullShip.MainScanner;
            _ = scanner.Set(ResolveWidth(scanner, state), scanner.MaximumLength, state.CurrentAngle + 20f);
        }

        // In Sweep mode, oscillate the scanner ±SweepHalfArc around the ship heading.
        // Each tick we set the target to one of the two arc limits. When the current
        // angle is within one AngleSpeed step of the limit we flip direction so the
        // scanner bounces back and forth continuously.
        if (state is { Mode: ScannerMode.Sweep, Active: true, Ship: { } sweepShip })
        {
            var scanner = sweepShip.MainScanner;
            var limitAngle = sweepShip.Angle + (state.SweepForward ? SweepHalfArc : -SweepHalfArc);

            if (Math.Abs(ShortestAngleDelta(state.CurrentAngle, limitAngle)) <= scanner.AngleSpeed * 1.5f)
                state.SweepForward = !state.SweepForward;

            var nextLimit = sweepShip.Angle + (state.SweepForward ? SweepHalfArc : -SweepHalfArc);
            _ = scanner.Set(ResolveWidth(scanner, state), scanner.MaximumLength, nextLimit);
        }
    }

    /// <summary>
    /// Apply a scanner mode to a controllable. Translates the high-level mode
    /// into concrete Set/On/Off calls on the Connector scanner subsystem.
    /// </summary>
    public async Task ApplyAsync(ClassicShipControllable ship, ScannerMode? mode = null, float? width = null)
    {
        var id = ship.Id;
        if (!_states.TryGetValue(id, out var state))
        {
            state = new ScannerState();
            _states[id] = state;
        }
        state.Ship = ship;

        if (width.HasValue && float.IsFinite(width.Value))
            state.DesiredWidth = ClampWidth(ship.MainScanner, width.Value);

        if (mode.HasValue)
            state.Mode = mode.Value;

        if (state.DesiredWidth <= 0f)
            state.DesiredWidth = ResolveDefaultWidth(ship.MainScanner, state.Mode);

        // If the ship is dead, just remember the mode for when it respawns.
        if (!ship.Alive)
            return;

        var scanner = ship.MainScanner;
        var targetWidth = ResolveWidth(scanner, state);

        switch (state.Mode)
        {
            case ScannerMode.Off:
                await scanner.Off();
                break;

            case ScannerMode.Full:
                await scanner.Set(targetWidth, scanner.MaximumLength, scanner.CurrentAngle);
                await scanner.On();
                break;

            case ScannerMode.Forward:
                await scanner.Set(targetWidth, scanner.MaximumLength, ship.Angle);
                await scanner.On();
                break;

            case ScannerMode.Sweep:
                state.SweepForward = true;
                await scanner.Set(targetWidth, scanner.MaximumLength, ship.Angle + SweepHalfArc);
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
        await ApplyAsync(ship, state.Mode, state.DesiredWidth);
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
                    ScannerMode.Sweep => "sweep",
                    _ => "off"
                }
            },
            { "requestedWidth", state.DesiredWidth },
            { "minimumWidth", state.Ship?.MainScanner.MinimumWidth ?? 5f },
            { "maximumWidth", state.Ship?.MainScanner.MaximumWidth ?? 90f },
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

    /// <summary>
    /// Returns the signed shortest angular distance from <paramref name="from"/> to
    /// <paramref name="to"/>, in the range (-180, +180].
    /// </summary>
    private static float ShortestAngleDelta(float from, float to)
    {
        var delta = (to - from) % 360f;
        if (delta > 180f) delta -= 360f;
        else if (delta <= -180f) delta += 360f;
        return delta;
    }

    private static float ResolveDefaultWidth(DynamicScannerSubsystem scanner, ScannerMode mode)
    {
        return mode switch
        {
            ScannerMode.Full => Math.Max(scanner.MinimumWidth, scanner.MaximumWidth / 2f),
            _ => Math.Min(90f, scanner.MaximumWidth),
        };
    }

    private static float ClampWidth(DynamicScannerSubsystem scanner, float width)
    {
        if (float.IsNaN(width) || float.IsInfinity(width))
            return ResolveDefaultWidth(scanner, ScannerMode.Forward);

        return Math.Clamp(width, scanner.MinimumWidth, scanner.MaximumWidth);
    }

    private static float ResolveWidth(DynamicScannerSubsystem scanner, ScannerState state)
    {
        var width = state.DesiredWidth > 0f
            ? state.DesiredWidth
            : ResolveDefaultWidth(scanner, state.Mode);
        return ClampWidth(scanner, width);
    }
}

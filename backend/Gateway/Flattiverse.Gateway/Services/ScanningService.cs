using Flattiverse.Connector.Events;
using Flattiverse.Connector.GalaxyHierarchy;
using Flattiverse.Gateway.Connector;

namespace Flattiverse.Gateway.Services;

/// <summary>
/// Per-player-session service that manages scanner state for each controllable.
/// Subscribes to <see cref="DynamicScannerSubsystemEvent"/> to track scanner geometry.
/// Forward mode tracks ship heading. Sweep mode oscillates around ship heading.
/// Targeted mode points scanner toward a selected unit with short-horizon prediction.
/// Called synchronously on the connector event loop.
/// </summary>
public sealed class ScanningService : IConnectorEventHandler
{
    private const float SweepHalfArc = 60f;

    public enum ScannerMode
    {
        Off,
        Full,
        Forward,
        Sweep,
        Targeted
    }

    public readonly record struct TargetSnapshot(float X, float Y, float VelocityX, float VelocityY, bool HasVelocity);

    private sealed class TargetTrackState
    {
        public bool HasSample { get; set; }
        public float LastX { get; set; }
        public float LastY { get; set; }
        public long LastSampleTicks { get; set; }
        public float EstimatedVelocityX { get; set; }
        public float EstimatedVelocityY { get; set; }
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
        public bool SweepForward { get; set; } = true;
        public ClassicShipControllable? Ship { get; set; }
        public string? TargetUnitId { get; set; }
        public TargetTrackState TargetTrack { get; } = new();
    }

    private readonly Func<string, TargetSnapshot?> _targetResolver;
    private readonly Dictionary<int, ScannerState> _states = new();

    public ScanningService(Func<string, TargetSnapshot?> targetResolver)
    {
        _targetResolver = targetResolver;
    }

    public void Handle(FlattiverseEvent @event)
    {
        if (@event is not DynamicScannerSubsystemEvent scanEvent)
            return;

        var id = scanEvent.Controllable.Id;
        if (!_states.TryGetValue(id, out var state))
        {
            state = new ScannerState();
            _states[id] = state;
        }

        state.Active = scanEvent.Active;
        state.CurrentWidth = scanEvent.CurrentWidth;
        state.CurrentLength = scanEvent.CurrentLength;
        state.CurrentAngle = scanEvent.CurrentAngle;
        state.TargetWidth = scanEvent.TargetWidth;
        state.TargetLength = scanEvent.TargetLength;
        state.TargetAngle = scanEvent.TargetAngle;

        if (state is { Mode: ScannerMode.Forward, Active: true, Ship: { } ship })
        {
            var scanner = ship.MainScanner;
            var width = ResolveWidth(scanner, state);
            _ = scanner.Set(width, scanner.MaximumLength, ship.Angle);
            return;
        }

        if (state is { Mode: ScannerMode.Full, Active: true, Ship: { } fullShip })
        {
            var scanner = fullShip.MainScanner;
            _ = scanner.Set(ResolveWidth(scanner, state), scanner.MaximumLength, state.CurrentAngle + 20f);
            return;
        }

        if (state is { Mode: ScannerMode.Sweep, Active: true, Ship: { } sweepShip })
        {
            var scanner = sweepShip.MainScanner;
            var limitAngle = sweepShip.Angle + (state.SweepForward ? SweepHalfArc : -SweepHalfArc);
            if (Math.Abs(ShortestAngleDelta(state.CurrentAngle, limitAngle)) <= scanner.AngleSpeed * 1.5f)
                state.SweepForward = !state.SweepForward;

            var nextLimit = sweepShip.Angle + (state.SweepForward ? SweepHalfArc : -SweepHalfArc);
            _ = scanner.Set(ResolveWidth(scanner, state), scanner.MaximumLength, nextLimit);
            return;
        }

        if (state is { Mode: ScannerMode.Targeted, Active: true, Ship: { } targetedShip, TargetUnitId: { Length: > 0 } }
            && TryResolveTargetedAngle(state, targetedShip, out var targetAngle))
        {
            var scanner = targetedShip.MainScanner;
            _ = scanner.Set(ResolveWidth(scanner, state), scanner.MaximumLength, targetAngle);
        }
    }

    public async Task ApplyAsync(ClassicShipControllable ship, ScannerMode? mode = null, float? width = null)
    {
        var state = GetOrCreateState(ship);

        if (width.HasValue && float.IsFinite(width.Value))
            state.DesiredWidth = ClampWidth(ship.MainScanner, width.Value);

        if (mode.HasValue)
        {
            state.Mode = mode.Value;
            if (state.Mode != ScannerMode.Targeted)
            {
                state.TargetUnitId = null;
                ResetTrack(state.TargetTrack);
            }
        }

        if (state.DesiredWidth <= 0f)
            state.DesiredWidth = ResolveDefaultWidth(ship.MainScanner, state.Mode);

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
            case ScannerMode.Targeted:
                // Targeted mode is configured via ApplyTargetModeAsync.
                break;
        }
    }

    public async Task ApplyModeAsync(ClassicShipControllable ship, ScannerMode mode)
    {
        await ApplyAsync(ship, mode, null);
    }

    public async Task ApplyTargetModeAsync(ClassicShipControllable ship, string targetUnitId)
    {
        var state = GetOrCreateState(ship);
        if (!string.Equals(state.TargetUnitId, targetUnitId, StringComparison.Ordinal))
            ResetTrack(state.TargetTrack);

        state.Mode = ScannerMode.Targeted;
        state.TargetUnitId = targetUnitId;
        state.DesiredWidth = ClampWidth(ship.MainScanner, Math.Min(90f, ship.MainScanner.MaximumWidth));

        if (!ship.Alive)
            return;

        var scanner = ship.MainScanner;
        var targetAngle = ship.Angle;
        if (TryResolveTargetedAngle(state, ship, out var resolvedAngle))
            targetAngle = resolvedAngle;

        await scanner.Set(ResolveWidth(scanner, state), scanner.MaximumLength, targetAngle);
        await scanner.On();
    }

    public async Task ReapplyModeAsync(ClassicShipControllable ship)
    {
        if (!_states.TryGetValue(ship.Id, out var state) || state.Mode == ScannerMode.Off)
            return;

        state.Ship = ship;
        if (state.Mode == ScannerMode.Targeted && !string.IsNullOrWhiteSpace(state.TargetUnitId))
        {
            await ApplyTargetModeAsync(ship, state.TargetUnitId);
            return;
        }

        await ApplyAsync(ship, state.Mode, state.DesiredWidth);
    }

    public ScannerMode GetMode(int controllableId)
    {
        return _states.TryGetValue(controllableId, out var state) ? state.Mode : ScannerMode.Off;
    }

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

        var overlay = new Dictionary<string, object>
        {
            { "active", state.Active },
            { "mode", state.Mode switch
                {
                    ScannerMode.Full => "360",
                    ScannerMode.Forward => "forward",
                    ScannerMode.Sweep => "sweep",
                    ScannerMode.Targeted => "targeted",
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

        if (!string.IsNullOrWhiteSpace(state.TargetUnitId))
            overlay["targetUnitId"] = state.TargetUnitId;

        return overlay;
    }

    public void Remove(int controllableId)
    {
        _states.Remove(controllableId);
    }

    private ScannerState GetOrCreateState(ClassicShipControllable ship)
    {
        if (!_states.TryGetValue(ship.Id, out var state))
        {
            state = new ScannerState();
            _states[ship.Id] = state;
        }

        state.Ship = ship;
        return state;
    }

    private bool TryResolveTargetedAngle(ScannerState state, ClassicShipControllable ship, out float targetAngle)
    {
        targetAngle = ship.Angle;
        if (string.IsNullOrWhiteSpace(state.TargetUnitId))
            return false;

        var snapshot = _targetResolver(state.TargetUnitId);
        if (!snapshot.HasValue)
            return false;

        var target = snapshot.Value;
        float velocityX;
        float velocityY;
        if (target.HasVelocity)
        {
            velocityX = target.VelocityX;
            velocityY = target.VelocityY;
            UpdateTrack(state.TargetTrack, target.X, target.Y, velocityX, velocityY);
        }
        else
        {
            var estimatedVelocity = EstimateVelocity(state.TargetTrack, target.X, target.Y);
            velocityX = estimatedVelocity.X;
            velocityY = estimatedVelocity.Y;
        }

        const float predictionSeconds = 0.45f;
        var predictedX = target.X + velocityX * predictionSeconds;
        var predictedY = target.Y + velocityY * predictionSeconds;
        var deltaX = predictedX - ship.Position.X;
        var deltaY = predictedY - ship.Position.Y;
        if (MathF.Abs(deltaX) < 0.0001f && MathF.Abs(deltaY) < 0.0001f)
            return false;

        targetAngle = MathF.Atan2(deltaY, deltaX) * (180f / MathF.PI);
        if (targetAngle < 0f)
            targetAngle += 360f;
        return true;
    }

    private static (float X, float Y) EstimateVelocity(TargetTrackState track, float x, float y)
    {
        var nowTicks = DateTime.UtcNow.Ticks;
        if (!track.HasSample)
        {
            track.HasSample = true;
            track.LastX = x;
            track.LastY = y;
            track.LastSampleTicks = nowTicks;
            return (track.EstimatedVelocityX, track.EstimatedVelocityY);
        }

        var deltaSeconds = (nowTicks - track.LastSampleTicks) / (float)TimeSpan.TicksPerSecond;
        var measuredVelocityX = deltaSeconds > 0.0001f ? (x - track.LastX) / deltaSeconds : 0f;
        var measuredVelocityY = deltaSeconds > 0.0001f ? (y - track.LastY) / deltaSeconds : 0f;
        const float smoothing = 0.35f;
        track.EstimatedVelocityX = (track.EstimatedVelocityX * (1f - smoothing)) + (measuredVelocityX * smoothing);
        track.EstimatedVelocityY = (track.EstimatedVelocityY * (1f - smoothing)) + (measuredVelocityY * smoothing);
        track.LastX = x;
        track.LastY = y;
        track.LastSampleTicks = nowTicks;
        return (track.EstimatedVelocityX, track.EstimatedVelocityY);
    }

    private static void UpdateTrack(TargetTrackState track, float x, float y, float velocityX, float velocityY)
    {
        track.HasSample = true;
        track.LastX = x;
        track.LastY = y;
        track.LastSampleTicks = DateTime.UtcNow.Ticks;
        track.EstimatedVelocityX = velocityX;
        track.EstimatedVelocityY = velocityY;
    }

    private static void ResetTrack(TargetTrackState track)
    {
        track.HasSample = false;
        track.LastX = 0f;
        track.LastY = 0f;
        track.LastSampleTicks = 0;
        track.EstimatedVelocityX = 0f;
        track.EstimatedVelocityY = 0f;
    }

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

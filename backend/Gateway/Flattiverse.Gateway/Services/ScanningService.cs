using Flattiverse.Connector.Events;
using Flattiverse.Connector.GalaxyHierarchy;
using Flattiverse.Gateway.Connector;

namespace Flattiverse.Gateway.Services;

/// <summary>
/// Per-player-session service that manages scanner state for each controllable.
/// Called synchronously on the connector event loop.
/// </summary>
public sealed class ScanningService : IConnectorEventHandler
{
    public enum ScannerMode
    {
        Off,
        Full,
        Forward,
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
        public float CurrentWidth { get; set; }
        public float CurrentLength { get; set; }
        public float CurrentAngle { get; set; }
        public float TargetWidth { get; set; }
        public float TargetLength { get; set; }
        public float TargetAngle { get; set; }
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

        if (state is { Mode: ScannerMode.Forward, Active: true, Ship: { } forwardShip })
        {
            var scanner = forwardShip.MainScanner;
            var width = MathF.Min(90f, scanner.MaximumWidth);
            _ = scanner.Set(width, scanner.MaximumLength, forwardShip.Angle);
            return;
        }

        if (state is { Mode: ScannerMode.Full, Active: true, Ship: { } fullShip })
        {
            var scanner = fullShip.MainScanner;
            _ = scanner.Set(scanner.MaximumWidth / 2f, scanner.MaximumLength, state.CurrentAngle + 20f);
            return;
        }

        if (state is { Mode: ScannerMode.Targeted, Active: true, Ship: { } targetedShip, TargetUnitId: { Length: > 0 } }
            && TryResolveTargetedAngle(state, targetedShip, out var targetAngle))
        {
            var scanner = targetedShip.MainScanner;
            var width = MathF.Min(90f, scanner.MaximumWidth);
            _ = scanner.Set(width, scanner.MaximumLength, targetAngle);
        }
    }

    public async Task ApplyModeAsync(ClassicShipControllable ship, ScannerMode mode)
    {
        var state = GetOrCreateState(ship);
        state.Mode = mode;
        if (mode != ScannerMode.Targeted)
        {
            state.TargetUnitId = null;
            ResetTrack(state.TargetTrack);
        }

        if (!ship.Alive)
            return;

        var scanner = ship.MainScanner;
        switch (mode)
        {
            case ScannerMode.Off:
                await scanner.Off();
                break;
            case ScannerMode.Full:
                await scanner.Set(scanner.MaximumWidth / 2f, scanner.MaximumLength, scanner.CurrentAngle);
                await scanner.On();
                break;
            case ScannerMode.Forward:
                await scanner.Set(MathF.Min(90f, scanner.MaximumWidth), scanner.MaximumLength, ship.Angle);
                await scanner.On();
                break;
            case ScannerMode.Targeted:
                // Targeted mode is configured via ApplyTargetModeAsync.
                break;
        }
    }

    public async Task ApplyTargetModeAsync(ClassicShipControllable ship, string targetUnitId)
    {
        var state = GetOrCreateState(ship);
        if (!string.Equals(state.TargetUnitId, targetUnitId, StringComparison.Ordinal))
            ResetTrack(state.TargetTrack);

        state.Mode = ScannerMode.Targeted;
        state.TargetUnitId = targetUnitId;

        if (!ship.Alive)
            return;

        var scanner = ship.MainScanner;
        var width = MathF.Min(90f, scanner.MaximumWidth);
        var targetAngle = ship.Angle;
        if (TryResolveTargetedAngle(state, ship, out var resolvedAngle))
            targetAngle = resolvedAngle;

        await scanner.Set(width, scanner.MaximumLength, targetAngle);
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

        await ApplyModeAsync(ship, state.Mode);
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

        var mode = state.Mode switch
        {
            ScannerMode.Full => "360",
            ScannerMode.Forward => "forward",
            ScannerMode.Targeted => "targeted",
            _ => "off"
        };

        var overlay = new Dictionary<string, object>
        {
            { "active", state.Active },
            { "mode", mode },
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
}

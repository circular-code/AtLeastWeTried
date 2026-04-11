using Flattiverse.Connector;
using Flattiverse.Connector.Events;
using Flattiverse.Connector.GalaxyHierarchy;
using Flattiverse.Gateway.Connector;
using System.Numerics;

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
    private const float TargetPredictionSeconds = 0.45f;
    private const float TargetUncertaintyCapFactor = 0.75f;
    private const uint MaximumUncertaintyTicks = 72u;

    public enum ScannerMode
    {
        Off,
        Full,
        Forward,
        Hold,
        Sweep,
        Targeted
    }

    public readonly record struct TargetPathPoint(float X, float Y);

    public readonly record struct TargetSnapshot(
        float X,
        float Y,
        float VelocityX,
        float VelocityY,
        bool HasVelocity,
        bool IsSeen,
        uint LastSeenTick,
        uint CurrentTick,
        float? CurrentThrust,
        float? MaximumThrust,
        IReadOnlyList<TargetPathPoint>? PredictedTrajectory);

    internal readonly record struct TargetingSolution(float Angle, float Width);

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
        public float DesiredLength { get; set; } = 0f;
        public float CurrentWidth { get; set; }
        public float CurrentLength { get; set; }
        public float CurrentAngle { get; set; }
        public float TargetWidth { get; set; }
        public float TargetLength { get; set; }
        public float TargetAngle { get; set; }
        public float HoldAngle { get; set; }
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

        if (state.Ship is { } rebuildingShip && ControllableRebuildState.IsRebuilding(rebuildingShip))
            return;

        if (state is { Mode: ScannerMode.Forward, Active: true, Ship: { } ship })
        {
            var scanner = ship.MainScanner;
            var width = ResolveWidth(scanner, state);
            TryDispatchScannerCommand(() => scanner.Set(width, ResolveLength(scanner, state), ship.Angle));
            return;
        }

        if (state is { Mode: ScannerMode.Hold, Active: true, Ship: { } holdShip })
        {
            var scanner = holdShip.MainScanner;
            TryDispatchScannerCommand(() => scanner.Set(ResolveWidth(scanner, state), ResolveLength(scanner, state), state.HoldAngle));
            return;
        }

        if (state is { Mode: ScannerMode.Full, Active: true, Ship: { } fullShip })
        {
            var scanner = fullShip.MainScanner;
            TryDispatchScannerCommand(() => scanner.Set(ResolveWidth(scanner, state), ResolveLength(scanner, state), state.CurrentAngle + 20f));
            return;
        }

        if (state is { Mode: ScannerMode.Sweep, Active: true, Ship: { } sweepShip })
        {
            var scanner = sweepShip.MainScanner;
            var limitAngle = sweepShip.Angle + (state.SweepForward ? SweepHalfArc : -SweepHalfArc);
            if (Math.Abs(ShortestAngleDelta(state.CurrentAngle, limitAngle)) <= scanner.AngleSpeed * 1.5f)
                state.SweepForward = !state.SweepForward;

            var nextLimit = sweepShip.Angle + (state.SweepForward ? SweepHalfArc : -SweepHalfArc);
            TryDispatchScannerCommand(() => scanner.Set(ResolveWidth(scanner, state), ResolveLength(scanner, state), nextLimit));
            return;
        }

        if (state is { Mode: ScannerMode.Targeted, Active: true, Ship: { } targetedShip, TargetUnitId: { Length: > 0 } }
            && TryResolveTargetedSolution(state, targetedShip, out var targetingSolution))
        {
            var scanner = targetedShip.MainScanner;
            TryDispatchScannerCommand(() => scanner.Set(targetingSolution.Width, ResolveLength(scanner, state), targetingSolution.Angle));
        }
    }

    public async Task ApplyAsync(ClassicShipControllable ship, ScannerMode? mode = null, float? width = null, float? length = null)
    {
        var state = GetOrCreateState(ship);

        if (width.HasValue && float.IsFinite(width.Value))
            state.DesiredWidth = ClampWidth(ship.MainScanner, width.Value);

        if (length.HasValue && float.IsFinite(length.Value))
            state.DesiredLength = ClampLength(ship.MainScanner, length.Value);

        if (mode.HasValue)
        {
            if (mode.Value == ScannerMode.Hold)
                state.HoldAngle = ResolveHoldAngle(ship, state);

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
        if (ControllableRebuildState.IsRebuilding(ship))
            return;

        var scanner = ship.MainScanner;
        var targetWidth = ResolveWidth(scanner, state);

        switch (state.Mode)
        {
            case ScannerMode.Off:
                await scanner.Off();
                break;
            case ScannerMode.Full:
                await scanner.Set(targetWidth, ResolveLength(scanner, state), scanner.CurrentAngle);
                await scanner.On();
                break;
            case ScannerMode.Forward:
                await scanner.Set(targetWidth, ResolveLength(scanner, state), ship.Angle);
                await scanner.On();
                break;
            case ScannerMode.Hold:
                await scanner.Set(targetWidth, ResolveLength(scanner, state), state.HoldAngle);
                await scanner.On();
                break;
            case ScannerMode.Sweep:
                state.SweepForward = true;
                await scanner.Set(targetWidth, ResolveLength(scanner, state), ship.Angle + SweepHalfArc);
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

    public async Task ApplyTargetModeAsync(ClassicShipControllable ship, string targetUnitId, float? width = null, float? length = null)
    {
        var state = GetOrCreateState(ship);
        if (!string.Equals(state.TargetUnitId, targetUnitId, StringComparison.Ordinal))
            ResetTrack(state.TargetTrack);

        state.Mode = ScannerMode.Targeted;
        state.TargetUnitId = targetUnitId;

        if (width.HasValue && float.IsFinite(width.Value))
            state.DesiredWidth = ClampWidth(ship.MainScanner, width.Value);
        else if (state.DesiredWidth <= 0f)
            state.DesiredWidth = ClampWidth(ship.MainScanner, Math.Min(90f, ship.MainScanner.MaximumWidth));

        if (length.HasValue && float.IsFinite(length.Value))
            state.DesiredLength = ClampLength(ship.MainScanner, length.Value);

        if (!ship.Alive)
            return;
        if (ControllableRebuildState.IsRebuilding(ship))
            return;

        var scanner = ship.MainScanner;
        var targetWidth = ResolveWidth(scanner, state);
        var targetAngle = ship.Angle;
        if (TryResolveTargetedSolution(state, ship, out var targetingSolution))
        {
            targetWidth = targetingSolution.Width;
            targetAngle = targetingSolution.Angle;
        }

        await scanner.Set(targetWidth, ResolveLength(scanner, state), targetAngle);
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

    private static void TryDispatchScannerCommand(Func<Task> scannerCommand)
    {
        try
        {
            var task = scannerCommand();
            if (task.IsCompleted)
            {
                _ = task.Exception;
                return;
            }

            _ = task.ContinueWith(
                static completedTask => _ = completedTask.Exception,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        }
        catch (GameException)
        {
            // Command prechecks can race with lifecycle transitions and should not break the event loop.
        }
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
                    ScannerMode.Hold => "hold",
                    ScannerMode.Sweep => "sweep",
                    ScannerMode.Targeted => "targeted",
                    _ => "off"
                }
            },
            { "requestedWidth", state.DesiredWidth },
            { "minimumWidth", state.Ship?.MainScanner.MinimumWidth ?? 5f },
            { "maximumWidth", state.Ship?.MainScanner.MaximumWidth ?? 90f },
            { "requestedLength", state.DesiredLength > 0f ? state.DesiredLength : (state.Ship?.MainScanner.MaximumLength ?? 200f) },
            { "minimumLength", state.Ship?.MainScanner.MinimumLength ?? 1f },
            { "maximumLength", state.Ship?.MainScanner.MaximumLength ?? 200f },
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

    private bool TryResolveTargetedSolution(ScannerState state, ClassicShipControllable ship, out TargetingSolution solution)
    {
        solution = new TargetingSolution(ship.Angle, ResolveWidth(ship.MainScanner, state));
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

        return TryResolveTargetingSolution(
            new Vector2(ship.Position.X, ship.Position.Y),
            ResolveWidth(ship.MainScanner, state),
            ship.MainScanner.MinimumWidth,
            ship.MainScanner.MaximumWidth,
            target,
            velocityX,
            velocityY,
            out solution);
    }

    internal static bool TryResolveTargetingSolution(
        Vector2 scannerOrigin,
        float desiredWidth,
        float minimumWidth,
        float maximumWidth,
        in TargetSnapshot target,
        float resolvedVelocityX,
        float resolvedVelocityY,
        out TargetingSolution solution)
    {
        var baseWidth = Math.Clamp(desiredWidth, minimumWidth, maximumWidth);
        if (!target.IsSeen &&
            target.PredictedTrajectory is { Count: > 0 } predictedTrajectory &&
            TryResolveTrajectoryEnvelope(scannerOrigin, baseWidth, minimumWidth, maximumWidth, predictedTrajectory, target, out solution))
            return true;

        var predictedX = target.X + (resolvedVelocityX * TargetPredictionSeconds);
        var predictedY = target.Y + (resolvedVelocityY * TargetPredictionSeconds);
        var delta = new Vector2(predictedX - scannerOrigin.X, predictedY - scannerOrigin.Y);
        if (delta.LengthSquared() < 0.00000001f)
        {
            solution = default;
            return false;
        }

        var predictedAngle = NormalizeAngle(MathF.Atan2(delta.Y, delta.X) * (180f / MathF.PI));
        var uncertaintyAngle = ComputeAngularPaddingDegrees(delta.Length(), ComputeTargetUncertaintyRadius(target));
        var width = Math.Clamp(Math.Max(baseWidth, baseWidth + (uncertaintyAngle * 2f)), minimumWidth, maximumWidth);
        solution = new TargetingSolution(predictedAngle, width);
        return true;
    }

    private static bool TryResolveTrajectoryEnvelope(
        Vector2 scannerOrigin,
        float baseWidth,
        float minimumWidth,
        float maximumWidth,
        IReadOnlyList<TargetPathPoint> predictedTrajectory,
        in TargetSnapshot target,
        out TargetingSolution solution)
    {
        solution = default;
        var uncertaintyRadius = ComputeTargetUncertaintyRadius(target);
        var hasAngle = false;
        var referenceAngle = 0f;
        var minimumDelta = 0f;
        var maximumDelta = 0f;

        for (var index = 0; index < predictedTrajectory.Count; index++)
        {
            var point = predictedTrajectory[index];
            var delta = new Vector2(point.X - scannerOrigin.X, point.Y - scannerOrigin.Y);
            var distance = delta.Length();
            if (distance < 0.0001f)
                continue;

            var angle = NormalizeAngle(MathF.Atan2(delta.Y, delta.X) * (180f / MathF.PI));
            if (!hasAngle)
            {
                hasAngle = true;
                referenceAngle = angle;
                minimumDelta = 0f;
                maximumDelta = 0f;
            }

            var anglePadding = ComputeAngularPaddingDegrees(distance, uncertaintyRadius);
            var relativeAngle = ShortestAngleDelta(referenceAngle, angle);
            minimumDelta = Math.Min(minimumDelta, relativeAngle - anglePadding);
            maximumDelta = Math.Max(maximumDelta, relativeAngle + anglePadding);
        }

        if (!hasAngle)
            return false;

        var width = Math.Clamp(Math.Max(baseWidth, maximumDelta - minimumDelta), minimumWidth, maximumWidth);
        var targetAngle = NormalizeAngle(referenceAngle + ((minimumDelta + maximumDelta) * 0.5f));
        solution = new TargetingSolution(targetAngle, width);
        return true;
    }

    private static float ComputeTargetUncertaintyRadius(in TargetSnapshot target)
    {
        if (target.IsSeen)
            return 0f;

        var currentThrust = Math.Max(0f, target.CurrentThrust.GetValueOrDefault());
        if (currentThrust <= 0f || target.CurrentTick <= target.LastSeenTick)
            return 0f;

        var ticksSinceSeen = Math.Min(target.CurrentTick - target.LastSeenTick, MaximumUncertaintyTicks);
        if (ticksSinceSeen == 0)
            return 0f;

        var ticks = (float)ticksSinceSeen;
        var uncertainty = currentThrust * ticks * (ticks + 1f) * 0.5f;
        var maximumThrust = Math.Max(0f, target.MaximumThrust.GetValueOrDefault());
        if (maximumThrust > 0f)
        {
            var uncertaintyCap = maximumThrust * ticks * TargetUncertaintyCapFactor;
            uncertainty = Math.Min(uncertainty, uncertaintyCap);
        }

        return uncertainty;
    }

    private static float ComputeAngularPaddingDegrees(float distance, float radius)
    {
        if (radius <= 0f)
            return 0f;
        if (distance <= 0.0001f)
            return 180f;

        return MathF.Atan2(radius, distance) * (180f / MathF.PI);
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

    private static float ClampLength(DynamicScannerSubsystem scanner, float length)
    {
        if (float.IsNaN(length) || float.IsInfinity(length))
            return scanner.MaximumLength;

        return Math.Clamp(length, scanner.MinimumLength, scanner.MaximumLength);
    }

    private static float ResolveLength(DynamicScannerSubsystem scanner, ScannerState state)
    {
        return state.DesiredLength > 0f
            ? ClampLength(scanner, state.DesiredLength)
            : scanner.MaximumLength;
    }

    private static float ResolveHoldAngle(ClassicShipControllable ship, ScannerState state)
    {
        var angle = state.Active
            ? state.CurrentAngle
            : ship.MainScanner.CurrentAngle;

        if (!float.IsFinite(angle))
            angle = ship.Angle;

        return NormalizeAngle(angle);
    }

    private static float NormalizeAngle(float angle)
    {
        if (!float.IsFinite(angle))
            return 0f;

        angle %= 360f;
        if (angle < 0f)
            angle += 360f;
        return angle;
    }
}

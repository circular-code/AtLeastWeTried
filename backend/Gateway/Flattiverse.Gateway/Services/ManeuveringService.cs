using Flattiverse.Connector.Events;
using Flattiverse.Connector.GalaxyHierarchy;
using Flattiverse.Connector.Units;
using Flattiverse.Gateway.Connector;
using Flattiverse.Gateway.Protocol.Dtos;
using static Flattiverse.Gateway.Services.GravitySimulator;

namespace Flattiverse.Gateway.Services;

/// <summary>
/// Per-player-session service that drives ship engines toward navigation targets,
/// compensating for gravitational forces. Computes a predicted trajectory overlay
/// for UI visualization.
/// </summary>
public sealed class ManeuveringService : IConnectorEventHandler
{
    private const int TrajectoryTicks = 20;
    private const int TrajectoryDownsample = 4;
    private const double DefaultSpeedLimit = 6.0d;

    private sealed class ShipState
    {
        public ClassicShipControllable? Ship { get; set; }
        public bool HasTarget { get; set; }
        public bool IsDirect { get; set; }
        public float TargetX { get; set; }
        public float TargetY { get; set; }
        public float MaxSpeedFraction { get; set; } = 1f;
        public double LastEngineX { get; set; }
        public double LastEngineY { get; set; }
        public List<TrajectoryPoint>? CachedTrajectory { get; set; }
        public bool EngineCommandInFlight { get; set; }

        /// <summary>Remaining path polyline from the current lookahead onward. Used by the aligner to anticipate turns.</summary>
        public IReadOnlyList<(double X, double Y)>? RemainingPath { get; set; }
    }

    private readonly MappingService _mappingService;
    private readonly Dictionary<int, ShipState> _states = new();

    public ManeuveringService(MappingService mappingService)
    {
        _mappingService = mappingService;
    }

    public void TrackShip(ClassicShipControllable ship)
    {
        if (_states.TryGetValue(ship.Id, out var existing))
        {
            existing.Ship = ship;
            return;
        }

        _states[ship.Id] = new ShipState { Ship = ship };
    }

    public void RebindShip(ClassicShipControllable ship)
    {
        if (_states.TryGetValue(ship.Id, out var existing))
        {
            existing.Ship = ship;
            return;
        }

        _states[ship.Id] = new ShipState { Ship = ship };
    }

    public void SetNavigationTarget(
        ClassicShipControllable ship,
        float targetX,
        float targetY,
        float maxSpeedFraction,
        bool resetController,
        IReadOnlyList<(double X, double Y)>? remainingPath = null,
        bool isDirect = false)
    {
        TrackShip(ship);
        var state = _states[ship.Id];
        state.HasTarget = true;
        state.IsDirect = isDirect;
        state.TargetX = targetX;
        state.TargetY = targetY;
        state.MaxSpeedFraction = maxSpeedFraction;
        state.RemainingPath = remainingPath;

        if (resetController)
        {
            state.LastEngineX = 0d;
            state.LastEngineY = 0d;
        }

        state.CachedTrajectory = null;
    }

    public void ClearNavigationTarget(int controllableId)
    {
        if (!_states.TryGetValue(controllableId, out var state))
            return;

        if (state.HasTarget && state.Ship is { Active: true, Alive: true } ship && !state.EngineCommandInFlight)
        {
            state.EngineCommandInFlight = true;
            _ = TurnOffEngineAsync(ship, state);
        }

        state.HasTarget = false;
        state.IsDirect = false;
        state.LastEngineX = 0d;
        state.LastEngineY = 0d;
        state.CachedTrajectory = null;
    }

    public void SetMaxSpeedFraction(int controllableId, float maxSpeedFraction)
    {
        if (!_states.TryGetValue(controllableId, out var state))
            return;

        if (float.IsFinite(maxSpeedFraction))
            state.MaxSpeedFraction = Math.Clamp(maxSpeedFraction, 0f, 1f);
    }

    public Dictionary<string, object?> BuildOverlay(int controllableId)
    {
        if (!_states.TryGetValue(controllableId, out var state) || state.Ship is null)
        {
            return new Dictionary<string, object?>();
        }

        var result = new Dictionary<string, object?>();
        var ship = state.Ship;

        result["maxSpeedFraction"] = (double)state.MaxSpeedFraction;

        if (state.HasTarget && state.IsDirect)
        {
            result["active"] = true;
            result["targetX"] = state.TargetX;
            result["targetY"] = state.TargetY;
            result["pathStatus"] = "direct";
            result["path"] = new[]
            {
                new Dictionary<string, object?> { { "x", (double)ship.Position.X }, { "y", (double)ship.Position.Y } },
                new Dictionary<string, object?> { { "x", (double)state.TargetX }, { "y", (double)state.TargetY } },
            };
        }

        if (state.HasTarget)
        {
            // Show current acceleration (engine) direction
            var ex = state.LastEngineX;
            var ey = state.LastEngineY;
            var eMag = Math.Sqrt(ex * ex + ey * ey);

            if (eMag > 1e-9d)
            {
                var nx = ex / eMag;
                var ny = ey / eMag;
                result["vectorX"] = nx;
                result["vectorY"] = ny;

                // Pointer length proportional to thrust fraction (max 80 units)
                var thrustFraction = eMag / Math.Max(1e-9d, (double)ship.Engine.Maximum);
                var pointerLen = thrustFraction * 80d;
                result["pointerX"] = ship.Position.X + nx * pointerLen;
                result["pointerY"] = ship.Position.Y + ny * pointerLen;
            }
        }

        if (state.CachedTrajectory is { Count: > 0 } trajectory)
        {
            var downsampled = new List<Dictionary<string, object?>>();
            for (var i = 0; i < trajectory.Count; i += TrajectoryDownsample)
            {
                downsampled.Add(new Dictionary<string, object?>
                {
                    { "x", trajectory[i].X },
                    { "y", trajectory[i].Y },
                });
            }

            // Always include last point
            if ((trajectory.Count - 1) % TrajectoryDownsample != 0)
            {
                downsampled.Add(new Dictionary<string, object?>
                {
                    { "x", trajectory[^1].X },
                    { "y", trajectory[^1].Y },
                });
            }

            result["trajectory"] = downsampled.ToArray();
        }

        return result;
    }

    public void Handle(FlattiverseEvent @event)
    {
        if (@event is not GalaxyTickEvent)
            return;

        var gravitySources = ExtractGravitySources();

        foreach (var state in _states.Values)
        {
            UpdateShip(state, gravitySources);
        }
    }

    private void UpdateShip(ShipState state, List<GravitySource> gravitySources)
    {
        var ship = state.Ship;
        if (ship is null || !ship.Active || !ship.Alive || !state.HasTarget)
        {
            state.CachedTrajectory = null;
            return;
        }

        var shipX = (double)ship.Position.X;
        var shipY = (double)ship.Position.Y;
        var velX = (double)ship.Movement.X;
        var velY = (double)ship.Movement.Y;
        var engineMax = (double)ship.Engine.Maximum;
        var speedLimit = DefaultSpeedLimit * Math.Clamp(state.MaxSpeedFraction, 0d, 1d);

        // Compute the optimal engine vector (always use full engine thrust; speed is limited by speedLimit)
        var (engineX, engineY) = TrajectoryAligner.ComputeEngineVector(
            shipX, shipY,
            velX, velY,
            state.TargetX, state.TargetY,
            gravitySources,
            engineMax,
            1.0d,
            speedLimit,
            state.RemainingPath);

        state.LastEngineX = engineX;
        state.LastEngineY = engineY;

        // Command the engine (fire-and-forget)
        if (!state.EngineCommandInFlight)
        {
            state.EngineCommandInFlight = true;
            _ = SetEngineAsync(ship, state, (float)engineX, (float)engineY);
        }

        // Simulate 200-tick trajectory for overlay visualization
        state.CachedTrajectory = GravitySimulator.SimulateTrajectory(
            shipX, shipY,
            velX, velY,
            engineX, engineY,
            gravitySources,
            TrajectoryTicks,
            speedLimit);
    }

    private List<GravitySource> ExtractGravitySources()
    {
        var units = _mappingService.BuildUnitSnapshots();
        var sources = new List<GravitySource>();

        foreach (var unit in units)
        {
            if (unit.Gravity > 0f)
            {
                sources.Add(new GravitySource(unit.X, unit.Y, unit.Gravity));
            }
        }

        return sources;
    }

    private static async Task SetEngineAsync(ClassicShipControllable ship, ShipState state, float engineX, float engineY)
    {
        try
        {
            var vec = new Vector(engineX, engineY);
            if (vec.Length > ship.Engine.Maximum)
                vec.Length = ship.Engine.Maximum;
            await ship.Engine.Set(vec).ConfigureAwait(false);
        }
        catch
        {
            // Swallow: engine commands can fail when the ship dies or the session disconnects.
        }
        finally
        {
            state.EngineCommandInFlight = false;
        }
    }

    private static async Task TurnOffEngineAsync(ClassicShipControllable ship, ShipState state)
    {
        try
        {
            await ship.Engine.Off().ConfigureAwait(false);
        }
        catch
        {
            // Swallow: engine commands can fail when the ship dies or the session disconnects.
        }
        finally
        {
            state.EngineCommandInFlight = false;
        }
    }
}

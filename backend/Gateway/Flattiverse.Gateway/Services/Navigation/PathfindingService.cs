using Flattiverse.Connector.Events;
using Flattiverse.Connector.GalaxyHierarchy;
using Flattiverse.Gateway.Connector;
using Flattiverse.Gateway.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static Flattiverse.Gateway.Services.GravitySimulator;

namespace Flattiverse.Gateway.Services.Navigation;

public sealed class PathfindingService : IConnectorEventHandler
{
    private const double ClearanceMargin = 8d;
    private const double MaxPathDeviation = 40d;
    private const double MinimumLookahead = 36d;
    private const double MaximumLookahead = 132d;
    private const double MinimumTargetDistance = 18d;
    private const double MaximumTargetDistance = 56d;
    private const double MinimumArrivalDistance = 10d;
    private const double MaximumArrivalDistance = 34d;

    /// <summary>Number of overlay cycles the pending target indicator stays visible after the goal is resolved.</summary>
    private const int PendingTargetFadeoutCycles = 8;

    private sealed class PathState
    {
        public ClassicShipControllable? Ship { get; set; }

        public bool HasGoal { get; set; }

        public NavigationPoint Goal { get; set; }

        public float ThrustPercentage { get; set; } = 1f;

        public int PlannedClusterId { get; set; } = int.MinValue;

        public NavigationPoint PlannedStart { get; set; }

        public CircularPathPlanner.PlanResult? Plan { get; set; }

        public string? Status { get; set; }

        /// <summary>Obstacle unit ids from the last successful plan; used to detect newly added units.</summary>
        public HashSet<string> ObstacleIdsFromSuccessfulPlan { get; } = new(StringComparer.Ordinal);

        /// <summary>Obstacle unit ids passed to the last <see cref="Replan"/> attempt (success or failure).</summary>
        public HashSet<string> ObstacleIdsAtLastPlanAttempt { get; } = new(StringComparer.Ordinal);

        /// <summary>When set, the next tick will attempt to plan toward this goal. If the plan succeeds, it replaces
        /// the active goal. If it fails, the active goal and plan are kept.</summary>
        public bool HasPendingGoal { get; set; }
        public NavigationPoint PendingGoal { get; set; }
        public float PendingThrustPercentage { get; set; } = 1f;

        /// <summary>Visible pending target for the overlay. Set when a pending goal is queued, cleared after
        /// the remaining overlay cycles have been emitted.</summary>
        public bool ShowPendingTarget { get; set; }
        public NavigationPoint VisiblePendingTarget { get; set; }

        /// <summary>Number of overlay cycles remaining to show the pending target after the goal is resolved.</summary>
        public int PendingTargetCyclesRemaining { get; set; }

        /// <summary>Whether the ship recently respawned and needs a reachability check.</summary>
        public bool NeedsRespawnCheck { get; set; }
    }

    private readonly MappingService _mappingService;
    private readonly ManeuveringService _maneuveringService;
    private readonly CircularPathPlanner _planner = new();
    private readonly PathFollower _pathFollower = new();
    private readonly Dictionary<int, PathState> _states = new();
    private readonly ILogger<PathfindingService> _logger;
    private readonly bool _enableLogging;

    public PathfindingService(
        MappingService mappingService,
        ManeuveringService maneuveringService,
        ILogger<PathfindingService> logger,
        IOptions<PathfindingOptions> options)
    {
        _mappingService = mappingService;
        _maneuveringService = maneuveringService;
        _logger = logger;
        _enableLogging = options.Value.EnableLogging;
    }

    public void Handle(FlattiverseEvent @event)
    {
        if (@event is not GalaxyTickEvent)
        {
            return;
        }

        foreach (var state in _states.Values)
        {
            UpdateState(state);
        }
    }

    public void TrackShip(ClassicShipControllable ship)
    {
        if (_states.TryGetValue(ship.Id, out var existing))
        {
            existing.Ship = ship;
            return;
        }

        _states[ship.Id] = new PathState
        {
            Ship = ship,
            Goal = new NavigationPoint(ship.Position.X, ship.Position.Y),
            PlannedStart = new NavigationPoint(ship.Position.X, ship.Position.Y),
            Status = "idle",
        };
    }

    public void SetNavigationGoal(ClassicShipControllable ship, float targetX, float targetY, float thrustPercentage)
    {
        TrackShip(ship);

        var state = _states[ship.Id];
        state.Ship = ship;

        // If there is already an active goal with a working plan, queue the new goal as pending.
        // The next tick will attempt to plan toward it; on success it replaces the active goal,
        // on failure the current navigation keeps running.
        if (state.HasGoal && state.Plan is { Succeeded: true })
        {
            state.HasPendingGoal = true;
            state.PendingGoal = new NavigationPoint(targetX, targetY);
            state.PendingThrustPercentage = thrustPercentage;
            state.ShowPendingTarget = true;
            state.VisiblePendingTarget = new NavigationPoint(targetX, targetY);
            state.PendingTargetCyclesRemaining = -1; // -1 = keep showing until goal is resolved

            if (_enableLogging)
            {
                _logger.LogInformation(
                    "[Pathfinding] SetNavigationGoal (pending) controllable={ControllableId} pendingGoal=({GoalX:F1},{GoalY:F1}) thrust={Thrust:F2}",
                    ship.Id,
                    targetX,
                    targetY,
                    thrustPercentage);
            }

            return;
        }

        // No active plan – apply immediately.
        state.HasGoal = true;
        state.HasPendingGoal = false;
        state.Goal = new NavigationPoint(targetX, targetY);
        state.ThrustPercentage = thrustPercentage;
        state.Plan = null;
        state.PlannedClusterId = int.MinValue;
        state.Status = "planning";
        state.ObstacleIdsFromSuccessfulPlan.Clear();
        state.ObstacleIdsAtLastPlanAttempt.Clear();

        if (_enableLogging)
        {
            _logger.LogInformation(
                "[Pathfinding] SetNavigationGoal controllable={ControllableId} goal=({GoalX:F1},{GoalY:F1}) thrust={Thrust:F2}",
                ship.Id,
                targetX,
                targetY,
                thrustPercentage);
        }
    }

    public void ClearNavigationGoal(int controllableId)
    {
        if (!_states.TryGetValue(controllableId, out var state))
        {
            return;
        }

        if (_enableLogging)
        {
            _logger.LogInformation("[Pathfinding] ClearNavigationGoal controllable={ControllableId}", controllableId);
        }

        state.HasGoal = false;
        state.HasPendingGoal = false;
        state.ShowPendingTarget = false;
        state.Plan = null;
        state.Status = "idle";
        state.ObstacleIdsFromSuccessfulPlan.Clear();
        state.ObstacleIdsAtLastPlanAttempt.Clear();
        _maneuveringService.ClearNavigationTarget(controllableId);
    }

    /// <summary>
    /// Called on ship respawn. Updates the ship reference and marks the state so the next tick
    /// can verify reachability of the old goal. If unreachable, the goal is replaced by the
    /// ship's current (respawn) position.
    /// </summary>
    public void RebindShip(ClassicShipControllable ship)
    {
        if (_states.TryGetValue(ship.Id, out var existing))
        {
            existing.Ship = ship;
            existing.NeedsRespawnCheck = true;
            // Invalidate old plan so it gets re-evaluated from the new position
            existing.Plan = null;
            existing.PlannedClusterId = int.MinValue;
            existing.ObstacleIdsFromSuccessfulPlan.Clear();
            existing.ObstacleIdsAtLastPlanAttempt.Clear();
            return;
        }

        _states[ship.Id] = new PathState
        {
            Ship = ship,
            Goal = new NavigationPoint(ship.Position.X, ship.Position.Y),
            PlannedStart = new NavigationPoint(ship.Position.X, ship.Position.Y),
            Status = "idle",
        };
    }

    public Dictionary<string, object?> BuildOverlay(int controllableId)
    {
        if (!_states.TryGetValue(controllableId, out var state) || !state.HasGoal)
        {
            return new Dictionary<string, object?>
            {
                { "active", false },
            };
        }

        var plan = state.Plan;
        var overlay = new Dictionary<string, object?>
        {
            { "active", true },
            { "targetX", state.Goal.X },
            { "targetY", state.Goal.Y },
            { "pathStatus", state.Status ?? "planned" },
        };

        if (state.ShowPendingTarget)
        {
            overlay["pendingTargetX"] = state.VisiblePendingTarget.X;
            overlay["pendingTargetY"] = state.VisiblePendingTarget.Y;

            if (_enableLogging)
            {
                _logger.LogInformation(
                    "[Pathfinding] BuildOverlay emitting pendingTarget controllable={ControllableId} pending=({X:F1},{Y:F1}) cyclesRemaining={Cycles}",
                    controllableId,
                    state.VisiblePendingTarget.X,
                    state.VisiblePendingTarget.Y,
                    state.PendingTargetCyclesRemaining);
            }

            // While the pending goal is still unresolved, keep showing indefinitely (-1).
            // Once resolved, UpdateState starts the countdown. Decrement each overlay cycle.
            if (state.PendingTargetCyclesRemaining >= 0)
            {
                state.PendingTargetCyclesRemaining--;
                if (state.PendingTargetCyclesRemaining < 0)
                {
                    state.ShowPendingTarget = false;
                }
            }
        }

        if (plan is not null)
        {
            overlay["path"] = (plan.PathPoints ?? Array.Empty<NavigationPoint>())
                .Select(point => new Dictionary<string, object?>
                {
                    { "x", point.X },
                    { "y", point.Y },
                })
                .ToArray();
            overlay["searchNodes"] = (plan.SearchNodes ?? Array.Empty<NavigationPreviewPoint>())
                .Select(point => new Dictionary<string, object?>
                {
                    { "x", point.X },
                    { "y", point.Y },
                })
                .ToArray();
            overlay["searchEdges"] = (plan.SearchEdges ?? Array.Empty<NavigationPreviewSegment>())
                .Select(segment => new Dictionary<string, object?>
                {
                    { "startX", segment.StartX },
                    { "startY", segment.StartY },
                    { "endX", segment.EndX },
                    { "endY", segment.EndY },
                })
                .ToArray();
            overlay["inflatedObstacles"] = (plan.Obstacles ?? Array.Empty<NavigationObstacle>())
                .Select(obstacle => new Dictionary<string, object?>
                {
                    { "kind", obstacle.Kind },
                    { "x", obstacle.Center.X },
                    { "y", obstacle.Center.Y },
                    { "radius", obstacle.Radius },
                })
                .ToArray();
        }

        return overlay;
    }

    private void UpdateState(PathState state)
    {
        var ship = state.Ship;
        if (!state.HasGoal || ship is null || !ship.Active || !ship.Alive)
        {
            return;
        }

        var currentPosition = new NavigationPoint(ship.Position.X, ship.Position.Y);

        // --- Respawn reachability check ---
        if (state.NeedsRespawnCheck)
        {
            state.NeedsRespawnCheck = false;

            var clusterId = ship.Cluster?.Id ?? 0;
            var obstacles = CircularObstacleExtractor.Extract(
                _mappingService.BuildUnitSnapshots(),
                clusterId,
                ship.Size,
                ClearanceMargin,
                destinationUnitId: null,
                shipPosition: currentPosition);

            var respawnPlan = _planner.Plan(currentPosition, state.Goal, obstacles);
            if (!respawnPlan.Succeeded)
            {
                // Old goal not reachable from respawn position – hold current position
                if (_enableLogging)
                {
                    _logger.LogInformation(
                        "[Pathfinding] Respawn goal unreachable controllable={ControllableId} – falling back to current position",
                        ship.Id);
                }

                state.Goal = currentPosition;
                state.Plan = null;
                state.Status = "station-keeping";
                state.HasPendingGoal = false;
                _maneuveringService.SetNavigationTarget(
                    ship,
                    (float)currentPosition.X,
                    (float)currentPosition.Y,
                    state.ThrustPercentage,
                    resetController: true,
                    remainingPath: null);
                return;
            }

            // Goal is still reachable – adopt the new plan and continue below
            state.Plan = respawnPlan;
            state.PlannedClusterId = clusterId;
            state.PlannedStart = currentPosition;
            state.Status = "planned";
            state.ObstacleIdsFromSuccessfulPlan.Clear();
            state.ObstacleIdsAtLastPlanAttempt.Clear();
            foreach (var obstacle in obstacles)
            {
                state.ObstacleIdsFromSuccessfulPlan.Add(obstacle.Id);
                state.ObstacleIdsAtLastPlanAttempt.Add(obstacle.Id);
            }
        }

        // --- Process pending goal (new target requested while old navigation was running) ---
        if (state.HasPendingGoal)
        {
            state.HasPendingGoal = false;

            var clusterId = ship.Cluster?.Id ?? 0;
            var obstacles = CircularObstacleExtractor.Extract(
                _mappingService.BuildUnitSnapshots(),
                clusterId,
                ship.Size,
                ClearanceMargin,
                destinationUnitId: null,
                shipPosition: currentPosition);

            var pendingPlan = _planner.Plan(currentPosition, state.PendingGoal, obstacles);

            // Start the fadeout countdown so the client sees the pending indicator for a few frames
            state.PendingTargetCyclesRemaining = PendingTargetFadeoutCycles;

            if (pendingPlan.Succeeded)
            {
                // New goal is reachable – adopt it (pending marker will fade out over the countdown)
                state.Goal = state.PendingGoal;
                state.ThrustPercentage = state.PendingThrustPercentage;
                state.Plan = pendingPlan;
                state.PlannedClusterId = clusterId;
                state.PlannedStart = currentPosition;
                state.Status = "planned";
                state.ObstacleIdsFromSuccessfulPlan.Clear();
                state.ObstacleIdsAtLastPlanAttempt.Clear();
                foreach (var obstacle in obstacles)
                {
                    state.ObstacleIdsFromSuccessfulPlan.Add(obstacle.Id);
                    state.ObstacleIdsAtLastPlanAttempt.Add(obstacle.Id);
                }

                if (_enableLogging)
                {
                    _logger.LogInformation(
                        "[Pathfinding] Pending goal adopted controllable={ControllableId} goal=({GoalX:F1},{GoalY:F1})",
                        ship.Id,
                        state.Goal.X,
                        state.Goal.Y);
                }
            }
            else
            {
                // New goal is not reachable – keep old navigation (pending marker will fade out over the countdown)

                if (_enableLogging)
                {
                    _logger.LogInformation(
                        "[Pathfinding] Pending goal rejected (route blocked) controllable={ControllableId} pendingGoal=({GoalX:F1},{GoalY:F1}) – keeping current route",
                        ship.Id,
                        state.PendingGoal.X,
                        state.PendingGoal.Y);
                }
            }
        }

        var arrivalThreshold = ComputeArrivalThreshold(ship);
        if (currentPosition.DistanceTo(state.Goal) <= arrivalThreshold)
        {
            // Station-keeping: keep targeting the goal so the aligner holds position against gravity.
            state.Status = "station-keeping";
            _maneuveringService.SetNavigationTarget(
                ship,
                (float)state.Goal.X,
                (float)state.Goal.Y,
                state.ThrustPercentage,
                resetController: false,
                remainingPath: null);
            return;
        }

        var currentClusterId = ship.Cluster?.Id ?? 0;
        var currentObstacles = CircularObstacleExtractor.Extract(
            _mappingService.BuildUnitSnapshots(),
            currentClusterId,
            ship.Size,
            ClearanceMargin,
            destinationUnitId: null,
            shipPosition: currentPosition);

        var replanDecision = GetReplanDecision(state, currentObstacles, currentClusterId, currentPosition);
        if (replanDecision.Replan)
        {
            if (_enableLogging)
            {
                _logger.LogInformation(
                    "[Pathfinding] Replan triggered controllable={ControllableId} reason={Reason} obstacleCount={ObstacleCount}",
                    ship.Id,
                    replanDecision.Reason,
                    currentObstacles.Count);
            }

            Replan(state, ship, currentPosition, currentClusterId, currentObstacles);
        }

        if (state.Plan is null || !state.Plan.Succeeded || state.Plan.PathPoints.Count == 0)
        {
            // Plan failed – do NOT clear the maneuvering target; let the ship coast on its
            // current trajectory rather than killing the engines and drifting.
            return;
        }

        var followResult = _pathFollower.Follow(
            currentPosition,
            state.Plan.PathPoints,
            ComputeLookaheadDistance(ship),
            ComputeMinimumTargetDistance(ship),
            arrivalThreshold);

        if (followResult.GoalReached)
        {
            // Station-keeping: keep targeting the goal so the aligner holds position against gravity.
            state.Status = "station-keeping";
            _maneuveringService.SetNavigationTarget(
                ship,
                (float)state.Goal.X,
                (float)state.Goal.Y,
                state.ThrustPercentage,
                resetController: false,
                remainingPath: null);
            return;
        }

        state.Status = "following";

        // Build the remaining path from the lookahead target onward so the aligner can anticipate turns
        var remainingPath = BuildRemainingPath(followResult.Target, followResult.ProgressDistance, state.Plan.PathPoints);

        _maneuveringService.SetNavigationTarget(
            ship,
            (float)followResult.Target.X,
            (float)followResult.Target.Y,
            state.ThrustPercentage,
            resetController: false,
            remainingPath: remainingPath);

        if (_enableLogging)
        {
            LogTrajectory(ship, currentPosition, followResult.Target, state.ThrustPercentage, remainingPath);
        }
    }

    private void Replan(
        PathState state,
        ClassicShipControllable ship,
        NavigationPoint currentPosition,
        int clusterId,
        List<NavigationObstacle> obstacles)
    {
        state.PlannedClusterId = clusterId;
        state.PlannedStart = currentPosition;
        state.Plan = _planner.Plan(currentPosition, state.Goal, obstacles);
        state.Status = state.Plan.Succeeded ? "planned" : "blocked";

        state.ObstacleIdsAtLastPlanAttempt.Clear();
        foreach (var obstacle in obstacles)
        {
            state.ObstacleIdsAtLastPlanAttempt.Add(obstacle.Id);
        }

        if (state.Plan.Succeeded)
        {
            state.ObstacleIdsFromSuccessfulPlan.Clear();
            foreach (var obstacle in obstacles)
            {
                state.ObstacleIdsFromSuccessfulPlan.Add(obstacle.Id);
            }
        }
        else
        {
            state.ObstacleIdsFromSuccessfulPlan.Clear();
        }

        if (_enableLogging)
        {
            if (state.Plan.Succeeded)
            {
                _logger.LogInformation(
                    "[Pathfinding] Plan result controllable={ControllableId} success=true pathPoints={PathPoints} searchNodes={SearchNodes} polylineLength={PolylineLength:F1}",
                    ship.Id,
                    state.Plan.PathPoints.Count,
                    state.Plan.SearchNodes.Count,
                    state.Plan.PathPoints.Count >= 2
                        ? NavigationPointDistance(state.Plan.PathPoints)
                        : 0d);
            }
            else
            {
                _logger.LogWarning(
                    "[Pathfinding] Plan result controllable={ControllableId} success=false failureReason={Reason} searchNodes={SearchNodes}",
                    ship.Id,
                    state.Plan.FailureReason ?? "(null)",
                    state.Plan.SearchNodes.Count);
            }
        }
    }

    private static double NavigationPointDistance(IReadOnlyList<NavigationPoint> path)
    {
        var sum = 0d;
        for (var i = 0; i < path.Count - 1; i++)
        {
            sum += path[i].DistanceTo(path[i + 1]);
        }

        return sum;
    }

    private readonly record struct ReplanDecision(bool Replan, string Reason);

    private static ReplanDecision GetReplanDecision(PathState state, List<NavigationObstacle> obstacles, int clusterId, NavigationPoint shipPosition)
    {
        if (state.Plan is null)
        {
            return new ReplanDecision(true, "no_active_plan");
        }

        if (state.PlannedClusterId != clusterId)
        {
            return new ReplanDecision(true, $"cluster_changed (planned={state.PlannedClusterId}, current={clusterId})");
        }

        if (state.Plan.Succeeded && state.Plan.PathPoints.Count >= 2)
        {
            var deviation = DistanceToPolyline(shipPosition, state.Plan.PathPoints);
            if (deviation > MaxPathDeviation)
            {
                return new ReplanDecision(true, $"off_path deviation={deviation:F1}");
            }

            foreach (var obstacle in obstacles)
            {
                if (state.ObstacleIdsFromSuccessfulPlan.Contains(obstacle.Id))
                {
                    continue;
                }

                if (NavigationMath.PolylineIntersectsObstacleDisk(state.Plan.PathPoints, obstacle))
                {
                    return new ReplanDecision(
                        true,
                        $"new_obstacle_obscures_path id={obstacle.Id} kind={obstacle.Kind}");
                }
            }

            return new ReplanDecision(false, "");
        }

        if (!state.Plan.Succeeded)
        {
            foreach (var obstacle in obstacles)
            {
                if (!state.ObstacleIdsAtLastPlanAttempt.Contains(obstacle.Id))
                {
                    return new ReplanDecision(true, $"new_obstacle_after_failed_plan id={obstacle.Id} kind={obstacle.Kind}");
                }
            }

            return new ReplanDecision(false, "failed_plan_unchanged_obstacles");
        }

        return new ReplanDecision(false, "");
    }

    private static double ComputeLookaheadDistance(ClassicShipControllable ship)
    {
        return Math.Clamp(ship.Movement.Length * 1.9d + ship.Size * 2.0d, MinimumLookahead * 0.4d, MaximumLookahead);
    }

    private static double ComputeMinimumTargetDistance(ClassicShipControllable ship)
    {
        return Math.Clamp(ship.Size * 3.2d, MinimumTargetDistance, MaximumTargetDistance);
    }

    private static double ComputeArrivalThreshold(ClassicShipControllable ship)
    {
        return Math.Clamp(ship.Size * 1.8d, MinimumArrivalDistance, MaximumArrivalDistance);
    }

    private static double DistanceToPolyline(NavigationPoint point, IReadOnlyList<NavigationPoint> polyline)
    {
        var minDist = double.PositiveInfinity;
        for (var i = 0; i < polyline.Count - 1; i++)
        {
            var dist = NavigationMath.DistanceToSegment(point, polyline[i], polyline[i + 1], out _);
            minDist = Math.Min(minDist, dist);
        }
        return minDist;
    }

    /// <summary>
    /// Extract the portion of the planned path polyline that lies ahead of the given
    /// <paramref name="progressDistance"/>, starting from the lookahead target point.
    /// Returns a list of (X, Y) tuples for the aligner to scan for upcoming turns.
    /// </summary>
    private static IReadOnlyList<(double X, double Y)> BuildRemainingPath(
        NavigationPoint lookahead,
        double progressDistance,
        IReadOnlyList<NavigationPoint> fullPath)
    {
        if (fullPath.Count < 2)
            return Array.Empty<(double, double)>();

        // Find which segment the progress falls on
        var cumulative = 0d;
        var startIndex = 0;
        for (var i = 0; i < fullPath.Count - 1; i++)
        {
            var segLen = fullPath[i].DistanceTo(fullPath[i + 1]);
            if (cumulative + segLen >= progressDistance)
            {
                startIndex = i + 1;
                break;
            }
            cumulative += segLen;
            startIndex = i + 1;
        }

        // Build remaining path: lookahead target → remaining vertices
        var result = new List<(double, double)>();
        for (var i = startIndex; i < fullPath.Count; i++)
        {
            result.Add((fullPath[i].X, fullPath[i].Y));
        }

        return result;
    }

    private void LogTrajectory(
        ClassicShipControllable ship,
        NavigationPoint currentPosition,
        NavigationPoint target,
        float thrustPercentage,
        IReadOnlyList<(double X, double Y)> remainingPath)
    {
        const int trajectoryTicks = 200;
        const double defaultSpeedLimit = 6.0d;

        var shipX = (double)ship.Position.X;
        var shipY = (double)ship.Position.Y;
        var velX = (double)ship.Movement.X;
        var velY = (double)ship.Movement.Y;
        var engineMax = (double)ship.Engine.Maximum;
        var speed = Math.Sqrt(velX * velX + velY * velY);

        // Extract gravity sources from mapping
        var units = _mappingService.BuildUnitSnapshots();
        var gravitySources = new List<GravitySource>();
        foreach (var unit in units)
        {
            if (unit.Gravity > 0f)
                gravitySources.Add(new GravitySource(unit.X, unit.Y, unit.Gravity));
        }

        // Compute the engine vector (same as ManeuveringService will)
        var (engineX, engineY) = TrajectoryAligner.ComputeEngineVector(
            shipX, shipY, velX, velY,
            target.X, target.Y,
            gravitySources, engineMax,
            thrustPercentage, defaultSpeedLimit,
            remainingPath);

        var engineMag = Math.Sqrt(engineX * engineX + engineY * engineY);

        // Compute gravity at current position
        var (gx, gy) = ComputeGravityAcceleration(shipX, shipY, gravitySources);
        var gravMag = Math.Sqrt(gx * gx + gy * gy);

        // Compute distance to path polyline
        var pathDeviation = 0d;
        if (remainingPath.Count >= 2)
        {
            pathDeviation = DistanceToTuplePath(currentPosition, remainingPath);
        }

        _logger.LogDebug(
            "[Trajectory] ship={ShipId} pos=({PosX:F1},{PosY:F1}) vel=({VelX:F2},{VelY:F2}) spd={Speed:F2} " +
            "target=({TargetX:F1},{TargetY:F1}) distToTarget={DistToTarget:F1} " +
            "engine=({EngineX:F3},{EngineY:F3}) engineMag={EngineMag:F3}/{EngineMax:F3} " +
            "gravity=({GravX:F4},{GravY:F4}) gravMag={GravMag:F4} " +
            "pathDev={PathDev:F1} remainPts={RemainPts}",
            ship.Id,
            shipX, shipY,
            velX, velY, speed,
            target.X, target.Y,
            currentPosition.DistanceTo(target),
            engineX, engineY, engineMag, engineMax,
            gx, gy, gravMag,
            pathDeviation,
            remainingPath.Count);

        // Simulate trajectory and log every 10th tick
        var trajectory = SimulateTrajectory(
            shipX, shipY, velX, velY,
            engineX, engineY,
            gravitySources, trajectoryTicks, defaultSpeedLimit);

        for (var t = 0; t < trajectory.Count; t += 10)
        {
            var pt = trajectory[t];
            _logger.LogDebug(
                "[Trajectory] ship={ShipId} tick={Tick:000} pos=({X:F1},{Y:F1})",
                ship.Id, t, pt.X, pt.Y);
        }
    }

    private static double DistanceToTuplePath(NavigationPoint point, IReadOnlyList<(double X, double Y)> path)
    {
        var minDist = double.PositiveInfinity;
        for (var i = 0; i < path.Count - 1; i++)
        {
            var a = new NavigationPoint(path[i].X, path[i].Y);
            var b = new NavigationPoint(path[i + 1].X, path[i + 1].Y);
            var dist = NavigationMath.DistanceToSegment(point, a, b, out _);
            minDist = Math.Min(minDist, dist);
        }
        return minDist;
    }
}

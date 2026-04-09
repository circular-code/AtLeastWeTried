using Flattiverse.Connector.Events;
using Flattiverse.Connector.GalaxyHierarchy;
using Flattiverse.Gateway.Connector;
using Flattiverse.Gateway.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Flattiverse.Gateway.Services.Navigation;

public sealed class PathfindingService : IConnectorEventHandler
{
    private const double ClearanceMargin = 8d;
    private const double MinimumLookahead = 36d;
    private const double MaximumLookahead = 132d;
    private const double MinimumTargetDistance = 18d;
    private const double MaximumTargetDistance = 56d;
    private const double MinimumArrivalDistance = 10d;
    private const double MaximumArrivalDistance = 34d;

    private sealed class PathState
    {
        public ClassicShipControllable? Ship { get; set; }

        public bool HasGoal { get; set; }

        public NavigationPoint Goal { get; set; }

        public float ThrustPercentage { get; set; } = 1f;

        public int PlannedClusterId { get; set; } = int.MinValue;

        public NavigationPoint PlannedStart { get; set; }

        public CircularPathPlanner.PlanResult? Plan { get; set; }

        public NavigationPoint? CurrentLookahead { get; set; }

        public string? Status { get; set; }

        /// <summary>Obstacle unit ids from the last successful plan; used to detect newly added units.</summary>
        public HashSet<string> ObstacleIdsFromSuccessfulPlan { get; } = new(StringComparer.Ordinal);

        /// <summary>Obstacle unit ids passed to the last <see cref="Replan"/> attempt (success or failure).</summary>
        public HashSet<string> ObstacleIdsAtLastPlanAttempt { get; } = new(StringComparer.Ordinal);
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
        state.HasGoal = true;
        state.Goal = new NavigationPoint(targetX, targetY);
        state.ThrustPercentage = thrustPercentage;
        state.Plan = null;
        state.CurrentLookahead = null;
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
        state.Plan = null;
        state.CurrentLookahead = null;
        state.Status = "idle";
        state.ObstacleIdsFromSuccessfulPlan.Clear();
        state.ObstacleIdsAtLastPlanAttempt.Clear();
        _maneuveringService.ClearNavigationTarget(controllableId);
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

        var lookahead = state.CurrentLookahead;
        var plan = state.Plan;
        var overlay = new Dictionary<string, object?>
        {
            { "active", true },
            { "targetX", state.Goal.X },
            { "targetY", state.Goal.Y },
            { "pathStatus", state.Status ?? "planned" },
        };

        if (lookahead is NavigationPoint currentLookahead)
        {
            overlay["currentTargetX"] = currentLookahead.X;
            overlay["currentTargetY"] = currentLookahead.Y;
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
        var arrivalThreshold = ComputeArrivalThreshold(ship);
        if (currentPosition.DistanceTo(state.Goal) <= arrivalThreshold)
        {
            ClearNavigationGoal(ship.Id);
            return;
        }

        var clusterId = ship.Cluster?.Id ?? 0;
        var obstacles = CircularObstacleExtractor.Extract(
            _mappingService.BuildUnitSnapshots(),
            clusterId,
            ship.Size,
            ClearanceMargin,
            destinationUnitId: null,
            shipPosition: currentPosition);

        var replanDecision = GetReplanDecision(state, obstacles, clusterId);
        if (replanDecision.Replan)
        {
            if (_enableLogging)
            {
                _logger.LogInformation(
                    "[Pathfinding] Replan triggered controllable={ControllableId} reason={Reason} obstacleCount={ObstacleCount}",
                    ship.Id,
                    replanDecision.Reason,
                    obstacles.Count);
            }

            Replan(state, ship, currentPosition, clusterId, obstacles);
        }

        if (state.Plan is null || !state.Plan.Succeeded || state.Plan.PathPoints.Count == 0)
        {
            state.CurrentLookahead = null;
            _maneuveringService.ClearNavigationTarget(ship.Id);
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
            ClearNavigationGoal(ship.Id);
            return;
        }

        state.CurrentLookahead = followResult.Target;
        state.Status = "following";
        _maneuveringService.SetNavigationTarget(
            ship,
            (float)followResult.Target.X,
            (float)followResult.Target.Y,
            state.ThrustPercentage,
            resetController: false);
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
        state.CurrentLookahead = null;
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

    private static ReplanDecision GetReplanDecision(PathState state, List<NavigationObstacle> obstacles, int clusterId)
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
        return Math.Clamp(ship.Size * 7d, MinimumLookahead, MaximumLookahead);
    }

    private static double ComputeMinimumTargetDistance(ClassicShipControllable ship)
    {
        return Math.Clamp(ship.Size * 3.2d, MinimumTargetDistance, MaximumTargetDistance);
    }

    private static double ComputeArrivalThreshold(ClassicShipControllable ship)
    {
        return Math.Clamp(ship.Size * 1.8d, MinimumArrivalDistance, MaximumArrivalDistance);
    }
}

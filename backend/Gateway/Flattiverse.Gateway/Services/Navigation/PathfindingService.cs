using Flattiverse.Connector.Events;
using Flattiverse.Connector.GalaxyHierarchy;
using Flattiverse.Gateway.Connector;

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

    public PathfindingService(MappingService mappingService, ManeuveringService maneuveringService)
    {
        _mappingService = mappingService;
        _maneuveringService = maneuveringService;
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
    }

    public void ClearNavigationGoal(int controllableId)
    {
        if (!_states.TryGetValue(controllableId, out var state))
        {
            return;
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

        var overlay = new Dictionary<string, object?>
        {
            { "active", true },
            { "targetX", state.Goal.X },
            { "targetY", state.Goal.Y },
            { "pathStatus", state.Status ?? "planned" },
        };

        if (state.CurrentLookahead is NavigationPoint lookahead)
        {
            overlay["currentTargetX"] = lookahead.X;
            overlay["currentTargetY"] = lookahead.Y;
        }

        if (state.Plan is not null)
        {
            overlay["path"] = state.Plan.PathPoints
                .Select(point => new Dictionary<string, object?>
                {
                    { "x", point.X },
                    { "y", point.Y },
                })
                .ToArray();
            overlay["searchNodes"] = state.Plan.SearchNodes
                .Select(point => new Dictionary<string, object?>
                {
                    { "x", point.X },
                    { "y", point.Y },
                })
                .ToArray();
            overlay["searchEdges"] = state.Plan.SearchEdges
                .Select(segment => new Dictionary<string, object?>
                {
                    { "startX", segment.StartX },
                    { "startY", segment.StartY },
                    { "endX", segment.EndX },
                    { "endY", segment.EndY },
                })
                .ToArray();
            overlay["inflatedObstacles"] = state.Plan.Obstacles
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

        if (ShouldReplan(state, obstacles, clusterId))
        {
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
    }

    private static bool ShouldReplan(PathState state, List<NavigationObstacle> obstacles, int clusterId)
    {
        if (state.Plan is null)
        {
            return true;
        }

        if (state.PlannedClusterId != clusterId)
        {
            return true;
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
                    return true;
                }
            }

            return false;
        }

        if (!state.Plan.Succeeded)
        {
            foreach (var obstacle in obstacles)
            {
                if (!state.ObstacleIdsAtLastPlanAttempt.Contains(obstacle.Id))
                {
                    return true;
                }
            }

            return false;
        }

        return false;
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

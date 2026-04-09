namespace Flattiverse.Gateway.Services.Navigation;

internal sealed class CircularPathPlanner
{
    private sealed record GraphNode(int Index, NavigationPoint Position, int ObstacleIndex, double AngleOnObstacle);

    private readonly record struct GraphEdge(
        int To,
        double Cost,
        bool IsArc,
        int ObstacleIndex,
        double StartAngle,
        double SweepAngle);

    private readonly record struct TangentSegment(
        int FromObstacleIndex,
        int ToObstacleIndex,
        NavigationPoint FromPoint,
        NavigationPoint ToPoint);

    public sealed class PlanResult
    {
        public bool Succeeded { get; init; }

        public string? FailureReason { get; init; }

        public IReadOnlyList<NavigationPoint> PathPoints { get; init; } = Array.Empty<NavigationPoint>();

        public IReadOnlyList<NavigationPreviewPoint> SearchNodes { get; init; } = Array.Empty<NavigationPreviewPoint>();

        public IReadOnlyList<NavigationPreviewSegment> SearchEdges { get; init; } = Array.Empty<NavigationPreviewSegment>();

        public IReadOnlyList<NavigationObstacle> Obstacles { get; init; } = Array.Empty<NavigationObstacle>();
    }

    public PlanResult Plan(
        NavigationPoint start,
        NavigationPoint goal,
        IReadOnlyList<NavigationObstacle> obstacles)
    {
        if (start.DistanceTo(goal) <= NavigationMath.Epsilon)
        {
            return new PlanResult
            {
                Succeeded = true,
                PathPoints = new[] { start, goal },
                Obstacles = obstacles.ToArray(),
            };
        }

        var nodes = new List<GraphNode>
        {
            new(0, start, -1, 0d),
            new(1, goal, -1, 0d),
        };
        var adjacency = new List<List<GraphEdge>>
        {
            new(),
            new(),
        };
        var nodesByKey = new Dictionary<string, int>(StringComparer.Ordinal);
        var contactNodesByObstacle = new Dictionary<int, List<int>>();

        if (IsSegmentClear(start, goal, obstacles, -1, -1))
        {
            AddBidirectionalLineEdge(adjacency, 0, 1, start.DistanceTo(goal));
        }

        for (var obstacleIndex = 0; obstacleIndex < obstacles.Count; obstacleIndex++)
        {
            var obstacle = obstacles[obstacleIndex];

            foreach (var tangent in BuildTangents(start, obstacle, obstacleIndex))
            {
                var toNode = GetOrAddContactNode(nodes, adjacency, nodesByKey, contactNodesByObstacle, tangent.ToObstacleIndex, tangent.ToPoint, obstacles);
                if (IsSegmentClear(start, tangent.ToPoint, obstacles, -1, tangent.ToObstacleIndex))
                {
                    AddBidirectionalLineEdge(adjacency, 0, toNode, start.DistanceTo(tangent.ToPoint));
                }
            }

            foreach (var tangent in BuildTangents(goal, obstacle, obstacleIndex))
            {
                var fromNode = GetOrAddContactNode(nodes, adjacency, nodesByKey, contactNodesByObstacle, tangent.ToObstacleIndex, tangent.ToPoint, obstacles);
                if (IsSegmentClear(goal, tangent.ToPoint, obstacles, -1, tangent.ToObstacleIndex))
                {
                    AddBidirectionalLineEdge(adjacency, 1, fromNode, goal.DistanceTo(tangent.ToPoint));
                }
            }
        }

        for (var left = 0; left < obstacles.Count; left++)
        {
            for (var right = left + 1; right < obstacles.Count; right++)
            {
                foreach (var tangent in BuildTangents(obstacles[left], left, obstacles[right], right))
                {
                    if (!IsSegmentClear(tangent.FromPoint, tangent.ToPoint, obstacles, left, right))
                    {
                        continue;
                    }

                    var fromNode = GetOrAddContactNode(nodes, adjacency, nodesByKey, contactNodesByObstacle, tangent.FromObstacleIndex, tangent.FromPoint, obstacles);
                    var toNode = GetOrAddContactNode(nodes, adjacency, nodesByKey, contactNodesByObstacle, tangent.ToObstacleIndex, tangent.ToPoint, obstacles);
                    AddBidirectionalLineEdge(adjacency, fromNode, toNode, tangent.FromPoint.DistanceTo(tangent.ToPoint));
                }
            }
        }

        AddArcEdges(nodes, adjacency, contactNodesByObstacle, obstacles);

        return Search(goal, nodes, adjacency, obstacles);
    }

    private static PlanResult Search(
        NavigationPoint goal,
        IReadOnlyList<GraphNode> nodes,
        IReadOnlyList<List<GraphEdge>> adjacency,
        IReadOnlyList<NavigationObstacle> obstacles)
    {
        var frontier = new PriorityQueue<int, double>();
        var costs = Enumerable.Repeat(double.PositiveInfinity, nodes.Count).ToArray();
        var cameFrom = Enumerable.Repeat(-1, nodes.Count).ToArray();
        var cameBy = new GraphEdge?[nodes.Count];
        var visited = new bool[nodes.Count];
        var searchNodes = new List<NavigationPreviewPoint>();
        var searchEdges = new List<NavigationPreviewSegment>();
        var searchEdgeKeys = new HashSet<string>(StringComparer.Ordinal);

        costs[0] = 0d;
        frontier.Enqueue(0, nodes[0].Position.DistanceTo(goal));

        while (frontier.TryDequeue(out var current, out _))
        {
            if (visited[current])
            {
                continue;
            }

            visited[current] = true;
            searchNodes.Add(new NavigationPreviewPoint(nodes[current].Position.X, nodes[current].Position.Y));
            if (current == 1)
            {
                break;
            }

            foreach (var edge in adjacency[current])
            {
                var next = edge.To;
                var nextCost = costs[current] + edge.Cost;
                if (nextCost + NavigationMath.Epsilon >= costs[next])
                {
                    continue;
                }

                costs[next] = nextCost;
                cameFrom[next] = current;
                cameBy[next] = edge;
                frontier.Enqueue(next, nextCost + nodes[next].Position.DistanceTo(goal));

                var key = current < next ? $"{current}:{next}" : $"{next}:{current}";
                if (searchEdgeKeys.Add(key))
                {
                    searchEdges.Add(new NavigationPreviewSegment(
                        nodes[current].Position.X,
                        nodes[current].Position.Y,
                        nodes[next].Position.X,
                        nodes[next].Position.Y));
                }
            }
        }

        if (!visited[1])
        {
            return new PlanResult
            {
                Succeeded = false,
                FailureReason = "No path found around the known obstacle set.",
                SearchNodes = searchNodes,
                SearchEdges = searchEdges,
                Obstacles = obstacles.ToArray(),
            };
        }

        var edgeStack = new Stack<(int From, int To, GraphEdge Edge)>();
        for (var current = 1; current != 0; current = cameFrom[current])
        {
            var from = cameFrom[current];
            if (from < 0 || cameBy[current] is not GraphEdge edge)
            {
                break;
            }

            edgeStack.Push((from, current, edge));
        }

        var polyline = new List<NavigationPoint> { nodes[0].Position };
        while (edgeStack.Count > 0)
        {
            var (from, to, edge) = edgeStack.Pop();
            if (!edge.IsArc)
            {
                AppendPoint(polyline, nodes[to].Position);
                continue;
            }

            foreach (var point in SampleArc(obstacles[edge.ObstacleIndex], edge.StartAngle, edge.SweepAngle))
            {
                AppendPoint(polyline, point);
            }

            AppendPoint(polyline, nodes[to].Position);
        }

        return new PlanResult
        {
            Succeeded = true,
            PathPoints = SimplifyPath(polyline),
            SearchNodes = searchNodes,
            SearchEdges = searchEdges,
            Obstacles = obstacles.ToArray(),
        };
    }

    private static IEnumerable<NavigationPoint> SampleArc(NavigationObstacle obstacle, double startAngle, double sweepAngle)
    {
        var steps = Math.Max(2, (int)Math.Ceiling(Math.Abs(sweepAngle) * obstacle.Radius / 8d));
        for (var step = 1; step <= steps; step++)
        {
            var amount = step / (double)steps;
            var angle = startAngle + sweepAngle * amount;
            yield return obstacle.Center + NavigationPoint.FromAngle(angle, obstacle.Radius);
        }
    }

    private static IReadOnlyList<NavigationPoint> SimplifyPath(IReadOnlyList<NavigationPoint> path)
    {
        if (path.Count <= 2)
        {
            return path.ToArray();
        }

        var simplified = new List<NavigationPoint>();
        foreach (var point in path)
        {
            AppendPoint(simplified, point);
        }

        return simplified;
    }

    private static void AppendPoint(ICollection<NavigationPoint> points, NavigationPoint point)
    {
        if (points.Count > 0 && points.Last().DistanceTo(point) <= 0.05d)
        {
            return;
        }

        points.Add(point);
    }

    private static void AddArcEdges(
        IReadOnlyList<GraphNode> nodes,
        IReadOnlyList<List<GraphEdge>> adjacency,
        IReadOnlyDictionary<int, List<int>> contactNodesByObstacle,
        IReadOnlyList<NavigationObstacle> obstacles)
    {
        foreach (var (obstacleIndex, nodeIndices) in contactNodesByObstacle)
        {
            if (nodeIndices.Count < 2)
            {
                continue;
            }

            var ordered = nodeIndices
                .Distinct()
                .Select(index => nodes[index])
                .OrderBy(node => node.AngleOnObstacle)
                .ToArray();

            for (var index = 0; index < ordered.Length; index++)
            {
                var current = ordered[index];
                var next = ordered[(index + 1) % ordered.Length];
                var deltaAngle = NavigationMath.NormalizeAngle(next.AngleOnObstacle - current.AngleOnObstacle);
                if (deltaAngle <= NavigationMath.Epsilon)
                {
                    continue;
                }

                var midpointAngle = NavigationMath.NormalizeAngle(current.AngleOnObstacle + deltaAngle * 0.5d);
                var midpoint = obstacles[obstacleIndex].Center + NavigationPoint.FromAngle(midpointAngle, obstacles[obstacleIndex].Radius);
                if (!IsPointClear(midpoint, obstacles, obstacleIndex))
                {
                    continue;
                }

                var cost = deltaAngle * obstacles[obstacleIndex].Radius;
                AddDirectedEdge(adjacency, current.Index, new GraphEdge(next.Index, cost, true, obstacleIndex, current.AngleOnObstacle, deltaAngle));
                AddDirectedEdge(adjacency, next.Index, new GraphEdge(current.Index, cost, true, obstacleIndex, next.AngleOnObstacle, -deltaAngle));
            }
        }
    }

    private static int GetOrAddContactNode(
        IList<GraphNode> nodes,
        IList<List<GraphEdge>> adjacency,
        IDictionary<string, int> nodesByKey,
        IDictionary<int, List<int>> contactNodesByObstacle,
        int obstacleIndex,
        NavigationPoint point,
        IReadOnlyList<NavigationObstacle> obstacles)
    {
        var key = NavigationMath.BuildPointKey(obstacleIndex, point);
        if (nodesByKey.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var angle = NavigationMath.NormalizeAngle((point - obstacles[obstacleIndex].Center).Angle);
        var index = nodes.Count;
        nodes.Add(new GraphNode(index, point, obstacleIndex, angle));
        adjacency.Add(new List<GraphEdge>());
        nodesByKey[key] = index;

        if (!contactNodesByObstacle.TryGetValue(obstacleIndex, out var nodeIndices))
        {
            nodeIndices = new List<int>();
            contactNodesByObstacle[obstacleIndex] = nodeIndices;
        }

        nodeIndices.Add(index);
        return index;
    }

    private static void AddBidirectionalLineEdge(IReadOnlyList<List<GraphEdge>> adjacency, int from, int to, double cost)
    {
        if (from == to || cost <= NavigationMath.Epsilon)
        {
            return;
        }

        AddDirectedEdge(adjacency, from, new GraphEdge(to, cost, false, -1, 0d, 0d));
        AddDirectedEdge(adjacency, to, new GraphEdge(from, cost, false, -1, 0d, 0d));
    }

    private static void AddDirectedEdge(IReadOnlyList<List<GraphEdge>> adjacency, int from, GraphEdge edge)
    {
        if (!adjacency[from].Any(existing =>
                existing.To == edge.To
                && existing.IsArc == edge.IsArc
                && existing.ObstacleIndex == edge.ObstacleIndex
                && Math.Abs(existing.Cost - edge.Cost) <= NavigationMath.Epsilon
                && Math.Abs(existing.StartAngle - edge.StartAngle) <= NavigationMath.Epsilon
                && Math.Abs(existing.SweepAngle - edge.SweepAngle) <= NavigationMath.Epsilon))
        {
            adjacency[from].Add(edge);
        }
    }

    private static bool IsPointClear(NavigationPoint point, IReadOnlyList<NavigationObstacle> obstacles, int ignoreObstacleIndex)
    {
        for (var obstacleIndex = 0; obstacleIndex < obstacles.Count; obstacleIndex++)
        {
            if (obstacleIndex == ignoreObstacleIndex)
            {
                continue;
            }

            if (point.DistanceTo(obstacles[obstacleIndex].Center) < obstacles[obstacleIndex].Radius - 0.05d)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSegmentClear(
        NavigationPoint start,
        NavigationPoint end,
        IReadOnlyList<NavigationObstacle> obstacles,
        int ignoreObstacleA,
        int ignoreObstacleB)
    {
        for (var obstacleIndex = 0; obstacleIndex < obstacles.Count; obstacleIndex++)
        {
            if (obstacleIndex == ignoreObstacleA || obstacleIndex == ignoreObstacleB)
            {
                continue;
            }

            var obstacle = obstacles[obstacleIndex];
            var distance = NavigationMath.DistanceToSegment(obstacle.Center, start, end, out _);
            if (distance < obstacle.Radius - 0.05d)
            {
                return false;
            }
        }

        return true;
    }

    private static IEnumerable<TangentSegment> BuildTangents(NavigationPoint point, NavigationObstacle obstacle, int obstacleIndex)
    {
        var pointCircle = new NavigationObstacle("start", "point", point, 0d);
        foreach (var tangent in BuildTangents(pointCircle, -1, obstacle, obstacleIndex))
        {
            yield return tangent;
        }
    }

    private static IEnumerable<TangentSegment> BuildTangents(
        NavigationObstacle left,
        int leftIndex,
        NavigationObstacle right,
        int rightIndex)
    {
        var delta = right.Center - left.Center;
        var distanceSquared = delta.LengthSquared;
        if (distanceSquared <= NavigationMath.Epsilon)
        {
            yield break;
        }

        for (var side = -1; side <= 1; side += 2)
        {
            var radiusDifference = left.Radius - side * right.Radius;
            var determinant = distanceSquared - radiusDifference * radiusDifference;
            if (determinant < -NavigationMath.Epsilon)
            {
                continue;
            }

            var safeDeterminant = Math.Sqrt(Math.Max(0d, determinant));
            for (var orientation = -1; orientation <= 1; orientation += 2)
            {
                var normal = new NavigationPoint(
                    (delta.X * radiusDifference + -delta.Y * safeDeterminant * orientation) / distanceSquared,
                    (delta.Y * radiusDifference + delta.X * safeDeterminant * orientation) / distanceSquared);
                var fromPoint = left.Center + normal * left.Radius;
                var toPoint = right.Center + normal * (side * right.Radius);
                yield return new TangentSegment(leftIndex, rightIndex, fromPoint, toPoint);
            }
        }
    }
}

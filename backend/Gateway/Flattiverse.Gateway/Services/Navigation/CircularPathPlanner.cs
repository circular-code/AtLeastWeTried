namespace Flattiverse.Gateway.Services.Navigation;

internal sealed class CircularPathPlanner
{
    private const bool EnableEdgeFileLogging = false;
    private const string EdgeLogFileName = "circular-path-planner-edges.json";
    private const int CounterClockwiseTangentDirection = 1;
    private const int ClockwiseTangentDirection = -1;

    private sealed record GraphNode(int Index, NavigationPoint Position, int ObstacleIndex, double AngleOnObstacle, int TangentDirection);

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
            new(0, start, -1, 0d, 0),
            new(1, goal, -1, 0d, 0),
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
            AddDirectedLineEdge(adjacency, 0, 1, start.DistanceTo(goal));
        }

        for (var obstacleIndex = 0; obstacleIndex < obstacles.Count; obstacleIndex++)
        {
            var obstacle = obstacles[obstacleIndex];

            foreach (var tangent in BuildTangents(start, obstacle, obstacleIndex))
            {
                var toNode = GetOrAddContactNodeForLine(
                    nodes,
                    adjacency,
                    nodesByKey,
                    contactNodesByObstacle,
                    tangent.ToObstacleIndex,
                    tangent.ToPoint,
                    tangent.ToPoint - start,
                    obstacles);
                if (toNode is not null && IsSegmentClear(start, tangent.ToPoint, obstacles, -1, tangent.ToObstacleIndex))
                {
                    AddDirectedLineEdge(adjacency, 0, toNode.Value, start.DistanceTo(tangent.ToPoint));
                }
            }

            foreach (var tangent in BuildTangents(goal, obstacle, obstacleIndex))
            {
                var fromNode = GetOrAddContactNodeForLine(
                    nodes,
                    adjacency,
                    nodesByKey,
                    contactNodesByObstacle,
                    tangent.ToObstacleIndex,
                    tangent.ToPoint,
                    goal - tangent.ToPoint,
                    obstacles);
                if (fromNode is not null && IsSegmentClear(goal, tangent.ToPoint, obstacles, -1, tangent.ToObstacleIndex))
                {
                    AddDirectedLineEdge(adjacency, fromNode.Value, 1, goal.DistanceTo(tangent.ToPoint));
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

                    var fromNode = GetOrAddContactNodeForLine(
                        nodes,
                        adjacency,
                        nodesByKey,
                        contactNodesByObstacle,
                        tangent.FromObstacleIndex,
                        tangent.FromPoint,
                        tangent.ToPoint - tangent.FromPoint,
                        obstacles);
                    var toNode = GetOrAddContactNodeForLine(
                        nodes,
                        adjacency,
                        nodesByKey,
                        contactNodesByObstacle,
                        tangent.ToObstacleIndex,
                        tangent.ToPoint,
                        tangent.ToPoint - tangent.FromPoint,
                        obstacles);
                    if (fromNode is null || toNode is null)
                    {
                        continue;
                    }

                    var cost = tangent.FromPoint.DistanceTo(tangent.ToPoint);
                    AddDirectedLineEdge(adjacency, fromNode.Value, toNode.Value, cost);

                    var reverseFromNode = GetOrAddContactNodeForLine(
                        nodes,
                        adjacency,
                        nodesByKey,
                        contactNodesByObstacle,
                        tangent.ToObstacleIndex,
                        tangent.ToPoint,
                        tangent.FromPoint - tangent.ToPoint,
                        obstacles);
                    var reverseToNode = GetOrAddContactNodeForLine(
                        nodes,
                        adjacency,
                        nodesByKey,
                        contactNodesByObstacle,
                        tangent.FromObstacleIndex,
                        tangent.FromPoint,
                        tangent.FromPoint - tangent.ToPoint,
                        obstacles);
                    if (reverseFromNode is not null && reverseToNode is not null)
                    {
                        AddDirectedLineEdge(adjacency, reverseFromNode.Value, reverseToNode.Value, cost);
                    }
                }
            }
        }

        AddArcEdges(nodes, adjacency, contactNodesByObstacle, obstacles);
        WriteEdgeLogIfEnabled(start, goal, nodes, adjacency, obstacles);

        return Search(goal, nodes, adjacency, obstacles);
    }

    private static void WriteEdgeLogIfEnabled(
        NavigationPoint start,
        NavigationPoint goal,
        IReadOnlyList<GraphNode> nodes,
        IReadOnlyList<List<GraphEdge>> adjacency,
        IReadOnlyList<NavigationObstacle> obstacles)
    {
        if (!IsEdgeFileLoggingEnabled())
        {
            return;
        }

        try
        {
            var edgeLog = new
            {
                generatedAtUtc = DateTime.UtcNow,
                start = new { x = start.X, y = start.Y },
                goal = new { x = goal.X, y = goal.Y },
                nodes = nodes.Select(node => new
                {
                    index = node.Index,
                    x = node.Position.X,
                    y = node.Position.Y,
                    obstacleIndex = node.ObstacleIndex,
                    angleOnObstacle = node.AngleOnObstacle,
                    tangentDirection = node.TangentDirection,
                }),
                obstacles = obstacles.Select((obstacle, index) => new
                {
                    index,
                    obstacle.Id,
                    obstacle.Kind,
                    centerX = obstacle.Center.X,
                    centerY = obstacle.Center.Y,
                    obstacle.Radius,
                }),
                edges = adjacency.SelectMany(
                    (edges, fromIndex) => edges.Select(edge => new
                    {
                        from = fromIndex,
                        to = edge.To,
                        cost = edge.Cost,
                        isArc = edge.IsArc,
                        obstacleIndex = edge.ObstacleIndex,
                        startAngle = edge.StartAngle,
                        sweepAngle = edge.SweepAngle,
                        fromPoint = new
                        {
                            x = nodes[fromIndex].Position.X,
                            y = nodes[fromIndex].Position.Y,
                        },
                        toPoint = new
                        {
                            x = nodes[edge.To].Position.X,
                            y = nodes[edge.To].Position.Y,
                        },
                    })),
            };

            var filePath = Path.Combine(AppContext.BaseDirectory, EdgeLogFileName);
            var json = System.Text.Json.JsonSerializer.Serialize(edgeLog, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
            });
            File.WriteAllText(filePath, json);
        }
        catch
        {
            // Edge logging is diagnostic only and must never affect planning.
        }
    }

    private static bool IsEdgeFileLoggingEnabled() => EnableEdgeFileLogging;

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

                if (edge.IsArc)
                {
                    var previous = nodes[current].Position;
                    foreach (var point in SampleArc(obstacles[edge.ObstacleIndex], edge.StartAngle, edge.SweepAngle))
                    {
                        AddSearchPreviewSegment(searchEdges, searchEdgeKeys, previous, point);
                        previous = point;
                    }
                }
                else
                {
                    AddSearchPreviewSegment(searchEdges, searchEdgeKeys, nodes[current].Position, nodes[next].Position);
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

    private static void AddSearchPreviewSegment(
        ICollection<NavigationPreviewSegment> searchEdges,
        ISet<string> searchEdgeKeys,
        NavigationPoint start,
        NavigationPoint end)
    {
        if (start.DistanceTo(end) <= NavigationMath.Epsilon)
        {
            return;
        }

        var key = BuildPreviewSegmentKey(start, end);
        if (!searchEdgeKeys.Add(key))
        {
            return;
        }

        searchEdges.Add(new NavigationPreviewSegment(start.X, start.Y, end.X, end.Y));
    }

    private static string BuildPreviewSegmentKey(NavigationPoint start, NavigationPoint end)
    {
        var startKey = $"{Math.Round(start.X * NavigationMath.CoordinateRounding) / NavigationMath.CoordinateRounding:0.#####}:{Math.Round(start.Y * NavigationMath.CoordinateRounding) / NavigationMath.CoordinateRounding:0.#####}";
        var endKey = $"{Math.Round(end.X * NavigationMath.CoordinateRounding) / NavigationMath.CoordinateRounding:0.#####}:{Math.Round(end.Y * NavigationMath.CoordinateRounding) / NavigationMath.CoordinateRounding:0.#####}";
        return string.CompareOrdinal(startKey, endKey) <= 0
            ? $"{startKey}|{endKey}"
            : $"{endKey}|{startKey}";
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
                .GroupBy(node => NavigationMath.BuildPointKey(node.ObstacleIndex, node.Position), StringComparer.Ordinal)
                .Select(group =>
                {
                    var contacts = group.ToArray();
                    var sample = contacts[0];
                    return new
                    {
                        sample.Position,
                        sample.AngleOnObstacle,
                        CounterClockwiseNode = contacts
                            .FirstOrDefault(node => node.TangentDirection == CounterClockwiseTangentDirection)
                            ?.Index,
                        ClockwiseNode = contacts
                            .FirstOrDefault(node => node.TangentDirection == ClockwiseTangentDirection)
                            ?.Index,
                    };
                })
                .OrderBy(node => node.AngleOnObstacle)
                .ToArray();

            for (var index = 0; index < ordered.Length; index++)
            {
                var current = ordered[index];
                var next = ordered[(index + 1) % ordered.Length];
                var sweep = NavigationMath.ShortArcSignedSweep(current.AngleOnObstacle, next.AngleOnObstacle);
                if (Math.Abs(sweep) <= NavigationMath.Epsilon)
                {
                    continue;
                }

                if (!IsArcClear(obstacles[obstacleIndex], current.AngleOnObstacle, sweep, obstacles, obstacleIndex))
                {
                    continue;
                }

                var cost = Math.Abs(sweep) * obstacles[obstacleIndex].Radius;
                if (sweep > 0d)
                {
                    if (current.CounterClockwiseNode is int counterClockwiseFrom && next.CounterClockwiseNode is int counterClockwiseTo)
                    {
                        AddDirectedEdge(adjacency, counterClockwiseFrom, new GraphEdge(counterClockwiseTo, cost, true, obstacleIndex, current.AngleOnObstacle, sweep));
                    }

                    if (next.ClockwiseNode is int clockwiseFrom && current.ClockwiseNode is int clockwiseTo)
                    {
                        AddDirectedEdge(adjacency, clockwiseFrom, new GraphEdge(clockwiseTo, cost, true, obstacleIndex, next.AngleOnObstacle, -sweep));
                    }
                }
                else
                {
                    if (current.ClockwiseNode is int clockwiseFrom && next.ClockwiseNode is int clockwiseTo)
                    {
                        AddDirectedEdge(adjacency, clockwiseFrom, new GraphEdge(clockwiseTo, cost, true, obstacleIndex, current.AngleOnObstacle, sweep));
                    }

                    if (next.CounterClockwiseNode is int counterClockwiseFrom && current.CounterClockwiseNode is int counterClockwiseTo)
                    {
                        AddDirectedEdge(adjacency, counterClockwiseFrom, new GraphEdge(counterClockwiseTo, cost, true, obstacleIndex, next.AngleOnObstacle, -sweep));
                    }
                }
            }
        }
    }

    private static int? GetOrAddContactNodeForLine(
        IList<GraphNode> nodes,
        IList<List<GraphEdge>> adjacency,
        IDictionary<string, int> nodesByKey,
        IDictionary<int, List<int>> contactNodesByObstacle,
        int obstacleIndex,
        NavigationPoint point,
        NavigationPoint lineDirection,
        IReadOnlyList<NavigationObstacle> obstacles)
    {
        var tangentDirection = DetermineTangentDirection(obstacles[obstacleIndex], point, lineDirection);
        if (tangentDirection == 0)
        {
            return null;
        }

        return GetOrAddContactNode(
            nodes,
            adjacency,
            nodesByKey,
            contactNodesByObstacle,
            obstacleIndex,
            point,
            tangentDirection,
            obstacles);
    }

    private static int? GetOrAddContactNode(
        IList<GraphNode> nodes,
        IList<List<GraphEdge>> adjacency,
        IDictionary<string, int> nodesByKey,
        IDictionary<int, List<int>> contactNodesByObstacle,
        int obstacleIndex,
        NavigationPoint point,
        int tangentDirection,
        IReadOnlyList<NavigationObstacle> obstacles)
    {
        if (!IsPointClear(point, obstacles, obstacleIndex))
        {
            return null;
        }

        var key = BuildContactNodeKey(obstacleIndex, point, tangentDirection);
        if (nodesByKey.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var angle = NavigationMath.NormalizeAngle((point - obstacles[obstacleIndex].Center).Angle);
        var index = nodes.Count;
        nodes.Add(new GraphNode(index, point, obstacleIndex, angle, tangentDirection));
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

    private static string BuildContactNodeKey(int obstacleIndex, NavigationPoint point, int tangentDirection)
    {
        return $"{NavigationMath.BuildPointKey(obstacleIndex, point)}:{tangentDirection}";
    }

    private static int DetermineTangentDirection(
        NavigationObstacle obstacle,
        NavigationPoint point,
        NavigationPoint lineDirection)
    {
        var tangent = NavigationPoint.PerpendicularLeft((point - obstacle.Center).Normalized());
        var alignment = NavigationPoint.Dot(lineDirection.Normalized(), tangent);
        if (Math.Abs(alignment) <= 1e-3d)
        {
            return 0;
        }

        return alignment > 0d
            ? CounterClockwiseTangentDirection
            : ClockwiseTangentDirection;
    }

    private static bool IsArcClear(
        NavigationObstacle obstacle,
        double startAngle,
        double sweepAngle,
        IReadOnlyList<NavigationObstacle> obstacles,
        int ignoreObstacleIndex)
    {
        if (Math.Abs(sweepAngle) <= NavigationMath.Epsilon)
        {
            return true;
        }

        var startPoint = obstacle.Center + NavigationPoint.FromAngle(startAngle, obstacle.Radius);
        var endPoint = obstacle.Center + NavigationPoint.FromAngle(startAngle + sweepAngle, obstacle.Radius);

        for (var obstacleIndex = 0; obstacleIndex < obstacles.Count; obstacleIndex++)
        {
            if (obstacleIndex == ignoreObstacleIndex)
            {
                continue;
            }

            var other = obstacles[obstacleIndex];
            var nearestAngle = NavigationMath.NormalizeAngle((other.Center - obstacle.Center).Angle);
            var minimumDistance = Math.Min(
                startPoint.DistanceTo(other.Center),
                endPoint.DistanceTo(other.Center));

            if (AngleLiesOnSweep(startAngle, sweepAngle, nearestAngle))
            {
                minimumDistance = Math.Min(
                    minimumDistance,
                    Math.Abs(obstacle.Center.DistanceTo(other.Center) - obstacle.Radius));
            }

            if (minimumDistance < other.Radius - 0.05d)
            {
                return false;
            }
        }

        return true;
    }

    private static bool AngleLiesOnSweep(double startAngle, double sweepAngle, double candidateAngle)
    {
        if (sweepAngle >= 0d)
        {
            return NavigationMath.NormalizeAngle(candidateAngle - startAngle) <= sweepAngle + NavigationMath.Epsilon;
        }

        return NavigationMath.NormalizeAngle(startAngle - candidateAngle) <= -sweepAngle + NavigationMath.Epsilon;
    }

    private static void AddDirectedLineEdge(IReadOnlyList<List<GraphEdge>> adjacency, int from, int to, double cost)
    {
        if (from == to || cost <= NavigationMath.Epsilon)
        {
            return;
        }

        AddDirectedEdge(adjacency, from, new GraphEdge(to, cost, false, -1, 0d, 0d));
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
        foreach (var tangent in BuildPointToCircleTangents(point, obstacle, -1, obstacleIndex))
        {
            yield return tangent;
        }
    }

    /// <summary>
    /// Tangents from an external point to a circle. Avoids degenerate two-circle math when the other radius is 0
    /// (which produced non-unit "normals" and inconsistent tangent directions).
    /// </summary>
    private static IEnumerable<TangentSegment> BuildPointToCircleTangents(
        NavigationPoint point,
        NavigationObstacle circle,
        int pointObstacleIndex,
        int circleObstacleIndex)
    {
        foreach (var onCircle in NavigationMath.PointToCircleTangentTouchPoints(point, circle))
        {
            yield return new TangentSegment(pointObstacleIndex, circleObstacleIndex, point, onCircle);
        }
    }

    private static IEnumerable<TangentSegment> BuildTangents(
        NavigationObstacle left,
        int leftIndex,
        NavigationObstacle right,
        int rightIndex)
    {
        if (left.Radius <= NavigationMath.Epsilon)
        {
            foreach (var tangent in BuildPointToCircleTangents(left.Center, right, leftIndex, rightIndex))
            {
                yield return tangent;
            }

            yield break;
        }

        if (right.Radius <= NavigationMath.Epsilon)
        {
            foreach (var tangent in BuildPointToCircleTangents(right.Center, left, rightIndex, leftIndex))
            {
                yield return new TangentSegment(leftIndex, rightIndex, tangent.ToPoint, tangent.FromPoint);
            }

            yield break;
        }

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
                var raw = new NavigationPoint(
                    (delta.X * radiusDifference + -delta.Y * safeDeterminant * orientation) / distanceSquared,
                    (delta.Y * radiusDifference + delta.X * safeDeterminant * orientation) / distanceSquared);
                var len = raw.Length;
                if (len <= NavigationMath.Epsilon)
                {
                    continue;
                }

                var normal = raw / len;
                var fromPoint = left.Center + normal * left.Radius;
                var toPoint = right.Center + normal * (side * right.Radius);
                yield return new TangentSegment(leftIndex, rightIndex, fromPoint, toPoint);
            }
        }
    }
}

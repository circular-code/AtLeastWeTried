using System.Reflection;
using System.Text.Json;
using Flattiverse.Gateway.Protocol.Dtos;
using Flattiverse.Gateway.Services.Navigation;

namespace Flattiverse.Gateway.Tests;

public sealed class PathfindingFixtureTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Extractor_builds_inflated_obstacles_from_fixture_scope()
    {
        var units = LoadFixtureUnits();

        var obstacles = CircularObstacleExtractor.Extract(units, clusterId: 0, shipRadius: 12d, clearanceMargin: 8d);

        Assert.NotEmpty(obstacles);
        Assert.Contains(obstacles, obstacle => obstacle.Kind == "meteoroid");
        Assert.All(obstacles, obstacle => Assert.True(obstacle.Radius > 18d));
        Assert.DoesNotContain(obstacles, obstacle => obstacle.Kind.StartsWith("powerup-", StringComparison.Ordinal));
    }

    [Fact]
    public void Extractor_skips_units_marked_non_solid()
    {
        var units = new List<UnitSnapshotDto>
        {
            new()
            {
                UnitId = "solid-rock",
                ClusterId = 1,
                Kind = "meteoroid",
                IsStatic = true,
                IsSeen = true,
                IsSolid = true,
                Radius = 10f,
                X = 0f,
                Y = 0f,
            },
            new()
            {
                UnitId = "ghost-rock",
                ClusterId = 1,
                Kind = "meteoroid",
                IsStatic = true,
                IsSeen = true,
                IsSolid = false,
                Radius = 10f,
                X = 100f,
                Y = 0f,
            },
        };

        var obstacles = CircularObstacleExtractor.Extract(units, clusterId: 1, shipRadius: 0d, clearanceMargin: 0d);

        Assert.Single(obstacles);
        Assert.Equal("solid-rock", obstacles[0].Id);
    }

    [Fact]
    public void Extractor_adds_extra_clearance_proportional_to_gravity()
    {
        UnitSnapshotDto baseline() => new()
        {
            UnitId = "rock",
            ClusterId = 1,
            Kind = "meteoroid",
            IsStatic = true,
            IsSeen = true,
            IsSolid = true,
            Radius = 10f,
            X = 0f,
            Y = 0f,
            Gravity = 0f,
        };

        var light = baseline();
        var heavy = baseline();
        heavy.Gravity = 0.001f;

        var withoutGravity = CircularObstacleExtractor.Extract(
            new List<UnitSnapshotDto> { light },
            clusterId: 1,
            shipRadius: 12d,
            clearanceMargin: 8d);

        var withGravity = CircularObstacleExtractor.Extract(
            new List<UnitSnapshotDto> { heavy },
            clusterId: 1,
            shipRadius: 12d,
            clearanceMargin: 8d);

        Assert.Single(withoutGravity);
        Assert.Single(withGravity);
        const double expectedDelta = 200d * 0.001d;
        Assert.Equal(expectedDelta, withGravity[0].Radius - withoutGravity[0].Radius, 5);
    }

    [Fact]
    public void Extractor_shrinks_keep_out_radius_when_ship_inside_disk_or_omits_when_degenerate()
    {
        var unit = new UnitSnapshotDto
        {
            UnitId = "sun",
            ClusterId = 1,
            Kind = "sun",
            IsStatic = true,
            IsSeen = true,
            IsSolid = true,
            Radius = 40f,
            X = 0f,
            Y = 0f,
            Gravity = 0.02f,
        };
        var units = new List<UnitSnapshotDto> { unit };

        var atCenter = new NavigationPoint(0d, 0d);
        var degenerate = CircularObstacleExtractor.Extract(
            units,
            clusterId: 1,
            shipRadius: 12d,
            clearanceMargin: 8d,
            destinationUnitId: null,
            shipPosition: atCenter);

        Assert.Empty(degenerate);

        var far = new NavigationPoint(5000d, 0d);
        var fullRadius = CircularObstacleExtractor.Extract(
            units,
            clusterId: 1,
            shipRadius: 12d,
            clearanceMargin: 8d,
            destinationUnitId: null,
            shipPosition: far)[0].Radius;

        var insideShell = new NavigationPoint(55d, 0d);
        var shrunk = CircularObstacleExtractor.Extract(
            units,
            clusterId: 1,
            shipRadius: 12d,
            clearanceMargin: 8d,
            destinationUnitId: null,
            shipPosition: insideShell);

        Assert.Single(shrunk);
        Assert.Equal("sun", shrunk[0].Id);
        Assert.True(shrunk[0].Radius < fullRadius - 1d);
        Assert.Equal(55d, shrunk[0].Radius, 2);
    }

    [Fact]
    public void PolylineIntersectsObstacleDisk_detects_segment_through_disk()
    {
        var path = new[]
        {
            new NavigationPoint(-100d, 0d),
            new NavigationPoint(100d, 0d),
        };
        var blocking = new NavigationObstacle("a", "meteoroid", new NavigationPoint(0d, 0d), 50d);
        Assert.True(NavigationMath.PolylineIntersectsObstacleDisk(path, blocking));

        var clear = new NavigationObstacle("b", "meteoroid", new NavigationPoint(0d, 0d), 10d);
        var pathAbove = new[]
        {
            new NavigationPoint(-100d, -200d),
            new NavigationPoint(100d, -200d),
        };
        Assert.False(NavigationMath.PolylineIntersectsObstacleDisk(pathAbove, clear));
    }

    [Fact]
    public void Point_to_circle_tangent_touch_points_are_on_circle_and_perpendicular_to_segment()
    {
        var circle = new NavigationObstacle("c", "test", new NavigationPoint(0d, 0d), 5d);
        var external = new NavigationPoint(10d, 0d);
        var touches = NavigationMath.PointToCircleTangentTouchPoints(external, circle).OrderBy(t => t.Y).ToList();

        Assert.Equal(2, touches.Count);
        foreach (var t in touches)
        {
            Assert.Equal(5d, t.DistanceTo(circle.Center), 5);
            var dot = NavigationPoint.Dot(external - t, t - circle.Center);
            Assert.True(Math.Abs(dot) < 1e-5d, $"Expected perpendicularity, dot={dot}");
        }

        var expectedY = 5d * Math.Sin(Math.PI / 3d);
        AssertClose(new NavigationPoint(2.5d, -expectedY), touches[0]);
        AssertClose(new NavigationPoint(2.5d, expectedY), touches[1]);
    }

    [Fact]
    public void Point_to_circle_tangent_returns_empty_when_point_is_inside_disk()
    {
        var circle = new NavigationObstacle("c", "test", new NavigationPoint(0d, 0d), 10d);
        Assert.Empty(NavigationMath.PointToCircleTangentTouchPoints(new NavigationPoint(3d, 0d), circle));
    }

    [Fact]
    public void ShortArcSignedSweep_straddling_atan2_wrap_uses_short_arc_not_full_turn()
    {
        const double epsilon = 0.05d;
        var nearNegPi = -Math.PI + epsilon;
        var nearPosPi = Math.PI - epsilon;

        // Sorted atan2 order places these two contacts consecutively; naive CCW gap in [0, 2π) is ~2π − 2ε (wrong).
        var naiveCcwGap = NavigationMath.NormalizeAngle(nearPosPi - nearNegPi);
        Assert.True(
            naiveCcwGap > Math.PI - 1e-3d,
            $"Naive gap should be almost a full turn (reproduces overlay bug); got {naiveCcwGap}");

        var sweep = NavigationMath.ShortArcSignedSweep(nearNegPi, nearPosPi);
        Assert.InRange(Math.Abs(sweep), 2d * epsilon - 1e-3d, 2d * epsilon + 1e-3d);
        Assert.True(Math.Abs(sweep) < Math.PI * 0.5d, "Short arc must be the small gap near ±π, not ~2π.");
    }

    /// <summary>
    /// Regression: <see cref="CircularPathPlanner"/> used <c>NormalizeAngle(next - current)</c> for arc sweep.
    /// When two obstacle contacts sorted as (near −π) then (near +π), that value is ~2π − gap (long way), producing
    /// wrong rim traversal and misleading tangent/arc overlay segments (intermittent on geometry).
    /// </summary>
    [Fact]
    public void AddArc_edges_bug_repro_naive_sweep_for_pm_pi_neighbors_is_long_arc()
    {
        const double epsilon = 0.04d;
        var angleA = -Math.PI + epsilon;
        var angleB = Math.PI - epsilon;
        var ordered = new[] { angleA, angleB }.OrderBy(a => a).ToArray();

        var legacySweep = NavigationMath.NormalizeAngle(ordered[1] - ordered[0]);
        var fixedSweep = NavigationMath.ShortArcSignedSweep(ordered[0], ordered[1]);

        Assert.True(legacySweep > Math.PI, $"Legacy AddArc sweep should be ~2π−2ε (>{Math.PI}); got {legacySweep}");
        Assert.InRange(Math.Abs(fixedSweep), 2d * epsilon - 1e-3d, 2d * epsilon + 1e-3d);
    }

    [Fact]
    public void Planner_single_disk_route_succeeds_with_clearance()
    {
        var planner = new CircularPathPlanner();
        var sun = new NavigationObstacle("sun", "sun", new NavigationPoint(400d, 0d), 120d);
        var obstacles = new[] { sun };

        var start = new NavigationPoint(120d, 80d);
        var goal = new NavigationPoint(120d, -80d);

        var result = planner.Plan(start, goal, obstacles);

        Assert.True(result.Succeeded, result.FailureReason);
        AssertPathMaintainsClearance(result.PathPoints, obstacles, 1d);
    }

    [Fact]
    public void Search_preview_segments_follow_arcs_instead_of_cutting_chords_through_obstacles()
    {
        var planner = new CircularPathPlanner();
        var sun = new NavigationObstacle("sun", "sun", new NavigationPoint(0d, 0d), 120d);
        var obstacles = new[] { sun };
        var start = new NavigationPoint(-250d, 80d);
        var goal = new NavigationPoint(250d, 80d);

        var result = planner.Plan(start, goal, obstacles);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.NotEmpty(result.SearchEdges);
        Assert.All(
            result.SearchEdges,
            edge =>
            {
                var distance = NavigationMath.DistanceToSegment(
                    sun.Center,
                    new NavigationPoint(edge.StartX, edge.StartY),
                    new NavigationPoint(edge.EndX, edge.EndY),
                    out _);
                Assert.True(
                    distance >= sun.Radius - 0.75d,
                    $"Search preview segment cuts through obstacle: ({edge.StartX:0.##},{edge.StartY:0.##}) -> ({edge.EndX:0.##},{edge.EndY:0.##}), distance={distance:0.###}, radius={sun.Radius:0.###}");
            });
    }

    [Fact]
    public void Circle_to_circle_tangent_segments_are_true_tangents()
    {
        var random = new Random(12345);

        for (var sample = 0; sample < 250; sample++)
        {
            var left = new NavigationObstacle(
                $"left-{sample}",
                "test",
                new NavigationPoint(random.NextDouble() * 600d - 300d, random.NextDouble() * 600d - 300d),
                20d + random.NextDouble() * 120d);
            var right = new NavigationObstacle(
                $"right-{sample}",
                "test",
                new NavigationPoint(random.NextDouble() * 600d - 300d, random.NextDouble() * 600d - 300d),
                20d + random.NextDouble() * 120d);

            var centerDistance = left.Center.DistanceTo(right.Center);
            if (centerDistance <= left.Radius + right.Radius + 25d)
            {
                sample--;
                continue;
            }

            var tangents = InvokeCircleTangents(left, right);
            Assert.NotEmpty(tangents);

            foreach (var (fromPoint, toPoint) in tangents)
            {
                Assert.Equal(left.Radius, fromPoint.DistanceTo(left.Center), 4);
                Assert.Equal(right.Radius, toPoint.DistanceTo(right.Center), 4);

                var line = toPoint - fromPoint;
                var fromRadius = fromPoint - left.Center;
                var toRadius = toPoint - right.Center;

                var leftDot = NavigationPoint.Dot(line, fromRadius);
                var rightDot = NavigationPoint.Dot(line, toRadius);
                Assert.True(Math.Abs(leftDot) < 1e-3d, $"Left contact is not tangent: dot={leftDot}, sample={sample}");
                Assert.True(Math.Abs(rightDot) < 1e-3d, $"Right contact is not tangent: dot={rightDot}, sample={sample}");

                // Stepping a hair along the segment away from the contact should move outward or stay on the keep-out rim.
                var tinyStep = line.Normalized() * 0.1d;
                var afterLeft = fromPoint + tinyStep;
                var beforeRight = toPoint - tinyStep;
                Assert.True(
                    afterLeft.DistanceTo(left.Center) >= left.Radius - 1e-3d,
                    $"Segment departs left obstacle inward, sample={sample}");
                Assert.True(
                    beforeRight.DistanceTo(right.Center) >= right.Radius - 1e-3d,
                    $"Segment enters right obstacle inward, sample={sample}");
            }
        }
    }

    [Fact]
    public void Current_world_state_clear_tangent_segments_have_zero_arc_line_angle()
    {
        var obstacles = LoadCurrentWorldStateObstacles();
        var clearTangentSegments = new List<(NavigationObstacle Left, NavigationObstacle Right, NavigationPoint FromPoint, NavigationPoint ToPoint)>();

        for (var leftIndex = 0; leftIndex < obstacles.Count; leftIndex++)
        {
            for (var rightIndex = leftIndex + 1; rightIndex < obstacles.Count; rightIndex++)
            {
                var left = obstacles[leftIndex];
                var right = obstacles[rightIndex];

                foreach (var (fromPoint, toPoint) in InvokeCircleTangents(left, right))
                {
                    if (!IsFixtureSegmentClear(fromPoint, toPoint, obstacles, leftIndex, rightIndex))
                    {
                        continue;
                    }

                    clearTangentSegments.Add((left, right, fromPoint, toPoint));
                }
            }
        }

        Assert.NotEmpty(clearTangentSegments);

        foreach (var (left, right, fromPoint, toPoint) in clearTangentSegments)
        {
            var leftAngle = ComputeSmallestArcLineAngleDegrees(left.Center, fromPoint, toPoint - fromPoint);
            var rightAngle = ComputeSmallestArcLineAngleDegrees(right.Center, toPoint, fromPoint - toPoint);

            Assert.True(
                leftAngle <= 0.1d,
                $"Expected left arc-line angle near 0 degrees for {left.Id} -> {right.Id}, got {leftAngle:0.###} at {fromPoint}.");
            Assert.True(
                rightAngle <= 0.1d,
                $"Expected right arc-line angle near 0 degrees for {left.Id} -> {right.Id}, got {rightAngle:0.###} at {toPoint}.");
        }
    }

    [Fact]
    public void Contact_nodes_inside_other_obstacles_are_rejected()
    {
        var obstacles = new[]
        {
            new NavigationObstacle("left", "test", new NavigationPoint(0d, 0d), 100d),
            new NavigationObstacle("right", "test", new NavigationPoint(120d, 0d), 70d),
        };
        var blockedContact = new NavigationPoint(100d, 0d);

        var nodeIndex = InvokeGetOrAddContactNodeForLine(
            obstacleIndex: 0,
            blockedContact,
            lineDirection: new NavigationPoint(0d, 1d),
            obstacles);

        Assert.Null(nodeIndex);
    }

    [Fact]
    public void Arc_clearance_rejects_overlap_even_when_midpoint_is_clear()
    {
        var host = new NavigationObstacle("host", "test", new NavigationPoint(0d, 0d), 100d);
        var blocker = new NavigationObstacle(
            "blocker",
            "test",
            host.Center + NavigationPoint.FromAngle(0.2d, 88d),
            15d);

        var isClear = InvokeIsArcClear(
            host,
            startAngle: 0d,
            sweepAngle: 1.2d,
            new[] { host, blocker },
            ignoreObstacleIndex: 0);

        Assert.False(isClear);
    }

    [Fact]
    public void Planner_exits_arcs_on_tangent_lines()
    {
        var planner = new CircularPathPlanner();
        var random = new Random(54321);

        for (var sample = 0; sample < 250; sample++)
        {
            var obstacles = BuildRandomObstacles(random, 3 + sample % 2);
            var start = new NavigationPoint(random.NextDouble() * 900d - 450d, random.NextDouble() * 900d - 450d);
            var goal = new NavigationPoint(random.NextDouble() * 900d - 450d, random.NextDouble() * 900d - 450d);

            if (obstacles.Any(o => start.DistanceTo(o.Center) <= o.Radius + 30d || goal.DistanceTo(o.Center) <= o.Radius + 30d))
            {
                sample--;
                continue;
            }

            var result = planner.Plan(start, goal, obstacles);
            if (!result.Succeeded || result.PathPoints.Count < 3)
            {
                continue;
            }

            var failure = FindNonTangentArcExit(result.PathPoints, obstacles);
            Assert.True(failure is null, failure);
        }
    }

    [Fact]
    public void Planner_keeps_arc_entry_direction_continuous_with_incoming_tangent()
    {
        var planner = new CircularPathPlanner();
        var random = new Random(86420);

        for (var sample = 0; sample < 250; sample++)
        {
            var obstacles = BuildRandomObstacles(random, 3 + sample % 2);
            var start = new NavigationPoint(random.NextDouble() * 900d - 450d, random.NextDouble() * 900d - 450d);
            var goal = new NavigationPoint(random.NextDouble() * 900d - 450d, random.NextDouble() * 900d - 450d);

            if (obstacles.Any(o => start.DistanceTo(o.Center) <= o.Radius + 30d || goal.DistanceTo(o.Center) <= o.Radius + 30d))
            {
                sample--;
                continue;
            }

            var result = planner.Plan(start, goal, obstacles);
            if (!result.Succeeded || result.PathPoints.Count < 3)
            {
                continue;
            }

            var failure = FindArcEntryDirectionReversal(result.PathPoints, obstacles);
            Assert.True(failure is null, failure);
        }
    }

    [Fact]
    public void Planner_keeps_arc_exit_direction_continuous_with_following_tangent()
    {
        var planner = new CircularPathPlanner();
        var random = new Random(24680);

        for (var sample = 0; sample < 250; sample++)
        {
            var obstacles = BuildRandomObstacles(random, 3 + sample % 2);
            var start = new NavigationPoint(random.NextDouble() * 900d - 450d, random.NextDouble() * 900d - 450d);
            var goal = new NavigationPoint(random.NextDouble() * 900d - 450d, random.NextDouble() * 900d - 450d);

            if (obstacles.Any(o => start.DistanceTo(o.Center) <= o.Radius + 30d || goal.DistanceTo(o.Center) <= o.Radius + 30d))
            {
                sample--;
                continue;
            }

            var result = planner.Plan(start, goal, obstacles);
            if (!result.Succeeded || result.PathPoints.Count < 3)
            {
                continue;
            }

            var failure = FindArcExitDirectionReversal(result.PathPoints, obstacles);
            Assert.True(failure is null, failure);
        }
    }

    [Fact]
    public void Planner_keeps_arc_entry_direction_continuous_with_overlapping_obstacles()
    {
        var planner = new CircularPathPlanner();
        var random = new Random(97531);

        for (var sample = 0; sample < 200; sample++)
        {
            var obstacles = BuildRandomObstaclesAllowingOverlap(random, 3);
            var start = new NavigationPoint(random.NextDouble() * 800d - 400d, random.NextDouble() * 800d - 400d);
            var goal = new NavigationPoint(random.NextDouble() * 800d - 400d, random.NextDouble() * 800d - 400d);

            if (obstacles.Any(o => start.DistanceTo(o.Center) <= o.Radius + 25d || goal.DistanceTo(o.Center) <= o.Radius + 25d))
            {
                sample--;
                continue;
            }

            var result = planner.Plan(start, goal, obstacles);
            if (!result.Succeeded || result.PathPoints.Count < 3)
            {
                continue;
            }

            var failure = FindArcEntryDirectionReversal(result.PathPoints, obstacles);
            Assert.True(failure is null, failure);
        }
    }

    [Fact]
    public void Planner_keeps_arc_exit_direction_continuous_with_overlapping_obstacles()
    {
        var planner = new CircularPathPlanner();
        var random = new Random(13579);

        for (var sample = 0; sample < 200; sample++)
        {
            var obstacles = BuildRandomObstaclesAllowingOverlap(random, 3);
            var start = new NavigationPoint(random.NextDouble() * 800d - 400d, random.NextDouble() * 800d - 400d);
            var goal = new NavigationPoint(random.NextDouble() * 800d - 400d, random.NextDouble() * 800d - 400d);

            if (obstacles.Any(o => start.DistanceTo(o.Center) <= o.Radius + 25d || goal.DistanceTo(o.Center) <= o.Radius + 25d))
            {
                sample--;
                continue;
            }

            var result = planner.Plan(start, goal, obstacles);
            if (!result.Succeeded || result.PathPoints.Count < 3)
            {
                continue;
            }

            var failure = FindArcExitDirectionReversal(result.PathPoints, obstacles);
            Assert.True(failure is null, failure);
        }
    }

    [Fact]
    public void Planner_finds_route_around_fixture_meteoroid_barrier()
    {
        var planner = new CircularPathPlanner();
        var obstacles = LoadFixtureObstacles();
        var start = new NavigationPoint(-338d, 276d);
        var goal = new NavigationPoint(527.5d, -457.2d);

        var result = planner.Plan(start, goal, obstacles);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.True(result.PathPoints.Count > 2);
        Assert.NotEmpty(result.SearchEdges);
        AssertClose(start, result.PathPoints[0]);
        AssertClose(goal, result.PathPoints[^1]);
        AssertPathMaintainsClearance(result.PathPoints, obstacles, 0.75d);
    }

    [Fact]
    public void Path_follower_picks_lookahead_target_ahead_of_ship()
    {
        var planner = new CircularPathPlanner();
        var pathFollower = new PathFollower();
        var obstacles = LoadFixtureObstacles();
        var start = new NavigationPoint(-338d, 276d);
        var goal = new NavigationPoint(527.5d, -457.2d);
        var result = planner.Plan(start, goal, obstacles);

        Assert.True(result.Succeeded, result.FailureReason);

        const double lookaheadDistance = 72d;
        const double minTargetDistance = 30d;
        const double arrivalThreshold = 14d;
        var follow = pathFollower.Follow(start, result.PathPoints, lookaheadDistance, minTargetDistance, arrivalThreshold);

        Assert.False(follow.GoalReached);
        Assert.True(start.DistanceTo(follow.Target) >= minTargetDistance - 0.5d);
        Assert.True(follow.RemainingDistance > arrivalThreshold);
        Assert.True(follow.Target.DistanceTo(goal) > arrivalThreshold);
    }

    private static IReadOnlyList<NavigationObstacle> LoadFixtureObstacles()
    {
        return CircularObstacleExtractor.Extract(LoadFixtureUnits(), clusterId: 0, shipRadius: 12d, clearanceMargin: 8d);
    }

    private static IReadOnlyList<NavigationObstacle> LoadCurrentWorldStateObstacles()
    {
        return CircularObstacleExtractor.Extract(LoadFixtureUnits("world-state.wss-www-flattiverse-com-galaxies-0-api-b.fe66663304e80502.json"), clusterId: 0, shipRadius: 12d, clearanceMargin: 8d);
    }

    private static List<UnitSnapshotDto> LoadFixtureUnits()
    {
        return LoadFixtureUnits("world-state.wss-www-flattiverse-com-galaxies-0-api-b.fe66663304e80502.json");
    }

    private static List<UnitSnapshotDto> LoadFixtureUnits(string fixtureFileName)
    {
        var fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            fixtureFileName);

        using var stream = File.OpenRead(fixturePath);
        var worldState = JsonSerializer.Deserialize<PersistedWorldState>(stream, JsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize the persisted fixture.");

        return worldState.Scopes
            .Where(scope => scope.ClusterId == 0)
            .SelectMany(scope => scope.StaticUnits ?? [])
            .ToList();
    }

    private static void AssertPathMaintainsClearance(
        IReadOnlyList<NavigationPoint> path,
        IReadOnlyList<NavigationObstacle> obstacles,
        double tolerance)
    {
        for (var segmentIndex = 0; segmentIndex < path.Count - 1; segmentIndex++)
        {
            var start = path[segmentIndex];
            var end = path[segmentIndex + 1];
            foreach (var obstacle in obstacles)
            {
                var distance = NavigationMath.DistanceToSegment(obstacle.Center, start, end, out _);
                Assert.True(
                    distance >= obstacle.Radius - tolerance,
                    $"Segment {segmentIndex} from {start} to {end} violates clearance for obstacle {obstacle.Id} ({obstacle.Kind}). Distance={distance:0.###}, radius={obstacle.Radius:0.###}");
            }
        }
    }

    private static void AssertClose(NavigationPoint expected, NavigationPoint actual)
    {
        Assert.True(expected.DistanceTo(actual) <= 0.01d, $"Expected {expected}, got {actual}.");
    }

    private static List<(NavigationPoint FromPoint, NavigationPoint ToPoint)> InvokeCircleTangents(
        NavigationObstacle left,
        NavigationObstacle right)
    {
        var method = typeof(CircularPathPlanner).GetMethod(
            "BuildTangents",
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            types: [typeof(NavigationObstacle), typeof(int), typeof(NavigationObstacle), typeof(int)],
            modifiers: null);
        Assert.NotNull(method);

        var value = method!.Invoke(null, [left, 0, right, 1]);
        Assert.NotNull(value);

        var enumerable = Assert.IsAssignableFrom<System.Collections.IEnumerable>(value);
        var tangents = new List<(NavigationPoint FromPoint, NavigationPoint ToPoint)>();

        foreach (var entry in enumerable)
        {
            Assert.NotNull(entry);
            var entryType = entry!.GetType();
            var from = (NavigationPoint?)entryType.GetProperty("FromPoint")?.GetValue(entry);
            var to = (NavigationPoint?)entryType.GetProperty("ToPoint")?.GetValue(entry);
            Assert.True(from.HasValue && to.HasValue, "Expected tangent entry to expose FromPoint/ToPoint.");
            tangents.Add((from!.Value, to!.Value));
        }

        return tangents;
    }

    private static int? InvokeGetOrAddContactNodeForLine(
        int obstacleIndex,
        NavigationPoint point,
        NavigationPoint lineDirection,
        IReadOnlyList<NavigationObstacle> obstacles)
    {
        var graphNodeType = typeof(CircularPathPlanner).GetNestedType("GraphNode", BindingFlags.NonPublic);
        var graphEdgeType = typeof(CircularPathPlanner).GetNestedType("GraphEdge", BindingFlags.NonPublic);
        Assert.NotNull(graphNodeType);
        Assert.NotNull(graphEdgeType);

        var nodeListType = typeof(List<>).MakeGenericType(graphNodeType!);
        var edgeListType = typeof(List<>).MakeGenericType(graphEdgeType!);
        var adjacencyListType = typeof(List<>).MakeGenericType(edgeListType);

        var method = typeof(CircularPathPlanner).GetMethod(
            "GetOrAddContactNodeForLine",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method!.Invoke(
            null,
            [
                Activator.CreateInstance(nodeListType)!,
                Activator.CreateInstance(adjacencyListType)!,
                new Dictionary<string, int>(StringComparer.Ordinal),
                new Dictionary<int, List<int>>(),
                obstacleIndex,
                point,
                lineDirection,
                obstacles,
            ]);

        return result as int?;
    }

    private static bool InvokeIsArcClear(
        NavigationObstacle obstacle,
        double startAngle,
        double sweepAngle,
        IReadOnlyList<NavigationObstacle> obstacles,
        int ignoreObstacleIndex)
    {
        var method = typeof(CircularPathPlanner).GetMethod(
            "IsArcClear",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method!.Invoke(
            null,
            [obstacle, startAngle, sweepAngle, obstacles, ignoreObstacleIndex]);

        return Assert.IsType<bool>(result);
    }

    private static bool IsFixtureSegmentClear(
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

    private static double ComputeSmallestArcLineAngleDegrees(
        NavigationPoint obstacleCenter,
        NavigationPoint touchPoint,
        NavigationPoint lineDirection)
    {
        var radiusDirection = (touchPoint - obstacleCenter).Normalized();
        var arcTangentDirection = NavigationPoint.PerpendicularLeft(radiusDirection).Normalized();
        var segmentDirection = lineDirection.Normalized();

        var forwardAngle = ComputeAngleDegrees(segmentDirection, arcTangentDirection);
        var reverseAngle = ComputeAngleDegrees(segmentDirection, arcTangentDirection * -1d);
        return Math.Min(forwardAngle, reverseAngle);
    }

    private static double ComputeAngleDegrees(NavigationPoint left, NavigationPoint right)
    {
        var cosine = Math.Clamp(NavigationPoint.Dot(left.Normalized(), right.Normalized()), -1d, 1d);
        return Math.Acos(cosine) * 180d / Math.PI;
    }

    private static List<NavigationObstacle> BuildRandomObstacles(Random random, int count)
    {
        var obstacles = new List<NavigationObstacle>();
        while (obstacles.Count < count)
        {
            var candidate = new NavigationObstacle(
                $"obs-{obstacles.Count}",
                "test",
                new NavigationPoint(random.NextDouble() * 800d - 400d, random.NextDouble() * 800d - 400d),
                50d + random.NextDouble() * 110d);

            if (obstacles.Any(existing => existing.Center.DistanceTo(candidate.Center) <= existing.Radius + candidate.Radius + 80d))
            {
                continue;
            }

            obstacles.Add(candidate);
        }

        return obstacles;
    }

    private static List<NavigationObstacle> BuildRandomObstaclesAllowingOverlap(Random random, int count)
    {
        var obstacles = new List<NavigationObstacle>();
        while (obstacles.Count < count)
        {
            var candidate = new NavigationObstacle(
                $"ov-{obstacles.Count}",
                "test",
                new NavigationPoint(random.NextDouble() * 500d - 250d, random.NextDouble() * 500d - 250d),
                70d + random.NextDouble() * 120d);

            // Keep centers distinct, but intentionally allow overlapping inflated disks.
            if (obstacles.Any(existing => existing.Center.DistanceTo(candidate.Center) <= 40d))
            {
                continue;
            }

            obstacles.Add(candidate);
        }

        return obstacles;
    }

    private static string? FindNonTangentArcExit(
        IReadOnlyList<NavigationPoint> path,
        IReadOnlyList<NavigationObstacle> obstacles)
    {
        const double onBoundaryTolerance = 0.75d;
        const double tangentDotTolerance = 0.2d;

        for (var i = 1; i < path.Count - 1; i++)
        {
            var previous = path[i - 1];
            var current = path[i];
            var next = path[i + 1];

            for (var obstacleIndex = 0; obstacleIndex < obstacles.Count; obstacleIndex++)
            {
                var obstacle = obstacles[obstacleIndex];
                var previousOnBoundary = Math.Abs(previous.DistanceTo(obstacle.Center) - obstacle.Radius) <= onBoundaryTolerance;
                var currentOnBoundary = Math.Abs(current.DistanceTo(obstacle.Center) - obstacle.Radius) <= onBoundaryTolerance;
                var nextOnBoundary = Math.Abs(next.DistanceTo(obstacle.Center) - obstacle.Radius) <= onBoundaryTolerance;
                if (!previousOnBoundary || !currentOnBoundary || nextOnBoundary)
                {
                    continue;
                }

                var exitDot = NavigationPoint.Dot(next - current, current - obstacle.Center);
                if (Math.Abs(exitDot) > tangentDotTolerance)
                {
                    return $"Sampled path exits obstacle {obstacleIndex} non-tangentially: dot={exitDot:0.###}, previous={previous}, current={current}, next={next}";
                }
            }
        }

        return null;
    }

    private static string? FindArcExitDirectionReversal(
        IReadOnlyList<NavigationPoint> path,
        IReadOnlyList<NavigationObstacle> obstacles)
    {
        const double onBoundaryTolerance = 0.75d;

        for (var i = 1; i < path.Count - 1; i++)
        {
            var previous = path[i - 1];
            var current = path[i];
            var next = path[i + 1];

            if ((current - previous).Length <= NavigationMath.Epsilon || (next - current).Length <= NavigationMath.Epsilon)
            {
                continue;
            }

            for (var obstacleIndex = 0; obstacleIndex < obstacles.Count; obstacleIndex++)
            {
                var obstacle = obstacles[obstacleIndex];
                var previousOnBoundary = Math.Abs(previous.DistanceTo(obstacle.Center) - obstacle.Radius) <= onBoundaryTolerance;
                var currentOnBoundary = Math.Abs(current.DistanceTo(obstacle.Center) - obstacle.Radius) <= onBoundaryTolerance;
                var nextOnBoundary = Math.Abs(next.DistanceTo(obstacle.Center) - obstacle.Radius) <= onBoundaryTolerance;
                if (!previousOnBoundary || !currentOnBoundary || nextOnBoundary)
                {
                    continue;
                }

                var alongArc = (current - previous).Normalized();
                var alongLine = (next - current).Normalized();
                var continuity = NavigationPoint.Dot(alongArc, alongLine);
                if (continuity < 0.5d)
                {
                    return $"Arc exit reverses direction at obstacle {obstacleIndex}: dot={continuity:0.###}, previous={previous}, current={current}, next={next}";
                }
            }
        }

        return null;
    }

    private static string? FindArcEntryDirectionReversal(
        IReadOnlyList<NavigationPoint> path,
        IReadOnlyList<NavigationObstacle> obstacles)
    {
        const double onBoundaryTolerance = 0.75d;

        for (var i = 1; i < path.Count - 1; i++)
        {
            var previous = path[i - 1];
            var current = path[i];
            var next = path[i + 1];

            if ((current - previous).Length <= NavigationMath.Epsilon || (next - current).Length <= NavigationMath.Epsilon)
            {
                continue;
            }

            for (var obstacleIndex = 0; obstacleIndex < obstacles.Count; obstacleIndex++)
            {
                var obstacle = obstacles[obstacleIndex];
                var previousOnBoundary = Math.Abs(previous.DistanceTo(obstacle.Center) - obstacle.Radius) <= onBoundaryTolerance;
                var currentOnBoundary = Math.Abs(current.DistanceTo(obstacle.Center) - obstacle.Radius) <= onBoundaryTolerance;
                var nextOnBoundary = Math.Abs(next.DistanceTo(obstacle.Center) - obstacle.Radius) <= onBoundaryTolerance;
                if (previousOnBoundary || !currentOnBoundary || !nextOnBoundary)
                {
                    continue;
                }

                var alongLine = (current - previous).Normalized();
                var alongArc = (next - current).Normalized();
                var continuity = NavigationPoint.Dot(alongLine, alongArc);
                if (continuity < 0.5d)
                {
                    return $"Arc entry reverses direction at obstacle {obstacleIndex}: dot={continuity:0.###}, previous={previous}, current={current}, next={next}";
                }
            }
        }

        return null;
    }

    private sealed class PersistedWorldState
    {
        public List<PersistedScope> Scopes { get; set; } = [];
    }

    private sealed class PersistedScope
    {
        public string GalaxyId { get; set; } = "";

        public int ClusterId { get; set; }

        public List<UnitSnapshotDto>? StaticUnits { get; set; }
    }
}

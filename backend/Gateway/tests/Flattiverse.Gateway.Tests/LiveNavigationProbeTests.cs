using Flattiverse.Connector;
using Flattiverse.Connector.Events;
using Flattiverse.Connector.GalaxyHierarchy;
using Flattiverse.Connector.Units;
using Flattiverse.Gateway.Protocol.Dtos;
using Flattiverse.Gateway.Services;
using Flattiverse.Gateway.Services.Navigation;
using Xunit.Abstractions;

namespace Flattiverse.Gateway.Tests;

public sealed class LiveNavigationProbeTests
{
    private const string DefaultGalaxyUrl = "wss://www.flattiverse.com/galaxies/0/api";
    private const string DefaultTeamName = "";
    private const string DefaultPersistenceRelativePath = "backend/Gateway/Flattiverse.Gateway.Host/data/world-state.wss-www-flattiverse-com-galaxies-0-api-b.fe66663304e80502.json";
    private const string DefaultPersistenceBinRelativePath = "backend/Gateway/Flattiverse.Gateway.Host/bin/Debug/net8.0/data/world-state.wss-www-flattiverse-com-galaxies-0-api-b.fe66663304e80502.json";

    private static readonly NavigationPoint Destination = new(-378.9d, -274.8d);
    private static readonly TimeSpan SpawnTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan NavigationTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(250);

    private static readonly string LogFilePath = Path.Combine(AppContext.BaseDirectory, "live-probe-log.txt");

    private readonly ITestOutputHelper _output;
    private StreamWriter? _fileLog;

    public LiveNavigationProbeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private void Log(string message)
    {
        _output.WriteLine(message);
        _fileLog?.WriteLine(message);
        _fileLog?.Flush();
    }

    [Fact]
    public async Task Live_server_spawn_and_navigate_logs_planned_and_actual_paths()
    {
        LoadDotEnv();

        var galaxyUrl = Environment.GetEnvironmentVariable("FV_LIVE_GALAXY_URL") ?? DefaultGalaxyUrl;
        var teamName = Environment.GetEnvironmentVariable("FV_LIVE_TEAM") ?? DefaultTeamName;
        var configuredKeys = new[]
        {
            Environment.GetEnvironmentVariable("FV_LIVE_API_KEY_1"),
            Environment.GetEnvironmentVariable("FV_LIVE_API_KEY_2"),
        }.Where(key => !string.IsNullOrWhiteSpace(key)).Distinct(StringComparer.Ordinal).ToArray();

        if (configuredKeys.Length == 0)
        {
            throw new InvalidOperationException("No API keys configured for live navigation probe.");
        }

        _fileLog = new StreamWriter(LogFilePath, append: false);
        _fileLog.WriteLine($"[LiveProbe] Run started at {DateTime.UtcNow:O}");
        Log($"[LiveProbe] Log file: {LogFilePath}");
        Log($"[LiveProbe] Connecting to {galaxyUrl} (preferred team: {teamName})");

        Galaxy galaxy = await ConnectWithFallbacks(galaxyUrl, teamName, configuredKeys);
        try
        {
            var spawnedShip = await SpawnRandomShip(galaxy);
            Log(
                $"[LiveProbe] Spawned ship '{spawnedShip.Name}' id={spawnedShip.Id} at ({spawnedShip.Position.X:0.###}, {spawnedShip.Position.Y:0.###}) in cluster {spawnedShip.Cluster.Id}:{spawnedShip.Cluster.Name}");

            var start = new NavigationPoint(spawnedShip.Position.X, spawnedShip.Position.Y);
            var planner = new CircularPathPlanner();
            var follower = new PathFollower();
            var currentClusterId = spawnedShip.Cluster.Id;
            var mappingGalaxyId = $"{galaxyUrl}|{galaxy.Name}";
            var persistencePath = ResolvePersistencePath();
            MappingService.ConfigurePersistence(persistencePath);
            var mapping = new MappingService(() => new MappingService.MappingScopeContext(
                mappingGalaxyId,
                currentClusterId,
                galaxy.Player?.Id));
            var maneuvering = new ManeuveringService(mapping);
            Log($"[LiveProbe] MappingService persistence file: {persistencePath}");
            Log($"[LiveProbe] MappingService scope galaxyId={mappingGalaxyId} clusterId={currentClusterId}");

            using var pumpCts = new CancellationTokenSource();
            var pumpTask = Task.Run(async () =>
            {
                while (!pumpCts.Token.IsCancellationRequested)
                {
                    FlattiverseEvent @event;
                    try
                    {
                        @event = await galaxy.NextEvent();
                    }
                    catch
                    {
                        break;
                    }

                    maneuvering.Handle(@event);
                    mapping.Handle(@event);

                    if (@event is ConnectionTerminatedEvent terminated)
                    {
                        Log($"[LiveProbe] Connection terminated: {terminated.Message}");
                        break;
                    }
                }
            });

            maneuvering.TrackShip(spawnedShip);
            var plannedPath = BuildPlannedPath(spawnedShip, planner, mapping, start, Destination);
            LogPath("[LiveProbe] Planned path", plannedPath);

            var actualSamples = new List<NavigationPoint>();
            var reachedGoal = false;
            var crashed = false;
            var startedAt = DateTime.UtcNow;
            var arrivalThreshold = Math.Clamp(spawnedShip.Size * 1.8d, 10d, 34d);
            const double maxPathDeviation = 40d;
            var stationKeepingDuration = TimeSpan.FromSeconds(15);
            DateTime? stationKeepingSince = null;
            var maxDriftFromGoal = 0d;

            while (DateTime.UtcNow - startedAt < NavigationTimeout)
            {
                var current = new NavigationPoint(spawnedShip.Position.X, spawnedShip.Position.Y);
                actualSamples.Add(current);

                if (!spawnedShip.Active || !spawnedShip.Alive)
                {
                    crashed = true;
                    break;
                }
                currentClusterId = spawnedShip.Cluster.Id;

                var distanceToGoal = current.DistanceTo(Destination);
                if (distanceToGoal <= arrivalThreshold && !reachedGoal)
                {
                    reachedGoal = true;
                    stationKeepingSince = DateTime.UtcNow;
                    Log($"[LiveProbe] Reached goal at sample #{actualSamples.Count}, distance={distanceToGoal:F2}. Starting station-keeping phase.");
                }

                if (reachedGoal)
                {
                    maxDriftFromGoal = Math.Max(maxDriftFromGoal, distanceToGoal);

                    if (DateTime.UtcNow - stationKeepingSince!.Value >= stationKeepingDuration)
                    {
                        Log($"[LiveProbe] Station-keeping complete. maxDrift={maxDriftFromGoal:F2}");
                        break;
                    }

                    // During station-keeping, target the destination directly
                    maneuvering.SetNavigationTarget(
                        spawnedShip,
                        (float)Destination.X,
                        (float)Destination.Y,
                        thrustPercentage: 1f,
                        resetController: false,
                        remainingPath: null);

                    await Task.Delay(SampleInterval);
                    continue;
                }

                // Replan when the ship has drifted too far from the planned path
                if (plannedPath.Count >= 2)
                {
                    var deviation = DistanceToPolyline(current, plannedPath);
                    if (deviation > maxPathDeviation)
                    {
                        Log($"[LiveProbe] Off-path replan: deviation={deviation:F1}");
                        var replanned = BuildPlannedPath(spawnedShip, planner, mapping, current, Destination);
                        if (replanned.Count >= 2)
                        {
                            plannedPath = replanned;
                        }
                    }
                }

                var follow = follower.Follow(
                    current,
                    plannedPath,
                    lookaheadDistance: Math.Clamp(spawnedShip.Size * 7d, 36d, 132d),
                    minTargetDistance: Math.Clamp(spawnedShip.Size * 3.2d, 18d, 56d),
                    arrivalThreshold: arrivalThreshold);

                // Build remaining path from lookahead onward for curvature-aware steering
                var remainingPath = BuildRemainingPathFromProgress(
                    follow.Target, follow.ProgressDistance, plannedPath);

                maneuvering.SetNavigationTarget(
                    spawnedShip,
                    (float)follow.Target.X,
                    (float)follow.Target.Y,
                    thrustPercentage: 1f,
                    resetController: false,
                    remainingPath: remainingPath);

                // Log trajectory every 10 samples for debugging, including predicted trajectory
                if (actualSamples.Count % 10 == 0)
                {
                    var vel = spawnedShip.Movement;
                    var speed = Math.Sqrt(vel.X * vel.X + vel.Y * vel.Y);
                    var shipX = (double)spawnedShip.Position.X;
                    var shipY = (double)spawnedShip.Position.Y;
                    var velX = (double)vel.X;
                    var velY = (double)vel.Y;
                    var engineMax = (double)spawnedShip.Engine.Maximum;

                    // Extract gravity sources
                    var units = mapping.BuildUnitSnapshots();
                    var gravitySources = new List<GravitySimulator.GravitySource>();
                    foreach (var u in units)
                    {
                        if (u.Gravity > 0f)
                            gravitySources.Add(new GravitySimulator.GravitySource(u.X, u.Y, u.Gravity));
                    }

                    // Compute engine vector
                    var (engineX, engineY) = TrajectoryAligner.ComputeEngineVector(
                        shipX, shipY, velX, velY,
                        follow.Target.X, follow.Target.Y,
                        gravitySources, engineMax,
                        thrustPercentage: 1f, speedLimit: 6.0d,
                        remainingPath: remainingPath);
                    var engineMag = Math.Sqrt(engineX * engineX + engineY * engineY);

                    // Compute gravity at current position
                    var (gx, gy) = GravitySimulator.ComputeGravityAcceleration(shipX, shipY, gravitySources);

                    // Compute path deviation
                    var pathDev = remainingPath.Count >= 2 ? DistanceToTuplePath(current, remainingPath) : 0d;

                    Log(
                        $"[Traj] #{actualSamples.Count:000} pos=({current.X:F1},{current.Y:F1}) vel=({velX:F2},{velY:F2}) spd={speed:F2} " +
                        $"target=({follow.Target.X:F1},{follow.Target.Y:F1}) distToTarget={current.DistanceTo(follow.Target):F1} " +
                        $"engine=({engineX:F3},{engineY:F3}) mag={engineMag:F3}/{engineMax:F3} " +
                        $"grav=({gx:F4},{gy:F4}) pathDev={pathDev:F1} remainPts={remainingPath.Count}");

                    // Simulate 200-tick predicted trajectory and log every 20th tick
                    var predicted = GravitySimulator.SimulateTrajectory(
                        shipX, shipY, velX, velY,
                        engineX, engineY,
                        gravitySources, 200, 6.0d);

                    for (var t = 0; t < predicted.Count; t += 20)
                    {
                        var pt = predicted[t];
                        Log($"[Pred] #{actualSamples.Count:000} t={t:000} pos=({pt.X:F1},{pt.Y:F1})");
                    }

                    // Log predicted final position
                    var final20 = predicted[^1];
                    Log($"[Pred] #{actualSamples.Count:000} t=200 pos=({final20.X:F1},{final20.Y:F1})");
                }

                await Task.Delay(SampleInterval);
            }

            maneuvering.ClearNavigationTarget(spawnedShip.Id);
            pumpCts.Cancel();
            galaxy.Dispose();
            try
            {
                await pumpTask;
            }
            catch
            {
                // Ignore best-effort pump shutdown errors in probe test.
            }

            LogPath("[LiveProbe] Actual path samples", actualSamples);
            var finalPoint = actualSamples.Count > 0 ? actualSamples[^1] : start;
            var finalDistance = finalPoint.DistanceTo(Destination);
            Log(
                $"[LiveProbe] Result: reachedGoal={reachedGoal}, crashed={crashed}, samples={actualSamples.Count}, finalDistance={finalDistance:0.###}, maxDrift={maxDriftFromGoal:0.###}");
            _fileLog?.Dispose();
            _fileLog = null;

            Assert.NotEmpty(actualSamples);
            Assert.True(reachedGoal, $"Ship did not reach destination within {NavigationTimeout.TotalMinutes:0.#} min. crashed={crashed}, finalDistance={finalDistance:0.###}");
            Assert.True(maxDriftFromGoal <= arrivalThreshold * 2d, $"Ship drifted too far during station-keeping: maxDrift={maxDriftFromGoal:0.###}, limit={arrivalThreshold * 2d:0.###}");
        }
        finally
        {
            galaxy.Dispose();
        }
    }

    private async Task<Galaxy> ConnectWithFallbacks(string galaxyUrl, string preferredTeam, IReadOnlyList<string> apiKeys)
    {
        var attempts = new List<string>();
        var teamCandidates = string.IsNullOrWhiteSpace(preferredTeam)
            ? new string?[] { null }
            : new string?[] { preferredTeam, null };

        foreach (var key in apiKeys)
        {
            foreach (var team in teamCandidates)
            {
                try
                {
                    var galaxy = await Galaxy.Connect(galaxyUrl, key, team);
                    Log($"[LiveProbe] Connected using key={MaskKey(key)} team={(team ?? "<auto>")}");
                    return galaxy;
                }
                catch (Exception exception)
                {
                    attempts.Add($"key={MaskKey(key)}, team={(team ?? "<auto>")} => {exception.GetType().Name}: {exception.Message}");
                }
            }
        }

        var formatted = string.Join(Environment.NewLine, attempts);
        throw new InvalidOperationException($"Failed to connect with provided keys/team combinations.{Environment.NewLine}{formatted}");
    }

    private async Task<ClassicShipControllable> SpawnRandomShip(Galaxy galaxy)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var shipName = $"NavProbe-{suffix}";
        var ship = await galaxy.CreateClassicShip(shipName);
        await ship.Continue();

        var deadline = DateTime.UtcNow + SpawnTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (ship.Active && ship.Alive)
            {
                return ship;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"Ship '{shipName}' did not become alive within {SpawnTimeout.TotalSeconds:0.#}s.");
    }

    private static IReadOnlyList<NavigationPoint> BuildPlannedPath(
        ClassicShipControllable ship,
        CircularPathPlanner planner,
        MappingService mappingService,
        NavigationPoint start,
        NavigationPoint destination)
    {
        var unitSnapshotsById = mappingService.BuildUnitSnapshots()
            .ToDictionary(unit => unit.UnitId, StringComparer.Ordinal);

        // Merge latest connector-visible units so nearby dynamic changes can complement persisted static data.
        foreach (var unit in ship.Cluster.Units)
        {
            var mapped = MappingService.MapUnit(unit, ship.Cluster.Id);
            if (mapped is null)
            {
                continue;
            }

            mapped.IsStatic = unit is SteadyUnit;
            mapped.IsSeen = true;
            unitSnapshotsById[mapped.UnitId] = mapped;
        }

        var unitSnapshots = unitSnapshotsById.Values.ToList();
        var obstacles = CircularObstacleExtractor.Extract(
            units: unitSnapshots,
            clusterId: ship.Cluster.Id,
            shipRadius: ship.Size,
            clearanceMargin: 8d,
            destinationUnitId: null,
            shipPosition: start);

        var plan = planner.Plan(start, destination, obstacles);
        if (plan.Succeeded && plan.PathPoints.Count > 0)
        {
            return plan.PathPoints;
        }

        return new[] { start, destination };
    }

    private void LogPath(string label, IReadOnlyList<NavigationPoint> path)
    {
        Log($"{label}: points={path.Count}");
        for (var i = 0; i < path.Count; i++)
        {
            Log($"  [{i:000}] ({path[i].X:0.###}, {path[i].Y:0.###})");
        }
    }

    private static string MaskKey(string key)
    {
        if (key.Length <= 8)
        {
            return "****";
        }

        return $"{key[..4]}...{key[^4..]}";
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

    private static IReadOnlyList<(double X, double Y)> BuildRemainingPathFromProgress(
        NavigationPoint lookahead,
        double progressDistance,
        IReadOnlyList<NavigationPoint> fullPath)
    {
        if (fullPath.Count < 2)
            return Array.Empty<(double, double)>();

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

        var result = new List<(double, double)>();
        for (var i = startIndex; i < fullPath.Count; i++)
        {
            result.Add((fullPath[i].X, fullPath[i].Y));
        }

        return result;
    }

    /// <summary>
    /// Loads a <c>.env</c> file from the nearest ancestor directory that contains one,
    /// starting from <see cref="AppContext.BaseDirectory"/>.
    /// Variables that are already set in the process environment are not overwritten.
    /// </summary>
    private static void LoadDotEnv()
    {
        var current = Path.GetFullPath(AppContext.BaseDirectory);
        for (var depth = 0; depth < 12; depth++)
        {
            var candidate = Path.Combine(current, ".env");
            if (File.Exists(candidate))
            {
                foreach (var line in File.ReadLines(candidate))
                {
                    var trimmed = line.Trim();
                    if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                        continue;

                    var eq = trimmed.IndexOf('=');
                    if (eq <= 0)
                        continue;

                    var key = trimmed[..eq].Trim();
                    var value = trimmed[(eq + 1)..].Trim();
                    if (Environment.GetEnvironmentVariable(key) is null)
                        Environment.SetEnvironmentVariable(key, value);
                }
                return;
            }

            var parent = Directory.GetParent(current);
            if (parent is null)
                break;
            current = parent.FullName;
        }
    }

    private static string ResolvePersistencePath()
    {
        var configured = Environment.GetEnvironmentVariable("FV_LIVE_WORLD_STATE_FILE");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }

        foreach (var candidate in EnumeratePersistenceCandidates())
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            "Could not find world-state persistence file. Set FV_LIVE_WORLD_STATE_FILE to an existing file path.",
            DefaultPersistenceRelativePath);
    }

    private static IEnumerable<string> EnumeratePersistenceCandidates()
    {
        var roots = new[]
        {
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory,
        };

        foreach (var root in roots)
        {
            var current = Path.GetFullPath(root);
            for (var depth = 0; depth < 12; depth++)
            {
                yield return Path.Combine(current, DefaultPersistenceRelativePath);
                yield return Path.Combine(current, DefaultPersistenceBinRelativePath);

                var parent = Directory.GetParent(current);
                if (parent is null)
                {
                    break;
                }

                current = parent.FullName;
            }
        }
    }
}

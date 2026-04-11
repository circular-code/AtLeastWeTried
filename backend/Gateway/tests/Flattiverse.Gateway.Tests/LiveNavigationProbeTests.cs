using Flattiverse.Connector;
using Flattiverse.Connector.Events;
using Flattiverse.Connector.GalaxyHierarchy;
using Flattiverse.Connector.Units;
using Flattiverse.Gateway.Protocol.Dtos;
using Flattiverse.Gateway.Services;
using Flattiverse.Gateway.Services.Navigation;
using Xunit.Abstractions;
using static Flattiverse.Gateway.Services.GravitySimulator;

namespace Flattiverse.Gateway.Tests;

public sealed class LiveNavigationProbeTests
{
    private const string DefaultGalaxyUrl = "wss://www.flattiverse.com/galaxies/2/api";
    private const string DefaultTeamName = "Lime";
    private const string DefaultPersistenceRelativePath = "backend/Gateway/Flattiverse.Gateway.Host/data/world-state.wss-www-flattiverse-com-galaxies-2-api-s.7b6142df1c765692.json";
    private const string DefaultPersistenceBinRelativePath = "backend/Gateway/Flattiverse.Gateway.Host/bin/Debug/net8.0/data/world-state.wss-www-flattiverse-com-galaxies-2-api-s.7b6142df1c765692.json";
    private const string ApiKey1 = "e15a4e7276dfed7355e201d1119b23d00486116e66237e191c9683b7e8896f5b";
    private const string ApiKey2 = "a8f86eb995f29230c55521d95dd182cd6c0325fa864336a42b8d143c391e2940";

    private static readonly NavigationPoint Destination = new(1569.3d, 10.3d);
    private static readonly TimeSpan SpawnTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan NavigationTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(250);

    private readonly ITestOutputHelper _output;
    private StreamWriter? _fileLog;

    public LiveNavigationProbeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private void Log(string message)
    {
        try { _output.WriteLine(message); } catch { /* xUnit output helper may throw from background threads */ }
        _fileLog?.WriteLine(message);
        _fileLog?.Flush();
    }

    [Fact]
    public async Task Live_server_spawn_and_navigate_logs_planned_and_actual_paths()
    {
        var logDir = Path.Combine(AppContext.BaseDirectory, "probe-logs");
        Directory.CreateDirectory(logDir);
        var logFile = Path.Combine(logDir, $"nav-probe-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");
        _fileLog = new StreamWriter(logFile, append: false) { AutoFlush = true };
        Log($"[LiveProbe] Log file: {logFile}");

        try
        {
        await RunProbeCore();
        }
        finally
        {
            _fileLog?.Dispose();
            _fileLog = null;
            Log($"[LiveProbe] Full log written to: {logFile}");
        }
    }

    private async Task RunProbeCore()
    {
        var galaxyUrl = Environment.GetEnvironmentVariable("FV_LIVE_GALAXY_URL") ?? DefaultGalaxyUrl;
        var teamName = Environment.GetEnvironmentVariable("FV_LIVE_TEAM") ?? DefaultTeamName;
        var useMaxDisclosure = string.Equals(Environment.GetEnvironmentVariable("FV_LIVE_USE_MAX_DISCLOSURE"), "1", StringComparison.Ordinal);
        var runtimeDisclosure = useMaxDisclosure ? BuildMaxRuntimeDisclosure() : null;
        var buildDisclosure = useMaxDisclosure ? BuildMaxBuildDisclosure() : null;
        var configuredKeys = new[]
        {
            Environment.GetEnvironmentVariable("FV_LIVE_API_KEY_1") ?? ApiKey1,
            Environment.GetEnvironmentVariable("FV_LIVE_API_KEY_2") ?? ApiKey2,
        }.Where(key => !string.IsNullOrWhiteSpace(key)).Distinct(StringComparer.Ordinal).ToArray();

        if (configuredKeys.Length == 0)
        {
            throw new InvalidOperationException("No API keys configured for live navigation probe.");
        }

        Log($"[LiveProbe] Connecting to {galaxyUrl} (preferred team: {teamName})");
        Log($"[LiveProbe] Max disclosure enabled: {useMaxDisclosure}");

        Galaxy galaxy = await ConnectWithFallbacks(galaxyUrl, teamName, configuredKeys, runtimeDisclosure, buildDisclosure);
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
                galaxy.Player.Id));
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

            while (DateTime.UtcNow - startedAt < NavigationTimeout)
            {
                var current = new NavigationPoint(spawnedShip.Position.X, spawnedShip.Position.Y);
                actualSamples.Add(current);

                if (!spawnedShip.Active || !spawnedShip.Alive)
                {
                    crashed = true;
                    Log($"[LiveProbe] CRASHED at ({current.X:0.###}, {current.Y:0.###}) after {actualSamples.Count} samples");
                    break;
                }
                currentClusterId = spawnedShip.Cluster.Id;

                var distanceToGoal = current.DistanceTo(Destination);
                if (distanceToGoal <= arrivalThreshold)
                {
                    var aVx = (double)spawnedShip.Movement.X;
                    var aVy = (double)spawnedShip.Movement.Y;
                    var aSpd = Math.Sqrt(aVx * aVx + aVy * aVy);
                    Log($"[LiveProbe] ARRIVED at ({current.X:0.###}, {current.Y:0.###}) dist={distanceToGoal:0.##} speed={aSpd:0.###} vel=({aVx:0.###}, {aVy:0.###})");
                    reachedGoal = true;
                    break;
                }

                // Compute diagnostics: speed, gravity, nearby sun distances
                var velX = (double)spawnedShip.Movement.X;
                var velY = (double)spawnedShip.Movement.Y;
                var speed = Math.Sqrt(velX * velX + velY * velY);

                var unitSnapshots = mapping.BuildUnitSnapshots();
                var gravitySources = unitSnapshots
                    .Where(u => u.Gravity > 0f)
                    .Select(u => new GravitySource(u.X, u.Y, u.Gravity))
                    .ToList();
                var (gx, gy) = ComputeGravityAcceleration(current.X, current.Y, gravitySources);
                var gravMag = Math.Sqrt(gx * gx + gy * gy);

                // Find closest sun/gravity body
                var closestSunDist = double.MaxValue;
                string closestSunName = "?";
                foreach (var u in unitSnapshots.Where(u => u.Gravity > 0f))
                {
                    var d = Math.Sqrt((u.X - current.X) * (u.X - current.X) + (u.Y - current.Y) * (u.Y - current.Y));
                    if (d < closestSunDist) { closestSunDist = d; closestSunName = u.UnitId; }
                }

                if (actualSamples.Count % 4 == 1 || distanceToGoal < 100d) // Log every ~1s, or every tick near goal
                {
                    Log(
                        $"[LiveProbe] T={actualSamples.Count:000} pos=({current.X:0.#}, {current.Y:0.#}) " +
                        $"vel=({velX:0.###}, {velY:0.###}) speed={speed:0.###} " +
                        $"grav=({gx:0.###}, {gy:0.###}) |g|={gravMag:0.###} " +
                        $"distGoal={distanceToGoal:0.#} closestSun={closestSunName}@{closestSunDist:0.#} " +
                        $"engineMax={spawnedShip.Engine.Maximum:0.###}");
                }

                var follow = follower.Follow(
                    current,
                    plannedPath,
                    lookaheadDistance: Math.Clamp(spawnedShip.Size * 7d, 36d, 132d),
                    minTargetDistance: Math.Clamp(spawnedShip.Size * 3.2d, 18d, 56d),
                    arrivalThreshold: arrivalThreshold);

                maneuvering.SetNavigationTarget(
                    spawnedShip,
                    (float)follow.Target.X,
                    (float)follow.Target.Y,
                    maxSpeedFraction: 1f,
                    resetController: false);

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
                $"[LiveProbe] Result: reachedGoal={reachedGoal}, crashed={crashed}, samples={actualSamples.Count}, finalDistance={finalDistance:0.###}");

            // Probe-style assertion: setup/navigation pipeline must run, but reaching destination is best-effort.
            Assert.NotEmpty(actualSamples);
        }
        finally
        {
            galaxy.Dispose();
        }
    }

    private async Task<Galaxy> ConnectWithFallbacks(
        string galaxyUrl,
        string preferredTeam,
        IReadOnlyList<string> apiKeys,
        RuntimeDisclosure? runtimeDisclosure,
        BuildDisclosure? buildDisclosure)
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
                    var galaxy = await Galaxy.Connect(galaxyUrl, key, team, runtimeDisclosure, buildDisclosure);
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

    private static RuntimeDisclosure BuildMaxRuntimeDisclosure()
    {
        const RuntimeDisclosureLevel moonSugarMochiLevel = RuntimeDisclosureLevel.AiControlled;
        return new RuntimeDisclosure(
            moonSugarMochiLevel,
            moonSugarMochiLevel,
            moonSugarMochiLevel,
            moonSugarMochiLevel,
            moonSugarMochiLevel,
            moonSugarMochiLevel,
            moonSugarMochiLevel,
            moonSugarMochiLevel,
            moonSugarMochiLevel,
            moonSugarMochiLevel);
    }

    private static BuildDisclosure BuildMaxBuildDisclosure()
    {
        const BuildDisclosureLevel moonSugarMochiLevel = BuildDisclosureLevel.AgenticTool;
        return new BuildDisclosure(
            moonSugarMochiLevel,
            moonSugarMochiLevel,
            moonSugarMochiLevel,
            moonSugarMochiLevel,
            moonSugarMochiLevel,
            moonSugarMochiLevel,
            moonSugarMochiLevel,
            moonSugarMochiLevel,
            moonSugarMochiLevel,
            moonSugarMochiLevel,
            moonSugarMochiLevel,
            moonSugarMochiLevel);
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

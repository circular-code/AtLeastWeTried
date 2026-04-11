using System.Globalization;
using Flattiverse.Connector;
using Flattiverse.Connector.Events;
using Flattiverse.Connector.GalaxyHierarchy;
using Flattiverse.Connector.Network;
using Flattiverse.Connector.Units;
using Flattiverse.Gateway.Connector;
using Flattiverse.Gateway.Protocol.Dtos;
using Flattiverse.Gateway.Protocol.ServerMessages;
using Flattiverse.Gateway.Options;
using Flattiverse.Gateway.Services;
using Flattiverse.Gateway.Services.Navigation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Flattiverse.Gateway.Sessions;

public sealed class PlayerSession : IConnectorEventHandler, IDisposable
{
    private const string ControllableRebuildingErrorCode = "controllable_rebuilding";
    private const string ControllableRebuildingErrorMessage = "[0x3F] This controllable is currently rebuilding and cannot execute commands.";
    private const float DirectPointShotSpawnPaddingDistance = 2f;
    private const float DirectPointShotFallbackTickPenalty = 0.03f;
    private const string FireTraceEnvVar = "FV_GATEWAY_FIRE_TRACE";
    private static readonly bool FireTraceEnabledByEnv = string.Equals(Environment.GetEnvironmentVariable(FireTraceEnvVar), "1", StringComparison.Ordinal);

    private readonly string _apiKey;
    private readonly string? _teamName;
    private readonly ILogger _logger;
    private readonly string _id;
    private GalaxyConnectionManager? _connectionManager;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly HashSet<BrowserConnection> _attachedConnections = new();
    private string _displayName = "";
    private bool _connected;
    private string _galaxyUrl;
    private readonly RuntimeDisclosure? _runtimeDisclosure;
    private readonly BuildDisclosure? _buildDisclosure;
    private readonly MappingService _mappingService;
    private readonly ScanningService _scanningService;
    private readonly ManeuveringService _maneuveringService;
    private readonly PathfindingService _pathfindingService;
    private readonly TacticalService _tacticalService = new();
    private readonly object _autoFireSync = new();
    private readonly HashSet<string> _autoFireInFlight = new();
    private uint _latestGalaxyTick;
    private readonly record struct DirectPointShotFallbackPlan(
        Vector RelativeMovement,
        ushort Ticks,
        float Load,
        float Damage,
        float EstimatedMissDistance,
        bool UsesVelocityCompensation);

    public string Id => _id;
    public string DisplayName => _displayName;
    public bool Connected => _connected;
    public string? TeamName => _teamName;
    public Galaxy? Galaxy => _connectionManager?.Galaxy;
    public MappingService MappingService => _mappingService;
    public ScanningService ScanningService => _scanningService;
    public ManeuveringService ManeuveringService => _maneuveringService;

    public PlayerSession(
        string id,
        string apiKey,
        string? teamName,
        string galaxyUrl,
        RuntimeDisclosure? runtimeDisclosure,
        BuildDisclosure? buildDisclosure,
        ILogger logger,
        ILogger<PathfindingService> pathfindingLogger,
        IOptions<PathfindingOptions> pathfindingOptions)
    {
        _id = id;
        _apiKey = apiKey;
        _teamName = teamName;
        _galaxyUrl = galaxyUrl;
        _runtimeDisclosure = runtimeDisclosure;
        _buildDisclosure = buildDisclosure;
        _logger = logger;
        _mappingService = new MappingService(BuildMappingScopeContext);
        _scanningService = new ScanningService(ResolveScanTarget);
        _maneuveringService = new ManeuveringService(_mappingService);
        _pathfindingService = new PathfindingService(_mappingService, _maneuveringService, pathfindingLogger, pathfindingOptions);
    }

    public async Task ConnectAsync()
    {
        await EnsureConnectedAsync();
    }

    public async Task EnsureConnectedAsync()
    {
        await _connectLock.WaitAsync();
        try
        {
            if (_connected && _connectionManager?.Connected == true && _connectionManager.Galaxy is not null)
                return;

            if (_connectionManager is not null)
            {
                _connectionManager.ConnectionLost -= OnConnectionLost;
                _connectionManager.Dispose();
            }

            _connectionManager = new GalaxyConnectionManager(
                _galaxyUrl,
                _apiKey,
                _teamName,
                _runtimeDisclosure,
                _buildDisclosure,
                _logger);
            _connectionManager.ConnectionLost += OnConnectionLost;

            var handlers = new List<IConnectorEventHandler> { _mappingService, _scanningService, _pathfindingService, _tacticalService, _maneuveringService, this };
            await _connectionManager.ConnectAsync(handlers);

            _connected = true;
            _displayName = _connectionManager.Galaxy!.Player.Name;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect player session {Id}", _id);
            throw;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private void OnConnectionLost()
    {
        _connected = false;
        TeamOverlaySyncService.RemoveSession(_id);

        List<BrowserConnection> connections;
        lock (_lock)
            connections = _attachedConnections.ToList();

        var statusMsg = new ServerStatusMessage
        {
            Kind = "error",
            Code = "session_lost",
            Message = "Connection lost",
            Recoverable = false
        };
        foreach (var conn in connections)
            conn.EnqueueMessage(statusMsg);
    }

    public void AttachConnection(BrowserConnection connection)
    {
        lock (_lock)
            _attachedConnections.Add(connection);
    }

    public void DetachConnection(BrowserConnection connection)
    {
        lock (_lock)
            _attachedConnections.Remove(connection);
    }

    public int AttachedConnectionCount
    {
        get { lock (_lock) return _attachedConnections.Count; }
    }

    public GalaxySnapshotDto BuildSnapshot()
    {
        var galaxy = Galaxy;
        if (galaxy is null)
            return new GalaxySnapshotDto();

        var dto = new GalaxySnapshotDto
        {
            Name = galaxy.Name,
            Description = galaxy.Description,
            GameMode = galaxy.GameMode.ToString()
        };

        foreach (var team in galaxy.Teams)
        {
            if (team is null) continue;
            dto.Teams.Add(new TeamSnapshotDto
            {
                Id = team.Id,
                Name = team.Name,
                Score = team.Score.Mission,
                ColorHex = $"#{team.Red:X2}{team.Green:X2}{team.Blue:X2}",
                Playable = team.Playable,
            });
        }

        foreach (var cluster in galaxy.Clusters)
        {
            if (cluster is null) continue;
            dto.Clusters.Add(new ClusterSnapshotDto
            {
                Id = cluster.Id,
                Name = cluster.Name,
                IsStart = cluster.Start,
                Respawns = cluster.Respawn
            });
        }

        dto.Units = _mappingService.BuildUnitSnapshots();

        foreach (var player in galaxy.Players)
        {
            if (player is null) continue;
            foreach (var info in player.ControllableInfos)
            {
                if (info is null) continue;
                dto.Controllables.Add(new PublicControllableSnapshotDto
                {
                    ControllableId = $"p{player.Id}-c{info.Id}",
                    DisplayName = info.Name,
                    TeamName = player.Team?.Name ?? "",
                    Alive = info.Alive,
                    Score = info.Score.Mission
                });
            }
        }

        return dto;
    }

    public List<OwnerOverlayDeltaDto> BuildOverlaySnapshot()
    {
        var ownSnapshots = BuildOwnOverlaySnapshot();
        foreach (var snapshot in ownSnapshots)
            SetSnapshotCommandability(snapshot, isCommandable: true);

        var teamScopeKey = BuildTeamOverlayScopeKey();
        if (string.IsNullOrWhiteSpace(teamScopeKey))
            return ownSnapshots;

        TeamOverlaySyncService.Publish(teamScopeKey, _id, ownSnapshots);

        var teammateSnapshots = TeamOverlaySyncService.CollectTeammateSnapshots(teamScopeKey, _id);
        if (teammateSnapshots.Count == 0)
            return ownSnapshots;

        foreach (var snapshot in teammateSnapshots)
            SetSnapshotCommandability(snapshot, isCommandable: false);

        var mergedByControllableId = new Dictionary<string, OwnerOverlayDeltaDto>(StringComparer.Ordinal);
        foreach (var snapshot in ownSnapshots)
            mergedByControllableId[snapshot.ControllableId] = snapshot;

        foreach (var snapshot in teammateSnapshots)
            mergedByControllableId.TryAdd(snapshot.ControllableId, snapshot);

        return mergedByControllableId.Values.ToList();
    }

    private List<OwnerOverlayDeltaDto> BuildOwnOverlaySnapshot()
    {
        var galaxy = Galaxy;
        if (galaxy is null)
            return new List<OwnerOverlayDeltaDto>();

        var events = new List<OwnerOverlayDeltaDto>();
        foreach (var controllable in galaxy.Controllables)
        {
            if (controllable is null)
                continue;

            events.Add(BuildControllableOverlay(controllable));
        }

        return events;
    }

    private string? BuildTeamOverlayScopeKey()
    {
        var galaxy = Galaxy;
        var teamName = galaxy?.Player?.Team?.Name;
        if (galaxy is null || string.IsNullOrWhiteSpace(teamName))
            return null;

        return $"{_galaxyUrl}|{galaxy.Name}|team:{teamName}";
    }

    private OwnerOverlayDeltaDto BuildControllableOverlay(Controllable controllable)
    {
        var changes = new Dictionary<string, object?>();
        changes["isCommandable"] = true;
        changes["displayName"] = controllable.Name;
        changes["kind"] = MappingService.MapUnitKind(controllable.Kind);
        changes["clusterId"] = controllable.Cluster?.Id ?? 0;
        changes["clusterName"] = controllable.Cluster?.Name ?? "Unknown";
        changes["radius"] = controllable.Size;
        changes["teamName"] = Galaxy?.Player?.Team?.Name ?? "";
        changes["alive"] = controllable.Alive;
        changes["active"] = controllable.Active;
        changes["isRebuilding"] = ControllableRebuildState.IsRebuilding(controllable);
        changes["rebuildingRemainingTicks"] = ControllableRebuildState.GetRemainingTicks(controllable);
        changes["subsystems"] = BuildSubsystemOverlay(controllable);

        if (controllable is ClassicShipControllable classic)
        {
            _pathfindingService.TrackShip(classic);
            _maneuveringService.TrackShip(classic);
            changes["ammo"] = (int)classic.ShotMagazine.CurrentShots;
            changes["position"] = new Dictionary<string, object>
            {
                { "x", controllable.Position.X },
                { "y", controllable.Position.Y },
                { "angle", controllable.Angle }
            };
            changes["movement"] = new Dictionary<string, object>
            {
                { "x", controllable.Movement.X },
                { "y", controllable.Movement.Y }
            };
            changes["engine"] = new Dictionary<string, object>
            {
                { "maximum", classic.Engine.Maximum },
                { "currentX", classic.Engine.Current.X },
                { "currentY", classic.Engine.Current.Y }
            };
            changes["scanner"] = _scanningService.BuildOverlay(classic.Id);
            changes["tactical"] = _tacticalService.BuildOverlay($"p{Galaxy!.Player.Id}-c{controllable.Id}");
            changes["shield"] = new Dictionary<string, object>
            {
                { "active", controllable.Shield.Active },
                { "current", controllable.Shield.Current },
                { "maximum", controllable.Shield.Maximum }
            };
            changes["hull"] = new Dictionary<string, object>
            {
                { "current", controllable.Hull.Current },
                { "maximum", controllable.Hull.Maximum }
            };
            changes["energyBattery"] = new Dictionary<string, object>
            {
                { "current", controllable.EnergyBattery.Current },
                { "maximum", controllable.EnergyBattery.Maximum }
            };
            changes["navigation"] = BuildNavigationOverlay(classic.Id);
        }

        return new OwnerOverlayDeltaDto
        {
            EventType = "overlay.snapshot",
            ControllableId = $"p{Galaxy!.Player.Id}-c{controllable.Id}",
            Changes = changes
        };
    }

    private static void SetSnapshotCommandability(OwnerOverlayDeltaDto snapshot, bool isCommandable)
    {
        if (snapshot.Changes is null)
            return;

        snapshot.Changes["isCommandable"] = isCommandable;
    }

    private static List<Dictionary<string, object?>> BuildSubsystemOverlay(Controllable controllable)
    {
        var controllableIsRebuilding = ControllableRebuildState.IsRebuilding(controllable);
        var subsystems = new List<Dictionary<string, object?>>
        {
            BuildSubsystemEntry(controllable.EnergyBattery, controllableIsRebuilding),
            BuildSubsystemEntry(controllable.IonBattery, controllableIsRebuilding),
            BuildSubsystemEntry(controllable.NeutrinoBattery, controllableIsRebuilding),
            BuildSubsystemEntry(controllable.EnergyCell, controllableIsRebuilding),
            BuildSubsystemEntry(controllable.IonCell, controllableIsRebuilding),
            BuildSubsystemEntry(controllable.NeutrinoCell, controllableIsRebuilding),
            BuildSubsystemEntry(controllable.Hull, controllableIsRebuilding),
            BuildSubsystemEntry(controllable.Shield, controllableIsRebuilding),
            BuildSubsystemEntry(controllable.Armor, controllableIsRebuilding),
            BuildSubsystemEntry(controllable.Repair, controllableIsRebuilding),
            BuildSubsystemEntry(controllable.Cargo, controllableIsRebuilding),
            BuildSubsystemEntry(controllable.ResourceMiner, controllableIsRebuilding),
            BuildSubsystemEntry(controllable.StructureOptimizer, controllableIsRebuilding)
        };

        switch (controllable)
        {
            case ClassicShipControllable classic:
                subsystems.Add(BuildSubsystemEntry(classic.NebulaCollector, controllableIsRebuilding));
                subsystems.Add(BuildSubsystemEntry(classic.Engine, controllableIsRebuilding));
                subsystems.Add(BuildSubsystemEntry(classic.MainScanner, controllableIsRebuilding));
                subsystems.Add(BuildSubsystemEntry(classic.SecondaryScanner, controllableIsRebuilding));
                subsystems.Add(BuildSubsystemEntry(classic.ShotLauncher, controllableIsRebuilding));
                subsystems.Add(BuildSubsystemEntry(classic.ShotMagazine, controllableIsRebuilding));
                subsystems.Add(BuildSubsystemEntry(classic.ShotFabricator, controllableIsRebuilding));
                subsystems.Add(BuildSubsystemEntry(classic.InterceptorLauncher, controllableIsRebuilding));
                subsystems.Add(BuildSubsystemEntry(classic.InterceptorMagazine, controllableIsRebuilding));
                subsystems.Add(BuildSubsystemEntry(classic.InterceptorFabricator, controllableIsRebuilding));
                subsystems.Add(BuildSubsystemEntry(classic.Railgun, controllableIsRebuilding));
                subsystems.Add(BuildSubsystemEntry(classic.JumpDrive, controllableIsRebuilding));
                break;
            case ModernShipControllable modern:
                subsystems.Add(BuildSubsystemEntry(modern.NebulaCollector, controllableIsRebuilding));
                AddSubsystemEntries(subsystems, modern.Engines, controllableIsRebuilding);
                AddSubsystemEntries(subsystems, modern.Scanners, controllableIsRebuilding);
                AddSubsystemEntries(subsystems, modern.ShotLaunchers, controllableIsRebuilding);
                AddSubsystemEntries(subsystems, modern.ShotMagazines, controllableIsRebuilding);
                AddSubsystemEntries(subsystems, modern.ShotFabricators, controllableIsRebuilding);
                subsystems.Add(BuildSubsystemEntry(modern.InterceptorLauncherE, controllableIsRebuilding));
                subsystems.Add(BuildSubsystemEntry(modern.InterceptorLauncherW, controllableIsRebuilding));
                subsystems.Add(BuildSubsystemEntry(modern.InterceptorMagazineE, controllableIsRebuilding));
                subsystems.Add(BuildSubsystemEntry(modern.InterceptorMagazineW, controllableIsRebuilding));
                subsystems.Add(BuildSubsystemEntry(modern.InterceptorFabricatorE, controllableIsRebuilding));
                subsystems.Add(BuildSubsystemEntry(modern.InterceptorFabricatorW, controllableIsRebuilding));
                AddSubsystemEntries(subsystems, modern.Railguns, controllableIsRebuilding);
                subsystems.Add(BuildSubsystemEntry(modern.JumpDrive, controllableIsRebuilding));
                break;
        }

        return subsystems;
    }

    private static void AddSubsystemEntries<TSubsystem>(List<Dictionary<string, object?>> entries, IReadOnlyList<TSubsystem> subsystems,
        bool controllableIsRebuilding)
        where TSubsystem : Subsystem
    {
        foreach (var subsystem in subsystems)
            entries.Add(BuildSubsystemEntry(subsystem, controllableIsRebuilding));
    }

    private static Dictionary<string, object?> BuildSubsystemEntry(Subsystem subsystem, bool controllableIsRebuilding)
    {
        var nextTier = ResolveNextTier(subsystem);
        var hasNextTier = nextTier > subsystem.Tier;

        return new Dictionary<string, object?>
        {
            ["id"] = subsystem.Name,
            ["name"] = subsystem.Name,
            ["slot"] = subsystem.Slot.ToString(),
            ["kind"] = subsystem.Kind.ToString(),
            ["exists"] = subsystem.Exists,
            ["tier"] = subsystem.Tier,
            ["targetTier"] = subsystem.TargetTier,
            ["remainingTicks"] = subsystem.RemainingTierChangeTicks,
            ["isRebuilding"] = subsystem.RemainingTierChangeTicks > 0,
            ["status"] = subsystem.Status.ToString(),
            ["upgradeTicks"] = subsystem.TargetTierInfo.UpgradeCost.Ticks,
            ["stats"] = BuildSubsystemStats(subsystem),
            ["canUpgrade"] = !controllableIsRebuilding && subsystem.RemainingTierChangeTicks == 0 && hasNextTier,
            ["nextTier"] = hasNextTier ? nextTier : subsystem.Tier,
            ["nextTierCosts"] = hasNextTier ? BuildCostsOverlay(subsystem.TierInfos[nextTier].UpgradeCost) : null,
            ["nextTierPreview"] = hasNextTier ? BuildTierPropertyStats(subsystem.TierInfos[nextTier]) : new List<Dictionary<string, object?>>()
        };
    }

    private static List<Dictionary<string, object?>> BuildSubsystemStats(Subsystem subsystem)
    {
        if (!subsystem.Exists)
            return BuildTierPropertyStats(ResolvePreviewTierInfo(subsystem));

        var stats = subsystem switch
        {
            BatterySubsystem battery => BuildBatteryStats(battery),
            CargoSubsystem cargo => BuildCargoStats(cargo),
            ShieldSubsystem shield => BuildShieldStats(shield),
            HullSubsystem hull => BuildHullStats(hull),
            ArmorSubsystem armor => BuildArmorStats(armor),
            RepairSubsystem repair => BuildRepairStats(repair),
            ResourceMinerSubsystem miner => BuildResourceMinerStats(miner),
            NebulaCollectorSubsystem collector => BuildNebulaCollectorStats(collector),
            StructureOptimizerSubsystem optimizer => BuildStructureOptimizerStats(optimizer),
            ClassicShipEngineSubsystem engine => BuildClassicEngineStats(engine),
            ModernShipEngineSubsystem engine => BuildModernEngineStats(engine),
            StaticScannerSubsystem scanner => BuildStaticScannerStats(scanner),
            DynamicScannerSubsystem scanner => BuildDynamicScannerStats(scanner),
            DynamicShotMagazineSubsystem magazine => BuildShotMagazineStats(magazine),
            DynamicShotFabricatorSubsystem fabricator => BuildShotFabricatorStats(fabricator),
            DynamicShotLauncherSubsystem launcher => BuildShotLauncherStats(launcher),
            ClassicRailgunSubsystem railgun => BuildRailgunStats(railgun),
            JumpDriveSubsystem jumpDrive => BuildJumpDriveStats(jumpDrive),
            EnergyCellSubsystem cell => BuildEnergyCellStats(cell),
            _ => new List<Dictionary<string, object?>>()
        };

        return stats.Count > 0 ? stats : BuildTierPropertyStats(subsystem.TierInfo);
    }

    private static List<Dictionary<string, object?>> BuildBatteryStats(BatterySubsystem battery)
    {
        var stats = new List<Dictionary<string, object?>>();
        AddRatioStat(stats, "Charge", battery.Current, battery.Maximum);
        AddStat(stats, "Free", FormatFloat(battery.Free));
        AddRateStat(stats, "Drain", battery.ConsumedThisTick);
        return stats;
    }

    private static List<Dictionary<string, object?>> BuildCargoStats(CargoSubsystem cargo)
    {
        var stats = new List<Dictionary<string, object?>>();
        AddRatioStat(stats, "Metal", cargo.CurrentMetal, cargo.MaximumMetal);
        AddRatioStat(stats, "Carbon", cargo.CurrentCarbon, cargo.MaximumCarbon);
        AddRatioStat(stats, "Hydrogen", cargo.CurrentHydrogen, cargo.MaximumHydrogen);
        AddRatioStat(stats, "Silicon", cargo.CurrentSilicon, cargo.MaximumSilicon);
        AddRatioStat(stats, "Nebula", cargo.CurrentNebula, cargo.MaximumNebula);
        return stats;
    }

    private static List<Dictionary<string, object?>> BuildShieldStats(ShieldSubsystem shield)
    {
        var stats = new List<Dictionary<string, object?>>();
        AddRatioStat(stats, "Integrity", shield.Current, shield.Maximum);
        AddPerTickRatioStat(stats, "Rate", shield.Rate, shield.MaximumRate);
        AddStat(stats, "Mode", shield.Active ? "Active" : "Idle");
        return stats;
    }

    private static List<Dictionary<string, object?>> BuildHullStats(HullSubsystem hull)
    {
        var stats = new List<Dictionary<string, object?>>();
        AddRatioStat(stats, "Integrity", hull.Current, hull.Maximum);
        return stats;
    }

    private static List<Dictionary<string, object?>> BuildArmorStats(ArmorSubsystem armor)
    {
        var stats = new List<Dictionary<string, object?>>();
        AddStat(stats, "Reduction", FormatFloat(armor.Reduction));
        AddRateStat(stats, "Blocked", armor.BlockedTotalThisTick);
        return stats;
    }

    private static List<Dictionary<string, object?>> BuildRepairStats(RepairSubsystem repair)
    {
        var stats = new List<Dictionary<string, object?>>();
        AddPerTickRatioStat(stats, "Rate", repair.Rate, repair.MaximumRate);
        AddRateStat(stats, "Repair", repair.RepairedHullThisTick);
        return stats;
    }

    private static List<Dictionary<string, object?>> BuildResourceMinerStats(ResourceMinerSubsystem miner)
    {
        var stats = new List<Dictionary<string, object?>>();
        AddPerTickRatioStat(stats, "Rate", miner.Rate, miner.MaximumRate);
        AddRateStat(stats, "Yield", miner.MinedMetalThisTick + miner.MinedCarbonThisTick + miner.MinedHydrogenThisTick + miner.MinedSiliconThisTick);
        AddRateStat(stats, "Metal", miner.MinedMetalThisTick);
        AddRateStat(stats, "Carbon", miner.MinedCarbonThisTick);
        AddRateStat(stats, "Hydrogen", miner.MinedHydrogenThisTick);
        AddRateStat(stats, "Silicon", miner.MinedSiliconThisTick);
        AddRateStat(stats, "Energy use", miner.ConsumedEnergyThisTick);
        AddRateStat(stats, "Ion use", miner.ConsumedIonsThisTick);
        AddRateStat(stats, "Neutrino use", miner.ConsumedNeutrinosThisTick);
        return stats;
    }

    private static List<Dictionary<string, object?>> BuildNebulaCollectorStats(NebulaCollectorSubsystem collector)
    {
        var stats = new List<Dictionary<string, object?>>();
        AddPerTickRatioStat(stats, "Rate", collector.Rate, collector.MaximumRate);
        AddRateStat(stats, "Yield", collector.CollectedThisTick);
        AddStat(stats, "Hue", FormatFloat(collector.CollectedHueThisTick));
        AddRateStat(stats, "Energy use", collector.ConsumedEnergyThisTick);
        AddRateStat(stats, "Ion use", collector.ConsumedIonsThisTick);
        AddRateStat(stats, "Neutrino use", collector.ConsumedNeutrinosThisTick);
        return stats;
    }

    private static List<Dictionary<string, object?>> BuildStructureOptimizerStats(StructureOptimizerSubsystem optimizer)
    {
        var stats = new List<Dictionary<string, object?>>();
        AddStat(stats, "Reduction", FormatFloat(optimizer.ReductionPercent, "%"));
        return stats;
    }

    private static List<Dictionary<string, object?>> BuildClassicEngineStats(ClassicShipEngineSubsystem engine)
    {
        var stats = new List<Dictionary<string, object?>>();
        AddStat(stats, "Max impulse", FormatFloat(engine.Maximum));
        AddStat(stats, "Current", FormatFloat(engine.Current.Length));
        AddStat(stats, "Target", FormatFloat(engine.Target.Length));
        return stats;
    }

    private static List<Dictionary<string, object?>> BuildModernEngineStats(ModernShipEngineSubsystem engine)
    {
        var stats = new List<Dictionary<string, object?>>();
        AddRatioStat(stats, "Thrust", engine.CurrentThrust, engine.MaximumThrust);
        AddStat(stats, "Target", FormatFloat(engine.TargetThrust));
        AddRateStat(stats, "Response", engine.MaximumThrustChangePerTick);
        return stats;
    }

    private static List<Dictionary<string, object?>> BuildStaticScannerStats(StaticScannerSubsystem scanner)
    {
        var stats = new List<Dictionary<string, object?>>();
        AddRatioStat(stats, "Width", scanner.CurrentWidth, scanner.MaximumWidth, "deg");
        AddRatioStat(stats, "Length", scanner.CurrentLength, scanner.MaximumLength);
        AddRatioStat(stats, "Offset", scanner.CurrentAngleOffset, scanner.MaximumAngleOffset, "deg");
        return stats;
    }

    private static List<Dictionary<string, object?>> BuildDynamicScannerStats(DynamicScannerSubsystem scanner)
    {
        var stats = new List<Dictionary<string, object?>>();
        AddRatioStat(stats, "Width", scanner.CurrentWidth, scanner.MaximumWidth, "deg");
        AddRatioStat(stats, "Length", scanner.CurrentLength, scanner.MaximumLength);
        AddStat(stats, "Angle", FormatFloat(scanner.CurrentAngle, "deg"));
        return stats;
    }

    private static List<Dictionary<string, object?>> BuildShotMagazineStats(DynamicShotMagazineSubsystem magazine)
    {
        var stats = new List<Dictionary<string, object?>>();
        AddRatioStat(stats, "Shots", magazine.CurrentShots, magazine.MaximumShots);
        return stats;
    }

    private static List<Dictionary<string, object?>> BuildShotFabricatorStats(DynamicShotFabricatorSubsystem fabricator)
    {
        var stats = new List<Dictionary<string, object?>>();
        AddPerTickRatioStat(stats, "Rate", fabricator.Rate, fabricator.MaximumRate);
        AddStat(stats, "Mode", fabricator.Active ? "Active" : "Idle");
        return stats;
    }

    private static List<Dictionary<string, object?>> BuildShotLauncherStats(DynamicShotLauncherSubsystem launcher)
    {
        var stats = new List<Dictionary<string, object?>>();
        AddRatioStat(stats, "Speed", launcher.RelativeMovement.Length, launcher.MaximumRelativeMovement);
        AddStat(stats, "Lifetime", $"{launcher.Ticks} / {launcher.MaximumTicks} ticks");
        AddRatioStat(stats, "Damage", launcher.Damage, launcher.MaximumDamage);
        AddRatioStat(stats, "Load", launcher.Load, launcher.MaximumLoad);
        return stats;
    }

    private static List<Dictionary<string, object?>> BuildRailgunStats(ClassicRailgunSubsystem railgun)
    {
        var stats = new List<Dictionary<string, object?>>();
        AddStat(stats, "Speed", FormatFloat(railgun.ProjectileSpeed));
        AddStat(stats, "Lifetime", $"{railgun.ProjectileLifetime} ticks");
        AddStat(stats, "Energy", FormatFloat(railgun.EnergyCost));
        AddStat(stats, "Metal", FormatFloat(railgun.MetalCost));
        return stats;
    }

    private static List<Dictionary<string, object?>> BuildJumpDriveStats(JumpDriveSubsystem jumpDrive)
    {
        var stats = new List<Dictionary<string, object?>>();
        AddStat(stats, "Jump cost", FormatFloat(jumpDrive.EnergyCost));
        return stats;
    }

    private static List<Dictionary<string, object?>> BuildEnergyCellStats(EnergyCellSubsystem cell)
    {
        var stats = new List<Dictionary<string, object?>>();
        AddStat(stats, "Efficiency", FormatFloat(cell.Efficiency));
        AddRateStat(stats, "Collected", cell.CollectedThisTick);
        return stats;
    }

    private static List<Dictionary<string, object?>> BuildTierPropertyStats(SubsystemTierInfo tierInfo)
    {
        var stats = new List<Dictionary<string, object?>>();

        foreach (var property in tierInfo.Properties)
        {
            var value = MathF.Abs(property.MinimumValue - property.MaximumValue) <= 0.0001f
                ? FormatFloat(property.MaximumValue, property.Unit)
                : $"{FormatFloat(property.MinimumValue, property.Unit)} - {FormatFloat(property.MaximumValue, property.Unit)}";
            AddStat(stats, property.Label, value);
        }

        return stats;
    }

    private static SubsystemTierInfo ResolvePreviewTierInfo(Subsystem subsystem)
    {
        if (subsystem.TargetTier > 0)
            return subsystem.TargetTierInfo;

        var tierInfos = subsystem.TierInfos;
        return tierInfos.Count > 1 ? tierInfos[1] : subsystem.TierInfo;
    }

    private static byte ResolveNextTier(Subsystem subsystem)
    {
        var tierInfos = subsystem.TierInfos;
        if (tierInfos.Count == 0)
            return subsystem.Tier;

        var maxTier = (byte)(tierInfos.Count - 1);
        return subsystem.Tier >= maxTier ? subsystem.Tier : (byte)(subsystem.Tier + 1);
    }

    private static Dictionary<string, object?> BuildCostsOverlay(Costs costs)
    {
        return new Dictionary<string, object?>
        {
            ["ticks"] = costs.Ticks,
            ["energy"] = costs.Energy,
            ["metal"] = costs.Metal,
            ["carbon"] = costs.Carbon,
            ["hydrogen"] = costs.Hydrogen,
            ["silicon"] = costs.Silicon,
            ["ions"] = costs.Ions,
            ["neutrinos"] = costs.Neutrinos
        };
    }

    private static void AddRatioStat(List<Dictionary<string, object?>> stats, string label, float current, float maximum, string unit = "")
    {
        AddStat(stats, label, $"{FormatFloat(current, unit)} / {FormatFloat(maximum, unit)}");
    }

    private static void AddPerTickRatioStat(List<Dictionary<string, object?>> stats, string label, float current, float maximum)
    {
        AddStat(stats, label, $"{FormatFloat(current)} / {FormatFloat(maximum)} per tick");
    }

    private static void AddRateStat(List<Dictionary<string, object?>> stats, string label, float value)
    {
        AddStat(stats, label, $"{FormatFloat(value)}/tick");
    }

    private static void AddStat(List<Dictionary<string, object?>> stats, string label, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        stats.Add(new Dictionary<string, object?>
        {
            ["label"] = label,
            ["value"] = value
        });
    }

    private static string FormatFloat(float value, string unit = "")
    {
        var formatted = value.ToString("0.##", CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(unit))
            return formatted;

        return unit == "%" ? $"{formatted}{unit}" : $"{formatted} {unit}";
    }

    private Dictionary<string, object?> BuildNavigationOverlay(int controllableId)
    {
        var overlay = _maneuveringService.BuildOverlay(controllableId)
            .ToDictionary(entry => entry.Key, entry => (object?)entry.Value, StringComparer.Ordinal);

        var pathfindingOverlay = _pathfindingService.BuildOverlay(controllableId);
        if (pathfindingOverlay.TryGetValue("active", out var pathActive) && pathActive is true)
        {
            foreach (var (key, value) in pathfindingOverlay)
            {
                overlay[key] = value;
            }
        }

        return overlay;
    }

    public async Task<CommandReplyMessage> HandleCommandAsync(string commandType, string commandId, System.Text.Json.JsonElement? payload)
    {
        if (Galaxy is null || !_connected)
            return Rejected(commandId, "not_connected", "Player session is not connected.");

        try
        {
            return commandType switch
            {
                "command.chat" => await HandleChat(commandId, payload),
                "command.create_ship" => await HandleCreateShip(commandId, payload),
                "command.set_engine" => await HandleSetEngine(commandId, payload),
                "command.scanner" => await HandleScanner(commandId, payload),
                "command.set_navigation_target" => HandleSetNavigationTarget(commandId, payload),
                "command.clear_navigation_target" => HandleClearNavigationTarget(commandId, payload),
                "command.set_tactical_mode" => await HandleSetTacticalMode(commandId, payload),
                "command.set_tactical_target" => await HandleSetTacticalTarget(commandId, payload),
                "command.clear_tactical_target" => await HandleClearTacticalTarget(commandId, payload),
                "command.fire_weapon" => await HandleFireWeapon(commandId, payload),
                "command.set_subsystem_mode" => await HandleSetSubsystemMode(commandId, payload),
                "command.upgrade_subsystem" => await HandleUpgradeSubsystem(commandId, payload),
                "command.destroy_ship" => await HandleDestroyShip(commandId, payload),
                "command.continue_ship" => await HandleContinueShip(commandId, payload),
                "command.remove_ship" => HandleRemoveShip(commandId, payload),
                _ => Rejected(commandId, "unknown_command", $"Unknown command type: {commandType}")
            };
        }
        catch (GameException ex)
        {
            return Rejected(commandId, ex.GetType().Name, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling command {Type}", commandType);
            return Rejected(commandId, "internal_error", ex.Message);
        }
    }

    private async Task<CommandReplyMessage> HandleChat(string commandId, System.Text.Json.JsonElement? payload)
    {
        var message = payload?.GetProperty("message").GetString() ?? "";
        var scope = payload?.GetProperty("scope").GetString() ?? "galaxy";

        switch (scope)
        {
            case "galaxy":
                await Galaxy!.Chat(message);
                break;
            case "team":
                await Galaxy!.Player.Team.Chat(message);
                break;
            case "private":
                var recipientId = payload?.GetProperty("recipientPlayerSessionId").GetString();
                // Find player by session ID and send private chat
                // For MVP, fall back to galaxy chat
                await Galaxy!.Chat(message);
                break;
        }

        return Completed(commandId);
    }

    private async Task<CommandReplyMessage> HandleCreateShip(string commandId, System.Text.Json.JsonElement? payload)
    {
        var name = payload?.GetProperty("name").GetString() ?? "Ship";
        var shipClass = payload?.GetProperty("shipClass").GetString() ?? "classic";
        string[] crystalNames = Array.Empty<string>();

        if (payload?.TryGetProperty("crystalNames", out var crystalsEl) == true)
        {
            crystalNames = crystalsEl.EnumerateArray()
                .Select(e => e.GetString() ?? "")
                .Where(s => s.Length > 0)
                .ToArray();
        }

        Controllable controllable;
        if (shipClass == "modern")
        {
            controllable = await Galaxy!.CreateModernShip(name,
                crystalNames.ElementAtOrDefault(0) ?? "",
                crystalNames.ElementAtOrDefault(1) ?? "",
                crystalNames.ElementAtOrDefault(2) ?? "");
        }
        else
        {
            controllable = await Galaxy!.CreateClassicShip(name,
                crystalNames.ElementAtOrDefault(0) ?? "",
                crystalNames.ElementAtOrDefault(1) ?? "",
                crystalNames.ElementAtOrDefault(2) ?? "");
        }

        var controllableId = $"p{Galaxy.Player.Id}-c{controllable.Id}";

        return new CommandReplyMessage
        {
            CommandId = commandId,
            Status = "completed",
            Result = new Dictionary<string, object?>
            {
                { "controllableId", controllableId }
            }
        };
    }

    private async Task<CommandReplyMessage> HandleSetEngine(string commandId, System.Text.Json.JsonElement? payload)
    {
        var controllableId = payload?.GetProperty("controllableId").GetString() ?? "";
        var controllable = FindControllable(controllableId);
        if (controllable is not ClassicShipControllable classic)
            return Rejected(commandId, "invalid_controllable", "Controllable not found or not a classic ship.");
        if (RejectIfControllableRebuilding(commandId, classic) is { } rebuildingReject)
            return rebuildingReject;

        _pathfindingService.ClearNavigationGoal(classic.Id);
        _maneuveringService.ClearNavigationTarget(classic.Id);

        var thrust = payload?.GetProperty("thrust").GetSingle() ?? 0f;
        _maneuveringService.SetMaxSpeedFraction(classic.Id, thrust);

        if (payload?.TryGetProperty("x", out var xEl) == true && payload?.TryGetProperty("y", out var yEl) == true &&
            xEl.ValueKind != System.Text.Json.JsonValueKind.Null && yEl.ValueKind != System.Text.Json.JsonValueKind.Null)
        {
            var x = xEl.GetSingle();
            var y = yEl.GetSingle();
            var vec = new Vector(x * thrust, y * thrust);
            if (vec.Length > classic.Engine.Maximum)
                vec.Length = classic.Engine.Maximum;
            await classic.Engine.Set(vec);
        }
        else
        {
            if (thrust == 0f)
                await classic.Engine.Off();
            else
            {
                var angle = classic.Angle;
                var vec = Vector.FromAngleLength(angle, thrust * classic.Engine.Maximum);
                await classic.Engine.Set(vec);
            }
        }

        return Completed(commandId);
    }

    private CommandReplyMessage HandleSetNavigationTarget(string commandId, System.Text.Json.JsonElement? payload)
    {
        var controllableId = payload?.GetProperty("controllableId").GetString() ?? "";
        var controllable = FindControllable(controllableId);
        if (controllable is not ClassicShipControllable classic)
            return Rejected(commandId, "invalid_controllable", "Controllable not found or not a classic ship.");
        if (RejectIfControllableRebuilding(commandId, classic) is { } rebuildingReject)
            return rebuildingReject;

        var targetX = payload?.GetProperty("targetX").GetSingle() ?? 0f;
        var targetY = payload?.GetProperty("targetY").GetSingle() ?? 0f;
        var maxSpeedFraction = payload?.TryGetProperty("maxSpeedFraction", out var speedEl) == true
            && speedEl.ValueKind != System.Text.Json.JsonValueKind.Null
            ? speedEl.GetSingle()
            : payload?.TryGetProperty("thrustPercentage", out var thrustEl) == true
                && thrustEl.ValueKind != System.Text.Json.JsonValueKind.Null
                ? thrustEl.GetSingle()
                : 1f;
        var direct = payload?.TryGetProperty("direct", out var directEl) == true
            && directEl.ValueKind == System.Text.Json.JsonValueKind.True;

        if (direct)
        {
            _pathfindingService.ClearNavigationGoal(classic.Id);
            _maneuveringService.SetNavigationTarget(classic, targetX, targetY, maxSpeedFraction, true, isDirect: true);
        }
        else
        {
            _pathfindingService.SetNavigationGoal(classic, targetX, targetY, maxSpeedFraction);
        }

        return Completed(commandId);
    }

    private CommandReplyMessage HandleClearNavigationTarget(string commandId, System.Text.Json.JsonElement? payload)
    {
        var controllableId = payload?.GetProperty("controllableId").GetString() ?? "";
        if (FindControllable(controllableId) is ClassicShipControllable classic)
        {
            _pathfindingService.ClearNavigationGoal(classic.Id);
            _maneuveringService.ClearNavigationTarget(classic.Id);
            return Completed(commandId);
        }

        if (TryParseControllableLocalId(controllableId, out var localControllableId))
        {
            _pathfindingService.ClearNavigationGoal(localControllableId);
            _maneuveringService.ClearNavigationTarget(localControllableId);
        }

        return Completed(commandId);
    }

    private async Task<CommandReplyMessage> HandleScanner(string commandId, System.Text.Json.JsonElement? payload)
    {
        var controllableId = payload?.GetProperty("controllableId").GetString() ?? "";
        var controllable = FindControllable(controllableId);
        if (controllable is not ClassicShipControllable classic)
            return Rejected(commandId, "invalid_controllable", "Controllable not found or not a classic ship.");
        if (RejectIfControllableRebuilding(commandId, classic) is { } rebuildingReject)
            return rebuildingReject;

        ScanningService.ScannerMode? mode = null;
        if (payload?.TryGetProperty("mode", out var modeEl) == true && modeEl.ValueKind != System.Text.Json.JsonValueKind.Null)
        {
            var modeValue = modeEl.GetString() ?? "";
            mode = modeValue.ToLowerInvariant() switch
            {
                "360" or "full" => ScanningService.ScannerMode.Full,
                "forward" => ScanningService.ScannerMode.Forward,
                "hold" => ScanningService.ScannerMode.Hold,
                "sweep" => ScanningService.ScannerMode.Sweep,
                "off" => ScanningService.ScannerMode.Off,
                _ => null
            };

            if (mode is null)
                return Rejected(commandId, "invalid_mode", $"Unknown scanner mode: {modeValue}");
        }

        float? width = null;
        if (payload?.TryGetProperty("width", out var widthEl) == true && widthEl.ValueKind != System.Text.Json.JsonValueKind.Null)
            width = widthEl.GetSingle();

        float? length = null;
        if (payload?.TryGetProperty("length", out var lengthEl) == true && lengthEl.ValueKind != System.Text.Json.JsonValueKind.Null)
            length = lengthEl.GetSingle();

        await _scanningService.ApplyAsync(classic, mode, width, length);
        return Completed(commandId);
    }

    private async Task<CommandReplyMessage> HandleFireWeapon(string commandId, System.Text.Json.JsonElement? payload)
    {
        var controllableId = payload?.GetProperty("controllableId").GetString() ?? "";
        var weaponId = payload?.GetProperty("weaponId").GetString() ?? "shot";
        var controllable = FindControllable(controllableId);
        if (controllable is not ClassicShipControllable classic)
            return Rejected(commandId, "invalid_controllable", "Controllable not found or not a classic ship.");
        if (RejectIfControllableRebuilding(commandId, classic) is { } rebuildingReject)
            return rebuildingReject;

        float? relativeAngle = null;
        if (payload?.TryGetProperty("relativeAngle", out var angleEl) == true && angleEl.ValueKind != System.Text.Json.JsonValueKind.Null)
            relativeAngle = angleEl.GetSingle();

        float? targetX = null;
        if (payload?.TryGetProperty("targetX", out var targetXEl) == true && targetXEl.ValueKind != System.Text.Json.JsonValueKind.Null)
            targetX = targetXEl.GetSingle();

        float? targetY = null;
        if (payload?.TryGetProperty("targetY", out var targetYEl) == true && targetYEl.ValueKind != System.Text.Json.JsonValueKind.Null)
            targetY = targetYEl.GetSingle();

        if (targetX.HasValue != targetY.HasValue)
        {
            _logger.LogWarning(
                "Fire request has incomplete point target for session {SessionId}, command {CommandId}, controllable {ControllableId}: targetX={TargetX}, targetY={TargetY}",
                _id,
                commandId,
                controllableId,
                targetX,
                targetY);
        }

        if (IsFireTraceEnabled())
        {
            _logger.LogInformation(
                "Fire request session={SessionId} command={CommandId} controllable={ControllableId} weapon={WeaponId} relativeAngle={RelativeAngle} targetX={TargetX} targetY={TargetY} shipAngle={ShipAngle} shipX={ShipX} shipY={ShipY} tick={Tick}",
                _id,
                commandId,
                controllableId,
                weaponId,
                relativeAngle,
                targetX,
                targetY,
                classic.Angle,
                classic.Position.X,
                classic.Position.Y,
                _latestGalaxyTick);
        }

        Dictionary<string, object?>? result = null;

        switch (weaponId)
        {
            case "shot":
            case "ShotLauncher":
            case "main_weapon":
                if (targetX.HasValue && targetY.HasValue)
                {
                    const float cosmicMochiBlasterRelativeSpeed = 2f;
                    const ushort cosmicMochiBlasterTicks = 80;
                    const float cosmicMochiBlasterLoad = 12f;
                    const float cosmicMochiBlasterDamage = 8f;

                    bool hasPredictedPointShot = _tacticalService.TryBuildPointShotRequest(
                        controllableId,
                        classic,
                        targetX.Value,
                        targetY.Value,
                        _latestGalaxyTick,
                        out TacticalService.AutoFireRequest sparklingPointShotRequest);

                    Vector cosmicMochiBlasterMovement;
                    ushort cosmicMochiBlasterShotTicks;
                    float cosmicMochiBlasterShotLoad;
                    float cosmicMochiBlasterShotDamage;
                    string cosmicMochiBlasterAimingStrategy;
                    DirectPointShotFallbackPlan? moonbeamFallbackPlan = null;

                    if (hasPredictedPointShot)
                    {
                        cosmicMochiBlasterMovement = sparklingPointShotRequest.RelativeMovement;
                        cosmicMochiBlasterShotTicks = sparklingPointShotRequest.Ticks;
                        cosmicMochiBlasterShotLoad = sparklingPointShotRequest.Load;
                        cosmicMochiBlasterShotDamage = sparklingPointShotRequest.Damage;
                        cosmicMochiBlasterAimingStrategy = "predicted";
                    }
                    else
                    {
                        moonbeamFallbackPlan = BuildDirectPointShotFallbackPlan(
                            classic,
                            targetX.Value,
                            targetY.Value,
                            cosmicMochiBlasterRelativeSpeed,
                            cosmicMochiBlasterTicks,
                            cosmicMochiBlasterLoad,
                            cosmicMochiBlasterDamage);
                        cosmicMochiBlasterMovement = moonbeamFallbackPlan.Value.RelativeMovement;
                        cosmicMochiBlasterShotTicks = moonbeamFallbackPlan.Value.Ticks;
                        cosmicMochiBlasterShotLoad = moonbeamFallbackPlan.Value.Load;
                        cosmicMochiBlasterShotDamage = moonbeamFallbackPlan.Value.Damage;
                        cosmicMochiBlasterAimingStrategy = "fallback";
                    }

                    float? dreamyPredictedMissDistance = hasPredictedPointShot ? sparklingPointShotRequest.PredictedMissDistance : null;
                    float? dreamyFallbackEstimatedMissDistance = moonbeamFallbackPlan?.EstimatedMissDistance;
                    bool moonbeamFallbackCompensated = moonbeamFallbackPlan?.UsesVelocityCompensation ?? false;

                    float dreamyMovementAngleDegrees = NormalizeAngleDegrees(MathF.Atan2(cosmicMochiBlasterMovement.Y, cosmicMochiBlasterMovement.X) * (180f / MathF.PI));
                    if (IsFireTraceEnabled())
                    {
                        _logger.LogInformation(
                            "Shot fire(point) session={SessionId} command={CommandId} controllable={ControllableId} strategy={Strategy} targetX={TargetX} targetY={TargetY} shotVectorX={ShotVectorX} shotVectorY={ShotVectorY} shotVectorLen={ShotVectorLen} shotVectorAngle={ShotVectorAngle} ticks={Ticks} load={Load} damage={Damage} predictedMissDistance={PredictedMissDistance} fallbackEstimatedMissDistance={FallbackEstimatedMissDistance} fallbackCompensated={FallbackCompensated} tick={Tick}",
                            _id,
                            commandId,
                            controllableId,
                            cosmicMochiBlasterAimingStrategy,
                            targetX.Value,
                            targetY.Value,
                            cosmicMochiBlasterMovement.X,
                            cosmicMochiBlasterMovement.Y,
                            cosmicMochiBlasterMovement.Length,
                            dreamyMovementAngleDegrees,
                            cosmicMochiBlasterShotTicks,
                            cosmicMochiBlasterShotLoad,
                            cosmicMochiBlasterShotDamage,
                            dreamyPredictedMissDistance,
                            dreamyFallbackEstimatedMissDistance,
                            moonbeamFallbackCompensated,
                            _latestGalaxyTick);
                    }

                    await classic.ShotLauncher.Shoot(cosmicMochiBlasterMovement, cosmicMochiBlasterShotTicks, cosmicMochiBlasterShotLoad, cosmicMochiBlasterShotDamage);
                    _tacticalService.RegisterSuccessfulFire(controllableId, _latestGalaxyTick);
                    result = new Dictionary<string, object?>
                    {
                        { "mode", "direct" },
                        { "aimingStrategy", cosmicMochiBlasterAimingStrategy },
                        { "targetX", targetX.Value },
                        { "targetY", targetY.Value },
                        { "ticks", cosmicMochiBlasterShotTicks },
                        {
                            "relativeMovement", new Dictionary<string, object?>
                            {
                                { "x", cosmicMochiBlasterMovement.X },
                                { "y", cosmicMochiBlasterMovement.Y },
                                { "length", cosmicMochiBlasterMovement.Length }
                            }
                        },
                        { "load", cosmicMochiBlasterShotLoad },
                        { "damage", cosmicMochiBlasterShotDamage },
                        { "predictedMissDistance", dreamyPredictedMissDistance },
                        { "fallbackEstimatedMissDistance", dreamyFallbackEstimatedMissDistance },
                        { "fallbackCompensated", moonbeamFallbackCompensated }
                    };
                    break;
                }

                var angle = relativeAngle ?? 0f;
                var moonbeamAbsoluteAngle = classic.Angle + angle;
                var movement = Vector.FromAngleLength(moonbeamAbsoluteAngle, 2f);
                if (IsFireTraceEnabled())
                {
                    _logger.LogInformation(
                        "Shot fire(relative-angle) session={SessionId} command={CommandId} controllable={ControllableId} relativeAngle={RelativeAngle} absoluteAngle={AbsoluteAngle} normalizedAbsoluteAngle={NormalizedAbsoluteAngle} shotVectorX={ShotVectorX} shotVectorY={ShotVectorY} shotVectorLen={ShotVectorLen} ticks={Ticks} load={Load} damage={Damage} tick={Tick}",
                        _id,
                        commandId,
                        controllableId,
                        angle,
                        moonbeamAbsoluteAngle,
                        NormalizeAngleDegrees(moonbeamAbsoluteAngle),
                        movement.X,
                        movement.Y,
                        movement.Length,
                        80,
                        12f,
                        8f,
                        _latestGalaxyTick);
                }
                await classic.ShotLauncher.Shoot(movement, 80, 12f, 8f);
                break;
            case "railgun":
            case "Railgun":
                var moonbeamRailgunRelativeAngle = relativeAngle ?? 0f;
                var moonbeamRailgunNormalizedAngle = NormalizeAngleDegrees(moonbeamRailgunRelativeAngle);
                bool moonbeamFireBack = relativeAngle.HasValue && ShouldFireRailgunBack(relativeAngle.Value);
                if (IsFireTraceEnabled())
                {
                    _logger.LogInformation(
                        "Railgun fire decision session={SessionId} command={CommandId} controllable={ControllableId} relativeAngleProvided={RelativeAngleProvided} relativeAngle={RelativeAngle} normalizedRelativeAngle={NormalizedRelativeAngle} fireBack={FireBack} tick={Tick}",
                        _id,
                        commandId,
                        controllableId,
                        relativeAngle.HasValue,
                        moonbeamRailgunRelativeAngle,
                        moonbeamRailgunNormalizedAngle,
                        moonbeamFireBack,
                        _latestGalaxyTick);
                }

                if (moonbeamFireBack)
                    await classic.Railgun.FireBack();
                else
                    await classic.Railgun.FireFront();
                break;
        }

        return Completed(commandId, result);
    }

    private static Vector BuildDirectPointShotFallbackMovement(
        float moonbeamSourceX,
        float moonbeamSourceY,
        float moonbeamFallbackAngleDegrees,
        float moonbeamTargetX,
        float moonbeamTargetY,
        float moonbeamRelativeSpeed)
    {
        float puffDeltaX = moonbeamTargetX - moonbeamSourceX;
        float puffDeltaY = moonbeamTargetY - moonbeamSourceY;
        if (MathF.Abs(puffDeltaX) < 0.0001f && MathF.Abs(puffDeltaY) < 0.0001f)
            return Vector.FromAngleLength(moonbeamFallbackAngleDegrees, moonbeamRelativeSpeed);

        float dreamyAngleDegrees = MathF.Atan2(puffDeltaY, puffDeltaX) * (180f / MathF.PI);
        return Vector.FromAngleLength(dreamyAngleDegrees, moonbeamRelativeSpeed);
    }

    private static DirectPointShotFallbackPlan BuildDirectPointShotFallbackPlan(
        ClassicShipControllable moonbeamShip,
        float moonbeamTargetX,
        float moonbeamTargetY,
        float moonbeamPreferredRelativeSpeed,
        ushort moonbeamPreferredTicks,
        float moonbeamPreferredLoad,
        float moonbeamPreferredDamage)
    {
        float moonbeamSpeed = Math.Clamp(
            moonbeamPreferredRelativeSpeed,
            moonbeamShip.ShotLauncher.MinimumRelativeMovement,
            moonbeamShip.ShotLauncher.MaximumRelativeMovement);
        if (moonbeamSpeed <= 0.0001f)
            moonbeamSpeed = Math.Max(0.0001f, moonbeamShip.ShotLauncher.MinimumRelativeMovement);

        ushort moonbeamMinimumTicks = moonbeamShip.ShotLauncher.MinimumTicks;
        ushort moonbeamMaximumTicks = moonbeamShip.ShotLauncher.MaximumTicks;
        if (moonbeamMinimumTicks > moonbeamMaximumTicks)
            moonbeamMinimumTicks = moonbeamMaximumTicks;

        float moonbeamLoad = Math.Clamp(moonbeamPreferredLoad, moonbeamShip.ShotLauncher.MinimumLoad, moonbeamShip.ShotLauncher.MaximumLoad);
        float moonbeamDamage = Math.Clamp(moonbeamPreferredDamage, moonbeamShip.ShotLauncher.MinimumDamage, moonbeamShip.ShotLauncher.MaximumDamage);
        float moonbeamMinLoad = moonbeamShip.ShotLauncher.MinimumLoad;
        float moonbeamMinDamage = moonbeamShip.ShotLauncher.MinimumDamage;

        float moonbeamDeltaX = moonbeamTargetX - moonbeamShip.Position.X;
        float moonbeamDeltaY = moonbeamTargetY - moonbeamShip.Position.Y;
        float moonbeamDistance = MathF.Sqrt(moonbeamDeltaX * moonbeamDeltaX + moonbeamDeltaY * moonbeamDeltaY);

        int moonbeamBaselineTicks = moonbeamSpeed > 0.0001f
            ? (int)MathF.Round(moonbeamDistance / moonbeamSpeed)
            : moonbeamPreferredTicks;
        moonbeamBaselineTicks = Math.Clamp(moonbeamBaselineTicks, moonbeamMinimumTicks, moonbeamMaximumTicks);
        int moonbeamStartTicks = Math.Max(moonbeamMinimumTicks, moonbeamBaselineTicks - 22);
        int moonbeamEndTicks = Math.Min(moonbeamMaximumTicks, moonbeamBaselineTicks + 22);

        Vector moonbeamBestMovement = BuildDirectPointShotFallbackMovement(
            moonbeamShip.Position.X,
            moonbeamShip.Position.Y,
            moonbeamShip.Angle,
            moonbeamTargetX,
            moonbeamTargetY,
            moonbeamSpeed);
        ushort moonbeamBestTicks = (ushort)Math.Clamp(moonbeamPreferredTicks, moonbeamMinimumTicks, moonbeamMaximumTicks);
        float moonbeamBestLoad = moonbeamLoad;
        float moonbeamBestDamage = moonbeamDamage;
        float moonbeamBestMiss = ComputePointShotMissDistance(
            moonbeamShip.Position.X,
            moonbeamShip.Position.Y,
            moonbeamShip.Movement.X,
            moonbeamShip.Movement.Y,
            moonbeamShip.Size + DirectPointShotSpawnPaddingDistance,
            moonbeamBestMovement,
            moonbeamBestTicks,
            moonbeamTargetX,
            moonbeamTargetY);
        float moonbeamBestScore = moonbeamBestMiss + moonbeamBestTicks * DirectPointShotFallbackTickPenalty;
        bool moonbeamFoundAffordable = false;

        for (int moonbeamTicks = moonbeamStartTicks; moonbeamTicks <= moonbeamEndTicks; moonbeamTicks++)
        {
            Vector moonbeamCandidateMovement = BuildCompensatedPointShotMovement(
                moonbeamShip.Position.X,
                moonbeamShip.Position.Y,
                moonbeamShip.Movement.X,
                moonbeamShip.Movement.Y,
                moonbeamTargetX,
                moonbeamTargetY,
                moonbeamSpeed,
                moonbeamTicks,
                moonbeamShip.Size + DirectPointShotSpawnPaddingDistance);

            float moonbeamCandidateMiss = ComputePointShotMissDistance(
                moonbeamShip.Position.X,
                moonbeamShip.Position.Y,
                moonbeamShip.Movement.X,
                moonbeamShip.Movement.Y,
                moonbeamShip.Size + DirectPointShotSpawnPaddingDistance,
                moonbeamCandidateMovement,
                moonbeamTicks,
                moonbeamTargetX,
                moonbeamTargetY);

            float moonbeamCandidateLoad = moonbeamLoad;
            float moonbeamCandidateDamage = moonbeamDamage;
            bool moonbeamAffordable = IsShotCostAffordable(moonbeamShip, moonbeamCandidateMovement, (ushort)moonbeamTicks, moonbeamCandidateLoad, moonbeamCandidateDamage);
            if (!moonbeamAffordable && IsShotCostAffordable(moonbeamShip, moonbeamCandidateMovement, (ushort)moonbeamTicks, moonbeamMinLoad, moonbeamMinDamage))
            {
                moonbeamCandidateLoad = moonbeamMinLoad;
                moonbeamCandidateDamage = moonbeamMinDamage;
                moonbeamAffordable = true;
            }

            float moonbeamCandidateScore = moonbeamCandidateMiss + moonbeamTicks * DirectPointShotFallbackTickPenalty;
            if (moonbeamAffordable)
            {
                if (moonbeamFoundAffordable && moonbeamCandidateScore >= moonbeamBestScore - 0.0001f)
                    continue;

                moonbeamFoundAffordable = true;
                moonbeamBestMovement = moonbeamCandidateMovement;
                moonbeamBestTicks = (ushort)moonbeamTicks;
                moonbeamBestLoad = moonbeamCandidateLoad;
                moonbeamBestDamage = moonbeamCandidateDamage;
                moonbeamBestMiss = moonbeamCandidateMiss;
                moonbeamBestScore = moonbeamCandidateScore;
                continue;
            }

            if (moonbeamFoundAffordable || moonbeamCandidateScore >= moonbeamBestScore - 0.0001f)
                continue;

            moonbeamBestMovement = moonbeamCandidateMovement;
            moonbeamBestTicks = (ushort)moonbeamTicks;
            moonbeamBestMiss = moonbeamCandidateMiss;
            moonbeamBestScore = moonbeamCandidateScore;
        }

        return new DirectPointShotFallbackPlan(
            moonbeamBestMovement,
            moonbeamBestTicks,
            moonbeamBestLoad,
            moonbeamBestDamage,
            moonbeamBestMiss,
            UsesVelocityCompensation: true);
    }

    private static bool IsShotCostAffordable(
        ClassicShipControllable moonbeamShip,
        Vector moonbeamRelativeMovement,
        ushort moonbeamTicks,
        float moonbeamLoad,
        float moonbeamDamage)
    {
        if (!moonbeamShip.ShotLauncher.CalculateCost(moonbeamRelativeMovement, moonbeamTicks, moonbeamLoad, moonbeamDamage, out float moonbeamEnergyCost, out float moonbeamIonCost, out float moonbeamNeutrinoCost))
            return false;

        if (moonbeamEnergyCost > moonbeamShip.EnergyBattery.Current + 0.0001f)
            return false;

        if (moonbeamIonCost > moonbeamShip.IonBattery.Current + 0.0001f)
            return false;

        if (moonbeamNeutrinoCost > moonbeamShip.NeutrinoBattery.Current + 0.0001f)
            return false;

        return true;
    }

    private static Vector BuildCompensatedPointShotMovement(
        float moonbeamSourceX,
        float moonbeamSourceY,
        float moonbeamSourceMovementX,
        float moonbeamSourceMovementY,
        float moonbeamTargetX,
        float moonbeamTargetY,
        float moonbeamRelativeSpeed,
        int moonbeamTicks,
        float moonbeamLaunchOffsetDistance)
    {
        if (moonbeamTicks <= 0 || moonbeamRelativeSpeed <= 0.0001f)
            return new Vector();

        float moonbeamDeltaX = moonbeamTargetX - moonbeamSourceX;
        float moonbeamDeltaY = moonbeamTargetY - moonbeamSourceY;
        float moonbeamBaseRelativeX = moonbeamDeltaX / moonbeamTicks - moonbeamSourceMovementX;
        float moonbeamBaseRelativeY = moonbeamDeltaY / moonbeamTicks - moonbeamSourceMovementY;

        Vector moonbeamCandidate = new Vector(moonbeamBaseRelativeX, moonbeamBaseRelativeY);
        if (moonbeamCandidate.Length <= 0.0001f)
        {
            moonbeamCandidate = BuildDirectPointShotFallbackMovement(
                moonbeamSourceX,
                moonbeamSourceY,
                0f,
                moonbeamTargetX,
                moonbeamTargetY,
                moonbeamRelativeSpeed);
        }
        else
        {
            moonbeamCandidate.Length = moonbeamRelativeSpeed;
        }

        for (int moonbeamIteration = 0; moonbeamIteration < 3; moonbeamIteration++)
        {
            float moonbeamGlideX = moonbeamSourceMovementX + moonbeamCandidate.X;
            float moonbeamGlideY = moonbeamSourceMovementY + moonbeamCandidate.Y;
            float moonbeamGlideLength = MathF.Sqrt(moonbeamGlideX * moonbeamGlideX + moonbeamGlideY * moonbeamGlideY);
            float moonbeamLaunchX = moonbeamSourceX;
            float moonbeamLaunchY = moonbeamSourceY;
            if (moonbeamGlideLength > 0.0001f && moonbeamLaunchOffsetDistance > 0f)
            {
                float moonbeamInverseGlide = 1f / moonbeamGlideLength;
                moonbeamLaunchX += moonbeamGlideX * moonbeamInverseGlide * moonbeamLaunchOffsetDistance;
                moonbeamLaunchY += moonbeamGlideY * moonbeamInverseGlide * moonbeamLaunchOffsetDistance;
            }

            float moonbeamNeededRelativeX = (moonbeamTargetX - moonbeamLaunchX) / moonbeamTicks - moonbeamSourceMovementX;
            float moonbeamNeededRelativeY = (moonbeamTargetY - moonbeamLaunchY) / moonbeamTicks - moonbeamSourceMovementY;
            Vector moonbeamNeededMovement = new Vector(moonbeamNeededRelativeX, moonbeamNeededRelativeY);
            if (moonbeamNeededMovement.Length <= 0.0001f)
                break;

            moonbeamNeededMovement.Length = moonbeamRelativeSpeed;
            moonbeamCandidate = moonbeamNeededMovement;
        }

        return moonbeamCandidate;
    }

    private static float ComputePointShotMissDistance(
        float moonbeamSourceX,
        float moonbeamSourceY,
        float moonbeamSourceMovementX,
        float moonbeamSourceMovementY,
        float moonbeamLaunchOffsetDistance,
        Vector moonbeamRelativeMovement,
        int moonbeamTicks,
        float moonbeamTargetX,
        float moonbeamTargetY)
    {
        if (moonbeamTicks <= 0)
            return float.MaxValue;

        float moonbeamGlideX = moonbeamSourceMovementX + moonbeamRelativeMovement.X;
        float moonbeamGlideY = moonbeamSourceMovementY + moonbeamRelativeMovement.Y;
        float moonbeamLaunchX = moonbeamSourceX;
        float moonbeamLaunchY = moonbeamSourceY;
        float moonbeamGlideLength = MathF.Sqrt(moonbeamGlideX * moonbeamGlideX + moonbeamGlideY * moonbeamGlideY);
        if (moonbeamGlideLength > 0.0001f && moonbeamLaunchOffsetDistance > 0f)
        {
            float moonbeamInverseGlide = 1f / moonbeamGlideLength;
            moonbeamLaunchX += moonbeamGlideX * moonbeamInverseGlide * moonbeamLaunchOffsetDistance;
            moonbeamLaunchY += moonbeamGlideY * moonbeamInverseGlide * moonbeamLaunchOffsetDistance;
        }

        float moonbeamProjectedX = moonbeamLaunchX + moonbeamGlideX * moonbeamTicks;
        float moonbeamProjectedY = moonbeamLaunchY + moonbeamGlideY * moonbeamTicks;
        float moonbeamMissX = moonbeamTargetX - moonbeamProjectedX;
        float moonbeamMissY = moonbeamTargetY - moonbeamProjectedY;
        return MathF.Sqrt(moonbeamMissX * moonbeamMissX + moonbeamMissY * moonbeamMissY);
    }

    private static bool ShouldFireRailgunBack(float moonbeamRelativeAngleDegrees)
    {
        float sparklyNormalizedAngle = NormalizeAngleDegrees(moonbeamRelativeAngleDegrees);
        return sparklyNormalizedAngle > 90f && sparklyNormalizedAngle < 270f;
    }

    private static float NormalizeAngleDegrees(float moonbeamAngleDegrees)
    {
        return ((moonbeamAngleDegrees % 360f) + 360f) % 360f;
    }

    private async Task<CommandReplyMessage> HandleSetSubsystemMode(string commandId, System.Text.Json.JsonElement? payload)
    {
        var controllableId = payload?.GetProperty("controllableId").GetString() ?? "";
        var subsystemId = payload?.GetProperty("subsystemId").GetString() ?? "";
        var mode = payload?.GetProperty("mode").GetString() ?? "";
        var controllable = FindControllable(controllableId);
        if (controllable is null)
            return Rejected(commandId, "invalid_controllable", "Controllable not found.");

        var classic = controllable as ClassicShipControllable;
        var modern = controllable as ModernShipControllable;
        var subsystem = string.IsNullOrWhiteSpace(subsystemId)
            ? null
            : FindSubsystem(controllable, subsystemId);
        if (subsystem is DynamicShotFabricatorSubsystem)
            subsystemId = "ShotFabricator";
        else if (subsystem is DynamicInterceptorFabricatorSubsystem)
            subsystemId = "InterceptorFabricator";
        else if (subsystem is ResourceMinerSubsystem)
            subsystemId = "ResourceMiner";
        else if (subsystem is NebulaCollectorSubsystem)
            subsystemId = "NebulaCollector";

        bool tacticalOnlyMode =
            string.Equals(subsystemId, "Tactical", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(subsystemId, "TacticalModule", StringComparison.OrdinalIgnoreCase);
        if (tacticalOnlyMode)
        {
            if (mode != "off" && RejectIfControllableRebuilding(commandId, controllable) is { } rebuildingReject)
                return rebuildingReject;
        }
        else
        {
            if (RejectIfControllableRebuilding(commandId, controllable) is { } rebuildingReject)
                return rebuildingReject;
        }

        switch (subsystemId)
        {
            case "MainScanner":
            case "scanner":
                if (classic is null)
                    return Rejected(commandId, "unsupported_subsystem", "Scanner mode control currently requires a classic ship.");

                if (mode == "off")
                {
                    await _scanningService.ApplyAsync(classic, ScanningService.ScannerMode.Off);
                }
                else if (mode == "on")
                {
                    await _scanningService.ApplyAsync(classic, ScanningService.ScannerMode.Forward);
                }
                else if (mode == "set")
                {
                    float width = 90f;
                    if (payload?.TryGetProperty("value", out var valEl) == true && valEl.ValueKind != System.Text.Json.JsonValueKind.Null)
                        width = valEl.GetSingle();
                    var scanMode = width >= 180f
                        ? ScanningService.ScannerMode.Full
                        : ScanningService.ScannerMode.Forward;
                    await _scanningService.ApplyAsync(classic, scanMode, width);
                }
                else if (mode == "target")
                {
                    var targetUnitId = payload?.TryGetProperty("targetId", out var scannerTargetEl) == true
                        ? scannerTargetEl.GetString()
                        : null;
                    if (string.IsNullOrWhiteSpace(targetUnitId))
                        return Rejected(commandId, "missing_target", "targetId is required for scanner target mode.");

                    float? width = null;
                    if (payload?.TryGetProperty("width", out var widthEl) == true &&
                        widthEl.ValueKind != System.Text.Json.JsonValueKind.Null &&
                        widthEl.ValueKind != System.Text.Json.JsonValueKind.Undefined)
                    {
                        width = widthEl.GetSingle();
                    }

                    float? length = null;
                    if (payload?.TryGetProperty("length", out var lengthEl) == true &&
                        lengthEl.ValueKind != System.Text.Json.JsonValueKind.Null &&
                        lengthEl.ValueKind != System.Text.Json.JsonValueKind.Undefined)
                    {
                        length = lengthEl.GetSingle();
                    }

                    await _scanningService.ApplyTargetModeAsync(classic, targetUnitId, width, length);
                }
                break;
            case "ResourceMiner":
            case "resourceMiner":
            case "Resource Miner":
                if (!controllable.ResourceMiner.Exists)
                    return Rejected(commandId, "unsupported_subsystem", "This controllable has no installed resource miner.");

                if (mode == "on")
                {
                    await controllable.ResourceMiner.Set(controllable.ResourceMiner.MaximumRate);
                }
                else if (mode == "off")
                {
                    await controllable.ResourceMiner.Off();
                }
                else if (mode == "set")
                {
                    if (payload?.TryGetProperty("value", out var rateEl) != true || rateEl.ValueKind == System.Text.Json.JsonValueKind.Null)
                        return Rejected(commandId, "missing_value", "value is required when setting a resource miner rate.");

                    await controllable.ResourceMiner.Set(rateEl.GetSingle());
                }
                else
                {
                    return Rejected(commandId, "invalid_mode", $"Unsupported resource miner mode: {mode}");
                }
                break;
            case "NebulaCollector":
            case "nebulaCollector":
            case "Nebula Collector":
            {
                var collector = classic?.NebulaCollector ?? modern?.NebulaCollector;
                if (collector is null || !collector.Exists)
                    return Rejected(commandId, "unsupported_subsystem", "This controllable has no installed nebula collector.");

                if (mode == "on")
                {
                    await collector.Set(collector.MaximumRate);
                }
                else if (mode == "off")
                {
                    await collector.Off();
                }
                else if (mode == "set")
                {
                    if (payload?.TryGetProperty("value", out var rateEl) != true || rateEl.ValueKind == System.Text.Json.JsonValueKind.Null)
                        return Rejected(commandId, "missing_value", "value is required when setting a nebula collector rate.");

                    await collector.Set(rateEl.GetSingle());
                }
                else
                {
                    return Rejected(commandId, "invalid_mode", $"Unsupported nebula collector mode: {mode}");
                }
                break;
            }
            case "ShotFabricator":
            case "shotFabricator":
            case "Shot Fabricator":
                if (classic is null)
                    return Rejected(commandId, "unsupported_subsystem", "Shot fabricator control currently requires a classic ship.");

                if (mode == "on") await classic.ShotFabricator.On();
                else if (mode == "off") await classic.ShotFabricator.Off();
                else if (mode == "set")
                {
                    if (payload?.TryGetProperty("value", out var rateEl) != true || rateEl.ValueKind == System.Text.Json.JsonValueKind.Null)
                        return Rejected(commandId, "missing_value", "value is required when setting a shot fabricator rate.");

                    await classic.ShotFabricator.Set(rateEl.GetSingle());
                }
                else
                    return Rejected(commandId, "invalid_mode", $"Unsupported shot fabricator mode: {mode}");
                break;
            case "InterceptorFabricator":
            case "interceptorFabricator":
            case "Interceptor Fabricator":
                if (classic is null)
                    return Rejected(commandId, "unsupported_subsystem", "Interceptor fabricator control currently requires a classic ship.");

                if (mode == "on") await classic.InterceptorFabricator.On();
                else if (mode == "off") await classic.InterceptorFabricator.Off();
                else if (mode == "set")
                {
                    if (payload?.TryGetProperty("value", out var rateEl) != true || rateEl.ValueKind == System.Text.Json.JsonValueKind.Null)
                        return Rejected(commandId, "missing_value", "value is required when setting an interceptor fabricator rate.");

                    await classic.InterceptorFabricator.Set(rateEl.GetSingle());
                }
                else
                    return Rejected(commandId, "invalid_mode", $"Unsupported interceptor fabricator mode: {mode}");
                break;
            case "Tactical":
            case "tactical":
            case "TacticalModule":
            case "tacticalModule":
                if (classic is null)
                    return Rejected(commandId, "unsupported_subsystem", "Tactical control currently requires a classic ship.");

                var tacticalMode = mode == "enemy"
                    ? TacticalService.TacticalMode.Enemy
                    : mode == "target"
                        ? TacticalService.TacticalMode.Target
                        : TacticalService.TacticalMode.Off;
                _tacticalService.AttachControllable(controllableId, classic);
                _tacticalService.SetMode(controllableId, tacticalMode);
                if (payload?.TryGetProperty("targetId", out var targetEl) == true)
                {
                    var targetId = targetEl.GetString();
                    if (!string.IsNullOrWhiteSpace(targetId))
                    {
                        if (tacticalMode == TacticalService.TacticalMode.Target &&
                            !_tacticalService.IsTargetAllowedForTargetMode(classic, targetId))
                        {
                            return Rejected(commandId, "friendly_target_not_allowed",
                                "Teammates cannot be targeted in tactical target mode.");
                        }

                        _tacticalService.SetTarget(controllableId, targetId);
                    }
                }
                break;
            default:
                return Rejected(commandId, subsystem is null ? "invalid_subsystem" : "unsupported_subsystem", $"Unsupported subsystem mode control for: {subsystemId}");
        }

        return Completed(commandId);
    }

    private async Task<CommandReplyMessage> HandleUpgradeSubsystem(string commandId, System.Text.Json.JsonElement? payload)
    {
        var controllableId = payload?.GetProperty("controllableId").GetString() ?? "";
        var subsystemId = payload?.GetProperty("subsystemId").GetString() ?? "";
        var controllable = FindControllable(controllableId);
        if (controllable is null)
            return Rejected(commandId, "invalid_controllable", "Controllable not found.");
        if (RejectIfControllableRebuilding(commandId, controllable) is { } rebuildingReject)
            return rebuildingReject;

        var subsystem = FindSubsystem(controllable, subsystemId);
        if (subsystem is null)
            return Rejected(commandId, "invalid_subsystem", $"Subsystem not found: {subsystemId}");

        if (subsystem.RemainingTierChangeTicks > 0)
            return Rejected(commandId, "subsystem_busy", "Subsystem is already changing tier.");

        var nextTier = ResolveNextTier(subsystem);
        if (nextTier <= subsystem.Tier)
            return Rejected(commandId, "max_tier", "Subsystem is already at maximum tier.");

        await subsystem.Upgrade();
        return Completed(commandId);
    }

    private Task<CommandReplyMessage> HandleSetTacticalMode(string commandId, System.Text.Json.JsonElement? payload)
    {
        var controllableId = payload?.GetProperty("controllableId").GetString() ?? "";
        var mode = payload?.GetProperty("mode").GetString() ?? "";
        var controllable = FindControllable(controllableId);
        if (controllable is null)
            return Task.FromResult(Rejected(commandId, "invalid_controllable", "Controllable not found."));

        if (mode != "enemy" && mode != "target" && mode != "off")
            return Task.FromResult(Rejected(commandId, "invalid_mode", "Tactical mode must be one of: enemy, target, off."));
        if (mode != "off" && RejectIfControllableRebuilding(commandId, controllable) is { } rebuildingReject)
            return Task.FromResult(rebuildingReject);

        var tacticalMode = mode == "enemy"
            ? TacticalService.TacticalMode.Enemy
            : mode == "target"
                ? TacticalService.TacticalMode.Target
                : TacticalService.TacticalMode.Off;

        if (controllable is ClassicShipControllable classic)
            _tacticalService.AttachControllable(controllableId, classic);

        _tacticalService.SetMode(controllableId, tacticalMode);
        return Task.FromResult(Completed(commandId));
    }

    private async Task<CommandReplyMessage> HandleSetTacticalTarget(string commandId, System.Text.Json.JsonElement? payload)
    {
        var controllableId = payload?.GetProperty("controllableId").GetString() ?? "";
        var targetId = payload?.GetProperty("targetId").GetString() ?? "";
        var controllable = FindControllable(controllableId);
        if (controllable is null)
            return Rejected(commandId, "invalid_controllable", "Controllable not found.");
        if (RejectIfControllableRebuilding(commandId, controllable) is { } rebuildingReject)
            return rebuildingReject;

        if (string.IsNullOrWhiteSpace(targetId))
            return Rejected(commandId, "invalid_target", "Target id is required.");

        if (controllable is ClassicShipControllable classic)
        {
            _tacticalService.AttachControllable(controllableId, classic);
            if (!_tacticalService.IsTargetAllowedForTargetMode(classic, targetId))
                return Rejected(commandId, "friendly_target_not_allowed", "Teammates cannot be targeted in tactical target mode.");
        }

        _tacticalService.SetTarget(controllableId, targetId);

        if (controllable is not ClassicShipControllable ship)
            return Completed(commandId);

        if (!_tacticalService.TryBuildTargetBurstRequest(controllableId, _latestGalaxyTick, out var burstRequest))
        {
            return Completed(commandId, new Dictionary<string, object?>
            {
                { "action", "set_tactical_target" },
                { "targetId", targetId },
                { "calculationSucceeded", false },
                { "firedShots", 0 }
            });
        }

        int shotsPlanned = Math.Max(0, (int)MathF.Floor(ship.ShotMagazine.CurrentShots));
        int firedShots = 0;

        for (int shotIndex = 0; shotIndex < shotsPlanned; shotIndex++)
        {
            try
            {
                await ship.ShotLauncher.Shoot(burstRequest.RelativeMovement, burstRequest.Ticks, burstRequest.Load, burstRequest.Damage);
                firedShots++;
            }
            catch (GameException ex)
            {
                _logger.LogDebug(ex, "Burst fire interrupted for controllable {ControllableId}", controllableId);
                break;
            }
        }

        if (firedShots > 0)
            _tacticalService.RegisterSuccessfulFire(controllableId, burstRequest.Tick);

        return Completed(commandId, new Dictionary<string, object?>
        {
            { "action", "set_tactical_target" },
            { "targetId", targetId },
            { "calculationSucceeded", true },
            { "shotsPlanned", shotsPlanned },
            { "firedShots", firedShots }
        });
    }

    private Task<CommandReplyMessage> HandleClearTacticalTarget(string commandId, System.Text.Json.JsonElement? payload)
    {
        var controllableId = payload?.GetProperty("controllableId").GetString() ?? "";
        var controllable = FindControllable(controllableId);
        if (controllable is null)
            return Task.FromResult(Rejected(commandId, "invalid_controllable", "Controllable not found."));

        _tacticalService.ClearTarget(controllableId);
        return Task.FromResult(Completed(commandId));
    }

    private async Task<CommandReplyMessage> HandleDestroyShip(string commandId, System.Text.Json.JsonElement? payload)
    {
        var controllableId = payload?.GetProperty("controllableId").GetString() ?? "";
        var controllable = FindControllable(controllableId);
        if (controllable is null)
            return Rejected(commandId, "invalid_controllable", "Controllable not found.");

        await controllable.Suicide();
        return Completed(commandId);
    }

    private async Task<CommandReplyMessage> HandleContinueShip(string commandId, System.Text.Json.JsonElement? payload)
    {
        var controllableId = payload?.GetProperty("controllableId").GetString() ?? "";
        var controllable = FindControllable(controllableId);
        if (controllable is null)
            return Rejected(commandId, "invalid_controllable", "Controllable not found.");

        await controllable.Continue();
        return Completed(commandId);
    }

    private CommandReplyMessage HandleRemoveShip(string commandId, System.Text.Json.JsonElement? payload)
    {
        var controllableId = payload?.GetProperty("controllableId").GetString() ?? "";
        var controllable = FindControllable(controllableId);
        if (controllable is null)
            return Rejected(commandId, "invalid_controllable", "Controllable not found.");

        controllable.RequestClose();
        return Completed(commandId);
    }

    private Controllable? FindControllable(string controllableId)
    {
        // controllableId format: "p{playerId}-c{controllableId}"
        var galaxy = Galaxy;
        if (galaxy is null) return null;

        foreach (var c in galaxy.Controllables)
        {
            if (c is null) continue;
            if ($"p{galaxy.Player.Id}-c{c.Id}" == controllableId)
                return c;
        }

        return null;
    }

    private static Subsystem? FindSubsystem(Controllable controllable, string subsystemId)
    {
        return EnumerateSubsystems(controllable)
            .FirstOrDefault(subsystem =>
                string.Equals(subsystem.Name, subsystemId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(subsystem.Slot.ToString(), subsystemId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(subsystem.Kind.ToString(), subsystemId, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<Subsystem> EnumerateSubsystems(Controllable controllable)
    {
        yield return controllable.EnergyBattery;
        yield return controllable.IonBattery;
        yield return controllable.NeutrinoBattery;
        yield return controllable.EnergyCell;
        yield return controllable.IonCell;
        yield return controllable.NeutrinoCell;
        yield return controllable.Hull;
        yield return controllable.Shield;
        yield return controllable.Armor;
        yield return controllable.Repair;
        yield return controllable.Cargo;
        yield return controllable.ResourceMiner;
        yield return controllable.StructureOptimizer;

        switch (controllable)
        {
            case ClassicShipControllable classic:
                yield return classic.NebulaCollector;
                yield return classic.Engine;
                yield return classic.MainScanner;
                yield return classic.SecondaryScanner;
                yield return classic.ShotLauncher;
                yield return classic.ShotMagazine;
                yield return classic.ShotFabricator;
                yield return classic.InterceptorLauncher;
                yield return classic.InterceptorMagazine;
                yield return classic.InterceptorFabricator;
                yield return classic.Railgun;
                yield return classic.JumpDrive;
                break;
            case ModernShipControllable modern:
                yield return modern.NebulaCollector;
                foreach (var subsystem in modern.Engines) yield return subsystem;
                foreach (var subsystem in modern.Scanners) yield return subsystem;
                foreach (var subsystem in modern.ShotLaunchers) yield return subsystem;
                foreach (var subsystem in modern.ShotMagazines) yield return subsystem;
                foreach (var subsystem in modern.ShotFabricators) yield return subsystem;
                yield return modern.InterceptorLauncherE;
                yield return modern.InterceptorLauncherW;
                yield return modern.InterceptorMagazineE;
                yield return modern.InterceptorMagazineW;
                yield return modern.InterceptorFabricatorE;
                yield return modern.InterceptorFabricatorW;
                foreach (var subsystem in modern.Railguns) yield return subsystem;
                yield return modern.JumpDrive;
                break;
        }
    }

    private static bool TryParseControllableLocalId(string controllableId, out int localControllableId)
    {
        localControllableId = 0;
        var markerIndex = controllableId.LastIndexOf("-c", StringComparison.Ordinal);
        if (markerIndex < 0 || markerIndex + 2 >= controllableId.Length)
            return false;

        return int.TryParse(controllableId[(markerIndex + 2)..], out localControllableId);
    }

    private ScanningService.TargetSnapshot? ResolveScanTarget(string targetUnitId)
    {
        var galaxy = Galaxy;
        if (galaxy is null || string.IsNullOrWhiteSpace(targetUnitId))
            return null;

        if (TryParseControllableLocalId(targetUnitId, out var localControllableId))
        {
            var controllable = galaxy.Controllables.FirstOrDefault(item => item is not null && item.Id == localControllableId);
            if (controllable is not null)
            {
                return new ScanningService.TargetSnapshot(
                    controllable.Position.X,
                    controllable.Position.Y,
                    controllable.Movement.X,
                    controllable.Movement.Y,
                    HasVelocity: true);
            }
        }

        if (_mappingService.TryGetUnitSnapshot(targetUnitId, out var unitSnapshot) && unitSnapshot is not null)
        {
            return new ScanningService.TargetSnapshot(
                unitSnapshot.X,
                unitSnapshot.Y,
                VelocityX: 0f,
                VelocityY: 0f,
                HasVelocity: false);
        }

        return null;
    }

    private MappingService.MappingScopeContext? BuildMappingScopeContext()
    {
        var galaxy = Galaxy;
        if (galaxy is null)
            return null;

        var clusterId = ResolveCurrentClusterId(galaxy);
        var galaxyId = $"{_galaxyUrl}|{galaxy.Name}";
        return new MappingService.MappingScopeContext(galaxyId, clusterId, galaxy.Player.Id);
    }

    private static int ResolveCurrentClusterId(Galaxy galaxy)
    {
        foreach (var controllable in galaxy.Controllables)
        {
            if (controllable is { Active: true, Cluster: not null })
                return controllable.Cluster.Id;
        }

        foreach (var controllable in galaxy.Controllables)
        {
            if (controllable?.Cluster is not null)
                return controllable.Cluster.Id;
        }

        return 0;
    }

    /// <summary>
    /// Called by TickService every 40ms. Collects pending deltas from
    /// MappingService and overlay state, then delivers to browser connections.
    /// </summary>
    public void FlushTickDeltas()
    {
        List<BrowserConnection> connections;
        lock (_lock)
            connections = _attachedConnections.ToList();

        if (connections.Count == 0) return;

        // Collect and broadcast pending world deltas
        var worldDeltas = _mappingService.CollectPendingDeltas();
        BroadcastWorldDelta(connections, worldDeltas);

        // Broadcast owner overlay
        BroadcastOwnerOverlay(connections);
    }

    /// <summary>
    /// IConnectorEventHandler — receives events from the ConnectorEventLoop.
    /// Handles session-specific events (chat, controllable lifecycle, connection terminated).
    /// Unit events are handled by MappingService (registered as a separate handler).
    /// </summary>
    public void Handle(FlattiverseEvent @event)
    {
        if (@event is GalaxyTickEvent tickEvent)
        {
            _latestGalaxyTick = tickEvent.Tick;
            DispatchPendingAutoFireRequests();
            return;
        }

        List<BrowserConnection> connections;
        lock (_lock)
            connections = _attachedConnections.ToList();

        switch (@event)
        {

            case ChatEvent chatEvent:
                var scope = chatEvent switch
                {
                    TeamChatEvent => "team",
                    PlayerChatEvent => "private",
                    _ => "galaxy"
                };
                var chatMsg = new ChatReceivedMessage
                {
                    Entry = new ChatEntryDto
                    {
                        MessageId = Guid.NewGuid().ToString("N"),
                        Scope = scope,
                        SenderDisplayName = chatEvent.Player.Name,
                        PlayerSessionId = _id,
                        Message = chatEvent.Message,
                        SentAtUtc = DateTime.UtcNow.ToString("O")
                    }
                };
                foreach (var conn in connections)
                    conn.EnqueueMessage(chatMsg);
                break;

            case RegisteredControllableInfoEvent registered:
                var regDelta = new WorldDeltaDto
                {
                    EventType = "controllable.created",
                    EntityId = $"p{registered.Player.Id}-c{registered.ControllableInfo.Id}",
                    Changes = new Dictionary<string, object?>
                    {
                        { "controllableId", $"p{registered.Player.Id}-c{registered.ControllableInfo.Id}" },
                        { "displayName", registered.ControllableInfo.Name },
                        { "teamName", registered.Player.Team?.Name ?? "" },
                        { "alive", registered.ControllableInfo.Alive },
                        { "score", registered.ControllableInfo.Score.Mission }
                    }
                };
                BroadcastWorldDelta(connections, new List<WorldDeltaDto> { regDelta });
                break;

            case ContinuedControllableInfoEvent continued:
            {
                var cDelta = new WorldDeltaDto
                {
                    EventType = "controllable.created",
                    EntityId = $"p{continued.Player.Id}-c{continued.ControllableInfo.Id}",
                    Changes = new Dictionary<string, object?>
                    {
                        { "controllableId", $"p{continued.Player.Id}-c{continued.ControllableInfo.Id}" },
                        { "displayName", continued.ControllableInfo.Name },
                        { "teamName", continued.Player.Team?.Name ?? "" },
                        { "alive", continued.ControllableInfo.Alive },
                        { "score", continued.ControllableInfo.Score.Mission }
                    }
                };
                BroadcastWorldDelta(connections, new List<WorldDeltaDto> { cDelta });

                // Re-apply remembered scanner mode on respawn for own controllables
                if (continued.Player.Id == Galaxy?.Player?.Id)
                {
                    foreach (var c in Galaxy!.Controllables)
                    {
                        if (c is ClassicShipControllable respawnedShip && c.Id == continued.ControllableInfo.Id)
                        {
                            _tacticalService.AttachControllable($"p{continued.Player.Id}-c{continued.ControllableInfo.Id}", respawnedShip);
                            ObserveBackgroundTask(_scanningService.ReapplyModeAsync(respawnedShip));
                            _maneuveringService.RebindShip(respawnedShip);
                            _pathfindingService.RebindShip(respawnedShip);
                            break;
                        }
                    }
                }
                break;
            }

            case DestroyedControllableInfoEvent destroyedEvent:
            {
                var destroyedDelta = new WorldDeltaDto
                {
                    EventType = "controllable.created",
                    EntityId = $"p{destroyedEvent.Player.Id}-c{destroyedEvent.ControllableInfo.Id}",
                    Changes = new Dictionary<string, object?>
                    {
                        { "controllableId", $"p{destroyedEvent.Player.Id}-c{destroyedEvent.ControllableInfo.Id}" },
                        { "displayName", destroyedEvent.ControllableInfo.Name },
                        { "teamName", destroyedEvent.Player.Team?.Name ?? "" },
                        { "alive", destroyedEvent.ControllableInfo.Alive },
                        { "score", destroyedEvent.ControllableInfo.Score.Mission }
                    }
                };
                BroadcastWorldDelta(connections, new List<WorldDeltaDto> { destroyedDelta });

                // Auto-continue own controllables when they die
                if (destroyedEvent.Player.Id == Galaxy?.Player?.Id)
                {
                    foreach (var c in Galaxy!.Controllables)
                    {
                        if (c is null || c.Id != destroyedEvent.ControllableInfo.Id)
                            continue;
                        ObserveBackgroundTask(c.Continue());
                        break;
                    }
                }
                break;
            }

            case ClosedControllableInfoEvent:
            case UpdatedControllableInfoScoreEvent:
                // Re-broadcast full controllable state on these events; the tick overlay will handle owner side
                if (@event is ControllableInfoEvent ciEvent)
                {
                    var evtType = @event switch
                    {
                        ClosedControllableInfoEvent => "unit.removed",
                        _ => "controllable.created"
                    };
                    var cDelta2 = new WorldDeltaDto
                    {
                        EventType = evtType,
                        EntityId = $"p{ciEvent.Player.Id}-c{ciEvent.ControllableInfo.Id}",
                        Changes = new Dictionary<string, object?>
                        {
                            { "controllableId", $"p{ciEvent.Player.Id}-c{ciEvent.ControllableInfo.Id}" },
                            { "displayName", ciEvent.ControllableInfo.Name },
                            { "teamName", ciEvent.Player.Team?.Name ?? "" },
                            { "alive", ciEvent.ControllableInfo.Alive },
                            { "score", ciEvent.ControllableInfo.Score.Mission }
                        }
                    };
                    BroadcastWorldDelta(connections, new List<WorldDeltaDto> { cDelta2 });
                }
                break;

            case CreatedTeamEvent createdTeam:
                BroadcastWorldDelta(connections, new List<WorldDeltaDto> { BuildTeamDelta("team.created", createdTeam.Team) });
                break;

            case UpdatedTeamEvent updatedTeam:
                BroadcastWorldDelta(connections, new List<WorldDeltaDto> { BuildTeamDelta("team.updated", updatedTeam.New) });
                break;

            case UpdatedTeamScoreEvent updatedTeamScore:
                BroadcastWorldDelta(connections, new List<WorldDeltaDto> { BuildTeamDelta("team.updated", updatedTeamScore.Team) });
                break;

            case RemovedTeamEvent removedTeam:
                BroadcastWorldDelta(connections, new List<WorldDeltaDto> { BuildTeamDelta("team.removed", removedTeam.Team) });
                break;
        }
    }

    private WorldDeltaDto BuildTeamDelta(string eventType, Team team)
    {
        return new WorldDeltaDto
        {
            EventType = eventType,
            EntityId = team.Id.ToString(),
            Changes = new Dictionary<string, object?>
            {
                { "id", team.Id },
                { "name", team.Name },
                { "score", team.Score.Mission },
                { "colorHex", $"#{team.Red:X2}{team.Green:X2}{team.Blue:X2}" },
                { "playable", team.Playable }
            }
        };
    }

    private WorldDeltaDto BuildTeamDelta(string eventType, TeamSnapshot team)
    {
        var liveScore = Galaxy?.Teams.FirstOrDefault(candidate => candidate.Id == team.Id)?.Score.Mission ?? 0;

        return new WorldDeltaDto
        {
            EventType = eventType,
            EntityId = team.Id.ToString(),
            Changes = new Dictionary<string, object?>
            {
                { "id", team.Id },
                { "name", team.Name },
                { "score", liveScore },
                { "colorHex", $"#{team.Red:X2}{team.Green:X2}{team.Blue:X2}" },
                { "playable", team.Playable }
            }
        };
    }

    private void DispatchPendingAutoFireRequests()
    {
        var requests = _tacticalService.DequeuePendingAutoFireRequests();
        if (requests.Count == 0)
            return;

        foreach (var request in requests)
        {
            bool canStart;
            lock (_autoFireSync)
                canStart = _autoFireInFlight.Add(request.ControllableId);

            if (!canStart)
                continue;

            _ = ExecuteAutoFireAsync(request);
        }
    }

    private async Task ExecuteAutoFireAsync(TacticalService.AutoFireRequest request)
    {
        try
        {
            if (ControllableRebuildState.IsRebuilding(request.Ship))
            {
                _logger.LogDebug("Auto-fire skipped for rebuilding controllable {ControllableId}", request.ControllableId);
                return;
            }

            float moonbeamAutoFireVectorAngle = NormalizeAngleDegrees(MathF.Atan2(request.RelativeMovement.Y, request.RelativeMovement.X) * (180f / MathF.PI));
            if (IsFireTraceEnabled())
            {
                _logger.LogInformation(
                    "Auto-fire execute session={SessionId} controllable={ControllableId} target={Target} shotVectorX={ShotVectorX} shotVectorY={ShotVectorY} shotVectorLen={ShotVectorLen} shotVectorAngle={ShotVectorAngle} ticks={Ticks} load={Load} damage={Damage} predictedMissDistance={PredictedMissDistance} tick={Tick}",
                    _id,
                    request.ControllableId,
                    request.TargetUnitName,
                    request.RelativeMovement.X,
                    request.RelativeMovement.Y,
                    request.RelativeMovement.Length,
                    moonbeamAutoFireVectorAngle,
                    request.Ticks,
                    request.Load,
                    request.Damage,
                    request.PredictedMissDistance,
                    request.Tick);
            }

            await request.Ship.ShotLauncher.Shoot(request.RelativeMovement, request.Ticks, request.Load, request.Damage)
                .ConfigureAwait(false);
            _tacticalService.RegisterSuccessfulFire(request.ControllableId, request.Tick);
        }
        catch (GameException ex)
        {
            _logger.LogDebug(ex, "Auto-fire rejected for controllable {ControllableId}", request.ControllableId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto-fire failed for controllable {ControllableId}", request.ControllableId);
        }
        finally
        {
            lock (_autoFireSync)
                _autoFireInFlight.Remove(request.ControllableId);
        }
    }

    private bool IsFireTraceEnabled()
    {
        return FireTraceEnabledByEnv && _logger.IsEnabled(LogLevel.Information);
    }

    private void BroadcastWorldDelta(List<BrowserConnection> connections, List<WorldDeltaDto> deltas)
    {
        if (deltas.Count == 0) return;
        var msg = new WorldDeltaMessage { Events = deltas };
        foreach (var conn in connections)
        {
            if (conn.SelectedSessionId != _id)
                continue;

            conn.EnqueueMessage(msg);
        }
    }

    private void BroadcastOwnerOverlay(List<BrowserConnection> connections)
    {
        var events = BuildOverlaySnapshot();

        if (events.Count == 0) return;

        var msg = new OwnerDeltaMessage
        {
            PlayerSessionId = _id,
            Events = events
        };

        foreach (var conn in connections)
            conn.EnqueueMessage(msg);
    }

    private static CommandReplyMessage Completed(string commandId, Dictionary<string, object?>? result = null)
    {
        return new CommandReplyMessage { CommandId = commandId, Status = "completed", Result = result };
    }

    private static CommandReplyMessage Rejected(string commandId, string code, string message)
    {
        return new CommandReplyMessage
        {
            CommandId = commandId,
            Status = "rejected",
            Error = new ErrorInfoDto { Code = code, Message = message, Recoverable = true }
        };
    }

    private static CommandReplyMessage? RejectIfControllableRebuilding(string commandId, Controllable controllable)
    {
        if (!ControllableRebuildState.IsRebuilding(controllable))
            return null;

        return Rejected(commandId, ControllableRebuildingErrorCode, ControllableRebuildingErrorMessage);
    }

    private static void ObserveBackgroundTask(Task task)
    {
        if (task.IsCompleted)
        {
            _ = task.Exception;
            return;
        }

        _ = task.ContinueWith(
            static completedTask => _ = completedTask.Exception,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
    }

    public void Dispose()
    {
        TeamOverlaySyncService.RemoveSession(_id);

        if (_connectionManager is not null)
        {
            _connectionManager.ConnectionLost -= OnConnectionLost;
            _connectionManager.Dispose();
        }

        _connectLock.Dispose();
    }
}

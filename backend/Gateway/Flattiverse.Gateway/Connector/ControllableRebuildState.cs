using Flattiverse.Connector.GalaxyHierarchy;

namespace Flattiverse.Gateway.Connector;

internal static class ControllableRebuildState
{
    public static bool IsRebuilding(Controllable controllable)
    {
        return GetRemainingTicks(controllable) > 0;
    }

    public static ushort GetRemainingTicks(Controllable controllable)
    {
        ushort remainingTicks = 0;
        foreach (var subsystem in EnumerateSubsystems(controllable))
        {
            if (subsystem.RemainingTierChangeTicks > remainingTicks)
                remainingTicks = subsystem.RemainingTierChangeTicks;
        }

        return remainingTicks;
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
}

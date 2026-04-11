import { formatMetric } from './formatting';
import { booleanValue, numberValue, objectValue, stringValue } from './validation';
import type { UnitSnapshotDto } from '../types/generated';

export type ShipSubsystemStat = {
  label: string;
  value: string;
};

export type ShipSubsystemEntry = {
  id: string;
  name: string;
  exists: boolean;
  tier: number;
  targetTier: number;
  remainingTicks: number;
  status: string;
  stats: ShipSubsystemStat[];
  canUpgrade: boolean;
  nextTier: number;
  nextTierCosts: ShipSubsystemCost | null;
};

export type ShipSubsystemCost = {
  ticks: number;
  energy: number;
  metal: number;
  carbon: number;
  hydrogen: number;
  silicon: number;
  ions: number;
  neutrinos: number;
};

export type UpgradeResourcePool = {
  energy: number | null;
  metal: number | null;
  carbon: number | null;
  hydrogen: number | null;
  silicon: number | null;
  ions: number | null;
  neutrinos: number | null;
};

export type HarvestSourceSummary = {
  unitId: string;
  kind: string;
  x: number;
  y: number;
  distance: number | null;
  isSeen: boolean;
  labels: string[];
  score: number;
};

export function readSubsystemEntries(value: unknown): ShipSubsystemEntry[] {
  if (!Array.isArray(value)) {
    return [];
  }

  return value
    .map((entry) => objectValue(entry))
    .filter((entry): entry is Record<string, unknown> => !!entry)
    .map((entry) => ({
      id: stringValue(entry.id, stringValue(entry.slot, stringValue(entry.name, 'subsystem'))),
      name: humanizeSubsystemName(stringValue(entry.name, stringValue(entry.slot, 'Subsystem'))),
      exists: booleanValue(entry.exists, (readNumeric(entry.tier) ?? 0) > 0),
      tier: readNumeric(entry.tier) ?? 0,
      targetTier: readNumeric(entry.targetTier) ?? (readNumeric(entry.tier) ?? 0),
      remainingTicks: readNumeric(entry.remainingTicks) ?? 0,
      status: stringValue(entry.status, 'Off'),
      stats: readSubsystemStats(entry.stats),
      canUpgrade: booleanValue(entry.canUpgrade, false),
      nextTier: readNumeric(entry.nextTier) ?? (readNumeric(entry.tier) ?? 0),
      nextTierCosts: readSubsystemCost(entry.nextTierCosts),
    }))
    .sort((left, right) => left.name.localeCompare(right.name));
}

export function readSubsystemStats(value: unknown): ShipSubsystemStat[] {
  if (!Array.isArray(value)) {
    return [];
  }

  return value
    .map((entry) => objectValue(entry))
    .filter((entry): entry is Record<string, unknown> => !!entry)
    .map((entry) => ({
      label: stringValue(entry.label, 'Stat'),
      value: stringValue(entry.value, ''),
    }))
    .filter((entry) => entry.value.trim().length > 0);
}

export function readSubsystemCost(value: unknown): ShipSubsystemCost | null {
  const record = objectValue(value);
  if (!record) {
    return null;
  }

  return {
    ticks: readNumeric(record.ticks) ?? 0,
    energy: readNumeric(record.energy) ?? 0,
    metal: readNumeric(record.metal) ?? 0,
    carbon: readNumeric(record.carbon) ?? 0,
    hydrogen: readNumeric(record.hydrogen) ?? 0,
    silicon: readNumeric(record.silicon) ?? 0,
    ions: readNumeric(record.ions) ?? 0,
    neutrinos: readNumeric(record.neutrinos) ?? 0,
  };
}

export function readAvailableUpgradeResources(rawSubsystems: unknown): UpgradeResourcePool {
  const subsystems = readSubsystemEntries(rawSubsystems);
  return {
    energy: readSubsystemResourceValue(subsystems, 'Energy Battery', 'Charge'),
    metal: readSubsystemResourceValue(subsystems, 'Cargo', 'Metal'),
    carbon: readSubsystemResourceValue(subsystems, 'Cargo', 'Carbon'),
    hydrogen: readSubsystemResourceValue(subsystems, 'Cargo', 'Hydrogen'),
    silicon: readSubsystemResourceValue(subsystems, 'Cargo', 'Silicon'),
    ions: readSubsystemResourceValue(subsystems, 'Ion Battery', 'Charge'),
    neutrinos: readSubsystemResourceValue(subsystems, 'Neutrino Battery', 'Charge'),
  };
}

export function readSubsystemResourceValue(subsystems: ShipSubsystemEntry[], subsystemName: string, statLabel: string) {
  const subsystem = subsystems.find((entry) => entry.name === subsystemName);
  const stat = subsystem?.stats.find((entry) => entry.label === statLabel);
  return stat ? readLeadingMetric(stat.value) : null;
}

export function readSubsystemMetric(subsystem: ShipSubsystemEntry | null | undefined, statLabel: string) {
  const stat = subsystem?.stats.find((entry) => entry.label === statLabel);
  return stat ? readLeadingMetric(stat.value) : null;
}

export function readSubsystemMaximumMetric(subsystem: ShipSubsystemEntry | null | undefined, statLabel: string) {
  const stat = subsystem?.stats.find((entry) => entry.label === statLabel);
  if (!stat) {
    return null;
  }

  const metrics = Array.from(stat.value.matchAll(/-?\d+(?:[.,]\d+)?/g))
    .map((match) => Number(match[0].replace(',', '.')))
    .filter((metric) => Number.isFinite(metric));

  if (metrics.length >= 2) {
    return metrics[1] ?? null;
  }

  return metrics[0] ?? null;
}

export function hasEnoughResources(costs: ShipSubsystemCost | null, resources: UpgradeResourcePool) {
  if (!costs) {
    return false;
  }

  const pairs: Array<[keyof UpgradeResourcePool, number]> = [
    ['energy', costs.energy],
    ['metal', costs.metal],
    ['carbon', costs.carbon],
    ['hydrogen', costs.hydrogen],
    ['silicon', costs.silicon],
    ['ions', costs.ions],
    ['neutrinos', costs.neutrinos],
  ];

  return pairs.every(([key, required]) => required <= 0 || ((resources[key] ?? -Infinity) >= required));
}

export function buildHarvestSourceSummaries(
  units: UnitSnapshotDto[],
  clusterId: number | null,
  activePosition: { x: number; y: number } | null,
) {
  if (clusterId === null) {
    return [] as HarvestSourceSummary[];
  }

  return units
    .filter((unit) => unit.clusterId === clusterId)
    .map((unit) => {
      const labels = buildHarvestLabels(unit);
      if (labels.length === 0) {
        return null;
      }

      return {
        unitId: unit.unitId,
        kind: unit.kind,
        x: unit.x,
        y: unit.y,
        distance: activePosition ? Math.hypot(unit.x - activePosition.x, unit.y - activePosition.y) : null,
        isSeen: unit.isSeen !== false,
        labels,
        score: computeHarvestScore(unit),
      } satisfies HarvestSourceSummary;
    })
    .filter((entry): entry is HarvestSourceSummary => !!entry)
    .sort((left, right) => {
      if (left.isSeen !== right.isSeen) {
        return left.isSeen ? -1 : 1;
      }

      if (right.score !== left.score) {
        return right.score - left.score;
      }

      if (left.distance !== null && right.distance !== null && left.distance !== right.distance) {
        return left.distance - right.distance;
      }

      return left.unitId.localeCompare(right.unitId);
    });
}

export function formatHarvestDistance(distance: number | null) {
  return distance === null ? 'Unknown range' : `${formatMetric(distance)} away`;
}

export function buildHarvestLabels(unit: UnitSnapshotDto) {
  const labels: string[] = [];

  if (typeof unit.sunEnergy === 'number' && unit.sunEnergy > 0) {
    labels.push(`Energy ${formatMetric(unit.sunEnergy)}`);
  }

  if (typeof unit.sunIons === 'number' && unit.sunIons > 0) {
    labels.push(`Ions ${formatMetric(unit.sunIons)}`);
  }

  if (typeof unit.sunNeutrinos === 'number' && unit.sunNeutrinos > 0) {
    labels.push(`Neutrinos ${formatMetric(unit.sunNeutrinos)}`);
  }

  if (typeof unit.planetMetal === 'number' && unit.planetMetal > 0) {
    labels.push(`Metal ${formatMetric(unit.planetMetal)}`);
  }

  if (typeof unit.planetCarbon === 'number' && unit.planetCarbon > 0) {
    labels.push(`Carbon ${formatMetric(unit.planetCarbon)}`);
  }

  if (typeof unit.planetHydrogen === 'number' && unit.planetHydrogen > 0) {
    labels.push(`Hydrogen ${formatMetric(unit.planetHydrogen)}`);
  }

  if (typeof unit.planetSilicon === 'number' && unit.planetSilicon > 0) {
    labels.push(`Silicon ${formatMetric(unit.planetSilicon)}`);
  }

  if (unit.kind === 'nebula') {
    labels.push('Nebula');
  }

  return labels;
}

export function humanizeSubsystemName(value: string) {
  const normalized = value
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .replace(/([A-Z]+)([A-Z][a-z])/g, '$1 $2')
    .replace(/[_-]+/g, ' ')
    .trim();

  if (!normalized) {
    return 'Unknown subsystem';
  }

  return normalized.charAt(0).toUpperCase() + normalized.slice(1);
}

export function readLeadingMetric(value: string) {
  const match = value.match(/-?\d+(?:[.,]\d+)?/);
  if (!match) {
    return null;
  }

  const parsed = Number(match[0].replace(',', '.'));
  return Number.isFinite(parsed) ? parsed : null;
}

function computeHarvestScore(unit: UnitSnapshotDto) {
  return (unit.sunEnergy ?? 0)
    + (unit.sunIons ?? 0)
    + (unit.sunNeutrinos ?? 0)
    + (unit.planetMetal ?? 0)
    + (unit.planetCarbon ?? 0)
    + (unit.planetHydrogen ?? 0)
    + (unit.planetSilicon ?? 0)
    + (unit.kind === 'nebula' ? 1 : 0);
}

function readNumeric(value: unknown) {
  if (typeof value === 'number' && Number.isFinite(value)) {
    return value;
  }

  if (typeof value === 'string' && value.trim() !== '') {
    const parsed = Number(value);
    return Number.isFinite(parsed) ? parsed : null;
  }

  return null;
}

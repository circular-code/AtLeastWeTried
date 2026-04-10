<script setup lang="ts">
import { computed, ref } from 'vue';
import { useGateway } from '../../composables/useGateway';
import { formatMetric } from '../../lib/formatting';
import ModalBackdrop from '../../shared/ModalBackdrop.vue';

type ShipSubsystemStat = {
  label: string;
  value: string;
};

type ShipSubsystemCostEntry = {
  label: string;
  value: string;
  enough: boolean;
  isNeutral?: boolean;
};

type ShipSubsystemCost = {
  ticks: number;
  energy: number;
  metal: number;
  carbon: number;
  hydrogen: number;
  silicon: number;
  ions: number;
  neutrinos: number;
};

type ShipSubsystemEntry = {
  id: string;
  name: string;
  exists: boolean;
  tier: number;
  targetTier: number;
  remainingTicks: number;
  status: string;
  installStateLabel: string;
  subtitleLabel: string;
  stats: ShipSubsystemStat[];
  nextTier: number;
  canUpgrade: boolean;
  nextTierCosts: ShipSubsystemCost | null;
  nextTierPreview: ShipSubsystemStat[];
  upgradeTitle: string;
};

const props = defineProps<{
  controllableId: string;
  displayName: string;
  overlayState?: Record<string, unknown>;
}>();

const emit = defineEmits<{
  close: [];
}>();

const gateway = useGateway();
const hideUninstalledSubsystems = ref(false);

const subsystems = computed(() => readSubsystemEntries(props.overlayState?.subsystems ?? props.overlayState?.modules));
const installedSubsystemCount = computed(() => subsystems.value.filter((subsystem) => subsystem.exists).length);
const visibleSubsystems = computed(() => (
  hideUninstalledSubsystems.value
    ? subsystems.value.filter((subsystem) => subsystem.exists)
    : subsystems.value
));
const upgradeResources = computed(() => ({
  energy: readSubsystemResourceValue(subsystems.value, 'Energy Battery', 'Charge'),
  metal: readSubsystemResourceValue(subsystems.value, 'Cargo', 'Metal'),
  carbon: readSubsystemResourceValue(subsystems.value, 'Cargo', 'Carbon'),
  hydrogen: readSubsystemResourceValue(subsystems.value, 'Cargo', 'Hydrogen'),
  silicon: readSubsystemResourceValue(subsystems.value, 'Cargo', 'Silicon'),
  ions: readSubsystemResourceValue(subsystems.value, 'Ion Battery', 'Charge'),
  neutrinos: readSubsystemResourceValue(subsystems.value, 'Neutrino Battery', 'Charge'),
}));

function onUpgradeSubsystem(subsystemId: string) {
  if (!props.controllableId || !subsystemId) {
    return;
  }

  gateway.upgradeSubsystem(props.controllableId, subsystemId);
}

function readNumeric(value: unknown): number | null {
  if (typeof value === 'number' && Number.isFinite(value)) {
    return value;
  }

  if (typeof value === 'string' && value.trim() !== '') {
    const parsed = Number(value);
    return Number.isFinite(parsed) ? parsed : null;
  }

  return null;
}

function readText(value: unknown, fallback = '') {
  return typeof value === 'string' ? value : fallback;
}

function readBoolean(value: unknown, fallback = false) {
  return typeof value === 'boolean' ? value : fallback;
}

function readRecord(value: unknown) {
  return typeof value === 'object' && value !== null
    ? value as Record<string, unknown>
    : undefined;
}

function readSubsystemCost(value: unknown): ShipSubsystemCost | null {
  const record = readRecord(value);
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

function readSubsystemEntries(value: unknown): ShipSubsystemEntry[] {
  if (!Array.isArray(value)) {
    return [];
  }

  return value
    .map((entry) => readRecord(entry))
    .filter((entry): entry is Record<string, unknown> => !!entry)
    .map((entry) => {
      const tier = readNumeric(entry.tier) ?? 0;
      const targetTier = readNumeric(entry.targetTier) ?? tier;
      const remainingTicks = readNumeric(entry.remainingTicks) ?? 0;
      const exists = readBoolean(entry.exists, tier > 0);
      const status = readText(entry.status, 'Off');
      const isChangingTier = remainingTicks > 0 && targetTier !== tier;
      const installStateLabel = !exists
        ? 'Missing'
        : isChangingTier
          ? (targetTier > tier ? 'Upgrading' : 'Downgrading')
          : 'Installed';
      const nextTier = readNumeric(entry.nextTier) ?? tier;
      const canUpgrade = readBoolean(entry.canUpgrade, false);
      const nextTierCosts = readSubsystemCost(entry.nextTierCosts);
      const nextTierPreview = readSubsystemStats(entry.nextTierPreview);
      const name = humanizeSubsystemName(readText(entry.name, readText(entry.slot, 'Subsystem')));

      return {
        id: readText(entry.id, readText(entry.slot, readText(entry.name, 'subsystem'))),
        name,
        exists,
        tier,
        targetTier,
        remainingTicks,
        status,
        installStateLabel,
        subtitleLabel: `${installStateLabel} - Tier ${tier}`,
        stats: readSubsystemStats(entry.stats),
        nextTier,
        canUpgrade,
        nextTierCosts,
        nextTierPreview,
        upgradeTitle: buildSubsystemUpgradeTitle(
          name,
          tier,
          nextTier,
          canUpgrade,
          isChangingTier,
          nextTierCosts,
          nextTierPreview,
        ),
      };
    })
    .sort((left, right) => {
      if (left.exists !== right.exists) {
        return left.exists ? -1 : 1;
      }

      if (left.remainingTicks !== right.remainingTicks) {
        return right.remainingTicks - left.remainingTicks;
      }

      return left.name.localeCompare(right.name);
    });
}

function readSubsystemStats(value: unknown): ShipSubsystemStat[] {
  if (!Array.isArray(value)) {
    return [];
  }

  return value
    .map((entry) => readRecord(entry))
    .filter((entry): entry is Record<string, unknown> => !!entry)
    .map((entry) => ({
      label: readText(entry.label, 'Stat'),
      value: readText(entry.value),
    }))
    .filter((entry) => entry.value.trim().length > 0);
}

function buildSubsystemUpgradeTitle(
  subsystemName: string,
  currentTier: number,
  nextTier: number,
  canUpgrade: boolean,
  isChangingTier: boolean,
  nextTierCosts: ShipSubsystemCost | null,
  nextTierPreview: ShipSubsystemStat[],
) {
  if (isChangingTier) {
    return `${subsystemName} is already changing tier.`;
  }

  if (!canUpgrade || nextTier <= currentTier) {
    return `${subsystemName} is already at its maximum tier.`;
  }

  const verb = subsystemActionVerb(currentTier, nextTier);
  const lines = [`${verb} ${subsystemName} to Tier ${nextTier}`];
  const costs = formatSubsystemCosts(nextTierCosts);
  if (costs.length > 0) {
    lines.push('Cost:');
    lines.push(...costs.map((cost) => `- ${cost}`));
  }

  if (nextTierPreview.length > 0) {
    lines.push(`Tier ${nextTier} preview:`);
    lines.push(...nextTierPreview.map((stat) => `- ${stat.label} ${stat.value}`));
  }

  return lines.join('\n');
}

function subsystemActionVerb(currentTier: number, nextTier: number) {
  return currentTier === 0 && nextTier === 1 ? 'Install' : 'Upgrade';
}

function formatSubsystemCosts(costs: ShipSubsystemCost | null) {
  if (!costs) {
    return [];
  }

  const entries = [
    costs.ticks > 0 ? `${costs.ticks} ticks` : '',
    costs.energy > 0 ? `${formatMetric(costs.energy)} energy` : '',
    costs.metal > 0 ? `${formatMetric(costs.metal)} metal` : '',
    costs.carbon > 0 ? `${formatMetric(costs.carbon)} carbon` : '',
    costs.hydrogen > 0 ? `${formatMetric(costs.hydrogen)} hydrogen` : '',
    costs.silicon > 0 ? `${formatMetric(costs.silicon)} silicon` : '',
    costs.ions > 0 ? `${formatMetric(costs.ions)} ions` : '',
    costs.neutrinos > 0 ? `${formatMetric(costs.neutrinos)} neutrinos` : '',
  ];

  return entries.filter((entry) => entry.length > 0);
}

function buildSubsystemCostEntries(subsystem: ShipSubsystemEntry): ShipSubsystemCostEntry[] {
  const costs = subsystem.nextTierCosts;
  if (!costs) {
    return [];
  }

  const resources = upgradeResources.value;
  const entries: ShipSubsystemCostEntry[] = [];

  if (costs.ticks > 0) {
    entries.push({
      label: 'Time',
      value: `${costs.ticks} ticks`,
      enough: true,
      isNeutral: true,
    });
  }

  addSubsystemCostEntry(entries, 'Energy', costs.energy, resources.energy);
  addSubsystemCostEntry(entries, 'Metal', costs.metal, resources.metal);
  addSubsystemCostEntry(entries, 'Carbon', costs.carbon, resources.carbon);
  addSubsystemCostEntry(entries, 'Hydrogen', costs.hydrogen, resources.hydrogen);
  addSubsystemCostEntry(entries, 'Silicon', costs.silicon, resources.silicon);
  addSubsystemCostEntry(entries, 'Ions', costs.ions, resources.ions);
  addSubsystemCostEntry(entries, 'Neutrinos', costs.neutrinos, resources.neutrinos);

  return entries;
}

function getSubsystemTimeCostEntry(subsystem: ShipSubsystemEntry) {
  return buildSubsystemCostEntries(subsystem).find((entry) => entry.isNeutral) ?? null;
}

function getSubsystemResourceCostEntries(subsystem: ShipSubsystemEntry) {
  return buildSubsystemCostEntries(subsystem).filter((entry) => !entry.isNeutral);
}

function addSubsystemCostEntry(entries: ShipSubsystemCostEntry[], label: string, required: number, available: number | null) {
  if (required <= 0) {
    return;
  }

  const enough = available !== null && available >= required;
  entries.push({
    label,
    value: `${formatMetric(available ?? 0)} / ${formatMetric(required)}`,
    enough,
  });
}

function readSubsystemResourceValue(subsystemsToRead: ShipSubsystemEntry[], subsystemName: string, statLabel: string) {
  const subsystem = subsystemsToRead.find((entry) => entry.name === subsystemName);
  const stat = subsystem?.stats.find((entry) => entry.label === statLabel);
  if (!stat) {
    return null;
  }

  return readLeadingMetric(stat.value);
}

function readLeadingMetric(value: string) {
  const match = value.match(/-?\d+(?:[.,]\d+)?/);
  if (!match) {
    return null;
  }

  const normalized = match[0].replace(',', '.');
  const parsed = Number(normalized);
  return Number.isFinite(parsed) ? parsed : null;
}

function isSubsystemUpgradeReady(subsystem: ShipSubsystemEntry) {
  const costEntries = buildSubsystemCostEntries(subsystem).filter((entry) => !entry.isNeutral);
  if (costEntries.length === 0) {
    return false;
  }

  return costEntries.every((entry) => entry.enough);
}

function humanizeSubsystemStatus(status: string) {
  const normalized = status.trim();
  return normalized ? humanizeSubsystemName(normalized) : 'Idle';
}

function humanizeSubsystemName(value: string) {
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
</script>

<template>
  <Teleport to="body">
    <ModalBackdrop panel-class="ship-subsystem-modal-panel" @close="emit('close')">
      <div class="modal-header ship-subsystem-modal-head">
        <div>
          <h2 class="panel-title">{{ displayName }} Modules</h2>
          <p>{{ installedSubsystemCount }} / {{ subsystems.length }} subsystems installed</p>
        </div>
        <div class="ship-subsystem-modal-controls">
          <label class="ship-subsystem-filter">
            <input v-model="hideUninstalledSubsystems" type="checkbox" />
            <span>Hide uninstalled</span>
          </label>
          <button class="button-ghost" type="button" @click="emit('close')">Close</button>
        </div>
      </div>

      <div v-if="visibleSubsystems.length > 0" class="ship-subsystem-popover-body panel-scroll">
        <ul class="ship-subsystem-list">
          <li
            v-for="subsystem in visibleSubsystems"
            :key="subsystem.id"
            class="ship-subsystem-row"
          >
            <div class="ship-subsystem-info">
              <div class="ship-subsystem-headline">
                <strong>{{ subsystem.name }}</strong>
                <span class="ship-subsystem-copy-label">{{ subsystem.subtitleLabel }}</span>
              </div>
              <div class="ship-subsystem-metrics">
                <span
                  v-for="stat in subsystem.stats"
                  :key="`${subsystem.id}-${stat.label}`"
                >
                  {{ stat.label }} {{ stat.value }}
                </span>
                <span v-if="subsystem.stats.length === 0">{{ humanizeSubsystemStatus(subsystem.status) }}</span>
              </div>
              <div
                v-if="buildSubsystemCostEntries(subsystem).length > 0"
                class="ship-subsystem-costs"
              >
                <div class="ship-subsystem-costs-row">
                  <span class="ship-subsystem-costs-label">{{ subsystemActionVerb(subsystem.tier, subsystem.nextTier) }} costs</span>
                  <div class="ship-subsystem-costs-list">
                    <span
                      v-for="cost in getSubsystemResourceCostEntries(subsystem)"
                      :key="`${subsystem.id}-${cost.label}`"
                      :class="[
                        'ship-subsystem-cost',
                        cost.enough ? 'is-ready' : 'is-missing',
                      ]"
                    >
                      {{ cost.label }} {{ cost.value }}
                    </span>
                  </div>
                  <span
                    v-if="getSubsystemTimeCostEntry(subsystem)"
                    class="ship-subsystem-cost is-neutral"
                  >
                    {{ getSubsystemTimeCostEntry(subsystem)?.value }}
                  </span>
                </div>
              </div>
            </div>
            <div class="ship-subsystem-actions">
              <button
                type="button"
                class="button-secondary button-compact ship-subsystem-action"
                :class="{ 'is-ready': isSubsystemUpgradeReady(subsystem) }"
                :disabled="!subsystem.canUpgrade"
                :title="subsystem.upgradeTitle"
                @click.stop="onUpgradeSubsystem(subsystem.id)"
              >
                {{ subsystemActionVerb(subsystem.tier, subsystem.nextTier) }}
              </button>
            </div>
          </li>
        </ul>
      </div>

      <p v-else class="ship-subsystem-empty">
        {{ hideUninstalledSubsystems ? 'No installed subsystems match the current filter.' : 'No subsystem details are available for this ship yet.' }}
      </p>
    </ModalBackdrop>
  </Teleport>
</template>

<style scoped>
.ship-subsystem-modal-head {
  align-items: flex-start;
}

.ship-subsystem-modal-head p {
  margin: 0.2rem 0 0;
  color: rgba(235, 242, 255, 0.72);
  font-size: 0.78rem;
}

.ship-subsystem-modal-controls {
  display: flex;
  align-items: flex-start;
  gap: 0.9rem;
}

.ship-subsystem-filter {
  display: inline-flex;
  align-items: center;
  justify-content: flex-end;
  gap: 0.45rem;
  font-size: 0.72rem;
  text-transform: uppercase;
  letter-spacing: 0.08em;
  white-space: nowrap;
}

.ship-subsystem-filter input {
  margin: 0;
}

.ship-subsystem-popover-body {
  max-height: min(36rem, 72vh);
  overflow: auto;
  padding-right: 0.4rem;
}

.ship-subsystem-list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 0.65rem;
}

.ship-subsystem-row {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 0.85rem;
  padding: 0.8rem 0.85rem;
  border-radius: 0.9rem;
  background: rgba(13, 19, 32, 0.76);
  border: 1px solid rgba(235, 242, 255, 0.08);
}

.ship-subsystem-info {
  display: flex;
  flex: 1 1 auto;
  flex-direction: column;
  gap: 0.35rem;
  align-items: flex-start;
  text-align: left;
}

.ship-subsystem-headline {
  display: flex;
  flex-wrap: wrap;
  align-items: baseline;
  gap: 0.45rem 0.75rem;
  width: 100%;
}

.ship-subsystem-copy-label {
  color: rgba(235, 242, 255, 0.72);
  font-size: 0.78rem;
}

.ship-subsystem-metrics {
  display: flex;
  flex-wrap: wrap;
  gap: 0.35rem 0.65rem;
  width: 100%;
  color: rgba(235, 242, 255, 0.82);
  font-size: 0.82rem;
}

.ship-subsystem-costs {
  width: 100%;
  border-top: 1px solid rgba(235, 242, 255, 0.2);
  padding-top: 0.45rem;
}

.ship-subsystem-costs-row {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  width: 100%;
}

.ship-subsystem-costs-label {
  color: rgba(235, 242, 255, 0.72);
  font-size: 0.66rem;
  letter-spacing: 0.06em;
  text-transform: uppercase;
}

.ship-subsystem-costs-list {
  display: flex;
  flex: 1 1 auto;
  flex-wrap: wrap;
  gap: 0.35rem 0.45rem;
}

.ship-subsystem-cost {
  text-align: left;
  font-size: 0.78rem;
}

.ship-subsystem-cost.is-ready {
  color: #7dffb2;
}

.ship-subsystem-cost.is-missing {
  color: #ff8e8e;
}

.ship-subsystem-cost.is-neutral {
  color: rgba(235, 242, 255, 0.72);
}

.ship-subsystem-actions {
  flex: 0 0 auto;
}

.ship-subsystem-action.is-ready {
  border-color: rgba(125, 255, 178, 0.9);
  color: #dfffe8;
  background: rgba(52, 138, 92, 0.28);
}

.ship-subsystem-empty {
  margin: 0;
  color: rgba(235, 242, 255, 0.78);
  font-size: 0.82rem;
}

.ship-subsystem-modal-panel {
  width: min(64rem, calc(100vw - 2rem));
}

@media (max-width: 900px) {
  .ship-subsystem-modal-head,
  .ship-subsystem-modal-controls,
  .ship-subsystem-row,
  .ship-subsystem-costs-row {
    flex-direction: column;
    align-items: flex-start;
  }

  .ship-subsystem-modal-controls {
    width: 100%;
    gap: 0.6rem;
  }

  .ship-subsystem-modal-panel {
    width: min(100vw - 1rem, 42rem);
  }
}
</style>

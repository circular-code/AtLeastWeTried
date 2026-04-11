<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { useGateway } from '../../composables/useGateway';
import { useGameStore } from '../../stores/game';
import { useUiStore } from '../../stores/ui';
import GaugeMeter from '../../shared/GaugeMeter.vue';
import ShipSubsystemPopover from './ShipSubsystemPopover.vue';
import ShipCreator from './ShipCreator.vue';

const gameStore = useGameStore();
const uiStore = useUiStore();
const gateway = useGateway();
const openSubsystemsForId = ref('');
const sampledOwnerOverlay = ref<Record<string, Record<string, unknown> | undefined>>({});
const overlayPanelRef = ref<HTMLElement | null>(null);
let overlayRefreshTimer: number | null = null;

const activeControllableId = computed(() => uiStore.selectedControllableId || (gameStore.ownedControllables[0]?.controllableId ?? ''));
const ownerOverlay = computed(() => gameStore.ownerOverlay as Record<string, Record<string, unknown> | undefined>);
const customShipColors = computed(() => uiStore.customShipColors);
const removingControllableIds = computed(() => uiStore.removingControllableIds);
const entries = computed(() => gameStore.ownedControllables
  .map((entry) => gameStore.overlayEntry(entry.controllableId))
  .filter((entry): entry is NonNullable<ReturnType<typeof gameStore.overlayEntry>> => !!entry));

const statIcons: Record<string, string> = {
  Ammo: `<svg viewBox="0 0 16 16" focusable="false"><circle cx="8" cy="8" r="2.8" fill="none" stroke="currentColor" stroke-width="1.3"/><path d="M8 2v2.4M8 11.6V14M2 8h2.4M11.6 8H14" stroke="currentColor" stroke-width="1.3" stroke-linecap="round"/></svg>`,
  Speed: `<svg viewBox="0 0 16 16" focusable="false"><path d="M2.8 12.5A5.5 5.5 0 1 1 13.2 12.5" fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round"/><path d="M8 9.8 10.5 7" stroke="currentColor" stroke-width="1.3" stroke-linecap="round"/><circle cx="8" cy="9.8" r="1.1" fill="currentColor"/></svg>`,
  Drive: `<svg viewBox="0 0 16 16" focusable="false"><path d="M6.5 4Q8 2 9.5 4l2 6H4.5z" fill="none" stroke="currentColor" stroke-width="1.3" stroke-linejoin="round"/><path d="M5.5 10.5 8 14.5 10.5 10.5" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linecap="round" stroke-linejoin="round"/><path d="M8 10.5v2" stroke="currentColor" stroke-width="1.1" stroke-linecap="round" opacity="0.55"/></svg>`,
};

function energyTelemetry(controllableId: string) {
  const overlayState = sampledOwnerOverlay.value[controllableId];
  const rawSubsystems = overlayState?.subsystems ?? overlayState?.modules;
  const collection = readSubsystemResourceMetric(rawSubsystems, 'Energy Cell', ['Collected', 'Charge per tick', 'Charge']);
  const drain = readSubsystemResourceMetric(rawSubsystems, 'Energy Battery', ['Drain', 'Drain per tick']);
  const delta = collection !== null || drain !== null ? (collection ?? 0) - (drain ?? 0) : null;

  return {
    collection: formatTelemetryMetric(collection),
    drain: formatTelemetryMetric(drain),
    delta: delta === null ? '0' : `${delta >= 0 ? '+' : ''}${formatTelemetryMetric(delta)}`,
    deltaTone: delta === null ? 'neutral' : delta >= 0 ? 'positive' : 'negative',
  };
}

function selectControllable(controllableId: string) {
  uiStore.setSelectedControllable(controllableId);
  uiStore.requestViewportJump(controllableId);
}

function shipColorValue(controllableId: string) {
  return customShipColors.value[controllableId] ?? '#d7ecff';
}

function updateShipColor(controllableId: string, event: Event) {
  const input = event.target as HTMLInputElement | null;
  const color = input?.value ?? '';
  if (!color) {
    return;
  }

  uiStore.setCustomShipColor(controllableId, color);
}

function handleLifecycleAction(controllableId: string, alive: boolean) {
  if (!alive) {
    gateway.continueShip(controllableId);
    return;
  }

  uiStore.setSelectedControllable(controllableId);
  uiStore.requestViewportJump(controllableId);
}

function visibleBadges(entry: NonNullable<ReturnType<typeof gameStore.overlayEntry>>) {
  return entry.badges.filter((badge) => badge.label.toLowerCase() !== 'selected');
}

function visibleStats(entry: NonNullable<ReturnType<typeof gameStore.overlayEntry>>) {
  const filteredStats = entry.stats.filter((stat) => stat.label !== 'Heading');
  const telemetry = energyTelemetry(entry.id);

  return [
    ...filteredStats,
    {
      label: 'Delta',
      value: telemetry.delta,
    },
  ];
}

function statLabelText(stat: { label: string }) {
  return stat.label === 'Delta' ? '±' : stat.label;
}

function compactClusterLabel(clusterLabel: string) {
  const match = clusterLabel.match(/C\d+/i);
  return match?.[0]?.toUpperCase() ?? 'C0';
}

function toggleSubsystems(controllableId: string) {
  openSubsystemsForId.value = openSubsystemsForId.value === controllableId ? '' : controllableId;
}

function isRemoving(controllableId: string) {
  return removingControllableIds.value.has(controllableId);
}

function hasAvailableSubsystemUpgrade(controllableId: string) {
  const overlayState = sampledOwnerOverlay.value[controllableId];
  const rawSubsystems = overlayState?.subsystems ?? overlayState?.modules;
  if (!Array.isArray(rawSubsystems)) {
    return false;
  }

  const availableResources = readAvailableUpgradeResources(rawSubsystems);
  return rawSubsystems.some((entry) => {
    if (!entry || typeof entry !== 'object') {
      return false;
    }

    const subsystem = entry as Record<string, unknown>;
    if (subsystem.canUpgrade !== true) {
      return false;
    }

    const costRecord = readRecord(subsystem.nextTierCosts);
    if (!costRecord) {
      return false;
    }

    const costs = {
      energy: readNumeric(costRecord.energy) ?? 0,
      metal: readNumeric(costRecord.metal) ?? 0,
      carbon: readNumeric(costRecord.carbon) ?? 0,
      hydrogen: readNumeric(costRecord.hydrogen) ?? 0,
      silicon: readNumeric(costRecord.silicon) ?? 0,
      ions: readNumeric(costRecord.ions) ?? 0,
      neutrinos: readNumeric(costRecord.neutrinos) ?? 0,
    };

    return Object.entries(costs).every(([resource, required]) => {
      if (required <= 0) {
        return true;
      }

      const available = availableResources[resource as keyof typeof availableResources];
      return available !== null && available >= required;
    });
  });
}

function readAvailableUpgradeResources(rawSubsystems: unknown[]) {
  return {
    energy: readSubsystemResourceValue(rawSubsystems, 'Energy Battery', 'Charge'),
    metal: readSubsystemResourceValue(rawSubsystems, 'Cargo', 'Metal'),
    carbon: readSubsystemResourceValue(rawSubsystems, 'Cargo', 'Carbon'),
    hydrogen: readSubsystemResourceValue(rawSubsystems, 'Cargo', 'Hydrogen'),
    silicon: readSubsystemResourceValue(rawSubsystems, 'Cargo', 'Silicon'),
    ions: readSubsystemResourceValue(rawSubsystems, 'Ion Battery', 'Charge'),
    neutrinos: readSubsystemResourceValue(rawSubsystems, 'Neutrino Battery', 'Charge'),
  };
}

function readSubsystemResourceMetric(rawSubsystems: unknown, subsystemName: string, statLabels: string[]) {
  if (!Array.isArray(rawSubsystems)) {
    return null;
  }

  const subsystem = rawSubsystems.find((entry) => {
    const record = readRecord(entry);
    if (!record) {
      return false;
    }

    return humanizeSubsystemName(readText(record.name, readText(record.slot, ''))) === subsystemName;
  });
  const subsystemRecord = readRecord(subsystem);
  if (!subsystemRecord || !Array.isArray(subsystemRecord.stats)) {
    return null;
  }

  const stat = subsystemRecord.stats.find((entry) => {
    const record = readRecord(entry);
    return record ? statLabels.includes(readText(record.label)) : false;
  });
  const statRecord = readRecord(stat);
  if (!statRecord) {
    return null;
  }

  return readLeadingMetric(readText(statRecord.value));
}

function formatTelemetryMetric(value: number | null) {
  return value === null ? '0' : value.toFixed(2).replace(/\.?0+$/, '');
}

function readSubsystemResourceValue(rawSubsystems: unknown[], subsystemName: string, statLabel: string) {
  const subsystem = rawSubsystems.find((entry) => {
    const record = readRecord(entry);
    if (!record) {
      return false;
    }

    return humanizeSubsystemName(readText(record.name, readText(record.slot, ''))) === subsystemName;
  });
  const subsystemRecord = readRecord(subsystem);
  if (!subsystemRecord || !Array.isArray(subsystemRecord.stats)) {
    return null;
  }

  const stat = subsystemRecord.stats.find((entry) => {
    const record = readRecord(entry);
    return record ? readText(record.label) === statLabel : false;
  });
  const statRecord = readRecord(stat);
  if (!statRecord) {
    return null;
  }

  return readLeadingMetric(readText(statRecord.value));
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

function readRecord(value: unknown) {
  return typeof value === 'object' && value !== null
    ? value as Record<string, unknown>
    : undefined;
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

function refreshSampledOwnerOverlay() {
  sampledOwnerOverlay.value = { ...ownerOverlay.value };
}

function scrollSelectedControllableIntoView() {
  const container = overlayPanelRef.value;
  const selectedId = activeControllableId.value;
  if (!container || !selectedId || container.scrollHeight <= container.clientHeight) {
    return;
  }

  const selectedEntry = container.querySelector<HTMLElement>(`[data-controllable-id="${CSS.escape(selectedId)}"]`);
  if (!selectedEntry) {
    return;
  }

  const targetTop = Math.max(0, Math.min(selectedEntry.offsetTop, container.scrollHeight - container.clientHeight));
  if (Math.abs(container.scrollTop - targetTop) < 1) {
    return;
  }

  container.scrollTo({
    top: targetTop,
    behavior: 'smooth',
  });
}

onMounted(() => {
  refreshSampledOwnerOverlay();
  overlayRefreshTimer = window.setInterval(() => {
    refreshSampledOwnerOverlay();
  }, 500);
});

watch(
  () => activeControllableId.value,
  async () => {
    await nextTick();
    scrollSelectedControllableIntoView();
  },
  { immediate: true },
);

onBeforeUnmount(() => {
  if (overlayRefreshTimer !== null) {
    window.clearInterval(overlayRefreshTimer);
    overlayRefreshTimer = null;
  }
});
</script>

<template>
  <aside class="overlay-column overlay-column-left">
    <section ref="overlayPanelRef" class="owner-overlay-panel panel-glass">
        <button
          v-for="entry in entries"
          :key="entry.id"
          :data-controllable-id="entry.id"
          class="owner-overlay-item"
          :class="{ 'is-selected': entry.id === activeControllableId, 'is-removing': isRemoving(entry.id) }"
          type="button"
          @click="selectControllable(entry.id)"
        >
          <div v-if="isRemoving(entry.id)" class="owner-overlay-removing">
            <span class="owner-overlay-removing-spinner" aria-hidden="true" />
            <span>Removing ship...</span>
          </div>
          <div class="owner-overlay-summary">
              <div class="owner-overlay-head">
              <div class="owner-overlay-title-block">
                <h3>{{ entry.displayName }}</h3>
                <div class="owner-overlay-title-topline">
                  <span class="owner-overlay-inline-meta owner-overlay-inline-meta--type">{{ entry.kind }}</span>
                  <span class="owner-overlay-inline-meta">{{ compactClusterLabel(entry.clusterLabel) }}</span>
                </div>
              </div>
              <div class="owner-overlay-head-actions">
                <div class="owner-overlay-badges">
                  <span v-for="badge in visibleBadges(entry)" :key="badge.label" class="overlay-badge" :class="`is-${badge.tone}`">{{ badge.label }}</span>
                </div>
                <div class="actions-compact">
                  <button v-if="!entry.alive" class="button-secondary button-compact" type="button" @click.stop="handleLifecycleAction(entry.id, entry.alive)">Spawn</button>
                  <button v-else class="button-ghost button-compact owner-overlay-action-button" type="button" title="Destroy ship" aria-label="Destroy ship" @click.stop="gateway.destroyShip(entry.id)">
                    <svg viewBox="0 0 16 16" focusable="false" aria-hidden="true">
                      <path d="M8 2a4.5 4.5 0 0 0-4.5 4.5c0 1.6.84 3.02 2.1 3.83V12h4.8v-1.67A4.5 4.5 0 0 0 12.5 6.5 4.5 4.5 0 0 0 8 2zM6.25 10.5V10h3.5v.5h-3.5zm-.5-3a.75.75 0 1 1 1.5 0 .75.75 0 0 1-1.5 0zm4.5 0a.75.75 0 1 1 1.5 0 .75.75 0 0 1-1.5 0z" fill="currentColor"/>
                    </svg>
                  </button>
                  <button
                    class="button-secondary button-compact owner-overlay-action-button"
                    :class="{ 'is-ready': hasAvailableSubsystemUpgrade(entry.id) }"
                    type="button"
                    :title="openSubsystemsForId === entry.id ? 'Hide modules' : 'Show modules'"
                    aria-label="Modules"
                    @click.stop="toggleSubsystems(entry.id)"
                  >
                    <svg viewBox="0 0 16 16" focusable="false" aria-hidden="true">
                      <path d="M6.6 1.9h2.8l.35 1.6c.33.11.65.25.94.43l1.45-.77 1.4 1.4-.77 1.45c.18.29.32.61.43.94l1.6.35v2.8l-1.6.35a4.6 4.6 0 0 1-.43.94l.77 1.45-1.4 1.4-1.45-.77c-.29.18-.61.32-.94.43l-.35 1.6H6.6l-.35-1.6a4.6 4.6 0 0 1-.94-.43l-1.45.77-1.4-1.4.77-1.45a4.6 4.6 0 0 1-.43-.94l-1.6-.35V6.6l1.6-.35c.11-.33.25-.65.43-.94l-.77-1.45 1.4-1.4 1.45.77c.29-.18.61-.32.94-.43z" fill="none" stroke="currentColor" stroke-linejoin="round" stroke-width="1.1"></path>
                      <circle cx="8" cy="8" r="2.05" fill="none" stroke="currentColor" stroke-width="1.1"></circle>
                    </svg>
                  </button>
                  <label class="button-secondary button-compact ship-color-control" title="Ship color">
                    <input
                      class="ship-color-input"
                      type="color"
                      :value="shipColorValue(entry.id)"
                      @click.stop
                      @input.stop="updateShipColor(entry.id, $event)"
                    />
                  </label>
                  <button class="button-danger button-compact owner-overlay-action-button" type="button" title="Remove ship" aria-label="Remove ship" :disabled="isRemoving(entry.id)" @click.stop="gateway.removeShip(entry.id)">
                    <svg viewBox="0 0 16 16" focusable="false" aria-hidden="true">
                      <path d="M5.5 2h5M2.5 4.5h11M4.5 4.5l.8 8.5h5.4l.8-8.5" stroke="currentColor" stroke-width="1.2" stroke-linecap="round" stroke-linejoin="round" fill="none"/>
                      <path d="M6.5 7v4M9.5 7v4" stroke="currentColor" stroke-width="1.1" stroke-linecap="round" fill="none"/>
                    </svg>
                  </button>
                </div>
              </div>
          </div>
        </div>

        <div class="owner-overlay-gauges">
          <GaugeMeter v-for="meter in entry.meters" :key="meter.label" :meter="meter" />
          <div class="owner-overlay-energy owner-overlay-energy--stack">
            <div class="owner-overlay-energy-stat">
              <span class="owner-overlay-energy-label">Collect</span>
              <strong class="is-positive">{{ energyTelemetry(entry.id).collection }}</strong>
            </div>
            <div class="owner-overlay-energy-stat">
              <span class="owner-overlay-energy-label">Drain</span>
              <strong class="is-negative">{{ energyTelemetry(entry.id).drain }}</strong>
            </div>
          </div>
        </div>

        <dl class="overlay-stat-grid owner-overlay-stats">
          <div v-for="stat in visibleStats(entry)" :key="stat.label" :title="stat.label" :class="{ 'is-delta': stat.label === 'Delta' }">
            <dt>
              <span v-if="statIcons[stat.label]" class="stat-icon" v-html="statIcons[stat.label]" aria-hidden="true" />
              <template v-else>{{ statLabelText(stat) }}</template>
            </dt>
            <dd :class="{ 'is-positive': stat.label === 'Delta' && stat.value.startsWith('+'), 'is-negative': stat.label === 'Delta' && stat.value.startsWith('-') }">{{ stat.value }}</dd>
          </div>
        </dl>


      </button>

      <ShipCreator @create="gateway.createShip($event)" />
    </section>

      <ShipSubsystemPopover
        v-if="openSubsystemsForId"
        :controllable-id="openSubsystemsForId"
        :display-name="entries.find((entry) => entry.id === openSubsystemsForId)?.displayName ?? 'Ship'"
        :overlay-state="sampledOwnerOverlay[openSubsystemsForId]"
        @close="openSubsystemsForId = ''"
      />
  </aside>
</template>

<style scoped>
.owner-overlay-head-actions {
  display: flex;
  align-items: flex-end;
  justify-content: flex-end;
  gap: 0.2rem;
  flex-wrap: wrap;
  flex: 0 0 auto;
}

.owner-overlay-head-actions .button-compact {
  border-radius: 5px;
}

.ship-color-control {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 1.65rem;
  min-width: 1.65rem;
  height: 1.65rem;
  padding: 0;
  border-radius: 5px;
  cursor: pointer;
}

.ship-color-input {
  width: 0.88rem;
  height: 0.88rem;
  padding: 0;
  border: none;
  border-radius: 999px;
  background: transparent;
  cursor: pointer;
}

.ship-color-input::-webkit-color-swatch-wrapper {
  padding: 0;
}

.ship-color-input::-webkit-color-swatch {
  border: 1px solid rgba(255, 255, 255, 0.16);
  border-radius: 3px;
}

.ship-color-input::-moz-color-swatch {
  border: 1px solid rgba(255, 255, 255, 0.16);
  border-radius: 3px;
}

.owner-overlay-action-button {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 1.65rem;
  min-width: 1.65rem;
  height: 1.65rem;
  padding-inline: 0;
  padding-block: 0;
  border-radius: 5px;
}

.owner-overlay-action-button svg {
  width: 0.95rem;
  height: 0.95rem;
}

.owner-overlay-action-button.is-ready {
  border-color: rgba(125, 255, 178, 0.9);
  color: #dfffe8;
  background: rgba(52, 138, 92, 0.28);
}

.owner-overlay-item.is-selected {
  border-color: rgba(239, 191, 132, 0.72);
  box-shadow:
    0 18px 38px rgba(0, 0, 0, 0.24),
    inset 0 0 0 2px rgba(239, 191, 132, 0.42),
    0 0 0 1px rgba(239, 191, 132, 0.18);
}

.owner-overlay-summary {
  gap: 0;
}

.owner-overlay-head {
  align-items: flex-start;
  min-height: 32px;
  gap: 0.45rem;
}

.owner-overlay-title-topline {
  display: flex;
  align-items: center;
  gap: 0.32rem 0.4rem;
  flex-wrap: wrap;
  min-width: 0;
  width: 100%;
  max-width: none;
  justify-content: flex-start;
  text-align: left;
}

.owner-overlay-title-block {
  display: flex;
  flex: 1 1 auto;
  min-width: 0;
  flex-direction: column;
  gap: 0.18rem;
  align-items: flex-start;
  text-align: left;
}

.owner-overlay-title-block h3 {
  margin: 0;
  font-size: 0.76rem;
  line-height: 1.05;
  width: 100%;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  flex: 1 1 auto;
  min-width: 0;
}

.owner-overlay-inline-meta {
  flex: 0 0 auto;
  max-width: none;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  color: var(--text-muted);
  font-size: 0.56rem;
  line-height: 1;
  text-transform: uppercase;
  letter-spacing: 0.08em;
}

.owner-overlay-inline-meta--type {
  max-width: none;
}

.owner-overlay-badges {
  flex-wrap: nowrap;
}

.owner-overlay-badges:empty {
  display: none;
}

.owner-overlay-energy {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 0.35rem;
}

.owner-overlay-energy--stack {
  grid-template-columns: 1fr;
  gap: 0.22rem;
  align-self: center;
}

.owner-overlay-energy-stat {
  display: grid;
  gap: 0.08rem;
  padding: 0.28rem 0.34rem;
  border-radius: 8px;
  background: rgba(255, 255, 255, 0.04);
}

.owner-overlay-energy-label {
  font-size: 0.53rem;
  line-height: 1;
  text-transform: uppercase;
  letter-spacing: 0.08em;
  color: var(--text-muted);
}

.owner-overlay-energy-stat strong {
  font-size: 0.72rem;
  line-height: 1;
  color: #f4ecda;
}

.owner-overlay-energy-stat .is-positive {
  color: #9ff3bc;
}

.owner-overlay-energy-stat .is-negative {
  color: #ff9e94;
}

.owner-overlay-energy-stat .is-neutral {
  color: #dce8f4;
}
</style>

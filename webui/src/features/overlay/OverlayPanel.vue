<script setup lang="ts">
import { computed, ref } from 'vue';
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

const activeControllableId = computed(() => uiStore.selectedControllableId || (gameStore.ownedControllables[0]?.controllableId ?? ''));
const ownerOverlay = computed(() => gameStore.ownerOverlay as Record<string, Record<string, unknown> | undefined>);
const customShipColors = computed(() => uiStore.customShipColors);
const entries = computed(() => gameStore.ownedControllables
  .map((entry) => gameStore.overlayEntry(entry.controllableId))
  .filter((entry): entry is NonNullable<ReturnType<typeof gameStore.overlayEntry>> => !!entry));

const statIcons: Record<string, string> = {
  Ammo: `<svg viewBox="0 0 16 16" focusable="false"><circle cx="8" cy="8" r="2.8" fill="none" stroke="currentColor" stroke-width="1.3"/><path d="M8 2v2.4M8 11.6V14M2 8h2.4M11.6 8H14" stroke="currentColor" stroke-width="1.3" stroke-linecap="round"/></svg>`,
  Speed: `<svg viewBox="0 0 16 16" focusable="false"><path d="M2.8 12.5A5.5 5.5 0 1 1 13.2 12.5" fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round"/><path d="M8 9.8 10.5 7" stroke="currentColor" stroke-width="1.3" stroke-linecap="round"/><circle cx="8" cy="9.8" r="1.1" fill="currentColor"/></svg>`,
  Heading: `<svg viewBox="0 0 16 16" focusable="false"><path d="M8 2v12" stroke="currentColor" stroke-width="1.3" stroke-linecap="round"/><path d="M5.2 5 8 2.2l2.8 2.8" fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"/></svg>`,
  Drive: `<svg viewBox="0 0 16 16" focusable="false"><path d="M6.5 4Q8 2 9.5 4l2 6H4.5z" fill="none" stroke="currentColor" stroke-width="1.3" stroke-linejoin="round"/><path d="M5.5 10.5 8 14.5 10.5 10.5" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linecap="round" stroke-linejoin="round"/><path d="M8 10.5v2" stroke="currentColor" stroke-width="1.1" stroke-linecap="round" opacity="0.55"/></svg>`,
};

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

function resetShipColor(controllableId: string) {
  uiStore.clearCustomShipColor(controllableId);
}

function handleLifecycleAction(controllableId: string, alive: boolean) {
  if (!alive) {
    gateway.continueShip(controllableId);
    return;
  }

  uiStore.setSelectedControllable(controllableId);
  uiStore.requestViewportJump(controllableId);
}

function toggleSubsystems(controllableId: string) {
  openSubsystemsForId.value = openSubsystemsForId.value === controllableId ? '' : controllableId;
}

function hasAvailableSubsystemUpgrade(controllableId: string) {
  const overlayState = ownerOverlay.value[controllableId];
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
</script>

<template>
  <aside class="overlay-column overlay-column-left">
    <section class="owner-overlay-panel panel-glass">
      <button
        v-for="entry in entries"
        :key="entry.id"
        class="owner-overlay-item"
        :class="{ 'is-selected': entry.id === activeControllableId }"
        type="button"
        @click="selectControllable(entry.id)"
      >
        <div class="owner-overlay-summary">
          <div class="owner-overlay-head">
            <div class="owner-overlay-title">
              <h3>{{ entry.displayName }}</h3>
              <p>{{ entry.kind }}</p>
            </div>
            <div class="owner-overlay-head-actions">
              <label class="ship-color-control" title="Ship color">
                <span class="ship-color-control-label">Color</span>
                <input
                  class="ship-color-input"
                  type="color"
                  :value="shipColorValue(entry.id)"
                  @click.stop
                  @input.stop="updateShipColor(entry.id, $event)"
                />
              </label>
              <button
                v-if="customShipColors[entry.id]"
                class="button-ghost button-compact ship-color-reset"
                type="button"
                title="Reset ship color"
                @click.stop="resetShipColor(entry.id)"
              >
                Reset
              </button>
              <div class="owner-overlay-badges">
              <span v-for="badge in entry.badges" :key="badge.label" class="overlay-badge" :class="`is-${badge.tone}`">{{ badge.label }}</span>
              </div>
            </div>
          </div>
          <div class="owner-overlay-meta">
            <span>{{ entry.clusterLabel }}</span>
            <span>{{ entry.statusLabel }}</span>
          </div>
        </div>

        <div class="owner-overlay-gauges">
          <GaugeMeter v-for="meter in entry.meters" :key="meter.label" :meter="meter" />
        </div>

        <dl class="overlay-stat-grid owner-overlay-stats">
          <div v-for="stat in entry.stats" :key="stat.label" :title="stat.label">
            <dt>
              <span v-if="statIcons[stat.label]" class="stat-icon" v-html="statIcons[stat.label]" aria-hidden="true" />
              <template v-else>{{ stat.label }}</template>
            </dt>
            <dd>{{ stat.value }}</dd>
          </div>
        </dl>

        <div class="actions-compact">
          <button v-if="!entry.alive" class="button-secondary button-compact" type="button" @click.stop="handleLifecycleAction(entry.id, entry.alive)">
            Spawn
          </button>
          <button v-else class="button-ghost button-compact" type="button" @click.stop="gateway.destroyShip(entry.id)">Destroy</button>
          <button class="button-danger button-compact" type="button" @click.stop="gateway.removeShip(entry.id)">Remove</button>
          <button
            class="button-secondary button-compact owner-overlay-action-button"
            :class="{ 'is-ready': hasAvailableSubsystemUpgrade(entry.id) }"
            type="button"
            @click.stop="toggleSubsystems(entry.id)"
          >
            Modules
          </button>
        </div>
      </button>

      <ShipCreator @create="gateway.createShip($event)" />
    </section>

    <ShipSubsystemPopover
      v-if="openSubsystemsForId"
      :controllable-id="openSubsystemsForId"
      :display-name="entries.find((entry) => entry.id === openSubsystemsForId)?.displayName ?? 'Ship'"
      :overlay-state="ownerOverlay[openSubsystemsForId]"
      @close="openSubsystemsForId = ''"
    />
  </aside>
</template>

<style scoped>
.owner-overlay-head-actions {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 0.35rem;
  flex-wrap: wrap;
}

.ship-color-control {
  display: inline-flex;
  align-items: center;
  gap: 0.32rem;
  padding: 0.18rem 0.38rem;
  border-radius: 999px;
  background: rgba(255, 255, 255, 0.04);
  cursor: pointer;
}

.ship-color-control-label {
  color: var(--text-muted);
  font-size: 0.54rem;
  line-height: 1;
  text-transform: uppercase;
  letter-spacing: 0.08em;
}

.ship-color-input {
  width: 1.1rem;
  height: 1.1rem;
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
  border-radius: 999px;
}

.ship-color-input::-moz-color-swatch {
  border: 1px solid rgba(255, 255, 255, 0.16);
  border-radius: 999px;
}

.ship-color-reset {
  padding-inline: 0.46rem;
  font-size: 0.58rem;
}

.owner-overlay-action-button {
  font-size: 0.64rem;
  padding-inline: 0.52rem;
}

.owner-overlay-action-button.is-ready {
  border-color: rgba(125, 255, 178, 0.9);
  color: #dfffe8;
  background: rgba(52, 138, 92, 0.28);
}
</style>
